using System.Collections.Concurrent;
using BingX.Net.Clients;
using Bitget.Net.Clients;
using Bitget.Net.Enums;
using Bybit.Net.Clients;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Options;
using CryptoExchange.Net.Objects.Sockets;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ExchangeType = CryptoBotWeb.Core.Enums.ExchangeType;

namespace CryptoBotWeb.Infrastructure.Services;

/// <summary>
/// Singleton holding one websocket top-of-book stream per (exchange, symbol).
///
/// Streams are opened on demand by <see cref="EnsureSubscribedAsync"/> and shared: two arbitrage
/// bots watching ADAUSDT on Bybit use the same connection. An entry whose consumers stopped
/// asking for it is dropped by the janitor after <see cref="IdleDropMinutes"/>, so stopping a bot
/// eventually closes its sockets without anyone having to track subscriptions explicitly.
///
/// Deliberately does NOT decide anything about freshness: it stamps every quote with arrival time
/// and hands consumers the raw last value. Judging staleness belongs to the caller, which is the
/// only side that knows whether it is about to trade on the number or merely display it.
/// </summary>
public sealed class QuoteStreamService : IQuoteStreamService, IDisposable
{
    // Drop a stream nobody asked about for this long. Must comfortably exceed the trading loop's
    // 5s tick so a temporarily slow tick never tears down a live subscription.
    private const int IdleDropMinutes = 5;

    // After a failed subscribe, wait this long before trying again — a symbol the exchange does
    // not stream would otherwise reconnect on every tick.
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan JanitorInterval = TimeSpan.FromMinutes(1);

    private readonly IExchangeServiceFactory _factory;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<QuoteStreamService> _logger;
    private readonly ConcurrentDictionary<string, StreamEntry> _streams = new();
    private readonly Timer _janitor;
    private volatile bool _disposed;

    public QuoteStreamService(IExchangeServiceFactory factory, IEncryptionService encryption,
        ILogger<QuoteStreamService> logger)
    {
        _factory = factory;
        _encryption = encryption;
        _logger = logger;
        _janitor = new Timer(_ => DropIdleStreams(), null, JanitorInterval, JanitorInterval);
    }

    public async Task EnsureSubscribedAsync(ExchangeAccount account, string symbol, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(symbol)) return;

        var key = Key(account.ExchangeType, symbol);
        var entry = _streams.GetOrAdd(key, _ => new StreamEntry());
        entry.LastRequestedUtc = DateTime.UtcNow;

        if (entry.Subscription != null) return;
        if (DateTime.UtcNow < entry.NextAttemptUtc) return;

        // Non-blocking: if another tick is already connecting, this caller just uses REST for now.
        // The trading loop must never wait on a websocket handshake.
        if (!await entry.Gate.WaitAsync(0, ct)) return;
        try
        {
            if (entry.Subscription != null || DateTime.UtcNow < entry.NextAttemptUtc) return;
            await SubscribeAsync(entry, account, symbol, key, ct);
        }
        catch (Exception ex)
        {
            entry.LastError = ex.Message;
            entry.NextAttemptUtc = DateTime.UtcNow + FailureBackoff;
            _logger.LogWarning(ex, "Quote stream {Key}: subscribe threw", key);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public QuoteSnapshot? TryGetQuote(ExchangeType exchange, string symbol)
        => _streams.TryGetValue(Key(exchange, symbol), out var entry) ? entry.Quote : null;

    public QuoteStreamStatus? GetStatus(ExchangeType exchange, string symbol)
        => _streams.TryGetValue(Key(exchange, symbol), out var entry)
            ? new QuoteStreamStatus(entry.Connected, entry.Quote?.UpdatedAtUtc,
                Interlocked.Read(ref entry.UpdateCount), entry.LastError)
            : null;

    // ────────────────────────── Subscription ──────────────────────────

    private async Task SubscribeAsync(StreamEntry entry, ExchangeAccount account, string symbol,
        string key, CancellationToken ct)
    {
        // Same failover/health-aware proxy the account's REST calls use: a stream leaving from a
        // different IP than the orders it feeds is asking for geo-block surprises.
        var proxy = ProxyApiFactory.Build(_factory.SelectProxyFor(account), _encryption);

        IDisposable client;
        CallResult<UpdateSubscription> result;

        switch (account.ExchangeType)
        {
            case ExchangeType.Bybit:
            {
                var bybit = new BybitSocketClient(o => Configure(o, proxy));
                client = bybit;
                // Depth-1 book (10ms push). Bybit sends deltas that may carry only one side, and
                // a zero-quantity entry means the level was removed — Apply keeps the last known
                // value for whichever side this message does not update.
                result = await bybit.V5LinearApi.SubscribeToOrderbookUpdatesAsync(
                    SymbolHelper.ToExchangeSymbol(symbol, ExchangeType.Bybit), 1,
                    update => Apply(entry, BestPrice(update.Data.Bids?.Select(b => (b.Price, b.Quantity))),
                                           BestPrice(update.Data.Asks?.Select(a => (a.Price, a.Quantity)))),
                    ct);
                break;
            }

            case ExchangeType.BingX:
            {
                var bingx = new BingXSocketClient(o => Configure(o, proxy));
                client = bingx;
                // BingX streams best bid/ask directly — no book reconstruction needed.
                result = await bingx.PerpetualFuturesApi.SubscribeToBookPriceUpdatesAsync(
                    SymbolHelper.ToExchangeSymbol(symbol, ExchangeType.BingX),
                    update => Apply(entry, update.Data.BestBidPrice, update.Data.BestAskPrice),
                    ct);
                break;
            }

            case ExchangeType.Bitget:
            {
                var bitget = new BitgetSocketClient(o => Configure(o, proxy));
                client = bitget;
                result = await bitget.FuturesApiV2.SubscribeToOrderBookUpdatesAsync(
                    BitgetProductTypeV2.UsdtFutures,
                    SymbolHelper.ToExchangeSymbol(symbol, ExchangeType.Bitget), 1,
                    update =>
                    {
                        var book = update.Data?.FirstOrDefault();
                        if (book == null) return;
                        Apply(entry, BestPrice(book.Bids?.Select(b => (b.Price, b.Quantity))),
                                     BestPrice(book.Asks?.Select(a => (a.Price, a.Quantity))));
                    },
                    ct);
                break;
            }

            default:
                // Dzengi (and anything new) has no stream here; FuturesArbitrage already refuses
                // Dzengi accounts, so this only means "keep using REST".
                entry.NextAttemptUtc = DateTime.MaxValue;
                entry.LastError = $"{account.ExchangeType} has no quote stream implementation";
                return;
        }

        if (!result.Success)
        {
            client.Dispose();
            entry.LastError = result.Error?.Message ?? "unknown error";
            entry.NextAttemptUtc = DateTime.UtcNow + FailureBackoff;
            _logger.LogWarning("Quote stream {Key}: subscribe failed — {Error}", key, entry.LastError);
            return;
        }

        var subscription = result.Data;
        subscription.ConnectionLost += () =>
        {
            entry.Connected = false;
            _logger.LogWarning("Quote stream {Key}: connection lost", key);
        };
        subscription.ConnectionRestored += downtime =>
        {
            entry.Connected = true;
            _logger.LogInformation("Quote stream {Key}: reconnected after {Seconds:F0}s", key, downtime.TotalSeconds);
        };
        subscription.Exception += ex =>
        {
            entry.LastError = ex.Message;
            _logger.LogWarning("Quote stream {Key}: {Error}", key, ex.Message);
        };

        entry.Client = client;
        entry.Subscription = subscription;
        entry.Connected = true;
        entry.LastError = null;
        _logger.LogInformation("Quote stream {Key}: subscribed{Proxy}", key, proxy == null ? "" : " (via proxy)");
    }

    private static void Configure(SocketExchangeOptions options, ApiProxy? proxy)
    {
        if (proxy != null) options.Proxy = proxy;

        // A silent socket is the dangerous failure mode: force the client to treat a quiet
        // connection as dead and reconnect, instead of serving a frozen book indefinitely.
        options.SocketNoDataTimeout = TimeSpan.FromSeconds(30);
        options.ReconnectInterval = TimeSpan.FromSeconds(5);
    }

    /// <summary>Best price from a book side, skipping levels the exchange is deleting (qty 0).</summary>
    private static decimal? BestPrice(IEnumerable<(decimal Price, decimal Quantity)>? side)
    {
        if (side == null) return null;
        foreach (var level in side)
            if (level.Quantity > 0 && level.Price > 0) return level.Price;
        return null;
    }

    private static void Apply(StreamEntry entry, decimal? bid, decimal? ask)
    {
        var previous = entry.Quote;
        var newBid = bid ?? previous?.Bid ?? 0m;
        var newAsk = ask ?? previous?.Ask ?? 0m;
        if (newBid <= 0 || newAsk <= 0) return;

        entry.Quote = new QuoteSnapshot(newBid, newAsk, DateTime.UtcNow);
        Interlocked.Increment(ref entry.UpdateCount);
        entry.Connected = true;
    }

    // ────────────────────────── Lifecycle ──────────────────────────

    private void DropIdleStreams()
    {
        if (_disposed) return;

        var cutoff = DateTime.UtcNow.AddMinutes(-IdleDropMinutes);
        foreach (var (key, entry) in _streams.ToArray())
        {
            if (entry.LastRequestedUtc > cutoff) continue;
            if (!_streams.TryRemove(key, out _)) continue;

            _logger.LogInformation("Quote stream {Key}: idle, closing", key);
            CloseEntry(entry);
        }
    }

    private void CloseEntry(StreamEntry entry)
    {
        try { entry.Client?.Dispose(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Quote stream: dispose failed"); }
        entry.Subscription = null;
        entry.Client = null;
        entry.Connected = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _janitor.Dispose();
        foreach (var (_, entry) in _streams.ToArray()) CloseEntry(entry);
        _streams.Clear();
    }

    private static string Key(ExchangeType exchange, string symbol)
        => $"{exchange}|{symbol.Trim().ToUpperInvariant()}";

    private sealed class StreamEntry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IDisposable? Client;
        public UpdateSubscription? Subscription;
        public long UpdateCount;
        public volatile bool Connected;
        public volatile string? LastError;

        private QuoteSnapshot? _quote;
        private long _lastRequestedTicks = DateTime.UtcNow.Ticks;
        private long _nextAttemptTicks;

        public QuoteSnapshot? Quote
        {
            get => Volatile.Read(ref _quote);
            set => Volatile.Write(ref _quote, value);
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
