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

    public async Task<List<SymbolDto>> GetSymbolsAsync()
    {
        var result = await _client.PerpetualFuturesApi.ExchangeData.GetContractsAsync();
        if (!result.Success || result.Data == null)
            return new List<SymbolDto>();

        return result.Data
            .Select(c => new SymbolDto { Symbol = c.Symbol.Replace("-", "") })
            .OrderBy(s => s.Symbol)
            .ToList();
    }

    public async Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit)
    {
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var interval = MapInterval(timeframe);
        var result = await _client.PerpetualFuturesApi.ExchangeData.GetKlinesAsync(
            bingxSymbol, interval, limit: limit);

        if (!result.Success)
            throw new Exception($"BingX GetKlines failed: {result.Error?.Message ?? "unknown error"}");
        if (result.Data == null)
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
            PositionSide.Both, quantity);

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
            PositionSide.Both, quantity);

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
        var (qtyStep, _) = await GetSymbolInfoAsync(bingxSymbol);
        var qty = FloorToStep(quantity, qtyStep);

        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Sell, FuturesOrderType.Market,
            PositionSide.Both, qty);

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
        var (qtyStep, _) = await GetSymbolInfoAsync(bingxSymbol);
        var qty = FloorToStep(quantity, qtyStep);

        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Buy, FuturesOrderType.Market,
            PositionSide.Both, qty);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId.ToString(),
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<FundingRateDto?> GetFundingRateAsync(string symbol)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.ExchangeData.GetFundingRateAsync(bingxSymbol);

            if (!result.Success || result.Data == null)
                return null;

            return new FundingRateDto
            {
                Rate = result.Data.LastFundingRate,
                NextFundingTime = result.Data.NextFundingTime
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<FundingRateDto>> GetAllFundingRatesAsync()
    {
        // BingX: GET /openApi/swap/v2/quote/premiumIndex — bulk endpoint, returns all perpetuals.
        var result = await _client.PerpetualFuturesApi.ExchangeData.GetFundingRatesAsync();
        if (!result.Success)
            throw new Exception($"BingX GetAllFundingRates failed: {result.Error?.Message}");
        if (result.Data == null)
            throw new Exception("BingX GetAllFundingRates returned null data");

        var list = new List<FundingRateDto>();
        foreach (var f in result.Data)
        {
            if (string.IsNullOrEmpty(f.Symbol))
                continue;
            // BingX symbols come as "BTC-USDT"; keep only USDT-quoted perps.
            if (!f.Symbol.EndsWith("USDT", StringComparison.Ordinal) && !f.Symbol.EndsWith("-USDT", StringComparison.Ordinal))
                continue;

            list.Add(new FundingRateDto
            {
                Symbol = f.Symbol.Replace("-", ""),
                Rate = f.LastFundingRate,
                NextFundingTime = f.NextFundingTime
            });
        }
        return list;
    }

    public async Task<OrderResultDto> PlaceLimitOrderAsync(string symbol, string side, decimal price, decimal quantity)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var orderSide = side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;

            var (qtyStep, minQty, priceStep) = await GetSymbolInfoWithPriceAsync(bingxSymbol);
            var roundedQty = FloorToStep(quantity, qtyStep);
            var roundedPrice = FloorToStep(price, priceStep);

            if (roundedQty < minQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
                bingxSymbol, orderSide, FuturesOrderType.Limit,
                PositionSide.Both, roundedQty, price: roundedPrice);

            return new OrderResultDto
            {
                Success = result.Success,
                OrderId = result.Data?.OrderId.ToString(),
                FilledPrice = roundedPrice,
                FilledQuantity = roundedQty,
                ErrorMessage = result.Error?.Message
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> CancelAllOrdersAsync(string symbol)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.Trading.CancelAllOrderAsync(bingxSymbol);
            return result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<List<LimitOrderDto>> GetOpenOrdersAsync(string symbol)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.Trading.GetOpenOrdersAsync(bingxSymbol);

            if (!result.Success || result.Data == null)
                return new List<LimitOrderDto>();

            return result.Data
                .Select(o => new LimitOrderDto
                {
                    OrderId = o.OrderId.ToString(),
                    Symbol = o.Symbol?.Replace("-", "") ?? symbol,
                    Side = o.Side.ToString(),
                    Price = o.Price ?? 0m,
                    Quantity = o.Quantity ?? 0m,
                    FilledQuantity = o.QuantityFilled ?? 0m,
                    Status = o.Status.ToString()
                })
                .ToList();
        }
        catch (Exception)
        {
            return new List<LimitOrderDto>();
        }
    }

    public async Task<PositionDto?> GetPositionAsync(string symbol, string side)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.Trading.GetPositionsAsync(bingxSymbol);

            if (!result.Success || result.Data == null)
                return null;

            var pos = result.Data.FirstOrDefault(p =>
                p.Symbol == bingxSymbol &&
                p.Size != 0);

            if (pos == null)
                return null;

            // In one-way mode Size sign indicates direction: positive = long, negative = short
            return new PositionDto
            {
                Symbol = symbol,
                Side = pos.Size >= 0 ? "Long" : "Short",
                Quantity = Math.Abs(pos.Size),
                EntryPrice = pos.AveragePrice,
                UnrealizedPnl = pos.UnrealizedProfit
            };
        }
        catch (Exception)
        {
            return null;
        }
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

    private async Task<(decimal qtyStep, decimal minQty, decimal priceStep)> GetSymbolInfoWithPriceAsync(string bingxSymbol)
    {
        var result = await _client.PerpetualFuturesApi.ExchangeData.GetContractsAsync();
        if (result.Success && result.Data != null)
        {
            var contract = result.Data.FirstOrDefault(c => c.Symbol == bingxSymbol);
            if (contract != null)
            {
                var qtyStep = (decimal)Math.Pow(10, -contract.QuantityPrecision);
                var priceStep = (decimal)Math.Pow(10, -contract.PricePrecision);
                return (qtyStep, contract.MinOrderQuantity, priceStep);
            }
        }
        return (0.001m, 0m, 0.01m);
    }

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    public void Dispose() => _client.Dispose();
}
