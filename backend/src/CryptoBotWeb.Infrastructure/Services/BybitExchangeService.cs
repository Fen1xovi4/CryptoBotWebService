using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BybitExchangeService : IExchangeService, IDisposable
{
    private readonly BybitRestClient _client;

    public BybitExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null)
    {
        _client = new BybitRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;
        });
    }

    public async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        var result = await _client.V5Api.Account.GetBalancesAsync(AccountType.Unified);
        return (result.Success, result.Error?.ToString());
    }

    public async Task<List<BalanceDto>> GetBalancesAsync()
    {
        var result = await _client.V5Api.Account.GetBalancesAsync(AccountType.Unified);
        if (!result.Success)
            return new List<BalanceDto>();

        var balances = new List<BalanceDto>();
        foreach (var account in result.Data.List)
        {
            if (account.Assets == null) continue;
            foreach (var asset in account.Assets)
            {
                var free = asset.Free ?? 0m;
                var locked = asset.Locked ?? 0m;
                if (free == 0 && locked == 0) continue;

                balances.Add(new BalanceDto
                {
                    Asset = asset.Asset,
                    Free = free,
                    Locked = locked
                });
            }
        }
        return balances;
    }

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var result = await _client.V5Api.ExchangeData.GetSpotTickersAsync(symbol);
        if (!result.Success || result.Data?.List == null)
            return null;

        var ticker = result.Data.List.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public void Dispose() => _client.Dispose();
}
