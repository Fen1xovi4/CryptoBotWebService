using CryptoExchange.Net.Objects;
using Microsoft.Extensions.Configuration;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;
using ExchangeType = CryptoBotWeb.Core.Enums.ExchangeType;

namespace CryptoBotWeb.Infrastructure.Services;

public class ExchangeServiceFactory : IExchangeServiceFactory
{
    private readonly IEncryptionService _encryption;
    private readonly IProxyHealthTracker _health;
    private readonly string? _bybitBrokerId;

    public ExchangeServiceFactory(IEncryptionService encryption, IProxyHealthTracker health, IConfiguration config)
    {
        _encryption = encryption;
        _health = health;
        // Bybit Broker Program code, sent as Referer header on every Bybit REST call.
        _bybitBrokerId = config["Bybit:BrokerId"];
    }

    public IExchangeService Create(ExchangeAccount account)
        => BuildExchangeService(account, BuildProxy(SelectProxy(account)));

    public IExchangeService CreateWithProxy(ExchangeAccount account, ProxyServer? proxy)
        => BuildExchangeService(account, BuildProxy(proxy));

    private IExchangeService BuildExchangeService(ExchangeAccount account, ApiProxy? proxy)
    {
        var apiKey = _encryption.Decrypt(account.ApiKeyEncrypted);
        var apiSecret = _encryption.Decrypt(account.ApiSecretEncrypted);

        return account.ExchangeType switch
        {
            ExchangeType.Bybit => new BybitExchangeService(apiKey, apiSecret, proxy, _bybitBrokerId),
            ExchangeType.Bitget => new BitgetExchangeService(
                apiKey, apiSecret,
                account.PassphraseEncrypted != null ? _encryption.Decrypt(account.PassphraseEncrypted) : null,
                proxy),
            ExchangeType.BingX => new BingXExchangeService(apiKey, apiSecret, proxy),
            ExchangeType.Dzengi => new DzengiExchangeService(apiKey, apiSecret, account.DzengiAccountId, proxy),
            _ => throw new ArgumentException($"Unsupported exchange type: {account.ExchangeType}")
        };
    }

    public ISpotExchangeService CreateSpot(ExchangeAccount account)
    {
        var apiKey = _encryption.Decrypt(account.ApiKeyEncrypted);
        var apiSecret = _encryption.Decrypt(account.ApiSecretEncrypted);
        var proxy = BuildProxy(SelectProxy(account));

        return account.ExchangeType switch
        {
            ExchangeType.Bybit => new BybitSpotExchangeService(apiKey, apiSecret, proxy, _bybitBrokerId),
            _ => throw new NotSupportedException(
                $"Spot trading is not implemented for {account.ExchangeType}. V1 ships Bybit-only spot.")
        };
    }

    public IFuturesExchangeService CreateFutures(ExchangeAccount account)
    {
        var apiKey = _encryption.Decrypt(account.ApiKeyEncrypted);
        var apiSecret = _encryption.Decrypt(account.ApiSecretEncrypted);
        var proxy = BuildProxy(SelectProxy(account));

        return account.ExchangeType switch
        {
            ExchangeType.Bybit => new BybitFuturesExchangeService(apiKey, apiSecret, proxy, _bybitBrokerId),
            ExchangeType.Bitget => new BitgetFuturesExchangeService(
                apiKey, apiSecret,
                account.PassphraseEncrypted != null ? _encryption.Decrypt(account.PassphraseEncrypted) : null,
                proxy),
            ExchangeType.BingX => new BingXFuturesExchangeService(apiKey, apiSecret, proxy),
            ExchangeType.Dzengi => new DzengiFuturesExchangeService(apiKey, apiSecret, account.DzengiAccountId, proxy),
            _ => throw new ArgumentException($"Unsupported exchange type: {account.ExchangeType}")
        };
    }

    /// <summary>
    /// Pick which proxy to use for this account, honoring failover order + health.
    /// Skips proxies in cooldown and TCP-prechecks candidates so a dead proxy is bypassed
    /// instead of hanging on the full exchange timeout. Requires AccountProxies (and their
    /// Proxy) to be loaded. Returns null for accounts with no proxies (direct connection).
    /// </summary>
    private ProxyServer? SelectProxy(ExchangeAccount account)
    {
        var proxies = account.OrderedProxies.ToList();
        if (proxies.Count == 0) return null;

        foreach (var p in proxies)
        {
            if (!_health.IsUsable(p.Id)) continue;
            // PrecheckAsync is bounded (~1.5 s) and cached (~30 s); blocking here is acceptable
            // on both the API request thread and the worker's parallel loop (no sync context).
            if (_health.PrecheckAsync(p).GetAwaiter().GetResult())
                return p;
            // precheck failed → proxy is now in cooldown; try the next one
        }

        // Nothing reachable right now: prefer any proxy not in cooldown, else the primary
        // (best effort — let the real call surface the error; next cycle re-prechecks).
        return proxies.FirstOrDefault(p => _health.IsUsable(p.Id)) ?? proxies[0];
    }

    private ApiProxy? BuildProxy(ProxyServer? proxyServer)
    {
        if (proxyServer == null) return null;

        // JKorf uses new Uri($"{Host}:{Port}"), so Host must include scheme
        var host = proxyServer.Host;
        if (!host.Contains("://"))
            host = $"socks5://{host}";

        if (proxyServer.Username != null && proxyServer.PasswordEncrypted != null)
        {
            var password = _encryption.Decrypt(proxyServer.PasswordEncrypted);
            return new ApiProxy(host, proxyServer.Port, proxyServer.Username, password);
        }

        return new ApiProxy(host, proxyServer.Port);
    }
}
