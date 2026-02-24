using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BybitFuturesExchangeService : IFuturesExchangeService
{
    private readonly BybitRestClient _client;

    public BybitFuturesExchangeService(string apiKey, string apiSecret)
    {
        _client = new BybitRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
        });
    }

    public async Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit)
    {
        var interval = MapInterval(timeframe);
        var result = await _client.V5Api.ExchangeData.GetKlinesAsync(
            Category.Linear, symbol, interval, limit: limit);

        if (!result.Success || result.Data?.List == null)
            return new List<CandleDto>();

        return result.Data.List
            .Select(k => new CandleDto
            {
                OpenTime = k.StartTime,
                CloseTime = k.StartTime + GetTimeframeSpan(timeframe),
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
        var result = await _client.V5Api.ExchangeData.GetLinearInverseTickersAsync(Category.Linear, symbol);
        if (!result.Success || result.Data?.List == null)
            return null;

        var ticker = result.Data.List.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public async Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount)
    {
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(symbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, symbol, OrderSide.Buy, NewOrderType.Market, quantity);

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
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(symbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, symbol, OrderSide.Sell, NewOrderType.Market, quantity);

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
        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, symbol, OrderSide.Sell, NewOrderType.Market, quantity,
            reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity)
    {
        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, symbol, OrderSide.Buy, NewOrderType.Market, quantity,
            reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
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
        "2h" => KlineInterval.TwoHours,
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
        "2h" => TimeSpan.FromHours(2),
        "4h" => TimeSpan.FromHours(4),
        "6h" => TimeSpan.FromHours(6),
        "12h" => TimeSpan.FromHours(12),
        "1d" => TimeSpan.FromDays(1),
        "1w" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1)
    };

    private async Task<(decimal qtyStep, decimal minQty)> GetSymbolInfoAsync(string symbol)
    {
        var result = await _client.V5Api.ExchangeData.GetLinearInverseSymbolsAsync(Category.Linear, symbol);
        if (result.Success && result.Data?.List?.Any() == true)
        {
            var info = result.Data.List.First();
            return (info.LotSizeFilter?.QuantityStep ?? 0.001m, info.LotSizeFilter?.MinOrderQuantity ?? 0m);
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
