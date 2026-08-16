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

    /// <summary>
    /// Historical klines over an arbitrary UTC window, paginating past the exchange's
    /// per-request limit. Ascending by OpenTime. Used by the simulator (backtesting) only.
    /// </summary>
    Task<List<CandleDto>> GetKlinesRangeAsync(string symbol, string timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        throw new NotSupportedException("GetKlinesRangeAsync not implemented");

    /// <summary>
    /// Historical funding settlements (rate + settle time) over a UTC window, paginated,
    /// ascending. Rate is the raw fraction (0.0001 = 0.01%), same as FundingRateDto.Rate.
    /// Used by the simulator (backtesting) only.
    /// </summary>
    Task<List<FundingEventDto>> GetFundingHistoryAsync(string symbol, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        throw new NotSupportedException("GetFundingHistoryAsync not implemented");
    Task<decimal?> GetTickerPriceAsync(string symbol);

    /// <summary>
    /// Best bid/ask (top of book) for a futures symbol. Used by FuturesArbitrage, where the
    /// executable spread must be computed from bid/ask, not last price. Returns null on a
    /// transient fetch failure — caller should skip the tick, not treat it as price 0.
    /// </summary>
    Task<BookTickerDto?> GetBookTickerAsync(string symbol) =>
        throw new NotSupportedException("GetBookTickerAsync not implemented");

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

    /// <summary>
    /// Same as <see cref="SetLeverageAsync"/>, but keeps the exchange's rejection text so the
    /// caller can log WHY the pin failed instead of a bare "could not set leverage", and reports
    /// "already at that leverage" rejections as success.
    /// Default: delegates to the bool overload, losing the detail.
    /// </summary>
    async Task<LeverageSetResult> SetLeverageDetailedAsync(string symbol, int leverage) =>
        new(await SetLeverageAsync(symbol, leverage));

    /// <summary>
    /// Returns the symbol's maximum allowed leverage from the exchange risk-limit table,
    /// or null if it can't be determined (network error / exchange doesn't expose it).
    /// Callers use this to clamp the leverage they set before placing orders so a stale
    /// account-level leverage (e.g. 1000x left over from a previously-traded symbol) isn't
    /// rejected by the exchange as "cannot set leverage [N] gt maxLeverage [M] by risk limit".
    /// Default: null — caller should treat as "unknown" and skip the clamp.
    /// </summary>
    Task<int?> GetMaxLeverageAsync(string symbol) =>
        Task.FromResult<int?>(null);

    /// <summary>
    /// Returns the symbol's lot-size step and minimum order quantity for futures.
    /// Default: (0, 0) — caller should treat zero values as "exchange doesn't expose this info"
    /// and skip the minimum-notional pre-check.
    /// </summary>
    Task<(decimal qtyStep, decimal minQty)> GetSymbolInfoAsync(string symbol) =>
        Task.FromResult((0m, 0m));

    // ────────────────────────── Hedge-mode (Bybit V1) ──────────────────────────
    // These methods target hedge-mode positions, where the long-side (positionIdx=1) and
    // short-side (positionIdx=2) coexist on the SAME symbol within ONE futures account.
    // Implementations should only enable these when the exchange account is configured
    // for hedge mode. Default: throws — caller must check support via IsHedgeModeSupported.

    /// <summary>True if this exchange service implements the hedge-mode order surface below.</summary>
    bool IsHedgeModeSupported => false;

    /// <summary>
    /// Probes the account's current position-mode setting for <paramref name="symbol"/>.
    /// Returns null if the probe failed (network, auth) — caller should treat as "unknown".
    /// </summary>
    Task<bool?> IsHedgeModeEnabledAsync(string symbol) =>
        throw new NotSupportedException("IsHedgeModeEnabledAsync not implemented");

    Task<OrderResultDto> OpenHedgeLongAsync(string symbol, decimal quoteAmount) =>
        throw new NotSupportedException("OpenHedgeLongAsync not implemented");

    Task<OrderResultDto> OpenHedgeShortAsync(string symbol, decimal quoteAmount) =>
        throw new NotSupportedException("OpenHedgeShortAsync not implemented");

    Task<OrderResultDto> CloseHedgeLongAsync(string symbol, decimal quantity) =>
        throw new NotSupportedException("CloseHedgeLongAsync not implemented");

    Task<OrderResultDto> CloseHedgeShortAsync(string symbol, decimal quantity) =>
        throw new NotSupportedException("CloseHedgeShortAsync not implemented");

    /// <summary>
    /// Place a limit order tied to a specific hedge-mode position side.
    /// <paramref name="positionSide"/> = "Long" routes to positionIdx=1 (the long-grid position);
    /// "Short" routes to positionIdx=2 (the short-hedge position). reduceOnly closes the
    /// matching side only.
    /// </summary>
    Task<OrderResultDto> PlaceLimitHedgeOrderAsync(
        string symbol, string side, string positionSide, decimal price, decimal quantity, bool reduceOnly = false) =>
        throw new NotSupportedException("PlaceLimitHedgeOrderAsync not implemented");
}
