using Bitget.Net.Clients;
using Bitget.Net.Enums.V2;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

// Bitget V2 spot client. Same shape as BybitSpotExchangeService; routes through SpotApiV2.
// Symbols are plain BTCUSDT (same as canonical). Funds live in the separate spot wallet —
// the user transfers USDT there by hand.
//
// Fees: Bitget charges the spot BUY fee in the base coin unless BGB deduction is enabled on
// the account; the V2 order record only exposes the total fee without its asset, so
// GetOrderAsync returns Fee with FeeAsset = null and callers fall back to the rate estimate
// clamped by the wallet's free balance.
public class BitgetSpotExchangeService : ISpotExchangeService
{
    // source: https://www.bitget.com/fee — standard spot tier, 0.1% maker and taker.
    public decimal TakerFeeRate => 0.001m;
    public decimal MakerFeeRate => 0.001m;

    private readonly BitgetRestClient _client;

    public BitgetSpotExchangeService(string apiKey, string apiSecret, string? passphrase, ApiProxy? proxy = null)
    {
        _client = new BitgetRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret, passphrase ?? "");
            if (proxy != null) options.Proxy = proxy;
        });
    }

    private static string Sym(string symbol) => SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bitget);

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var result = await _client.SpotApiV2.ExchangeData.GetTickersAsync(Sym(symbol));
        if (!result.Success || result.Data == null) return null;
        var ticker = result.Data.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public async Task<List<SymbolDto>> GetSymbolsAsync()
    {
        var result = await _client.SpotApiV2.ExchangeData.GetSymbolsAsync();
        if (!result.Success || result.Data == null) return new List<SymbolDto>();
        return result.Data
            .Where(s => s.Status == Bitget.Net.Enums.SymbolStatus.Online && !string.IsNullOrEmpty(s.Symbol))
            .Select(s => new SymbolDto { Symbol = s.Symbol })
            .OrderBy(s => s.Symbol)
            .ToList();
    }

    public async Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit)
    {
        var result = await _client.SpotApiV2.ExchangeData.GetKlinesAsync(Sym(symbol), MapInterval(timeframe), limit: limit);
        if (!result.Success)
            throw new Exception($"Bitget spot GetKlines failed: {result.Error?.Message ?? "unknown error"}");
        if (result.Data == null) return new List<CandleDto>();

        var span = SymbolHelper.GetTimeframeSpan(timeframe);
        return result.Data
            .Select(k => new CandleDto
            {
                OpenTime = k.OpenTime,
                CloseTime = k.OpenTime + span,
                Open = k.OpenPrice,
                High = k.HighPrice,
                Low = k.LowPrice,
                Close = k.ClosePrice,
                Volume = k.Volume
            })
            .OrderBy(c => c.OpenTime)
            .ToList();
    }

    // Bitget spot has no 3m bar; it falls back to 5m.
    private static KlineInterval MapInterval(string timeframe) => timeframe.ToLowerInvariant() switch
    {
        "1m" => KlineInterval.OneMinute,
        "5m" or "3m" => KlineInterval.FiveMinutes,
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

    public async Task<(decimal qtyStep, decimal minQty, decimal priceStep)> GetSymbolFiltersAsync(string symbol)
    {
        var result = await _client.SpotApiV2.ExchangeData.GetSymbolsAsync(Sym(symbol));
        if (result.Success && result.Data?.Any() == true)
        {
            var info = result.Data.First();
            // Bitget expresses granularity as decimal places, not step sizes.
            var qtyStep = info.QuantityPrecision.HasValue ? PrecisionToStep(info.QuantityPrecision.Value) : 0.00001m;
            var priceStep = info.PricePrecision.HasValue ? PrecisionToStep(info.PricePrecision.Value) : 0.01m;
            return (qtyStep, info.MinOrderQuantity, priceStep);
        }
        return (0.00001m, 0m, 0.01m);
    }

    public async Task<decimal> GetFreeBalanceAsync(string asset)
    {
        var result = await _client.SpotApiV2.Account.GetSpotBalancesAsync();
        if (!result.Success || result.Data == null) return 0m;
        var bal = result.Data.FirstOrDefault(b => b.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase));
        return bal?.Available ?? 0m;
    }

    public Task<OrderResultDto> PlaceLimitBuyAsync(string symbol, decimal price, decimal quantity)
        => PlaceLimitInternal(symbol, OrderSide.Buy, price, quantity);

    public Task<OrderResultDto> PlaceLimitSellAsync(string symbol, decimal price, decimal quantity)
        => PlaceLimitInternal(symbol, OrderSide.Sell, price, quantity);

    private async Task<OrderResultDto> PlaceLimitInternal(string symbol, OrderSide side, decimal price, decimal quantity)
    {
        try
        {
            var (qtyStep, minQty, priceStep) = await GetSymbolFiltersAsync(symbol);
            var roundedQty = FloorToStep(quantity, qtyStep);
            var roundedPrice = FloorToStep(price, priceStep);

            if (roundedQty < minQty || roundedQty <= 0)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            var result = await _client.SpotApiV2.Trading.PlaceOrderAsync(
                Sym(symbol), side, OrderType.Limit, roundedQty, TimeInForce.GoodTillCanceled, price: roundedPrice);

            return new OrderResultDto
            {
                Success = result.Success,
                OrderId = result.Data?.OrderId,
                FilledPrice = roundedPrice,
                FilledQuantity = roundedQty,
                ErrorMessage = result.Error?.ToString()
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<OrderResultDto> PlaceMarketSellAsync(string symbol, decimal quantity)
    {
        try
        {
            var (qtyStep, minQty, _) = await GetSymbolFiltersAsync(symbol);
            var roundedQty = FloorToStep(quantity, qtyStep);
            if (roundedQty < minQty || roundedQty <= 0)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            // Market SELL: quantity is in the base coin.
            var result = await _client.SpotApiV2.Trading.PlaceOrderAsync(
                Sym(symbol), OrderSide.Sell, OrderType.Market, roundedQty, TimeInForce.GoodTillCanceled);

            if (!result.Success || string.IsNullOrEmpty(result.Data?.OrderId))
                return new OrderResultDto { Success = false, ErrorMessage = result.Error?.ToString() };

            var status = await SafeGetOrderAsync(symbol, result.Data!.OrderId!);
            return new OrderResultDto
            {
                Success = true,
                OrderId = result.Data.OrderId,
                FilledQuantity = status?.FilledQuantity > 0 ? status.FilledQuantity : roundedQty,
                FilledPrice = status?.AverageFilledPrice ?? 0m
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<OrderResultDto> PlaceMarketBuyAsync(string symbol, decimal quoteAmount)
    {
        try
        {
            // Market BUY: per Bitget V2, quantity is the quote (USDT) notional.
            var result = await _client.SpotApiV2.Trading.PlaceOrderAsync(
                Sym(symbol), OrderSide.Buy, OrderType.Market, quoteAmount, TimeInForce.GoodTillCanceled);

            if (!result.Success || string.IsNullOrEmpty(result.Data?.OrderId))
                return new OrderResultDto { Success = false, ErrorMessage = result.Error?.ToString() };

            var status = await SafeGetOrderAsync(symbol, result.Data!.OrderId!);
            return new OrderResultDto
            {
                Success = true,
                OrderId = result.Data.OrderId,
                FilledQuantity = status?.FilledQuantity ?? 0m,
                FilledPrice = status?.AverageFilledPrice ?? 0m
            };
        }
        catch (Exception ex)
        {
            return new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> CancelOrderAsync(string symbol, string orderId)
    {
        try
        {
            var result = await _client.SpotApiV2.Trading.CancelOrderAsync(Sym(symbol), orderId: orderId);
            return result.Success;
        }
        catch { return false; }
    }

    // Enumerate-and-cancel instead of the symbol-wide endpoint: the futures counterpart was
    // observed cancelling orders on unrelated symbols in production, and the spot endpoint
    // shares the same request shape — not worth the risk for a handful of orders.
    public async Task<bool> CancelAllOrdersAsync(string symbol)
    {
        try
        {
            var open = await _client.SpotApiV2.Trading.GetOpenOrdersAsync(Sym(symbol));
            if (!open.Success || open.Data == null) return false;
            var ok = true;
            foreach (var o in open.Data)
            {
                if (string.IsNullOrEmpty(o.OrderId)) continue;
                var r = await _client.SpotApiV2.Trading.CancelOrderAsync(Sym(symbol), orderId: o.OrderId);
                ok &= r.Success;
            }
            return ok;
        }
        catch { return false; }
    }

    // Null = the exchange says the order does not exist. Transport/auth failures throw so a
    // caller never mistakes a glitch for a vanished order (same contract as the futures client).
    public async Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId)
    {
        var result = await _client.SpotApiV2.Trading.GetOrderAsync(orderId: orderId);
        if (!result.Success)
        {
            var msg = result.Error?.ToString() ?? "unknown error";
            if (msg.Contains("not exist", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new InvalidOperationException($"Bitget spot GetOrder failed for {orderId}: {msg}");
        }

        var o = result.Data?.FirstOrDefault();
        if (o == null) return null;

        var filled = o.QuantityFilled ?? 0m;
        var fee = o.Fees?.NewFees?.TotalFee;
        return new OrderStatusDto
        {
            OrderId = o.OrderId ?? orderId,
            Status = o.Status switch
            {
                OrderStatus.Filled => OrderLifecycleStatus.Filled,
                OrderStatus.PartiallyFilled => OrderLifecycleStatus.PartiallyFilled,
                OrderStatus.Canceled => OrderLifecycleStatus.Cancelled,
                OrderStatus.Rejected => OrderLifecycleStatus.Rejected,
                OrderStatus.New or OrderStatus.Live or OrderStatus.Initial => OrderLifecycleStatus.Open,
                _ => OrderLifecycleStatus.Unknown
            },
            FilledQuantity = filled,
            AverageFilledPrice = o.AveragePrice
                ?? (filled > 0 && o.QuoteQuantityFilled.HasValue ? o.QuoteQuantityFilled.Value / filled : 0m),
            // Bitget reports the fee as a negative number — normalised to positive. The asset is
            // not on the V2 order record, so FeeAsset stays null (callers estimate by rate).
            Fee = fee.HasValue ? Math.Abs(fee.Value) : null,
            FeeAsset = null
        };
    }

    private async Task<OrderStatusDto?> SafeGetOrderAsync(string symbol, string orderId)
    {
        try { return await GetOrderAsync(symbol, orderId); }
        catch { return null; }
    }

    private static decimal PrecisionToStep(int precision)
    {
        if (precision <= 0) return 1m;
        var step = 1m;
        for (var i = 0; i < precision; i++) step /= 10m;
        return step;
    }

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    public void Dispose() => _client.Dispose();
}
