using Bitget.Net.Clients;
using Bitget.Net.Enums;
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

    // Futures wallet plus the separate spot wallet (spot strategies trade from it), merged per
    // asset so the UI's per-asset rows and USDT total cover both.
    public async Task<List<BalanceDto>> GetBalancesAsync()
    {
        var merged = new Dictionary<string, BalanceDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in await GetFuturesBalancesAsync()) BalanceMerge.Add(merged, b);
        foreach (var b in await GetSpotBalancesAsync()) BalanceMerge.Add(merged, b);
        return merged.Values.ToList();
    }

    private async Task<List<BalanceDto>> GetSpotBalancesAsync()
    {
        var result = await _client.SpotApiV2.Account.GetSpotBalancesAsync();
        if (!result.Success || result.Data == null)
            return new List<BalanceDto>();
        return result.Data
            .Where(b => b.Available > 0 || b.Frozen > 0 || b.Locked > 0)
            .Select(b => new BalanceDto { Asset = b.Asset, Free = b.Available, Locked = b.Frozen + b.Locked })
            .ToList();
    }

    private async Task<List<BalanceDto>> GetFuturesBalancesAsync()
    {
        var result = await _client.FuturesApiV2.Account.GetBalancesAsync(BitgetProductTypeV2.UsdtFutures);
        if (!result.Success || result.Data == null)
            return new List<BalanceDto>();

        // Bitget V2 futures balance has no plain "wallet balance" field; `Equity` is what the
        // exchange UI shows as account balance (includes unrealized PnL). Anchor Total to Equity:
        // Free = Available (withdrawable), Locked = remainder so Free + Locked == Equity.
        return result.Data
            .Where(b => b.Available > 0 || b.Locked > 0 || b.Equity > 0)
            .Select(b =>
            {
                var equity = b.Equity;
                var available = b.Available;
                var locked = equity - available;
                if (locked < 0) locked = 0m;
                return new BalanceDto
                {
                    Asset = b.MarginAsset,
                    Free = available,
                    Locked = locked
                };
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
