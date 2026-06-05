using System.Collections.Concurrent;
using System.Net.Sockets;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

/// <summary>
/// Thread-safe, in-memory proxy health/cooldown cache used by <see cref="ExchangeServiceFactory"/>
/// for failover. A proxy that fails (network error or failed TCP precheck) is parked in a
/// cooldown window and skipped; it auto-recovers once the window passes. Precheck results are
/// cached for a short TTL so we don't TCP-probe on every single create.
/// </summary>
public class ProxyHealthTracker : IProxyHealthTracker
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PrecheckTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrecheckTimeout = TimeSpan.FromMilliseconds(1500);

    // proxyId -> UTC time the cooldown ends (proxy unusable until then)
    private readonly ConcurrentDictionary<Guid, DateTime> _cooldownUntil = new();
    // proxyId -> (ok, checkedAtUtc) cached TCP precheck result
    private readonly ConcurrentDictionary<Guid, (bool Ok, DateTime At)> _precheck = new();

    public bool IsUsable(Guid proxyId)
        => !_cooldownUntil.TryGetValue(proxyId, out var until) || DateTime.UtcNow >= until;

    public void ReportFailure(Guid proxyId)
    {
        _cooldownUntil[proxyId] = DateTime.UtcNow + Cooldown;
        _precheck[proxyId] = (false, DateTime.UtcNow);
    }

    public void ReportSuccess(Guid proxyId)
    {
        _cooldownUntil.TryRemove(proxyId, out _);
        _precheck[proxyId] = (true, DateTime.UtcNow);
    }

    public async Task<bool> PrecheckAsync(ProxyServer proxy, CancellationToken ct = default)
    {
        if (_precheck.TryGetValue(proxy.Id, out var cached) && DateTime.UtcNow - cached.At < PrecheckTtl)
            return cached.Ok;

        var ok = await TcpProbeAsync(proxy.Host, proxy.Port, ct);
        _precheck[proxy.Id] = (ok, DateTime.UtcNow);
        if (!ok)
            _cooldownUntil[proxy.Id] = DateTime.UtcNow + Cooldown;
        return ok;
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, CancellationToken ct)
    {
        // Host may carry a scheme (e.g. "socks5://1.2.3.4"); strip it for a raw TCP connect.
        var bare = host.Contains("://") ? host[(host.IndexOf("://", StringComparison.Ordinal) + 3)..] : host;
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PrecheckTimeout);
            await client.ConnectAsync(bare, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
