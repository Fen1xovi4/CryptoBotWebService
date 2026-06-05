using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

/// <summary>
/// In-memory, process-local health view of proxy servers used for failover.
/// API and Worker each keep their own instance (registered as a singleton in both).
/// </summary>
public interface IProxyHealthTracker
{
    /// <summary>False while the proxy is in a failure cooldown window.</summary>
    bool IsUsable(Guid proxyId);

    /// <summary>Mark a proxy as failed — puts it in cooldown so the factory skips it.</summary>
    void ReportFailure(Guid proxyId);

    /// <summary>Mark a proxy as healthy — clears any cooldown.</summary>
    void ReportSuccess(Guid proxyId);

    /// <summary>
    /// Fast TCP connect to Host:Port (short timeout), result cached briefly.
    /// Detects an unreachable/dead proxy without paying the full ~20 s exchange timeout.
    /// </summary>
    Task<bool> PrecheckAsync(ProxyServer proxy, CancellationToken ct = default);
}
