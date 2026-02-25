using Bitget.Net.Clients;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BitgetExchangeService : IExchangeService, IDisposable
{
    private readonly BitgetRestClient _client;

    public BitgetExchangeService(string apiKey, string apiSecret, string? passphrase, ApiProxy? proxy = null)
    {
        _client = new BitgetRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret, passphrase ?? "");
            if (proxy != null) options.Proxy = proxy;
        });
    }

    public async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        var result = await _client.SpotApiV2.Account.GetSpotBalancesAsync();
        return (result.Success, result.Error?.ToString());
    }

    public async Task<List<BalanceDto>> GetBalancesAsync()
    {
        var result = await _client.SpotApiV2.Account.GetSpotBalancesAsync();
        if (!result.Success || result.Data == null)
            return new List<BalanceDto>();

        return result.Data
            .Where(b => b.Available > 0 || b.Frozen > 0)
            .Select(b => new BalanceDto
            {
                Asset = b.Asset,
                Free = b.Available,
                Locked = b.Frozen
            })
            .ToList();
    }

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var result = await _client.SpotApiV2.ExchangeData.GetTickersAsync(symbol);
        if (!result.Success || result.Data == null)
            return null;

        var ticker = result.Data.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public void Dispose() => _client.Dispose();
}
