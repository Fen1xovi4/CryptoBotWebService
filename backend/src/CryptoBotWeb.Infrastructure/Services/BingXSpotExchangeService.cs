using BingX.Net.Clients;
using BingX.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

// BingX spot client. Same shape as BybitSpotExchangeService; routes through SpotApi.
// Symbols go through SymbolHelper (BTCUSDT → BTC-USDT). Order ids are longs on the wire.
//
// Fees: BingX charges the spot BUY fee in the base coin (the wallet receives qty − fee) and the
// SELL fee in the quote coin; GetOrderAsync surfaces Fee + FeeAsset so callers can size a sell
// against what actually landed.
public class BingXSpotExchangeService : ISpotExchangeService
{
    // source: https://bingx.com/en/support/costs — standard spot tier, 0.1% maker and taker.
    public decimal TakerFeeRate => 0.001m;
    public decimal MakerFeeRate => 0.001m;

    private readonly BingXRestClient _client;

    public BingXSpotExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null)
    {
        _client = new BingXRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;
        });
    }

    private static string Sym(string symbol) => SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.BingX);

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var result = await _client.SpotApi.ExchangeData.GetTickersAsync(Sym(symbol));
        if (!result.Success || result.Data == null) return null;
        var ticker = result.Data.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public async Task<(decimal qtyStep, decimal minQty, decimal priceStep)> GetSymbolFiltersAsync(string symbol)
    {
        var result = await _client.SpotApi.ExchangeData.GetSymbolsAsync(Sym(symbol));
        if (result.Success && result.Data?.Any() == true)
        {
            var info = result.Data.First();
            return (
                info.StepSize > 0 ? info.StepSize : 0.00001m,
                info.MinOrderQuantity,
                info.TickSize > 0 ? info.TickSize : 0.01m);
        }
        return (0.00001m, 0m, 0.01m);
    }

    public async Task<decimal> GetFreeBalanceAsync(string asset)
    {
        var result = await _client.SpotApi.Account.GetBalancesAsync();
        if (!result.Success || result.Data == null) return 0m;
        var bal = result.Data.FirstOrDefault(b => b.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase));
        return bal?.Free ?? 0m;
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

            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                Sym(symbol), side, OrderType.Limit,
                quantity: roundedQty, price: roundedPrice, timeInForce: TimeInForce.GoodTillCanceled);

            return new OrderResultDto
            {
                Success = result.Success,
                OrderId = result.Data?.OrderId.ToString(),
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

            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                Sym(symbol), OrderSide.Sell, OrderType.Market, quantity: roundedQty);

            if (!result.Success || result.Data == null)
                return new OrderResultDto { Success = false, ErrorMessage = result.Error?.ToString() };

            // Read the fill back so the caller gets the real average price.
            var status = await SafeGetOrderAsync(symbol, result.Data.OrderId.ToString());
            return new OrderResultDto
            {
                Success = true,
                OrderId = result.Data.OrderId.ToString(),
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
            // Spot market BUY by quote notional (USDT), like Bybit's MarketUnit.QuoteAsset.
            var result = await _client.SpotApi.Trading.PlaceOrderAsync(
                Sym(symbol), OrderSide.Buy, OrderType.Market, quoteQuantity: quoteAmount);

            if (!result.Success || result.Data == null)
                return new OrderResultDto { Success = false, ErrorMessage = result.Error?.ToString() };

            var status = await SafeGetOrderAsync(symbol, result.Data.OrderId.ToString());
            return new OrderResultDto
            {
                Success = true,
                OrderId = result.Data.OrderId.ToString(),
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
            if (!long.TryParse(orderId, out var id)) return false;
            var result = await _client.SpotApi.Trading.CancelOrderAsync(Sym(symbol), orderId: id);
            return result.Success;
        }
        catch { return false; }
    }

    public async Task<bool> CancelAllOrdersAsync(string symbol)
    {
        try
        {
            var result = await _client.SpotApi.Trading.CancelAllOrdersAsync(Sym(symbol));
            return result.Success;
        }
        catch { return false; }
    }

    // Null = the exchange says the order does not exist. Transport/auth failures throw so a
    // caller never mistakes a glitch for a vanished order (same contract as the futures client).
    public async Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId)
    {
        if (!long.TryParse(orderId, out var id)) return null;

        var result = await _client.SpotApi.Trading.GetOrderAsync(Sym(symbol), orderId: id);
        if (!result.Success)
        {
            var msg = result.Error?.ToString() ?? "unknown error";
            if (msg.Contains("not exist", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new InvalidOperationException($"BingX spot GetOrder failed for {orderId}: {msg}");
        }
        if (result.Data == null) return null;

        var o = result.Data;
        return new OrderStatusDto
        {
            OrderId = o.OrderId.ToString(),
            Status = o.Status switch
            {
                OrderStatus.New or OrderStatus.Pending => OrderLifecycleStatus.Open,
                OrderStatus.PartiallyFilled => OrderLifecycleStatus.PartiallyFilled,
                OrderStatus.Filled => OrderLifecycleStatus.Filled,
                OrderStatus.Canceled => OrderLifecycleStatus.Cancelled,
                OrderStatus.Failed => OrderLifecycleStatus.Rejected,
                _ => OrderLifecycleStatus.Unknown
            },
            FilledQuantity = o.QuantityFilled,
            // AveragePrice can be null on a still-open order; derive from the filled value instead.
            AverageFilledPrice = o.AveragePrice ?? (o.QuantityFilled > 0 ? o.ValueFilled / o.QuantityFilled : 0m),
            // BingX reports the commission as a negative number — normalised to positive.
            Fee = Math.Abs(o.Fee),
            FeeAsset = string.IsNullOrWhiteSpace(o.FeeAsset) ? null : o.FeeAsset
        };
    }

    private async Task<OrderStatusDto?> SafeGetOrderAsync(string symbol, string orderId)
    {
        try { return await GetOrderAsync(symbol, orderId); }
        catch { return null; }
    }

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    public void Dispose() => _client.Dispose();
}
