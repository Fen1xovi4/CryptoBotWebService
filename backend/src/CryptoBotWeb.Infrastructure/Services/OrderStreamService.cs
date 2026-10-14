using System.Collections.Concurrent;
using Bybit.Net.Clients;
using Bybit.Net.Enums;
using Bybit.Net.Objects.Models.V5;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects.Sockets;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ExchangeType = CryptoBotWeb.Core.Enums.ExchangeType;

namespace CryptoBotWeb.Infrastructure.Services;

/// <summary>
/// Singleton holding one private websocket order stream per exchange account (V1: Bybit only).
///
/// Every order update the account produces is folded into a per-account cache keyed by order id.
/// Callers never wait on the socket: they ask <see cref="TryGetTrusted"/>, and on "untrusted" fall
/// back to REST and <see cref="Seed"/> the answer so the next tick is served from the cache.
///
/// The trust rule is the safety core of this class — see <see cref="IOrderStreamService"/>.
/// A cached Open status is only believed while the socket has been continuously up since that
/// status was learned, and never for longer than <see cref="ReverifyAfter"/>, so a socket that
/// dies silently costs at most one REST lookup per resting order per window, not a missed fill.
/// </summary>
public sealed class OrderStreamService : IOrderStreamService, IDisposable
{
    // Drop an account stream nobody asked about for this long. Must comfortably exceed the
    // trading loop's 5s tick so a slow tick never tears a live stream down.
    private const int IdleDropMinutes = 5;

    // Cache entries untouched (neither queried nor updated) for this long are forgotten. A bot
    // tracking the order queries it every tick, so only orders nobody tracks age out.
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(24);

    // A non-terminal status is re-confirmed over REST at least this often, even with a healthy
    // socket. Bounds the damage of a socket that stays "connected" but stops delivering — a
    // failure mode the SDK cannot always detect on a private stream that is legitimately quiet.
    private static readonly TimeSpan ReverifyAfter = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan JanitorInterval = TimeSpan.FromMinutes(1);

    private readonly IExchangeServiceFactory _factory;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<OrderStreamService> _logger;
    private readonly ConcurrentDictionary<Guid, AccountStream> _streams = new();
    private readonly Timer _janitor;
    private volatile bool _disposed;

    public OrderStreamService(IExchangeServiceFactory factory, IEncryptionService encryption,
        ILogger<OrderStreamService> logger)
    {
        _factory = factory;
        _encryption = encryption;
        _logger = logger;
        _janitor = new Timer(_ => Janitor(), null, JanitorInterval, JanitorInterval);
    }

    public async Task EnsureSubscribedAsync(ExchangeAccount account, CancellationToken ct = default)
    {
        if (_disposed) return;

        var stream = _streams.GetOrAdd(account.Id, _ => new AccountStream(account.ExchangeType));
        stream.LastRequestedUtc = DateTime.UtcNow;

        if (stream.Subscription != null) return;
        if (DateTime.UtcNow < stream.NextAttemptUtc) return;

        // Non-blocking: if another bot on this account is already connecting, this caller just
        // uses REST for now. The trading loop must never wait on a websocket handshake.
        if (!await stream.Gate.WaitAsync(0, ct)) return;
        try
        {
            if (stream.Subscription != null || DateTime.UtcNow < stream.NextAttemptUtc) return;
            await SubscribeAsync(stream, account, ct);
        }
        catch (Exception ex)
        {
            stream.LastError = ex.Message;
            stream.NextAttemptUtc = DateTime.UtcNow + FailureBackoff;
            _logger.LogWarning(ex, "Order stream {Account}: subscribe threw", account.Id);
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    public bool IsHealthy(Guid accountId)
        => _streams.TryGetValue(accountId, out var s) && s.Connected && s.Subscription != null;

    public bool TryGetTrusted(Guid accountId, string orderId, out OrderStatusDto status)
    {
        status = null!;
        if (!_streams.TryGetValue(accountId, out var stream)) return false;
        if (!stream.Orders.TryGetValue(orderId, out var entry)) return false;

        var now = DateTime.UtcNow;
        lock (entry)
        {
            entry.LastTouchedUtc = now;

            if (!stream.Connected || stream.Subscription == null) return false;

            // Learned before the current uninterrupted connection began → a change may have
            // happened while we were deaf. Make the caller re-learn it over REST.
            if (entry.KnownAtUtc < stream.ContinuousSinceUtc) return false;

            if (!IsTerminal(entry.Status.Status) && now - entry.KnownAtUtc > ReverifyAfter) return false;

            status = Clone(entry.Status);
            return true;
        }
    }

    public void Seed(Guid accountId, OrderStatusDto status, DateTime knownAsOfUtc)
    {
        if (string.IsNullOrEmpty(status.OrderId)) return;
        // Only accounts with a stream implementation keep a cache; for the rest a seed would
        // never be trusted anyway.
        if (!_streams.TryGetValue(accountId, out var stream) || stream.Exchange != ExchangeType.Bybit) return;
        Merge(stream, Clone(status), knownAsOfUtc);
    }

    // ────────────────────────── Subscription ──────────────────────────

    private async Task SubscribeAsync(AccountStream stream, ExchangeAccount account, CancellationToken ct)
    {
        if (account.ExchangeType != ExchangeType.Bybit)
        {
            stream.NextAttemptUtc = DateTime.MaxValue;
            stream.LastError = $"{account.ExchangeType} has no order stream implementation";
            return;
        }

        // Same failover/health-aware proxy as the account's REST calls — a private stream from a
        // different IP than the orders it reports on invites IP-whitelist rejections.
        var proxy = ProxyApiFactory.Build(_factory.SelectProxyFor(account), _encryption);
        var apiKey = _encryption.Decrypt(account.ApiKeyEncrypted);
        var apiSecret = _encryption.Decrypt(account.ApiSecretEncrypted);

        var client = new BybitSocketClient(o =>
        {
            o.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) o.Proxy = proxy;
            o.ReconnectInterval = TimeSpan.FromSeconds(5);
            // NO SocketNoDataTimeout here, unlike the quote streams: a private order stream is
            // legitimately silent whenever nothing trades, and a no-data timeout would cycle the
            // connection (and invalidate the whole cache) every quiet half-minute. Liveness is
            // the SDK's ping/pong plus ReverifyAfter.
        });

        var result = await client.V5PrivateApi.SubscribeToOrderUpdatesAsync(
            update => OnOrderUpdates(stream, update.Data), ct);

        if (!result.Success)
        {
            client.Dispose();
            stream.LastError = result.Error?.Message ?? "unknown error";
            stream.NextAttemptUtc = DateTime.UtcNow + FailureBackoff;
            _logger.LogWarning("Order stream {Account}: subscribe failed — {Error}", account.Id, stream.LastError);
            return;
        }

        var subscription = result.Data;
        var id = account.Id;
        subscription.ConnectionLost += () =>
        {
            stream.Connected = false;
            _logger.LogWarning("Order stream {Account}: connection lost", id);
        };
        subscription.ConnectionRestored += downtime =>
        {
            // Anything learned before now may have changed while we were down — restarting the
            // continuity clock makes every cached entry untrusted until re-learned.
            stream.ContinuousSinceUtc = DateTime.UtcNow;
            stream.Connected = true;
            _logger.LogInformation("Order stream {Account}: reconnected after {Seconds:F0}s", id, downtime.TotalSeconds);
        };
        subscription.ConnectionClosed += () =>
        {
            // Closed for good (no auto-reconnect). Drop the client so the next Ensure rebuilds it.
            stream.Connected = false;
            _logger.LogWarning("Order stream {Account}: connection closed", id);
            CloseStream(stream);
        };
        subscription.ResubscribingFailed += error =>
        {
            stream.Connected = false;
            stream.LastError = $"resubscribe failed: {error}";
            _logger.LogWarning("Order stream {Account}: resubscribe failed — {Error}", id, error);
        };
        subscription.ActivityPaused += () =>
        {
            stream.Connected = false;
            _logger.LogWarning("Order stream {Account}: paused by the server", id);
        };
        subscription.ActivityUnpaused += () =>
        {
            stream.ContinuousSinceUtc = DateTime.UtcNow;
            stream.Connected = true;
            _logger.LogInformation("Order stream {Account}: resumed", id);
        };
        subscription.Exception += ex =>
        {
            stream.LastError = ex.Message;
            _logger.LogWarning("Order stream {Account}: {Error}", id, ex.Message);
        };

        stream.Client = client;
        stream.Subscription = subscription;
        // Stamped after the subscribe call returned — anything seeded before this is untrusted.
        stream.ContinuousSinceUtc = DateTime.UtcNow;
        stream.Connected = true;
        stream.LastError = null;
        _logger.LogInformation("Order stream {Account}: subscribed{Proxy}", id, proxy == null ? "" : " (via proxy)");
    }

    private void OnOrderUpdates(AccountStream stream, BybitOrderUpdate[]? orders)
    {
        if (orders == null) return;
        var now = DateTime.UtcNow;
        foreach (var o in orders)
        {
            // Linear perps only — the same scope GetOrderAsync queries. Spot orders on a shared
            // key (GridHedge) are someone else's business.
            if (o.Category != Category.Linear || string.IsNullOrEmpty(o.OrderId)) continue;

            Merge(stream, new OrderStatusDto
            {
                OrderId = o.OrderId,
                Status = BybitFuturesExchangeService.MapOrderStatus(o.Status),
                FilledQuantity = o.QuantityFilled ?? 0m,
                AverageFilledPrice = o.AveragePrice ?? 0m,
                Fee = o.ExecutedFee.HasValue ? Math.Abs(o.ExecutedFee.Value) : null
            }, now);
        }
        Interlocked.Add(ref stream.UpdateCount, orders.Length);
    }

    /// <summary>
    /// Monotonic merge: an order only moves forward (Open → PartiallyFilled → terminal) and its
    /// filled quantity only grows. An older view arriving late — a REST answer sent before a fill
    /// the socket already reported — can refresh the timestamp but never roll the status back.
    /// </summary>
    private static void Merge(AccountStream stream, OrderStatusDto incoming, DateTime knownAtUtc)
    {
        stream.Orders.AddOrUpdate(incoming.OrderId,
            _ => new OrderEntry(incoming, knownAtUtc),
            (_, existing) =>
            {
                lock (existing)
                {
                    var keepExisting = Rank(existing.Status.Status) > Rank(incoming.Status)
                        || (Rank(existing.Status.Status) == Rank(incoming.Status)
                            && existing.Status.FilledQuantity > incoming.FilledQuantity);
                    if (!keepExisting) existing.Status = incoming;
                    if (knownAtUtc > existing.KnownAtUtc) existing.KnownAtUtc = knownAtUtc;
                    existing.LastTouchedUtc = DateTime.UtcNow;
                    return existing;
                }
            });
    }

    private static int Rank(OrderLifecycleStatus s) => s switch
    {
        OrderLifecycleStatus.Open => 1,
        OrderLifecycleStatus.PartiallyFilled => 2,
        OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled or OrderLifecycleStatus.Rejected => 3,
        _ => 0
    };

    private static bool IsTerminal(OrderLifecycleStatus s) => Rank(s) == 3;

    private static OrderStatusDto Clone(OrderStatusDto s) => new()
    {
        OrderId = s.OrderId,
        Status = s.Status,
        FilledQuantity = s.FilledQuantity,
        AverageFilledPrice = s.AverageFilledPrice,
        Fee = s.Fee
    };

    // ────────────────────────── Lifecycle ──────────────────────────

    private void Janitor()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        var idleCutoff = now.AddMinutes(-IdleDropMinutes);

        foreach (var (accountId, stream) in _streams.ToArray())
        {
            if (stream.LastRequestedUtc <= idleCutoff && _streams.TryRemove(accountId, out _))
            {
                _logger.LogInformation("Order stream {Account}: idle, closing", accountId);
                CloseStream(stream);
                continue;
            }

            foreach (var (orderId, entry) in stream.Orders.ToArray())
                if (now - entry.LastTouchedUtc > EntryTtl)
                    stream.Orders.TryRemove(orderId, out _);
        }
    }

    private void CloseStream(AccountStream stream)
    {
        var client = stream.Client;
        stream.Subscription = null;
        stream.Client = null;
        stream.Connected = false;
        try { client?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Order stream: dispose failed"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _janitor.Dispose();
        foreach (var (_, stream) in _streams.ToArray()) CloseStream(stream);
        _streams.Clear();
    }

    private sealed class OrderEntry
    {
        public OrderEntry(OrderStatusDto status, DateTime knownAtUtc)
        {
            Status = status;
            KnownAtUtc = knownAtUtc;
            LastTouchedUtc = DateTime.UtcNow;
        }

        public OrderStatusDto Status;
        public DateTime KnownAtUtc;
        public DateTime LastTouchedUtc;
    }

    private sealed class AccountStream
    {
        public AccountStream(ExchangeType exchange) => Exchange = exchange;

        public readonly ExchangeType Exchange;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly ConcurrentDictionary<string, OrderEntry> Orders = new();
        public volatile IDisposable? Client;
        public volatile UpdateSubscription? Subscription;
        public volatile bool Connected;
        public volatile string? LastError;
        public long UpdateCount;

        private long _continuousSinceTicks = DateTime.MaxValue.Ticks;
        private long _lastRequestedTicks = DateTime.UtcNow.Ticks;
        private long _nextAttemptTicks;

        public DateTime ContinuousSinceUtc
        {
            get => new(Volatile.Read(ref _continuousSinceTicks), DateTimeKind.Utc);
            set => Volatile.Write(ref _continuousSinceTicks, value.Ticks);
        }

        public DateTime LastRequestedUtc
        {
            get => new(Volatile.Read(ref _lastRequestedTicks), DateTimeKind.Utc);
            set => Volatile.Write(ref _lastRequestedTicks, value.Ticks);
        }

        public DateTime NextAttemptUtc
        {
            get => new(Volatile.Read(ref _nextAttemptTicks), DateTimeKind.Utc);
            set => Volatile.Write(ref _nextAttemptTicks, value.Ticks);
        }
    }
}
