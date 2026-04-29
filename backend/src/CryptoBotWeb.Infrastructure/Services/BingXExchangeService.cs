using BingX.Net.Clients;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BingXExchangeService : IExchangeService, IDisposable
{
    private readonly BingXRestClient _client;

    public BingXExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null)
    {
        _client = new BingXRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;
        });
    }

    public async Task<(bool Success, string? Error)> TestConnectionAsync()
    {
        var result = await _client.SpotApi.Account.GetBalancesAsync();
        return (result.Success, result.Error?.ToString());
    }

    public async Task<List<BalanceDto>> GetBalancesAsync()
    {
        // Platform trades only USDT-M perpetual futures, so report the futures wallet, not spot.
        var result = await _client.PerpetualFuturesApi.Account.GetBalancesAsync();
        if (!result.Success || result.Data == null)
            return new List<BalanceDto>();

        // BingX futures: `balance` is the wallet balance shown on the exchange (no unrealized PnL).
        // `equity = balance + unrealizedProfit`. Decomposed: balance = availableMargin + usedMargin + frozenMargin.
        // We anchor Total to `balance` so it matches the exchange UI exactly, and derive Locked as the
        // remainder so Free + Locked == wallet balance even if the margin breakdown rounds slightly.
        // If `balance` is missing, fall back to `equity` (then Total includes unrealized PnL — best effort).
        return result.Data
            .Where(b => (b.Balance ?? 0) > 0 || (b.Equity ?? 0) > 0 || (b.AvailableMargin ?? 0) > 0 || (b.FrozenMargin ?? 0) > 0)
            .Select(b =>
            {
                var available = b.AvailableMargin ?? 0m;
                var wallet = b.Balance ?? b.Equity ?? available;
                var locked = wallet - available;
                if (locked < 0) locked = 0m;
                return new BalanceDto
                {
                    Asset = b.Asset,
                    Free = available,
                    Locked = locked
                };
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
