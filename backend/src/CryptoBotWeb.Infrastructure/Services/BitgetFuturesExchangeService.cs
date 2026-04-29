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
    // source: https://www.bitget.com/support/articles/12560603810662 — standard tier USDT-M perpetual
    public decimal TakerFeeRate => 0.0006m;
    public decimal MakerFeeRate => 0.0002m;

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
            OrderSide.Buy, OrderType.Market, MarginMode.CrossMargin, quantity);

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
            OrderSide.Sell, OrderType.Market, MarginMode.CrossMargin, quantity);

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
        var (qtyStep, _) = await GetSymbolInfoAsync(bitgetSymbol);
        var roundedQty = FloorToStep(quantity, qtyStep);

        var result = await _client.FuturesApiV2.Trading.PlaceOrderAsync(
            BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT",
            OrderSide.Sell, OrderType.Market, MarginMode.CrossMargin, roundedQty,
            reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            FilledQuantity = roundedQty,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        var (qtyStep, _) = await GetSymbolInfoAsync(bitgetSymbol);
        var roundedQty = FloorToStep(quantity, qtyStep);

        var result = await _client.FuturesApiV2.Trading.PlaceOrderAsync(
            BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT",
            OrderSide.Buy, OrderType.Market, MarginMode.CrossMargin, roundedQty,
            reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            FilledQuantity = roundedQty,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<FundingRateDto?> GetFundingRateAsync(string symbol)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);

            var rateResult = await _client.FuturesApiV2.ExchangeData.GetFundingRateAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol);

            if (!rateResult.Success || rateResult.Data == null)
                return null;

            // NextFundingTime is on a separate endpoint in Bitget
            var timeResult = await _client.FuturesApiV2.ExchangeData.GetNextFundingTimeAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol);

            return new FundingRateDto
            {
                Rate = rateResult.Data.FundingRate,
                NextFundingTime = timeResult.Success && timeResult.Data?.NextFundingTime != null
                    ? timeResult.Data.NextFundingTime.Value
                    : DateTime.UtcNow
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<FundingRateDto>> GetAllFundingRatesAsync()
    {
        var result = await _client.FuturesApiV2.ExchangeData.GetFundingRatesAsync(BitgetProductTypeV2.UsdtFutures);
        if (!result.Success || result.Data == null)
            throw new Exception($"Bitget GetAllFundingRates failed: {result.Error?.Message}");

        var list = new List<FundingRateDto>();
        foreach (var f in result.Data)
        {
            if (string.IsNullOrEmpty(f.Symbol))
                continue;

            list.Add(new FundingRateDto
            {
                Symbol = f.Symbol,
                Rate = f.FundingRate,
                NextFundingTime = DateTime.UtcNow // Bitget bulk endpoint doesn't return NextFundingTime
            });
        }
        return list;
    }

    public async Task<OrderResultDto> PlaceLimitOrderAsync(string symbol, string side, decimal price, decimal quantity, bool reduceOnly = false)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var isBuy = side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
            var orderSide = isBuy ? OrderSide.Buy : OrderSide.Sell;

            var (qtyStep, minQty, priceStep) = await GetSymbolInfoWithPriceAsync(bitgetSymbol);
            var roundedQty = FloorToStep(quantity, qtyStep);
            var roundedPrice = FloorToStep(price, priceStep);

            if (roundedQty < minQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            var result = await _client.FuturesApiV2.Trading.PlaceOrderAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT",
                orderSide, OrderType.Limit, MarginMode.CrossMargin, roundedQty,
                price: roundedPrice,
                reduceOnly: reduceOnly ? true : null);

            // Build a maximally informative error string. We've seen the SDK return Success=false
            // with Error=null (and even Success=true with Data=null) on Bitget rejections, which
            // surfaced as a blank "Не удалось выставить TP лимит:" log. Capture HTTP status and the
            // raw response body so the cause is always visible.
            var success = result.Success && !string.IsNullOrEmpty(result.Data?.OrderId);
            string? errorMessage = null;
            if (!success)
            {
                var parts = new List<string>();
                if (result.Error != null) parts.Add(result.Error.ToString());
                if (result.ResponseStatusCode != null) parts.Add($"HTTP {(int)result.ResponseStatusCode}");
                if (!string.IsNullOrEmpty(result.OriginalData)) parts.Add($"raw={result.OriginalData}");
                if (result.Success && string.IsNullOrEmpty(result.Data?.OrderId))
                    parts.Add("SDK reported Success=true but OrderId is empty/null");
                errorMessage = parts.Count > 0
                    ? string.Join(" | ", parts)
                    : "Bitget SDK returned no error and no order id (unknown failure)";
            }

            return new OrderResultDto
            {
                Success = success,
                OrderId = result.Data?.OrderId,
                FilledPrice = roundedPrice,
                FilledQuantity = roundedQty,
                ErrorMessage = errorMessage
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto
            {
                Success = false,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}" + (ex.InnerException != null ? $" → {ex.InnerException.Message}" : "")
            };
        }
    }

    public async Task<bool> CancelOrderAsync(string symbol, string orderId)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var result = await _client.FuturesApiV2.Trading.CancelOrderAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol,
                orderId, clientOrderId: null, marginAsset: "USDT");
            return result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var result = await _client.FuturesApiV2.Trading.GetOrderAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol,
                orderId, clientOrderId: null);

            if (!result.Success || result.Data == null)
                return null;

            var o = result.Data;
            return new OrderStatusDto
            {
                OrderId = o.OrderId ?? orderId,
                Status = MapOrderStatus(o.Status),
                FilledQuantity = o.QuantityFilled,
                AverageFilledPrice = o.AveragePrice ?? 0m
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OrderLifecycleStatus MapOrderStatus(Bitget.Net.Enums.V2.OrderStatus s) => s switch
    {
        Bitget.Net.Enums.V2.OrderStatus.Filled => OrderLifecycleStatus.Filled,
        Bitget.Net.Enums.V2.OrderStatus.PartiallyFilled => OrderLifecycleStatus.PartiallyFilled,
        Bitget.Net.Enums.V2.OrderStatus.Canceled => OrderLifecycleStatus.Cancelled,
        Bitget.Net.Enums.V2.OrderStatus.Rejected => OrderLifecycleStatus.Rejected,
        Bitget.Net.Enums.V2.OrderStatus.New => OrderLifecycleStatus.Open,
        Bitget.Net.Enums.V2.OrderStatus.Live => OrderLifecycleStatus.Open,
        Bitget.Net.Enums.V2.OrderStatus.Initial => OrderLifecycleStatus.Open,
        _ => OrderLifecycleStatus.Unknown
    };

    public async Task<bool> CancelAllOrdersAsync(string symbol)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var result = await _client.FuturesApiV2.Trading.CancelAllOrdersAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT");
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
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var result = await _client.FuturesApiV2.Trading.GetOpenOrdersAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol);

            if (!result.Success || result.Data == null)
                return new List<LimitOrderDto>();

            return (result.Data.Orders ?? Enumerable.Empty<Bitget.Net.Objects.Models.V2.BitgetFuturesOrder>())
                .Select(o => new LimitOrderDto
                {
                    OrderId = o.OrderId ?? string.Empty,
                    Symbol = o.Symbol ?? string.Empty,
                    Side = o.Side.ToString(),
                    Price = o.Price ?? 0m,
                    Quantity = o.Quantity,
                    FilledQuantity = o.QuantityFilled,
                    Status = o.Status.ToString()
                })
                .ToList();
        }
        catch (Exception)
        {
            return new List<LimitOrderDto>();
        }
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

    private async Task<(decimal qtyStep, decimal minQty, decimal priceStep)> GetSymbolInfoWithPriceAsync(string bitgetSymbol)
    {
        var result = await _client.FuturesApiV2.ExchangeData.GetContractsAsync(BitgetProductTypeV2.UsdtFutures);
        if (result.Success && result.Data != null)
        {
            var contract = result.Data.FirstOrDefault(c => c.Symbol == bitgetSymbol);
            if (contract != null)
            {
                // PriceStep is priceEndStep (last digit increment, e.g. 1 or 5),
                // PriceDecimals is pricePlace (number of decimal places).
                // Actual step = PriceStep * 10^(-PriceDecimals)
                var actualPriceStep = contract.PriceStep * (decimal)Math.Pow(10, -contract.PriceDecimals);
                return (contract.QuantityStep, contract.MinOrderQuantity, actualPriceStep);
            }
        }
        return (0.001m, 0m, 0.00001m);
    }

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    public async Task<PositionDto?> GetPositionAsync(string symbol, string side)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var result = await _client.FuturesApiV2.Trading.GetPositionAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT");

            if (!result.Success || result.Data == null)
                return null;

            var pos = result.Data.FirstOrDefault(p =>
                p.PositionSide.ToString().Equals(side, StringComparison.OrdinalIgnoreCase) &&
                p.Total != 0);

            if (pos == null)
                return null;

            return new PositionDto
            {
                Symbol = symbol,
                Side = side,
                Quantity = Math.Abs(pos.Total),
                EntryPrice = pos.AverageOpenPrice,
                UnrealizedPnl = pos.UnrealizedProfitAndLoss
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<PositionDto>> GetOpenPositionsAsync()
    {
        try
        {
            // Bitget V2: GetPositionsAsync(productType, marginAsset) — returns all open positions for asset.
            var result = await _client.FuturesApiV2.Trading.GetPositionsAsync(
                BitgetProductTypeV2.UsdtFutures, "USDT");

            if (!result.Success || result.Data == null)
                return new List<PositionDto>();

            var list = new List<PositionDto>();
            foreach (var p in result.Data)
            {
                if (p.Total == 0) continue;
                if (string.IsNullOrEmpty(p.Symbol)) continue;

                // Bitget PositionSide enum is "Long" / "Short" — already canonical.
                var side = p.PositionSide.ToString();
                var normalized = side.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "Long"
                               : side.Equals("Short", StringComparison.OrdinalIgnoreCase) ? "Short"
                               : side;

                list.Add(new PositionDto
                {
                    Symbol = p.Symbol,
                    Side = normalized,
                    Quantity = Math.Abs(p.Total),
                    EntryPrice = p.AverageOpenPrice,
                    UnrealizedPnl = p.UnrealizedProfitAndLoss
                });
            }
            return list;
        }
        catch (Exception)
        {
            return new List<PositionDto>();
        }
    }

    /// <summary>
    /// Fetches funding fee payment history from Bitget ledger (businessType = "funding_fee").
    /// The ledger entry contains the funding amount but not the funding rate or position size directly,
    /// so those fields are set to 0.
    /// </summary>
    public async Task<List<FundingPaymentDto>> GetFundingPaymentsAsync(string symbol, DateTime? startTime = null)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var payments = new List<FundingPaymentDto>();
            long? lastId = null;

            do
            {
                var result = await _client.FuturesApiV2.Account.GetLedgerAsync(
                    BitgetProductTypeV2.UsdtFutures,
                    businessType: "funding_fee",
                    startTime: startTime,
                    idLessThan: lastId,
                    limit: 100);

                if (!result.Success || result.Data?.Entries == null || result.Data.Entries.Length == 0)
                    break;

                foreach (var entry in result.Data.Entries)
                {
                    // Filter by exact symbol
                    if (!entry.Symbol.Equals(bitgetSymbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    payments.Add(new FundingPaymentDto
                    {
                        Symbol = symbol,
                        Amount = entry.Quantity,
                        FundingRate = 0m, // Bitget ledger does not include funding rate
                        PositionSize = 0m, // Bitget ledger does not include position size
                        Timestamp = entry.Timestamp
                    });
                }

                lastId = result.Data.EndId;
            }
            while (lastId.HasValue);

            return payments.OrderByDescending(p => p.Timestamp).ToList();
        }
        catch (Exception)
        {
            return new List<FundingPaymentDto>();
        }
    }

    public async Task<bool> SetLeverageAsync(string symbol, int leverage)
    {
        try
        {
            var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
            var result = await _client.FuturesApiV2.Account.SetLeverageAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol, "USDT", leverage);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
