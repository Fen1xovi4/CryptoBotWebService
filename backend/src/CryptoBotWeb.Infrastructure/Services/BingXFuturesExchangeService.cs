using BingX.Net.Clients;
using BingX.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BingXFuturesExchangeService : IFuturesExchangeService
{
    private readonly BingXRestClient _client;

    public BingXFuturesExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null)
    {
        _client = new BingXRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;
        });
    }

    public async Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit)
    {
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var interval = MapInterval(timeframe);
        var result = await _client.PerpetualFuturesApi.ExchangeData.GetKlinesAsync(
            bingxSymbol, interval, limit: limit);

        if (!result.Success || result.Data == null)
            return new List<CandleDto>();

        return result.Data
            .Select(k => new CandleDto
            {
                OpenTime = k.Timestamp,
                CloseTime = k.Timestamp + GetTimeframeSpan(timeframe),
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
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var result = await _client.PerpetualFuturesApi.ExchangeData.GetTickersAsync();

        if (!result.Success || result.Data == null)
            return null;

        var ticker = result.Data.FirstOrDefault(t => t.Symbol == bingxSymbol);
        return ticker?.LastPrice;
    }

    public async Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount)
    {
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(bingxSymbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Buy, FuturesOrderType.Market,
            PositionSide.Long, quantity);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId.ToString(),
            FilledPrice = price,
            FilledQuantity = quantity,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> OpenShortAsync(string symbol, decimal quoteAmount)
    {
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(bingxSymbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Sell, FuturesOrderType.Market,
            PositionSide.Short, quantity);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId.ToString(),
            FilledPrice = price,
            FilledQuantity = quantity,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> CloseLongAsync(string symbol, decimal quantity)
    {
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Sell, FuturesOrderType.Market,
            PositionSide.Long, quantity);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId.ToString(),
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity)
    {
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Buy, FuturesOrderType.Market,
            PositionSide.Short, quantity);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId.ToString(),
            ErrorMessage = result.Error?.Message
        };
    }

    private static KlineInterval MapInterval(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => KlineInterval.OneMinute,
        "3m" => KlineInterval.ThreeMinutes,
        "5m" => KlineInterval.FiveMinutes,
        "15m" => KlineInterval.FifteenMinutes,
        "30m" => KlineInterval.ThirtyMinutes,
        "1h" => KlineInterval.OneHour,
        "4h" => KlineInterval.FourHours,
        "6h" => KlineInterval.SixHours,
        "12h" => KlineInterval.TwelveHours,
        "1d" => KlineInterval.OneDay,
        "1w" => KlineInterval.OneWeek,
        _ => KlineInterval.OneHour
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

    private async Task<(decimal qtyStep, decimal minQty)> GetSymbolInfoAsync(string bingxSymbol)
    {
        var result = await _client.PerpetualFuturesApi.ExchangeData.GetContractsAsync();
        if (result.Success && result.Data != null)
        {
            var contract = result.Data.FirstOrDefault(c => c.Symbol == bingxSymbol);
            if (contract != null)
            {
                var step = (decimal)Math.Pow(10, -contract.QuantityPrecision);
                return (step, contract.MinOrderQuantity);
            }
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
