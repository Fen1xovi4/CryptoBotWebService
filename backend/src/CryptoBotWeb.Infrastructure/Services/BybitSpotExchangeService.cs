using Bybit.Net.Clients;
using Bybit.Net.Enums;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Services;

// Bybit V5 spot client. Mirrors BybitFuturesExchangeService's shape but routes through
// Category.Spot on Trading endpoints and GetSpotSymbolsAsync / GetSpotTickersAsync on
// ExchangeData. Broker referer (BrokerId) is wired the same way — Bybit Broker Program
// requires it on every signed REST call across spot AND derivatives.
public class BybitSpotExchangeService : ISpotExchangeService
{
    // source: https://www.bybit.com/en/help-center/article/Trading-fee-rate — non-VIP spot
    public decimal TakerFeeRate => 0.001m;
    public decimal MakerFeeRate => 0.001m;

    private readonly BybitRestClient _client;

    public BybitSpotExchangeService(string apiKey, string apiSecret, ApiProxy? proxy = null, string? brokerId = null)
    {
        _client = new BybitRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(apiKey, apiSecret);
            if (proxy != null) options.Proxy = proxy;
            if (!string.IsNullOrWhiteSpace(brokerId)) options.Referer = brokerId;
        });
    }

    public async Task<decimal?> GetTickerPriceAsync(string symbol)
    {
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var result = await _client.V5Api.ExchangeData.GetSpotTickersAsync(bybitSymbol);
        if (!result.Success || result.Data?.List == null)
            return null;
        var ticker = result.Data.List.FirstOrDefault();
        return ticker?.LastPrice;
    }

    public async Task<(decimal qtyStep, decimal minQty, decimal priceStep)> GetSymbolFiltersAsync(string symbol)
    {
        var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
        var result = await _client.V5Api.ExchangeData.GetSpotSymbolsAsync(bybitSymbol);
        if (result.Success && result.Data?.List?.Any() == true)
        {
            var info = result.Data.List.First();
            // Bybit spot uses BasePrecision (qty step) + MinOrderQuantity; PriceFilter.TickSize
            // controls the limit-price granularity. Fall back to conservative defaults if any
            // filter is missing — small enough not to cause min-notional inflation, large enough
            // to be a valid step for most ETHUSDT/BTCUSDT pairs.
            return (
                info.LotSizeFilter?.BasePrecision ?? 0.00001m,
                info.LotSizeFilter?.MinOrderQuantity ?? 0m,
                info.PriceFilter?.TickSize ?? 0.01m);
        }
        return (0.00001m, 0m, 0.01m);
    }

    public async Task<decimal> GetFreeBalanceAsync(string asset)
    {
        var result = await _client.V5Api.Account.GetBalancesAsync(AccountType.Unified);
        if (!result.Success || result.Data?.List == null) return 0m;
        foreach (var account in result.Data.List)
        {
            if (account.Assets == null) continue;
            foreach (var a in account.Assets)
            {
                if (a.Asset?.Equals(asset, StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Unified wallet: prefer Free; fall back to WalletBalance if Free is null
                    // (Bybit returns null when funds are backing open derivatives positions —
                    // but for grid TP placement we need the base-coin total).
                    return a.Free ?? a.WalletBalance ?? 0m;
                }
            }
        }
        return 0m;
    }

    public async Task<OrderResultDto> PlaceLimitBuyAsync(string symbol, decimal price, decimal quantity)
        => await PlaceLimitInternal(symbol, OrderSide.Buy, price, quantity);

    public async Task<OrderResultDto> PlaceLimitSellAsync(string symbol, decimal price, decimal quantity)
        => await PlaceLimitInternal(symbol, OrderSide.Sell, price, quantity);

    private async Task<OrderResultDto> PlaceLimitInternal(string symbol, OrderSide side, decimal price, decimal quantity)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var (qtyStep, minQty, priceStep) = await GetSymbolFiltersAsync(symbol);
            var roundedQty = FloorToStep(quantity, qtyStep);
            var roundedPrice = FloorToStep(price, priceStep);

            if (roundedQty < minQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            var result = await _client.V5Api.Trading.PlaceOrderAsync(
                Category.Spot, bybitSymbol, side, NewOrderType.Limit, roundedQty,
                price: roundedPrice, timeInForce: TimeInForce.GoodTillCanceled);

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

    public async Task<OrderResultDto> PlaceMarketSellAsync(string symbol, decimal quantity)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var (qtyStep, minQty, _) = await GetSymbolFiltersAsync(symbol);
            var roundedQty = FloorToStep(quantity, qtyStep);

            if (roundedQty < minQty)
                return new OrderResultDto { Success = false, ErrorMessage = $"Qty {roundedQty} < min {minQty} for {symbol}" };

            // Spot market SELL: qty is in base coin (default marketUnit).
            var result = await _client.V5Api.Trading.PlaceOrderAsync(
                Category.Spot, bybitSymbol, OrderSide.Sell, NewOrderType.Market, roundedQty);

            return new OrderResultDto
            {
                Success = result.Success,
                OrderId = result.Data?.OrderId,
                FilledQuantity = roundedQty,
                ErrorMessage = result.Error?.Message
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
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);

            // Bybit spot market BUY: qty is in quote currency (USDT) when marketUnit=QuoteAsset.
            // No qtyStep / minQty rounding — those filters are for base coin, not quote notional.
            // After fill we poll the order to learn the actual base-coin quantity filled.
            var result = await _client.V5Api.Trading.PlaceOrderAsync(
                Category.Spot, bybitSymbol, OrderSide.Buy, NewOrderType.Market, quoteAmount,
                marketUnit: MarketUnit.QuoteAsset);

            if (!result.Success || string.IsNullOrEmpty(result.Data?.OrderId))
            {
                return new OrderResultDto
                {
                    Success = false,
                    OrderId = result.Data?.OrderId,
                    ErrorMessage = result.Error?.Message
                };
            }

            // Poll the just-placed order so the caller gets the actual filled base-coin qty +
            // average price (Bybit market spot fills are typically immediate but we still need
            // the numbers off the executed record).
            var status = await GetOrderAsync(symbol, result.Data!.OrderId!);

            return new OrderResultDto
            {
                Success = true,
                OrderId = result.Data.OrderId,
                FilledQuantity = status?.FilledQuantity ?? 0m,
                FilledPrice = status?.AverageFilledPrice ?? 0m,
                ErrorMessage = null
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
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.Trading.CancelOrderAsync(
                Category.Spot, bybitSymbol, orderId: orderId);
            return result.Success;
        }
        catch { return false; }
    }

    public async Task<bool> CancelAllOrdersAsync(string symbol)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);
            var result = await _client.V5Api.Trading.CancelAllOrderAsync(Category.Spot, symbol: bybitSymbol);
            return result.Success;
        }
        catch { return false; }
    }

    public async Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId)
    {
        try
        {
            var bybitSymbol = SymbolHelper.ToExchangeSymbol(symbol, Core.Enums.ExchangeType.Bybit);

            var open = await _client.V5Api.Trading.GetOrdersAsync(
                Category.Spot, bybitSymbol, orderId: orderId);
            var order = open.Success ? open.Data?.List?.FirstOrDefault() : null;

            // Bybit moves spot orders to history shortly after they finalize — same fallback
            // we use on derivatives.
            if (order == null)
            {
                var hist = await _client.V5Api.Trading.GetOrderHistoryAsync(
                    Category.Spot, bybitSymbol, orderId: orderId);
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
        catch { return null; }
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

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    public void Dispose() => _client.Dispose();
}
