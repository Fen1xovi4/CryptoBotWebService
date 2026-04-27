using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

public interface IFuturesExchangeService : IDisposable
{
    // Dzengi's "TP" is a position attribute (set via /updateTradingPosition), not a resting
    // reduce-only limit order. We've observed it silently fail to fire even after price stays
    // past the target — so on Dzengi we skip placing it entirely and close at market on cross.
    bool UsesSoftTakeProfit => false;

    /// <summary>
    /// Fee rate for aggressive fills (market order, or marketable limit).
    /// Used when computing commissions on entry/close that take liquidity.
    /// </summary>
    decimal TakerFeeRate => 0.0006m;

    /// <summary>
    /// Fee rate for passive limit fills (resting maker order).
    /// </summary>
    decimal MakerFeeRate => 0.0002m;

    Task<List<SymbolDto>> GetSymbolsAsync();
    Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit);
    Task<decimal?> GetTickerPriceAsync(string symbol);
    Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount);
    Task<OrderResultDto> OpenShortAsync(string symbol, decimal quoteAmount);
    Task<OrderResultDto> CloseLongAsync(string symbol, decimal quantity);
    Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity);

    // HuntingFunding methods — default implementations for backward compatibility
    Task<FundingRateDto?> GetFundingRateAsync(string symbol) =>
        throw new NotSupportedException("GetFundingRateAsync not implemented");

    Task<OrderResultDto> PlaceLimitOrderAsync(string symbol, string side, decimal price, decimal quantity, bool reduceOnly = false) =>
        throw new NotSupportedException("PlaceLimitOrderAsync not implemented");

    Task<bool> CancelAllOrdersAsync(string symbol) =>
        throw new NotSupportedException("CancelAllOrdersAsync not implemented");

    Task<bool> CancelOrderAsync(string symbol, string orderId) =>
        throw new NotSupportedException("CancelOrderAsync not implemented");

    Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId) =>
        throw new NotSupportedException("GetOrderAsync not implemented");

    Task<List<LimitOrderDto>> GetOpenOrdersAsync(string symbol) =>
        throw new NotSupportedException("GetOpenOrdersAsync not implemented");

    Task<PositionDto?> GetPositionAsync(string symbol, string side) =>
        throw new NotSupportedException("GetPositionAsync not implemented");

    Task<List<PositionDto>> GetOpenPositionsAsync() =>
        throw new NotSupportedException("GetOpenPositionsAsync not implemented");

    Task<List<FundingRateDto>> GetAllFundingRatesAsync() =>
        throw new NotSupportedException("GetAllFundingRatesAsync not implemented");

    Task<List<FundingPaymentDto>> GetFundingPaymentsAsync(string symbol, DateTime? startTime = null) =>
        throw new NotSupportedException("GetFundingPaymentsAsync not implemented");

    Task<bool> SetLeverageAsync(string symbol, int leverage) =>
        throw new NotSupportedException("SetLeverageAsync not implemented");
}
