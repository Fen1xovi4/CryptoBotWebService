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

    // Bybit Broker Program: our broker code ("Ty001081") must be sent as the Referer header
    // on every signed Bybit REST call — spot AND derivatives. Bybit.Net forwards options.Referer
    // automatically. Mirrors the wiring in BybitFuturesExchangeService and BybitSpotExchangeService.
    public BybitExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null, string? brokerId = null)
    {
        _client = new BybitRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;
            if (!string.IsNullOrWhiteSpace(brokerId)) options.Referer = brokerId;
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
                // Bybit Unified: Free/Locked may be null when funds back open positions.
                // Anchor Total to WalletBalance (matches the exchange UI's wallet balance);
                // fall back to Equity if WalletBalance is missing. Free + Locked == anchor.
                var wallet = asset.WalletBalance ?? 0m;
                var equity = asset.Equity ?? 0m;
                var free = asset.Free ?? 0m;
                var locked = asset.Locked ?? 0m;
                if (wallet == 0 && equity == 0 && free == 0 && locked == 0) continue;

                var anchor = wallet > 0 ? wallet : equity > 0 ? equity : free + locked;
                var freeOut = free > 0 ? free : anchor - locked;
                if (freeOut < 0) freeOut = 0m;
                var lockedOut = anchor - freeOut;
                if (lockedOut < 0) lockedOut = 0m;

                balances.Add(new BalanceDto
                {
                    Asset = asset.Asset,
                    Free = freeOut,
                    Locked = lockedOut
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
