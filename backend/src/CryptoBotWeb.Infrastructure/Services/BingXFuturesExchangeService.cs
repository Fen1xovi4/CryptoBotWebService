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
    // source: https://bingx.com/en/support/articles/360029987112 — standard tier USDT perpetual
    public decimal TakerFeeRate => 0.0005m;
    public decimal MakerFeeRate => 0.0002m;

    private readonly BingXRestClient _client;

    public BingXFuturesExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null)
    {
        _client = new BingXRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;

            // Server-time auto-sync — see BybitFuturesExchangeService ctor for the rationale.
            // BingX rejects signed requests where the host clock skews beyond ReceiveWindow
            // (default 5s); AutoTimestamp removes the dependency on host clock accuracy.
            options.FuturesOptions.AutoTimestamp = true;
            options.FuturesOptions.TimestampRecalculationInterval = TimeSpan.FromMinutes(10);
            options.ReceiveWindow = TimeSpan.FromSeconds(15);
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

    // ─────────────────── Historical range fetch (backtesting simulator) ───────────────────
    // BingX perpetual klines/funding-history accept startTime/endTime + limit and return the most
    // recent bars within the window when it holds more than `limit`. Same backward-paging shape as
    // Bybit: fix startTime=fromUtc, walk endTime down from toUtc to each page's oldest bar (minus a
    // tick) until covered / empty / no progress. Deduped by OpenTime/Timestamp, returned ascending.
    private const int _rangeHardCap = 200_000;
    private const int _rangePageDelayMs = 120;

    public async Task<List<CandleDto>> GetKlinesRangeAsync(
        string symbol, string timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        const int pageSize = 1000; // BingX perpetual klines (max 1440/request; 1000 is a safe page)

        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var interval = MapInterval(timeframe);
        var span = GetTimeframeSpan(timeframe);

        var byOpen = new Dictionary<DateTime, CandleDto>();
        var cursorEnd = toUtc;

        while (cursorEnd > fromUtc)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _client.PerpetualFuturesApi.ExchangeData.GetKlinesAsync(
                bingxSymbol, interval, startTime: fromUtc, endTime: cursorEnd, limit: pageSize, ct: ct);

            if (!result.Success)
                throw new Exception($"BingX GetKlinesRange failed for {symbol}: {result.Error?.Message ?? "unknown error"}");

            var rows = result.Data?.ToList();
            if (rows == null || rows.Count == 0) break;

            var oldest = DateTime.MaxValue;
            foreach (var k in rows)
            {
                if (k.Timestamp < oldest) oldest = k.Timestamp;
                if (k.Timestamp < fromUtc || k.Timestamp >= toUtc) continue;
                if (byOpen.ContainsKey(k.Timestamp)) continue;

                byOpen[k.Timestamp] = new CandleDto
                {
                    OpenTime = k.Timestamp,
                    CloseTime = k.Timestamp + span,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = k.Volume
                };
            }

            if (byOpen.Count > _rangeHardCap)
                throw new InvalidOperationException(
                    $"BingX GetKlinesRange exceeded {_rangeHardCap} candles for {symbol} {timeframe} [{fromUtc:o}..{toUtc:o}] — narrow the window.");

            if (oldest <= fromUtc) break;
            var nextEnd = oldest.AddTicks(-1);
            if (nextEnd >= cursorEnd) break;
            cursorEnd = nextEnd;

            await Task.Delay(_rangePageDelayMs, ct);
        }

        return byOpen.Values.OrderBy(c => c.OpenTime).ToList();
    }

    public async Task<List<FundingEventDto>> GetFundingHistoryAsync(
        string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        const int pageSize = 1000; // BingX funding-rate history max per request

        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var byTime = new Dictionary<DateTime, FundingEventDto>();
        var cursorEnd = toUtc;

        while (cursorEnd > fromUtc)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _client.PerpetualFuturesApi.ExchangeData.GetFundingRateHistoryAsync(
                bingxSymbol, startTime: fromUtc, endTime: cursorEnd, limit: pageSize, ct: ct);

            if (!result.Success)
                throw new Exception($"BingX GetFundingHistory failed for {symbol}: {result.Error?.Message ?? "unknown error"}");

            var rows = result.Data?.ToList();
            if (rows == null || rows.Count == 0) break;

            var oldest = DateTime.MaxValue;
            foreach (var f in rows)
            {
                if (f.FundingTime < oldest) oldest = f.FundingTime;
                if (f.FundingTime < fromUtc || f.FundingTime >= toUtc) continue;
                if (byTime.ContainsKey(f.FundingTime)) continue;

                byTime[f.FundingTime] = new FundingEventDto
                {
                    Timestamp = f.FundingTime,
                    Rate = f.FundingRate
                };
            }

            if (byTime.Count > _rangeHardCap)
                throw new InvalidOperationException(
                    $"BingX GetFundingHistory exceeded {_rangeHardCap} events for {symbol} [{fromUtc:o}..{toUtc:o}] — narrow the window.");

            if (oldest <= fromUtc) break;
            var nextEnd = oldest.AddTicks(-1);
            if (nextEnd >= cursorEnd) break;
            cursorEnd = nextEnd;

            await Task.Delay(_rangePageDelayMs, ct);
        }

        return byTime.Values.OrderBy(e => e.Timestamp).ToList();
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

    public async Task<BookTickerDto?> GetBookTickerAsync(string symbol)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            // BingX swap/v2 has a dedicated symbol-order-book-ticker endpoint (bestBidPrice/
            // bestBidQty/bestAskPrice/bestAskQty) — single symbol, unlike GetTickersAsync which
            // returns every contract. Response carries no server timestamp.
            var result = await _client.PerpetualFuturesApi.ExchangeData.GetBookTickerAsync(bingxSymbol);

            if (!result.Success || result.Data == null)
                return null;

            // Prices are non-nullable on the SDK model, so a missing side deserializes as 0 —
            // that's not an executable book, treat it as a failed fetch.
            if (result.Data.BestBidPrice <= 0 || result.Data.BestAskPrice <= 0)
                return null;

            return new BookTickerDto
            {
                Symbol = symbol,
                BidPrice = result.Data.BestBidPrice,
                BidQuantity = result.Data.BestBidQuantity,
                AskPrice = result.Data.BestAskPrice,
                AskQuantity = result.Data.BestAskQuantity,
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception)
        {
            return null;
        }
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

        // One-way mode: PositionSide.Both + reduceOnly:true so BingX only reduces the existing long
        // and never opens a counter-position if the qty exceeds the remaining size.
        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Sell, FuturesOrderType.Market,
            PositionSide.Both, qty, price: null, reduceOnly: true);

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

        // One-way mode: PositionSide.Both + reduceOnly:true so BingX only reduces the existing short
        // and never opens a counter-position if the qty exceeds the remaining size.
        var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
            bingxSymbol, OrderSide.Buy, FuturesOrderType.Market,
            PositionSide.Both, qty, price: null, reduceOnly: true);

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

    public async Task<OrderResultDto> PlaceLimitOrderAsync(string symbol, string side, decimal price, decimal quantity, bool reduceOnly = false)
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

            // reduceOnly must be propagated — TP/SL limits set by SmaDca rely on it to safely close
            // the existing position without flipping into the opposite side.
            var result = await _client.PerpetualFuturesApi.Trading.PlaceOrderAsync(
                bingxSymbol, orderSide, FuturesOrderType.Limit,
                PositionSide.Both, roundedQty, price: roundedPrice, reduceOnly: reduceOnly);

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

    public async Task<bool> CancelOrderAsync(string symbol, string orderId)
    {
        try
        {
            // BingX uses long order IDs — refuse non-numeric inputs early instead of letting the SDK 400.
            if (!long.TryParse(orderId, out var parsedId))
                return false;

            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.Trading.CancelOrderAsync(
                bingxSymbol, parsedId, null, default);
            return result.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId)
    {
        // BingX uses long order IDs — refuse non-numeric inputs early instead of letting the SDK 400.
        if (!long.TryParse(orderId, out var parsedId))
            return null;

        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var result = await _client.PerpetualFuturesApi.Trading.GetOrderAsync(
            bingxSymbol, parsedId, null, default);

        if (!result.Success)
        {
            // BingX reports an unknown/purged order as an API error (80016 "order not exist"),
            // which maps to the null contract. Anything else (proxy, timeout, auth, rate limit)
            // must throw so callers don't mistake a transport failure for a vanished order and
            // orphan a live TP.
            var msg = result.Error?.ToString() ?? "unknown error";
            if (msg.Contains("not exist", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new InvalidOperationException($"BingX GetOrder query failed for {orderId}: {msg}");
        }

        if (result.Data == null)
            return null;

        var order = result.Data;
        var lifecycleStatus = order.Status switch
        {
            OrderStatus.New or OrderStatus.Pending => OrderLifecycleStatus.Open,
            OrderStatus.PartiallyFilled => OrderLifecycleStatus.PartiallyFilled,
            OrderStatus.Filled => OrderLifecycleStatus.Filled,
            OrderStatus.Canceled => OrderLifecycleStatus.Cancelled,
            OrderStatus.Failed => OrderLifecycleStatus.Rejected,
            _ => OrderLifecycleStatus.Unknown
        };

        return new OrderStatusDto
        {
            OrderId = order.OrderId.ToString(),
            Status = lifecycleStatus,
            FilledQuantity = order.QuantityFilled ?? 0m,
            AverageFilledPrice = order.AveragePrice ?? 0m
        };
    }

    public async Task<List<LimitOrderDto>> GetOpenOrdersAsync(string symbol)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.Trading.GetOpenOrdersAsync(bingxSymbol);

            if (!result.Success || result.Data == null)
                return new List<LimitOrderDto>();

            // BingX has been observed returning orders for all symbols on the account regardless of
            // the symbol filter. Enforce the filter client-side so callers can rely on the symbol
            // contract (otherwise SmaDca's orphan-order reconcile would cancel sibling bots' orders).
            return result.Data
                .Where(o => string.Equals(o.Symbol, bingxSymbol, StringComparison.OrdinalIgnoreCase))
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
        // No top-level try/catch: API errors MUST surface as exceptions so callers can skip the
        // tick instead of phantom-closing on a transient API failure. See parallel comment in
        // BybitFuturesExchangeService.GetPositionAsync.
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        var result = await _client.PerpetualFuturesApi.Trading.GetPositionsAsync(bingxSymbol);

        if (!result.Success)
            throw new Exception($"BingX GetPositions failed for {symbol}: {result.Error?.Message ?? "unknown error"} (code={result.Error?.Code})");

        if (result.Data == null)
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

    public async Task<List<PositionDto>> GetOpenPositionsAsync()
    {
        try
        {
            // BingX: GetPositionsAsync with null/empty symbol returns all open positions.
            var result = await _client.PerpetualFuturesApi.Trading.GetPositionsAsync(null!);

            if (!result.Success || result.Data == null)
                return new List<PositionDto>();

            var list = new List<PositionDto>();
            foreach (var p in result.Data)
            {
                if (p.Size == 0) continue;
                if (string.IsNullOrEmpty(p.Symbol)) continue;

                // BingX symbols come as "BTC-USDT"; strip dash to canonical form.
                var canonical = p.Symbol.Replace("-", "");

                // In one-way mode Size sign indicates direction: positive = long, negative = short.
                var side = p.Size >= 0 ? "Long" : "Short";

                list.Add(new PositionDto
                {
                    Symbol = canonical,
                    Side = side,
                    Quantity = Math.Abs(p.Size),
                    EntryPrice = p.AveragePrice,
                    UnrealizedPnl = p.UnrealizedProfit
                });
            }
            return list;
        }
        catch (Exception)
        {
            return new List<PositionDto>();
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

    async Task<(decimal qtyStep, decimal minQty)> IFuturesExchangeService.GetSymbolInfoAsync(string symbol)
    {
        var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
        return await GetSymbolInfoAsync(bingxSymbol);
    }

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

    public async Task<List<FundingPaymentDto>> GetFundingPaymentsAsync(string symbol, DateTime? startTime = null)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.Account.GetIncomesAsync(
                symbol: bingxSymbol,
                incomeType: IncomeType.FundingFee,
                startTime: startTime,
                limit: 100);

            if (!result.Success || result.Data == null)
                return new List<FundingPaymentDto>();

            return result.Data
                .Select(i => new FundingPaymentDto
                {
                    Symbol = (i.Symbol ?? bingxSymbol).Replace("-", ""),
                    Amount = i.Income,
                    FundingRate = 0m, // BingX income endpoint does not return the funding rate
                    PositionSize = 0m, // BingX income endpoint does not return position size
                    Timestamp = i.Timestamp
                })
                .OrderByDescending(p => p.Timestamp)
                .ToList();
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
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.Account.SetLeverageAsync(
                bingxSymbol, BingX.Net.Enums.PositionSide.Both, leverage);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int?> GetMaxLeverageAsync(string symbol)
    {
        try
        {
            var bingxSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);
            var result = await _client.PerpetualFuturesApi.ExchangeData.GetContractsAsync();
            var contract = result.Success
                ? result.Data?.FirstOrDefault(c => c.Symbol == bingxSymbol)
                : null;
            if (contract == null) return null;

            // One-way (PositionSide.Both) trades either direction across cycles — clamp to the
            // lower of the two side caps so the chosen leverage is valid for Long and Short alike.
            var max = Math.Min(Convert.ToInt32(contract.MaxLongLeverage), Convert.ToInt32(contract.MaxShortLeverage));
            return max > 0 ? max : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
