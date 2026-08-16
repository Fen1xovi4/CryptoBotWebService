using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

/// <summary>
/// Maps a stored <see cref="ProxyServer"/> onto the CryptoExchange.Net <see cref="ApiProxy"/> both
/// REST clients (<see cref="ExchangeServiceFactory"/>) and websocket clients (QuoteStreamService)
/// need. Shared so the two never drift — a websocket that skips the proxy would come from a
/// different egress IP than the REST calls it is paired with.
/// </summary>
public static class ProxyApiFactory
{
    public static ApiProxy? Build(ProxyServer? proxyServer, IEncryptionService encryption)
    {
        if (proxyServer == null) return null;

        // JKorf uses new Uri($"{Host}:{Port}"), so Host must include scheme
        var host = proxyServer.Host;
        if (!host.Contains("://"))
            host = $"socks5://{host}";

        if (proxyServer.Username != null && proxyServer.PasswordEncrypted != null)
        {
            var password = encryption.Decrypt(proxyServer.PasswordEncrypted);
            return new ApiProxy(host, proxyServer.Port, proxyServer.Username, password);
        }

        return new ApiProxy(host, proxyServer.Port);
    }
}
