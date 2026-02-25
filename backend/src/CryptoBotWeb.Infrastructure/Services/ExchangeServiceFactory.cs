using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;
using ExchangeType = CryptoBotWeb.Core.Enums.ExchangeType;

namespace CryptoBotWeb.Infrastructure.Services;

public class ExchangeServiceFactory : IExchangeServiceFactory
{
    private readonly IEncryptionService _encryption;

    public ExchangeServiceFactory(IEncryptionService encryption)
    {
        _encryption = encryption;
    }

    public IExchangeService Create(ExchangeAccount account)
    {
        var apiKey = _encryption.Decrypt(account.ApiKeyEncrypted);
        var apiSecret = _encryption.Decrypt(account.ApiSecretEncrypted);
        var proxy = BuildProxy(account.Proxy);

        return account.ExchangeType switch
        {
            ExchangeType.Bybit => new BybitExchangeService(apiKey, apiSecret, proxy),
            ExchangeType.Bitget => new BitgetExchangeService(
                apiKey, apiSecret,
                account.PassphraseEncrypted != null ? _encryption.Decrypt(account.PassphraseEncrypted) : null,
                proxy),
            ExchangeType.BingX => new BingXExchangeService(apiKey, apiSecret, proxy),
            _ => throw new ArgumentException($"Unsupported exchange type: {account.ExchangeType}")
        };
    }

    public IFuturesExchangeService CreateFutures(ExchangeAccount account)
    {
        var apiKey = _encryption.Decrypt(account.ApiKeyEncrypted);
        var apiSecret = _encryption.Decrypt(account.ApiSecretEncrypted);
        var proxy = BuildProxy(account.Proxy);

        return account.ExchangeType switch
        {
            ExchangeType.Bybit => new BybitFuturesExchangeService(apiKey, apiSecret, proxy),
            ExchangeType.Bitget => new BitgetFuturesExchangeService(
                apiKey, apiSecret,
                account.PassphraseEncrypted != null ? _encryption.Decrypt(account.PassphraseEncrypted) : null,
                proxy),
            ExchangeType.BingX => new BingXFuturesExchangeService(apiKey, apiSecret, proxy),
            _ => throw new ArgumentException($"Unsupported exchange type: {account.ExchangeType}")
        };
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
