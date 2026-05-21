using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

public class BybitFuturesExchangeService : IFuturesExchangeService
{
    // source: https://www.bybit.com/en/help-center/article/Trading-fee-rate — non-VIP USDT-perpetual
    public decimal TakerFeeRate => 0.00055m;
    public decimal MakerFeeRate => 0.0002m;

    // Public klines are per-IP rate-limited on Bybit and identical regardless of which user
    // asked. A process-wide cache keyed by (symbol, interval, limit) lets concurrent strategies
    // share one HTTP fetch — both reduces requests and coalesces parallel calls. TTL=4s is
    // below the 5s strategy tick and well below 1m candle granularity, so staleness is invisible.
    // Failed fetches are cached too (for TTL), giving an organic short circuit-breaker on outage.
    private static readonly TimeSpan _klinesCacheTtl = TimeSpan.FromSeconds(4);
    private static readonly object _klinesCacheLock = new();
    private static readonly Dictionary<string, (DateTime fetchedAt, Task<List<CandleDto>> task)> _klinesCache = new();

    private readonly BybitRestClient _client;

    // Bybit Broker Program: our broker code "Ty001081" must be sent as the Referer header
    // on every REST call. Bybit.Net forwards options.Referer to all requests automatically.
    public BybitFuturesExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null, string? brokerId = null)
    {
        _client = new BybitRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;
            if (!string.IsNullOrWhiteSpace(brokerId)) options.Referer = brokerId;

            // Server-time auto-sync. Bybit V5 rejects signed requests where the host clock is
            // outside [server-recv_window, server+1000ms]; the +1000ms upper bound is hard
            // regardless of recv_window, so a host clock drifting forward by even a few seconds
            // produces a flood of retCode=10002 errors. Without AutoTimestamp those errors
            // collapse into result.Success=false → GetPositionAsync returns null → GridFloat
            // reconcile interprets that as "no position" and phantom-closes the entire grid.
            // AutoTimestamp makes the SDK probe GetServerTime at startup and every
            // TimestampRecalculationInterval, stamp each signed request with the synced offset,
            // and ignore host time drift entirely.
            options.V5Options.AutoTimestamp = true;
            options.V5Options.TimestampRecalculationInterval = TimeSpan.FromMinutes(10);
            // Wider receive-window cushion for the rare case the host clock spikes between
            // recalculation intervals (default 5s is tight on a busy host).
            options.ReceiveWindow = TimeSpan.FromSeconds(15);
        });
    }

    public async Task<List<SymbolDto>> GetSymbolsAsync()
    {
        var all = new List<SymbolDto>();
        string? cursor = null;

        do
        {
            var result = await _client.V5Api.ExchangeData.GetLinearInverseSymbolsAsync(
                Category.Linear, limit: 1000, cursor: cursor);

            if (!result.Success || result.Data?.List == null)
                break;

            all.AddRange(result.Data.List.Select(s => new SymbolDto { Symbol = s.Name }));
            cursor = result.Data.NextPageCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return all.OrderBy(s => s.Symbol).ToList();
    }

    public Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit)
    {
        var key = $"{symbol}|{timeframe}|{limit}";
        lock (_klinesCacheLock)
        {
            var now = DateTime.UtcNow;
            if (_klinesCache.TryGetValue(key, out var entry) && (now - entry.fetchedAt) < _klinesCacheTtl)
                return entry.task;

            var fetchTask = FetchKlinesAsync(symbol, timeframe, limit);
            _klinesCache[key] = (now, fetchTask);
            return fetchTask;
        }
    }

    private async Task<List<CandleDto>> FetchKlinesAsync(string symbol, string timeframe, int limit)
    {
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var interval = MapInterval(timeframe);
        var result = await _client.V5Api.ExchangeData.GetKlinesAsync(
            Category.Linear, bybitSymbol, interval, limit: limit);

        if (!result.Success)
            throw new Exception($"Bybit GetKlines failed: {result.Error?.Message ?? "unknown error"}");
        if (result.Data?.List == null)
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
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var result = await _client.V5Api.ExchangeData.GetLinearInverseTickersAsync(Category.Linear, bybitSymbol);
        if (!result.Success || result.Data?.List == null)
            return null;

        var ticker = result.Data.List.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public async Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount)
    {
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(bybitSymbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, bybitSymbol, OrderSide.Buy, NewOrderType.Market, quantity);

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
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(bybitSymbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, bybitSymbol, OrderSide.Sell, NewOrderType.Market, quantity);

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
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var (qtyStep, _) = await GetSymbolInfoAsync(bybitSymbol);
        var qty = FloorToStep(quantity, qtyStep);

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, bybitSymbol, OrderSide.Sell, NewOrderType.Market, qty,
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
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var (qtyStep, _) = await GetSymbolInfoAsync(bybitSymbol);
        var qty = FloorToStep(quantity, qtyStep);

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, bybitSymbol, OrderSide.Buy, NewOrderType.Market, qty,
            reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<FundingRateDto?> GetFundingRateAsync(string symbol)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.ExchangeData.GetLinearInverseTickersAsync(Category.Linear, bybitSymbol);

            if (!result.Success || result.Data?.List == null)
                return null;

            var ticker = result.Data.List.FirstOrDefault();
            if (ticker == null)
                return null;

            return new FundingRateDto
            {
                Rate = ticker.FundingRate ?? 0m,
                NextFundingTime = ticker.NextFundingTime ?? DateTime.UtcNow
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<FundingRateDto>> GetAllFundingRatesAsync()
    {
        var result = await _client.V5Api.ExchangeData.GetLinearInverseTickersAsync(Category.Linear);
        if (!result.Success || result.Data?.List == null)
            throw new Exception($"Bybit GetAllFundingRates failed: {result.Error?.Message}");

        var list = new List<FundingRateDto>();
        foreach (var ticker in result.Data.List)
        {
            if (string.IsNullOrEmpty(ticker.Symbol))
                continue;
            if (!ticker.Symbol.EndsWith("USDT", StringComparison.Ordinal))
                continue;
            if (ticker.FundingRate == null)
                continue;

            list.Add(new FundingRateDto
            {
                Symbol = ticker.Symbol,
                Rate = ticker.FundingRate.Value,
                NextFundingTime = ticker.NextFundingTime ?? DateTime.UtcNow
            });
        }
        return list;
    }

    public async Task<OrderResultDto> PlaceLimitOrderAsync(string symbol, string side, decimal price, decimal quantity, bool reduceOnly = false)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var orderSide = side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;

            var (qtyStep, minQty, priceStep) = await GetSymbolInfoWithPriceAsync(bybitSymbol);
            var roundedQty = FloorToStep(quantity, qtyStep);
            var roundedPrice = FloorToStep(price, priceStep);

            if (roundedQty < minQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            // reduceOnly must be propagated — TP/SL limits rely on it to safely close the existing
            // position without flipping into the opposite side if the order outlives the position.
            var result = await _client.V5Api.Trading.PlaceOrderAsync(
                Category.Linear, bybitSymbol, orderSide, NewOrderType.Limit, roundedQty,
                price: roundedPrice, timeInForce: TimeInForce.GoodTillCanceled,
                reduceOnly: reduceOnly);

            return new OrderResultDto
            {
                Success = result.Success,
                OrderId = result.Data?.OrderId,
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
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.Trading.CancelAllOrderAsync(Category.Linear, symbol: bybitSymbol);
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
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.Trading.CancelOrderAsync(
                Category.Linear, bybitSymbol, orderId: orderId);
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
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);

            // Real-time endpoint covers open / recently-finalized orders.
            var open = await _client.V5Api.Trading.GetOrdersAsync(
                Category.Linear, bybitSymbol, orderId: orderId);
            var order = open.Success ? open.Data?.List?.FirstOrDefault() : null;

            // Fall back to history for fully-finalized orders past the real-time window.
            if (order == null)
            {
                var hist = await _client.V5Api.Trading.GetOrderHistoryAsync(
                    Category.Linear, bybitSymbol, orderId: orderId);
                order = hist.Success ? hist.Data?.List?.FirstOrDefault() : null;
            }

            if (order == null) return null;

            return new OrderStatusDto
            {
                OrderId = order.OrderId ?? orderId,
                Status = MapOrderStatus(order.Status),
                FilledQuantity = order.QuantityFilled ?? 0m,
                AverageFilledPrice = order.AveragePrice ?? 0m
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OrderLifecycleStatus MapOrderStatus(OrderStatus s) => s switch
    {
        OrderStatus.New or OrderStatus.Created or OrderStatus.Active or OrderStatus.Untriggered => OrderLifecycleStatus.Open,
        OrderStatus.PartiallyFilled => OrderLifecycleStatus.PartiallyFilled,
        OrderStatus.Filled => OrderLifecycleStatus.Filled,
        OrderStatus.Cancelled or OrderStatus.PartiallyFilledCanceled or OrderStatus.Deactivated => OrderLifecycleStatus.Cancelled,
        OrderStatus.Rejected => OrderLifecycleStatus.Rejected,
        _ => OrderLifecycleStatus.Unknown
    };

    public async Task<List<LimitOrderDto>> GetOpenOrdersAsync(string symbol)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.Trading.GetOrdersAsync(Category.Linear, symbol: bybitSymbol);

            if (!result.Success || result.Data?.List == null)
                return new List<LimitOrderDto>();

            return result.Data.List
                .Where(o => o.Status == OrderStatus.New
                         || o.Status == OrderStatus.PartiallyFilled)
                .Select(o => new LimitOrderDto
                {
                    OrderId = o.OrderId,
                    Symbol = o.Symbol,
                    Side = o.Side.ToString(),
                    Price = o.Price ?? 0m,
                    Quantity = o.Quantity,
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

    private async Task<(decimal qtyStep, decimal minQty)> GetSymbolInfoAsync(string bybitSymbol)
    {
        var result = await _client.V5Api.ExchangeData.GetLinearInverseSymbolsAsync(Category.Linear, bybitSymbol);
        if (result.Success && result.Data?.List?.Any() == true)
        {
            var info = result.Data.List.First();
            return (info.LotSizeFilter?.QuantityStep ?? 0.001m, info.LotSizeFilter?.MinOrderQuantity ?? 0m);
        }
        return (0.001m, 0m);
    }

    // Public interface implementation: takes canonical symbol, applies exchange-specific
    // mapping, defers to the internal helper. Used by GridFloat for the pre-placement
    // minimum-notional guard.
    async Task<(decimal qtyStep, decimal minQty)> IFuturesExchangeService.GetSymbolInfoAsync(string symbol)
    {
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        return await GetSymbolInfoAsync(bybitSymbol);
    }

    private async Task<(decimal qtyStep, decimal minQty, decimal priceStep)> GetSymbolInfoWithPriceAsync(string bybitSymbol)
    {
        var result = await _client.V5Api.ExchangeData.GetLinearInverseSymbolsAsync(Category.Linear, bybitSymbol);
        if (result.Success && result.Data?.List?.Any() == true)
        {
            var info = result.Data.List.First();
            return (
                info.LotSizeFilter?.QuantityStep ?? 0.001m,
                info.LotSizeFilter?.MinOrderQuantity ?? 0m,
                info.PriceFilter?.TickSize ?? 0.01m);
        }
        return (0.001m, 0m, 0.01m);
    }

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    public async Task<PositionDto?> GetPositionAsync(string symbol, string side)
    {
        // No top-level try/catch: API errors MUST surface as exceptions so callers (GridFloat
        // reconcile, SmaDca safety net, etc.) can skip the tick instead of phantom-closing on
        // a transient API failure. Returning null for both "no position" and "API failed" was
        // the bug that wiped GridFloat batches when the host clock drifted past Bybit's
        // recv_window — see AutoTimestamp note in the ctor.
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var result = await _client.V5Api.Trading.GetPositionsAsync(Category.Linear, bybitSymbol);

        if (!result.Success)
            throw new Exception($"Bybit GetPositions failed for {symbol}: {result.Error?.Message ?? "unknown error"} (code={result.Error?.Code})");

        if (result.Data?.List == null)
            return null;

        // In one-way mode, Bybit returns Side as Buy/Sell, but callers pass Long/Short
        var mappedSide = side.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "Buy"
                       : side.Equals("Short", StringComparison.OrdinalIgnoreCase) ? "Sell"
                       : side;

        var pos = result.Data.List.FirstOrDefault(p =>
            p.Symbol == bybitSymbol &&
            (p.Side.ToString().Equals(side, StringComparison.OrdinalIgnoreCase) ||
             p.Side.ToString().Equals(mappedSide, StringComparison.OrdinalIgnoreCase)) &&
            p.Quantity != 0);

        if (pos == null)
            return null;

        return new PositionDto
        {
            Symbol = symbol,
            Side = side,
            Quantity = Math.Abs(pos.Quantity),
            EntryPrice = pos.AveragePrice ?? 0m,
            UnrealizedPnl = pos.UnrealizedPnl ?? 0m
        };
    }

    public async Task<List<PositionDto>> GetOpenPositionsAsync()
    {
        try
        {
            // Bybit V5: GetPositionsAsync requires either symbol or settleAsset for linear category.
            // settleAsset="USDT" returns all USDT-linear positions in one call (max 200).
            var result = await _client.V5Api.Trading.GetPositionsAsync(Category.Linear, settleAsset: "USDT");

            if (!result.Success || result.Data?.List == null)
                return new List<PositionDto>();

            var list = new List<PositionDto>();
            foreach (var p in result.Data.List)
            {
                if (p.Quantity == 0) continue;
                if (string.IsNullOrEmpty(p.Symbol)) continue;

                // Bybit returns Side as Buy/Sell in one-way mode — map to Long/Short.
                var sideStr = p.Side.ToString();
                var mappedSide = sideStr.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? "Long"
                               : sideStr.Equals("Sell", StringComparison.OrdinalIgnoreCase) ? "Short"
                               : sideStr;

                list.Add(new PositionDto
                {
                    Symbol = p.Symbol,
                    Side = mappedSide,
                    Quantity = Math.Abs(p.Quantity),
                    EntryPrice = p.AveragePrice ?? 0m,
                    UnrealizedPnl = p.UnrealizedPnl ?? 0m
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
    /// Fetches funding fee payment history from Bybit transaction logs (type = Settlement).
    /// Bybit V5 transaction log API filters by baseAsset (e.g. "BTC"), not by full symbol.
    /// We post-filter results by symbol to ensure accuracy.
    /// </summary>
    public async Task<List<FundingPaymentDto>> GetFundingPaymentsAsync(string symbol, DateTime? startTime = null)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            // Extract base asset from symbol (e.g. "BTCUSDT" -> "BTC")
            var baseAsset = bybitSymbol.Replace("USDT", "", StringComparison.OrdinalIgnoreCase);
            var payments = new List<FundingPaymentDto>();
            string? cursor = null;

            do
            {
                var result = await _client.V5Api.Account.GetTransactionHistoryAsync(
                    category: Category.Linear,
                    baseAsset: baseAsset,
                    type: TransactionLogType.Settlement,
                    startTime: startTime,
                    limit: 50,
                    cursor: cursor);

                if (!result.Success || result.Data?.List == null)
                    break;

                foreach (var log in result.Data.List)
                {
                    // Post-filter by exact symbol in case baseAsset matches multiple pairs
                    if (!string.IsNullOrEmpty(log.Symbol) &&
                        !log.Symbol.Equals(bybitSymbol, StringComparison.OrdinalIgnoreCase))
                        continue;

                    payments.Add(new FundingPaymentDto
                    {
                        Symbol = symbol,
                        Amount = log.Funding ?? 0m,
                        FundingRate = log.FeeRate ?? 0m,
                        PositionSize = Math.Abs(log.Size ?? 0m),
                        Timestamp = log.TransactionTime
                    });
                }

                cursor = result.Data.NextPageCursor;
            }
            while (!string.IsNullOrEmpty(cursor));

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
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.Account.SetLeverageAsync(
                Category.Linear, bybitSymbol, leverage, leverage);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    // ────────────────────────── Hedge mode (V1 — Bybit-only) ──────────────────────────
    // Bybit V5 supports per-symbol hedge mode on linear USDT-perpetuals: a single account
    // can hold a long (positionIdx=1) AND short (positionIdx=2) on the same symbol
    // simultaneously. The account must be switched to Hedge Mode in the Bybit UI first —
    // we do not flip the mode from code, only consume it.

    public bool IsHedgeModeSupported => true;

    /// <summary>
    /// Probes the account's position-mode for the given symbol. Bybit V5 returns one row per
    /// position slot — in one-way mode the result has positionIdx=0; in hedge mode it has
    /// positionIdx=1 AND positionIdx=2 entries (even if the actual quantities are zero).
    /// Returns null if the probe failed (network/auth) or returned no slots at all.
    /// </summary>
    public async Task<bool?> IsHedgeModeEnabledAsync(string symbol)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.Trading.GetPositionsAsync(Category.Linear, bybitSymbol);
            if (!result.Success || result.Data?.List == null) return null;
            var slots = result.Data.List.ToList();
            if (slots.Count == 0) return null;
            return slots.Any(p => p.PositionIdx == PositionIdx.BuyHedgeMode
                               || p.PositionIdx == PositionIdx.SellHedgeMode);
        }
        catch
        {
            return null;
        }
    }

    public async Task<OrderResultDto> OpenHedgeLongAsync(string symbol, decimal quoteAmount)
        => await PlaceHedgeMarketAsync(symbol, OrderSide.Buy, quoteAmount, PositionIdx.BuyHedgeMode, reduceOnly: false);

    public async Task<OrderResultDto> OpenHedgeShortAsync(string symbol, decimal quoteAmount)
        => await PlaceHedgeMarketAsync(symbol, OrderSide.Sell, quoteAmount, PositionIdx.SellHedgeMode, reduceOnly: false);

    public async Task<OrderResultDto> CloseHedgeLongAsync(string symbol, decimal quantity)
        => await CloseHedgeMarketAsync(symbol, OrderSide.Sell, quantity, PositionIdx.BuyHedgeMode);

    public async Task<OrderResultDto> CloseHedgeShortAsync(string symbol, decimal quantity)
        => await CloseHedgeMarketAsync(symbol, OrderSide.Buy, quantity, PositionIdx.SellHedgeMode);

    private async Task<OrderResultDto> PlaceHedgeMarketAsync(
        string symbol, OrderSide side, decimal quoteAmount, PositionIdx posIdx, bool reduceOnly)
    {
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var price = await GetTickerPriceAsync(symbol);
        if (price == null || price == 0)
            return new OrderResultDto { Success = false, ErrorMessage = "Failed to get ticker price" };

        var (qtyStep, minQty) = await GetSymbolInfoAsync(bybitSymbol);
        var quantity = FloorToStep(quoteAmount / price.Value, qtyStep);

        if (quantity < minQty)
            return new OrderResultDto { Success = false, ErrorMessage = $"Qty {quantity} < min {minQty} for {symbol}" };

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, bybitSymbol, side, NewOrderType.Market, quantity,
            positionIdx: posIdx, reduceOnly: reduceOnly);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            FilledPrice = price,
            FilledQuantity = quantity,
            ErrorMessage = result.Error?.Message
        };
    }

    private async Task<OrderResultDto> CloseHedgeMarketAsync(
        string symbol, OrderSide side, decimal quantity, PositionIdx posIdx)
    {
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var (qtyStep, _) = await GetSymbolInfoAsync(bybitSymbol);
        var qty = FloorToStep(quantity, qtyStep);

        var result = await _client.V5Api.Trading.PlaceOrderAsync(
            Category.Linear, bybitSymbol, side, NewOrderType.Market, qty,
            positionIdx: posIdx, reduceOnly: true);

        return new OrderResultDto
        {
            Success = result.Success,
            OrderId = result.Data?.OrderId,
            ErrorMessage = result.Error?.Message
        };
    }

    public async Task<OrderResultDto> PlaceLimitHedgeOrderAsync(
        string symbol, string side, string positionSide, decimal price, decimal quantity, bool reduceOnly = false)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var orderSide = side.Equals("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell;
            var posIdx = positionSide.Equals("Long", StringComparison.OrdinalIgnoreCase)
                ? PositionIdx.BuyHedgeMode
                : PositionIdx.SellHedgeMode;

            var (qtyStep, minQty, priceStep) = await GetSymbolInfoWithPriceAsync(bybitSymbol);
            var roundedQty = FloorToStep(quantity, qtyStep);
            var roundedPrice = FloorToStep(price, priceStep);

            if (roundedQty < minQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            var result = await _client.V5Api.Trading.PlaceOrderAsync(
                Category.Linear, bybitSymbol, orderSide, NewOrderType.Limit, roundedQty,
                price: roundedPrice, timeInForce: TimeInForce.GoodTillCanceled,
                positionIdx: posIdx, reduceOnly: reduceOnly);

            return new OrderResultDto
            {
                Success = result.Success,
                OrderId = result.Data?.OrderId,
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

    public void Dispose() => _client.Dispose();
}
