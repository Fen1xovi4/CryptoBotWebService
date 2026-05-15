using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

// Spot trading surface. V1 ships Bybit-only; the GridHedge SameTicker variant uses this for
// the long-grid leg (limit buys + reduce-only limit sells for per-batch TP + market sell for
// the stop-loss / out-of-range exit).
public interface ISpotExchangeService : IDisposable
{
    // Bybit non-VIP spot fees — both legs identical at 0.1%. Override per-exchange if needed.
    decimal TakerFeeRate => 0.001m;
    decimal MakerFeeRate => 0.001m;

    Task<decimal?> GetTickerPriceAsync(string symbol);

    // Exchange-side lot/price filters. qtyStep + minQty drive the pre-placement minimum-notional
    // guard; priceStep is used to round limit prices into a valid tick.
    Task<(decimal qtyStep, decimal minQty, decimal priceStep)> GetSymbolFiltersAsync(string symbol);

    // Free (available) balance of a given coin in the spot wallet. For Bybit Unified, this is
    // pulled from the same Unified account as derivatives.
    Task<decimal> GetFreeBalanceAsync(string asset);

    Task<OrderResultDto> PlaceLimitBuyAsync(string symbol, decimal price, decimal quantity);
    Task<OrderResultDto> PlaceLimitSellAsync(string symbol, decimal price, decimal quantity);
    Task<OrderResultDto> PlaceMarketSellAsync(string symbol, decimal quantity);

    Task<bool> CancelOrderAsync(string symbol, string orderId);
    Task<bool> CancelAllOrdersAsync(string symbol);
    Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId);
}
