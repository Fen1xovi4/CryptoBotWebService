using Bitget.Net.Clients;
using Bitget.Net.Enums;
using Bitget.Net.Enums.V2;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BitgetFuturesExchangeService : IFuturesExchangeService
{
    private readonly BitgetRestClient _client;

    public BitgetFuturesExchangeService(string apiKey, string apiSecret, string? passphrase, ApiProxy? proxy = null)
    {
        _client = new BitgetRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret, passphrase ?? "");
            if (proxy != null) options.Proxy = proxy;
        });
    }

    public async Task<List<SymbolDto>> GetSymbolsAsync()
    {
        var result = await _client.FuturesApiV2.ExchangeData.GetContractsAsync(BitgetProductTypeV2.UsdtFutures);
        if (!result.Success || result.Data == null)
            return new List<SymbolDto>();

        return result.Data
            .Select(c => new SymbolDto { Symbol = c.Symbol })
            .OrderBy(s => s.Symbol)
            .ToList();
    }

    public async Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        var interval = MapInterval(timeframe);
        var result = await _client.FuturesApiV2.ExchangeData.GetKlinesAsync(
            BitgetProductTypeV2.UsdtFutures, bitgetSymbol, interval, limit: limit);

        if (!result.Success)
            throw new Exception($"Bitget GetKlines failed: {result.Error?.Message ?? "unknown error"}");
        if (result.Data == null)
            return new List<CandleDto>();

        return result.Data
            .Select(k => new CandleDto
            {
                OpenTime = k.OpenTime,
                CloseTime = k.OpenTime + GetTimeframeSpan(timeframe),
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            })
            .OrderBy(c => c.OpenTime)
            .ToList();
    }

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        var result = await _client.FuturesApiV2.ExchangeData.GetTickersAsync(
            BitgetProductTypeV2.UsdtFutures);

        if (!result.Success || result.Data == null)
            return null;

        var ticker = result.Data.FirstOrDefault(t => t.Symbol == bitgetSymbol);
        return ticker?.LastPrice;
    }

    public async Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(bitgetSymbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.FuturesApiV2.Trading.PlaceOrderAsync(
            BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT",
            OrderSide.Buy, OrderType.Market, MarginMode.CrossMargin, quantity,
            tradeSide: TradeSide.Open);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            FilledPrice = price,
            FilledQuantity = quantity,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> OpenShortAsync(string symbol, decimal quoteAmount)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(bitgetSymbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.FuturesApiV2.Trading.PlaceOrderAsync(
            BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT",
            OrderSide.Sell, OrderType.Market, MarginMode.CrossMargin, quantity,
            tradeSide: TradeSide.Open);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            FilledPrice = price,
            FilledQuantity = quantity,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> CloseLongAsync(string symbol, decimal quantity)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        var result = await _client.FuturesApiV2.Trading.PlaceOrderAsync(
            BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT",
            OrderSide.Sell, OrderType.Market, MarginMode.CrossMargin, quantity,
            tradeSide: TradeSide.Close, reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        var result = await _client.FuturesApiV2.Trading.PlaceOrderAsync(
            BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT",
            OrderSide.Buy, OrderType.Market, MarginMode.CrossMargin, quantity,
            tradeSide: TradeSide.Close, reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            ErrorMessage = result.Error?.Message
        };
    }

    private static BitgetFuturesKlineInterval MapInterval(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => BitgetFuturesKlineInterval.OneMinute,
        "3m" => BitgetFuturesKlineInterval.ThreeMinutes,
        "5m" => BitgetFuturesKlineInterval.FiveMinutes,
        "15m" => BitgetFuturesKlineInterval.FifteenMinutes,
        "30m" => BitgetFuturesKlineInterval.ThirtyMinutes,
        "1h" => BitgetFuturesKlineInterval.OneHour,
        "4h" => BitgetFuturesKlineInterval.FourHours,
        "6h" => BitgetFuturesKlineInterval.SixHours,
        "12h" => BitgetFuturesKlineInterval.TwelveHours,
        "1d" => BitgetFuturesKlineInterval.OneDay,
        "1w" => BitgetFuturesKlineInterval.OneWeek,
        _ => BitgetFuturesKlineInterval.OneHour
    };

    private static TimeSpan GetTimeframeSpan(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => TimeSpan.FromMinutes(1),
        "3m" => TimeSpan.FromMinutes(3),
        "5m" => TimeSpan.FromMinutes(5),
        "15m" => TimeSpan.FromMinutes(15),
        "30m" => TimeSpan.FromMinutes(30),
        "1h" => TimeSpan.FromHours(1),
        "4h" => TimeSpan.FromHours(4),
        "6h" => TimeSpan.FromHours(6),
        "12h" => TimeSpan.FromHours(12),
        "1d" => TimeSpan.FromDays(1),
        "1w" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1)
    };

    private async Task<(decimal qtyStep, decimal minQty)> GetSymbolInfoAsync(string bitgetSymbol)
    {
        var result = await _client.FuturesApiV2.ExchangeData.GetContractsAsync(BitgetProductTypeV2.UsdtFutures);
        if (result.Success && result.Data != null)
        {
            var contract = result.Data.FirstOrDefault(c => c.Symbol == bitgetSymbol);
            if (contract != null)
                return (contract.QuantityStep, contract.MinOrderQuantity);
        }
        return (0.001m, 0m);
    }

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    public void Dispose() => _client.Dispose();
}
