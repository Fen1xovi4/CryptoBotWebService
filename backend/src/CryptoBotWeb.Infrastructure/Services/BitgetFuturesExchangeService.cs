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
            // Keep the raw response body in CallResult.OriginalData so failure logs (notably the
            // observed "Success=true but OrderId empty" responses for ADA/DOT TP limits) show the
            // actual JSON the exchange returned — otherwise it's impossible to tell whether the
            // SDK lost the id or the exchange never sent it.
            options.OutputOriginalData = true;
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

            // Happy path: SDK returned an OrderId.
            if (result.Success && !string.IsNullOrEmpty(result.Data?.OrderId))
            {
                return new OrderResultDto
                {
                    Success = true,
                    OrderId = result.Data.OrderId,
                    FilledPrice = roundedPrice,
                    FilledQuantity = roundedQty
                };
            }

            // Recovery: SDK has been observed to return Success=true with Data.OrderId empty on
            // certain Bitget symbols (e.g. ADA/DOT one-way reduce-only TP limits) — the order
            // DOES land on the book, we just can't read its id from the response. Without this
            // recovery the SmaDca heal path re-places a fresh TP every 5s and orphans pile up
            // (TEST2 hit ~95k retries in 24d). Probe open orders and match by side+price+qty.
            if (result.Success && string.IsNullOrEmpty(result.Data?.OrderId))
            {
                var recovered = await RecoverOrderIdFromOpenOrders(
                    bitgetSymbol, orderSide, roundedPrice, roundedQty, reduceOnly);
                if (recovered != null)
                {
                    return new OrderResultDto
                    {
                        Success = true,
                        OrderId = recovered,
                        FilledPrice = roundedPrice,
                        FilledQuantity = roundedQty
                    };
                }
            }

            // Genuine failure (or recovery couldn't find the order). Build a maximally
            // informative error string — Bitget has been seen to return Success=false with
            // Error=null on rejections, which surfaced as a blank
            // "Не удалось выставить TP лимит:" log.
            var parts = new List<string>();
            if (result.Error != null) parts.Add(result.Error.ToString());
            if (result.ResponseStatusCode != null) parts.Add($"HTTP {(int)result.ResponseStatusCode}");
            if (!string.IsNullOrEmpty(result.OriginalData)) parts.Add($"raw={result.OriginalData}");
            if (result.Success && string.IsNullOrEmpty(result.Data?.OrderId))
                parts.Add("SDK reported Success=true but OrderId is empty/null AND no matching open order found");
            var errorMessage = parts.Count > 0
                ? string.Join(" | ", parts)
                : "Bitget SDK returned no error and no order id (unknown failure)";

            return new OrderResultDto
            {
                Success = false,
                OrderId = null,
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

    /// <summary>
    /// Called when PlaceOrderAsync returned Success=true with an empty OrderId — finds the
    /// just-placed order on the book by matching side+price+qty (+ ReduceOnly), and cancels
    /// any older duplicates that piled up from prior failed-recovery attempts. Returns the
    /// id of the order we kept, or null if no match was found.
    /// </summary>
    private async Task<string?> RecoverOrderIdFromOpenOrders(
        string bitgetSymbol, OrderSide side, decimal price, decimal quantity, bool reduceOnly)
    {
        try
        {
            var openResult = await _client.FuturesApiV2.Trading.GetOpenOrdersAsync(
                BitgetProductTypeV2.UsdtFutures, bitgetSymbol);
            if (!openResult.Success || openResult.Data?.Orders == null)
                return null;

            var matches = openResult.Data.Orders
                .Where(o => o.Side == side
                    && (o.Price ?? 0m) == price
                    && o.Quantity == quantity
                    && o.ReduceOnly == reduceOnly
                    && !string.IsNullOrEmpty(o.OrderId))
                .OrderByDescending(o => o.CreateTime)
                .ToList();

            if (matches.Count == 0) return null;

            // Keep the newest; cancel any older orphans from prior empty-id placements.
            // Best-effort — don't fail recovery if a cancel errors out.
            foreach (var dup in matches.Skip(1))
            {
                try
                {
                    await _client.FuturesApiV2.Trading.CancelOrderAsync(
                        BitgetProductTypeV2.UsdtFutures, bitgetSymbol,
                        dup.OrderId!, clientOrderId: null, marginAsset: "USDT");
                }
                catch { /* swallow — duplicate may already be gone */ }
            }
            return matches[0].OrderId;
        }
        catch
        {
            return null;
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
        // Bitget V2 cancel-all-orders endpoint cancels every USDT-futures order on the
        // account even when a per-symbol filter is supplied — verified twice in production
        // (a single-symbol full-close nuked DCAs/TPs on unrelated bots' symbols within
        // 9 seconds). Cause is either the SDK dropping the symbol param from the request
        // body or the API ignoring it when productType is also passed. Either way, the
        // safe substitute is to enumerate this symbol's open orders and cancel each
        // individually — no cross-symbol blast radius.
        try
        {
            var openOrders = await GetOpenOrdersAsync(symbol);
            if (openOrders.Count == 0) return true;

            var allOk = true;
            foreach (var order in openOrders)
            {
                if (string.IsNullOrEmpty(order.OrderId)) continue;
                var ok = await CancelOrderAsync(symbol, order.OrderId);
                if (!ok) allOk = false;
            }
            return allOk;
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

    async Task<(decimal qtyStep, decimal minQty)> IFuturesExchangeService.GetSymbolInfoAsync(string symbol)
    {
        var bitgetSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);
        return await GetSymbolInfoAsync(bitgetSymbol);
    }

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
