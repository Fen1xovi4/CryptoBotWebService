using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;

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

        return account.ExchangeType switch
        {
            ExchangeType.Bybit => new BybitExchangeService(apiKey, apiSecret),
            ExchangeType.Bitget => new BitgetExchangeService(
                apiKey, apiSecret,
                account.PassphraseEncrypted != null ? _encryption.Decrypt(account.PassphraseEncrypted) : null),
            ExchangeType.BingX => new BingXExchangeService(apiKey, apiSecret),
            _ => throw new ArgumentException($"Unsupported exchange type: {account.ExchangeType}")
        };
    }

    public IFuturesExchangeService CreateFutures(ExchangeAccount account)
    {
        var apiKey = _encryption.Decrypt(account.ApiKeyEncrypted);
        var apiSecret = _encryption.Decrypt(account.ApiSecretEncrypted);

        return account.ExchangeType switch
        {
            ExchangeType.Bybit => new BybitFuturesExchangeService(apiKey, apiSecret),
            ExchangeType.Bitget => new BitgetFuturesExchangeService(
                apiKey, apiSecret,
                account.PassphraseEncrypted != null ? _encryption.Decrypt(account.PassphraseEncrypted) : null),
            ExchangeType.BingX => new BingXFuturesExchangeService(apiKey, apiSecret),
            _ => throw new ArgumentException($"Unsupported exchange type: {account.ExchangeType}")
        };
    }
}
