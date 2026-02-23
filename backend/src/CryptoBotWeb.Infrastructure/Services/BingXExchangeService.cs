using BingX.Net.Clients;
using CryptoExchange.Net.Authentication;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BingXExchangeService : IExchangeService, IDisposable
{
    private readonly BingXRestClient _client;

    public BingXExchangeService(string apiKey, string apiSecret)
    {
        _client = new BingXRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
        });
    }

    public async Task<bool> TestConnectionAsync()
    {
        var result = await _client.SpotApi.Account.GetBalancesAsync();
        return result.Success;
    }

    public async Task<List<BalanceDto>> GetBalancesAsync()
    {
        var result = await _client.SpotApi.Account.GetBalancesAsync();
        if (!result.Success || result.Data == null)
            return new List<BalanceDto>();

        return result.Data
            .Where(b => b.Free > 0 || b.Locked > 0)
            .Select(b => new BalanceDto
            {
                Asset = b.Asset,
                Free = b.Free,
                Locked = b.Locked
            })
            .ToList();
    }

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var result = await _client.SpotApi.ExchangeData.GetTickersAsync(symbol);
        if (!result.Success || result.Data == null)
            return null;

        var ticker = result.Data.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public void Dispose() => _client.Dispose();
}
