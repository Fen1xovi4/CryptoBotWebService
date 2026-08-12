using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Shared, fee-aware bookkeeping for the reworked strategy simulators. Records fills as
/// <see cref="SimTrade"/> rows, accumulates gross/realized PnL and fees, tracks peak exposure and
/// equity drawdown, and samples the equity curve. Deterministic, no I/O.
///
/// Fee convention (per the backtest spec): every fill is charged <c>notional × feeRate</c> —
/// taker for market fills, maker for resting-limit fills. A closing fill's <see cref="SimTrade.PnlUsd"/>
/// is the FULL round-trip net: gross realized minus the fees accrued on the entry/scale-in legs of
/// the position minus this closing fill's own fee. Summary <c>NetPnlUsd = GrossPnlUsd − FeesUsd</c>
/// (funding is 0 in these simulators), which equals the sum of per-trade net PnL once the book is flat.
/// </summary>
internal sealed class SimLedger
{
    private readonly decimal _makerFeeRate;
    private readonly decimal _takerFeeRate;

    public List<SimTrade> Trades { get; } = new();
    public List<EquityPoint> EquityCurve { get; } = new();

    public decimal GrossRealizedUsd { get; private set; }
    public decimal FeesUsd { get; private set; }
    public decimal MaxNotionalUsd { get; private set; }
    public decimal MaxDrawdownUsd { get; private set; }

    /// <summary>Closing fills only (round trips) — the denominator for win rate.</summary>
    public int ClosedTrades { get; private set; }
    public int WinningTrades { get; private set; }
    public int LosingTrades { get; private set; }

    // Fees charged on the entry/scale-in legs of the currently open position, held aside so the
    // closing fill can report a full round-trip net PnL. Reset to 0 on every close.
    private decimal _openPositionFees;

    private decimal _peakEquity;
    private bool _peakSeen;

    public SimLedger(decimal makerFeeRate, decimal takerFeeRate)
    {
        _makerFeeRate = makerFeeRate;
        _takerFeeRate = takerFeeRate;
    }

    /// <summary>Realized PnL net of all fees booked so far (fees already include open-position legs).</summary>
    public decimal RealizedNetUsd => GrossRealizedUsd - FeesUsd;

    private decimal FeeFor(decimal notional, bool taker)
        => notional * (taker ? _takerFeeRate : _makerFeeRate);

    /// <summary>
    /// Records an opening or scale-in fill (Action = Open | Dca). PnlUsd is null (pure open).
    /// The fee is added to the running total and held against the position for the eventual close.
    /// </summary>
    public void RecordOpen(DateTime time, string side, string action, decimal price, decimal qty,
        bool taker, string reason)
    {
        var notional = qty * price;
        var fee = FeeFor(notional, taker);
        FeesUsd += fee;
        _openPositionFees += fee;

        Trades.Add(new SimTrade
        {
            Time = time,
            Side = side,
            Action = action,
            Price = price,
            Quantity = qty,
            NotionalUsd = Math.Round(notional, 8),
            FeeUsd = Math.Round(fee, 8),
            PnlUsd = null,
            PnlPercent = null,
            Reason = reason
        });
    }

    /// <summary>
    /// Records a full-position close (Action = Close | TakeProfit | StopLoss). <paramref name="grossRealizedUsd"/>
    /// is the PnL before fees for the whole closed quantity; <paramref name="pnlPercent"/> is the
    /// gross percentage move on the average entry.
    /// </summary>
    public void RecordClose(DateTime time, string side, string action, decimal price, decimal qty,
        bool taker, decimal grossRealizedUsd, decimal pnlPercent, string reason)
    {
        var notional = qty * price;
        var fee = FeeFor(notional, taker);
        FeesUsd += fee;
        GrossRealizedUsd += grossRealizedUsd;

        var netUsd = grossRealizedUsd - _openPositionFees - fee;
        _openPositionFees = 0m;

        ClosedTrades++;
        if (netUsd > 0) WinningTrades++;
        else LosingTrades++;

        Trades.Add(new SimTrade
        {
            Time = time,
            Side = side,
            Action = action,
            Price = price,
            Quantity = qty,
            NotionalUsd = Math.Round(notional, 8),
            FeeUsd = Math.Round(fee, 8),
            PnlUsd = Math.Round(netUsd, 8),
            PnlPercent = Math.Round(pnlPercent, 6),
            Reason = reason
        });
    }

    /// <summary>
    /// Records the close of a MULTI-LEG position — one logical round trip that is closed by several
    /// fills, possibly on different venues (cross-exchange arbitrage pair). The caller supplies the
    /// aggregated numbers directly: <paramref name="notionalUsd"/> and <paramref name="closeFeeUsd"/>
    /// for the closing fills, and <paramref name="entryFeesUsd"/> for the fees THIS position accrued
    /// while opening. That is necessary because the single-position fee pool used by
    /// <see cref="RecordClose"/> cannot attribute entry fees when several positions are open at once.
    /// Counts as ONE closed trade for the win-rate statistics.
    /// </summary>
    public void RecordCloseMultiLeg(DateTime time, string side, string action, decimal price, decimal qty,
        decimal notionalUsd, decimal closeFeeUsd, decimal entryFeesUsd, decimal grossRealizedUsd,
        decimal pnlPercent, string reason)
    {
        FeesUsd += closeFeeUsd;
        GrossRealizedUsd += grossRealizedUsd;

        // The opening legs already booked their fees (into FeesUsd and the pool) via RecordOpen —
        // drain this position's share so a book with several open positions stays consistent.
        _openPositionFees = Math.Max(0m, _openPositionFees - entryFeesUsd);

        var netUsd = grossRealizedUsd - entryFeesUsd - closeFeeUsd;

        ClosedTrades++;
        if (netUsd > 0) WinningTrades++;
        else LosingTrades++;

        Trades.Add(new SimTrade
        {
            Time = time,
            Side = side,
            Action = action,
            Price = price,
            Quantity = qty,
            NotionalUsd = Math.Round(notionalUsd, 8),
            FeeUsd = Math.Round(closeFeeUsd, 8),
            PnlUsd = Math.Round(netUsd, 8),
            PnlPercent = Math.Round(pnlPercent, 6),
            Reason = reason
        });
    }

    /// <summary>Update peak simultaneous open notional (cost basis of the live position).</summary>
    public void TrackOpenNotional(decimal openNotionalUsd)
    {
        if (openNotionalUsd > MaxNotionalUsd) MaxNotionalUsd = openNotionalUsd;
    }

    /// <summary>
    /// Append an equity sample and fold it into the peak-to-trough drawdown. Drawdown is measured
    /// over the sampled curve (hourly boundaries + final point), consistent with how EquityUsd is
    /// defined by the spec.
    /// </summary>
    public void SampleEquity(DateTime time, decimal unrealizedUsd)
    {
        var realized = RealizedNetUsd;
        var equity = realized + unrealizedUsd;

        EquityCurve.Add(new EquityPoint
        {
            Time = time,
            RealizedPnlUsd = Math.Round(realized, 8),
            UnrealizedPnlUsd = Math.Round(unrealizedUsd, 8),
            EquityUsd = Math.Round(equity, 8)
        });

        if (!_peakSeen)
        {
            _peakEquity = equity;
            _peakSeen = true;
        }
        else if (equity > _peakEquity)
        {
            _peakEquity = equity;
        }

        var dd = _peakEquity - equity;
        if (dd > MaxDrawdownUsd) MaxDrawdownUsd = dd;
    }
}
