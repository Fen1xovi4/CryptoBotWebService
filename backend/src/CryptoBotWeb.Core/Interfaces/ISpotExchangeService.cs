using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

// Spot trading surface: Bybit, BingX and Bitget. Used by the GridHedge SameTicker long-grid leg
// (Bybit only) and by SpotPingPong (all three). Dzengi has no spot client.
public interface ISpotExchangeService : IDisposable
{
    // Non-VIP spot fees — 0.1% both legs on all three exchanges. Override per-exchange if needed.
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

    // GridHedge level-0 entry uses this for the SameTicker spot leg. quoteAmount is in USDT;
    // Bybit spot market buys take quote-coin notional (MarketUnit.QuoteCoin).
    Task<OrderResultDto> PlaceMarketBuyAsync(string symbol, decimal quoteAmount);

    Task<bool> CancelOrderAsync(string symbol, string orderId);
    Task<bool> CancelAllOrdersAsync(string symbol);
    Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId);
}
