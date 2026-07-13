using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest simulator for the SmaDca strategy. Mirrors the decision logic of
/// <see cref="SmaDcaHandler"/> onto the 1-minute price path:
///
/// - Single direction (config.Direction). SMA(SmaPeriod) gate on CLOSED candles of the config
///   timeframe: Long enters when close &gt; SMA, Short when close &lt; SMA. First entry is ALWAYS
///   market (the handler never places an entry limit — <c>PlaceEntryLimit</c> is dead code).
/// - Tiered DCA ladder (config.Levels, with the legacy DcaStepPercent/DcaMultiplier/MaxDcaLevels
///   fallback when Levels is empty). DcaTriggerBase "Average"|"LastFill". DCA fills honor
///   config.OrderType: Market fires at the closed-candle close; Limit rests a maker order that fills
///   when a 1-minute tick crosses it.
/// - TP is a reduce-only maker limit at TakeProfitPercent from the recomputed average
///   (TakeProfitTierShiftEnabled shifts the % by tier). Modeled as a resting order filled on the
///   1-minute tick that crosses it. On TP fill the position resets and one closed candle is skipped
///   before re-entry (SkipNextCandle).
///
/// Fees follow the shared spec: market fills = taker, resting limits (DCA-limit and TP) = maker.
/// CompletedCycles = number of TP closes. The workspace open-position timer is intentionally ignored
/// (the sim runs the whole window).
/// </summary>
public class SmaDcaSimulator : IStrategySimulator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.SmaDca;

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();
        result.Warnings.AddRange(context.Warnings);

        var config = JsonSerializer.Deserialize<SmaDcaConfig>(context.ConfigJson, JsonOptions);
        if (config == null)
        {
            result.Warnings.Add("SmaDca: config could not be deserialized.");
            return result;
        }

        var path = context.PathCandles;
        if (path.Count == 0)
        {
            result.Warnings.Add("SmaDca: no path candles.");
            return result;
        }

        if (config.SmaPeriod < 2 || config.PositionSizeUsd <= 0 || config.TakeProfitPercent <= 0)
        {
            result.Warnings.Add(
                $"SmaDca: invalid parameters (SmaPeriod={config.SmaPeriod}, PositionSizeUsd={config.PositionSizeUsd}, TP={config.TakeProfitPercent}%).");
            return result;
        }

        var isLong = config.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var useLimitDca = config.OrderType.Equals("Limit", StringComparison.OrdinalIgnoreCase);
        var useLastFill = config.DcaTriggerBase.Equals("LastFill", StringComparison.OrdinalIgnoreCase);
        var effectiveLevels = GetEffectiveLevels(config);

        var ledger = new SimLedger(context.MakerFeeRate, context.TakerFeeRate);

        // ── Aggregate to the strategy timeframe and pre-compute the SMA line ──
        var span = SymbolHelper.GetTimeframeSpan(config.Timeframe);
        var agg = CandleAggregator.Aggregate(path, config.Timeframe);
        var closes = agg.Select(c => c.Close).ToArray();
        var sma = IndicatorCalculator.CalculateSma(closes, config.SmaPeriod);

        for (int j = config.SmaPeriod - 1; j >= 0 && j < agg.Count; j++)
        {
            if (sma[j] != 0m)
                result.IndicatorValues.Add(new IndicatorPoint { Time = agg[j].OpenTime, Value = Math.Round(sma[j], 8) });
        }

        var state = new DcaState { IsLong = isLong };

        var bucketEnd = new DateTime[agg.Count];
        for (int j = 0; j < agg.Count; j++)
            bucketEnd[j] = agg[j].OpenTime + span;

        int aggIdx = 0;

        foreach (var c in path)
        {
            if (c.OpenTime.Minute == 0)
                ledger.SampleEquity(c.OpenTime, Unrealized(state, c.Open));

            // 1) Intrabar order resolution — DCA-limit fills and the reduce-only TP limit, walked in
            //    the deterministic tick order so their relative fill sequence is realistic.
            if (state.InPosition)
                ResolveTicks(config, state, c, ledger);

            // 2) Closed timeframe buckets: SMA gate, market entry / market-DCA trigger, DCA-limit
            //    placement, and the skip-one-candle-after-exit rule.
            while (aggIdx < agg.Count && bucketEnd[aggIdx] <= c.CloseTime)
            {
                ProcessClosedCandle(config, state, agg[aggIdx], sma[aggIdx], effectiveLevels,
                    isLong, useLimitDca, useLastFill, ledger);
                aggIdx++;
            }
        }

        var lastPrice = path[^1].Close;
        ledger.SampleEquity(path[^1].CloseTime, Unrealized(state, lastPrice));

        result.Trades.AddRange(ledger.Trades);
        result.EquityCurve.AddRange(ledger.EquityCurve);
        BuildSummary(result, ledger, state, path, lastPrice);
        return result;
    }

    // ───────── Intrabar tick resolution: DCA-limit fill + reduce-only TP limit ─────────

    private void ResolveTicks(SmaDcaConfig config, DcaState state, CandleDto candle, SimLedger ledger)
    {
        foreach (var tick in CandlePathHelper.GetTicks(candle))
        {
            if (!state.InPosition) return;

            // Pending DCA maker limit — fills when price crosses it (Long Buy: tick ≤ limit; Short Sell: tick ≥ limit).
            if (state.DcaLimitActive)
            {
                var crossed = state.IsLong ? tick.Price <= state.DcaLimitPrice : tick.Price >= state.DcaLimitPrice;
                if (crossed)
                {
                    ApplyDcaFill(config, state, tick.Time, state.DcaLimitPrice, state.DcaLimitQuantity,
                        taker: false, ledger);
                    state.DcaLimitActive = false;
                }
            }

            if (!state.InPosition) return;

            // Reduce-only TP maker limit — fills when price crosses the TP level.
            if (state.CurrentTakeProfit > 0)
            {
                var tpHit = state.IsLong ? tick.Price >= state.CurrentTakeProfit : tick.Price <= state.CurrentTakeProfit;
                if (tpHit)
                {
                    CloseAtTakeProfit(config, state, tick.Time, ledger);
                    return;
                }
            }
        }
    }

    // ───────── Closed-candle logic ─────────

    private void ProcessClosedCandle(SmaDcaConfig config, DcaState state, CandleDto candle, decimal sma,
        List<SmaDcaLevel> effectiveLevels, bool isLong, bool useLimitDca, bool useLastFill, SimLedger ledger)
    {
        if (sma == 0m) return; // SMA not warmed up

        // Skip the first closed candle after an exit to prevent instant re-entry (handler §11).
        if (state.SkipNextCandle && !state.InPosition)
        {
            state.SkipNextCandle = false;
            return;
        }

        if (!state.InPosition)
        {
            var shouldEnter = isLong ? candle.Close > sma : candle.Close < sma;
            if (shouldEnter)
                OpenEntry(config, state, candle.CloseTime, candle.Close, isLong, ledger);
            return;
        }

        // In position — evaluate a DCA on this closed bar (skip if a DCA limit is already resting).
        if (state.DcaLimitActive) return;

        var tier = GetTierForDcaLevel(effectiveLevels, state.DcaLevel);
        if (tier == null || tier.Multiplier <= 0) return; // ladder exhausted / invalid tier

        var triggerBase = useLastFill && state.LastDcaPrice > 0 ? state.LastDcaPrice : state.AverageEntryPrice;
        var dcaTrigger = state.IsLong
            ? candle.Close <= triggerBase * (1m - tier.StepPercent / 100m)
            : candle.Close >= triggerBase * (1m + tier.StepPercent / 100m);
        if (!dcaTrigger) return;

        if (useLimitDca)
        {
            // Place a resting maker DCA limit offset from the close; it fills on a later crossing tick.
            var offsetMul = state.IsLong
                ? (1m - config.EntryLimitOffsetPercent / 100m)
                : (1m + config.EntryLimitOffsetPercent / 100m);
            var limitPrice = candle.Close * offsetMul;
            var targetQty = state.TotalQuantity * tier.Multiplier;
            if (targetQty <= 0) return;

            state.DcaLimitActive = true;
            state.DcaLimitPrice = limitPrice;
            state.DcaLimitQuantity = targetQty;
        }
        else
        {
            ApplyDcaFill(config, state, candle.CloseTime, candle.Close, state.TotalQuantity * tier.Multiplier,
                taker: true, ledger);
        }
    }

    // ───────── Position mutations ─────────

    private void OpenEntry(SmaDcaConfig config, DcaState state, DateTime time, decimal fillPrice, bool isLong,
        SimLedger ledger)
    {
        if (fillPrice <= 0) return;
        var qty = config.PositionSizeUsd / fillPrice;

        state.InPosition = true;
        state.IsLong = isLong;
        state.TotalQuantity = qty;
        state.TotalCost = fillPrice * qty;
        state.AverageEntryPrice = fillPrice;
        state.DcaLevel = 0;
        state.CurrentTakeProfit = ComputeTakeProfit(fillPrice, GetEffectiveTakeProfitPercent(config, 0), isLong);
        state.LastDcaPrice = fillPrice;
        state.SkipNextCandle = false;
        state.DcaLimitActive = false;

        ledger.RecordOpen(time, isLong ? "Long" : "Short", "Open", fillPrice, qty, taker: true, "Entry");
        ledger.TrackOpenNotional(state.TotalCost);
    }

    private void ApplyDcaFill(SmaDcaConfig config, DcaState state, DateTime time, decimal fillPrice,
        decimal filledQty, bool taker, SimLedger ledger)
    {
        if (filledQty <= 0 || fillPrice <= 0) return;

        state.TotalCost += fillPrice * filledQty;
        state.TotalQuantity += filledQty;
        state.AverageEntryPrice = state.TotalCost / state.TotalQuantity;
        state.DcaLevel++;
        state.CurrentTakeProfit = ComputeTakeProfit(state.AverageEntryPrice,
            GetEffectiveTakeProfitPercent(config, state.DcaLevel), state.IsLong);
        state.LastDcaPrice = fillPrice;

        ledger.RecordOpen(time, state.IsLong ? "Long" : "Short", "Dca", fillPrice, filledQty, taker,
            $"DCA#{state.DcaLevel}");
        ledger.TrackOpenNotional(state.TotalCost);
    }

    private void CloseAtTakeProfit(SmaDcaConfig config, DcaState state, DateTime time, SimLedger ledger)
    {
        var tpPrice = state.CurrentTakeProfit;
        var qty = state.TotalQuantity;
        var pnlPct = ComputePnlPercent(state.IsLong, state.AverageEntryPrice, tpPrice);
        var gross = state.TotalCost * pnlPct / 100m;

        ledger.RecordClose(time, state.IsLong ? "Long" : "Short", "TakeProfit", tpPrice, qty,
            taker: false, gross, pnlPct, "TakeProfit");

        ResetPosition(state);
        state.SkipNextCandle = true;
        state.CompletedCycles++;
    }

    private static void ResetPosition(DcaState state)
    {
        state.InPosition = false;
        state.TotalQuantity = 0;
        state.TotalCost = 0;
        state.AverageEntryPrice = 0;
        state.CurrentTakeProfit = 0;
        state.DcaLevel = 0;
        state.LastDcaPrice = 0;
        state.DcaLimitActive = false;
        state.DcaLimitPrice = 0;
        state.DcaLimitQuantity = 0;
    }

    // ───────── Ported SmaDcaHandler helpers ─────────

    private static List<SmaDcaLevel> GetEffectiveLevels(SmaDcaConfig config)
    {
        if (config.Levels != null && config.Levels.Count > 0) return config.Levels;
        return new List<SmaDcaLevel>
        {
            new()
            {
                StepPercent = config.DcaStepPercent,
                Multiplier = config.DcaMultiplier,
                Count = config.MaxDcaLevels
            }
        };
    }

    private static SmaDcaLevel? GetTierForDcaLevel(List<SmaDcaLevel> levels, int dcaLevel)
    {
        var cumulative = 0;
        foreach (var lvl in levels)
        {
            cumulative += lvl.Count;
            if (dcaLevel < cumulative) return lvl;
        }
        return null;
    }

    private static decimal ComputeTakeProfit(decimal averagePrice, decimal tpPercent, bool isLong)
        => isLong
            ? averagePrice * (1m + tpPercent / 100m)
            : averagePrice * (1m - tpPercent / 100m);

    private static decimal GetEffectiveTakeProfitPercent(SmaDcaConfig config, int dcaLevel)
    {
        if (!config.TakeProfitTierShiftEnabled || config.TakeProfitTierShifts.Count == 0)
            return config.TakeProfitPercent;

        var userTier = dcaLevel + 1;
        decimal? best = null;
        var bestFromTier = int.MinValue;
        foreach (var s in config.TakeProfitTierShifts)
        {
            if (s.FromTier <= 0 || s.TakeProfitPercent <= 0) continue;
            if (userTier < s.FromTier) continue;
            if (s.FromTier > bestFromTier)
            {
                bestFromTier = s.FromTier;
                best = s.TakeProfitPercent;
            }
        }
        return best ?? config.TakeProfitPercent;
    }

    private static decimal ComputePnlPercent(bool isLong, decimal entry, decimal exit)
        => isLong
            ? (exit - entry) / entry * 100m
            : (entry - exit) / entry * 100m;

    // ───────── Equity / summary ─────────

    private static decimal Unrealized(DcaState state, decimal price)
    {
        if (!state.InPosition) return 0m;
        return state.IsLong
            ? state.TotalQuantity * (price - state.AverageEntryPrice)
            : state.TotalQuantity * (state.AverageEntryPrice - price);
    }

    private static void BuildSummary(SimulationRunResult result, SimLedger ledger, DcaState state,
        List<CandleDto> path, decimal lastPrice)
    {
        var net = ledger.GrossRealizedUsd - ledger.FeesUsd; // funding = 0
        var openPositions = state.InPosition ? 1 : 0;
        var unrealizedAtEnd = Unrealized(state, lastPrice);

        result.Summary = new SimulationRunSummary
        {
            TotalTrades = ledger.ClosedTrades,
            WinningTrades = ledger.WinningTrades,
            LosingTrades = ledger.LosingTrades,
            WinRate = ledger.ClosedTrades > 0
                ? Math.Round((decimal)ledger.WinningTrades / ledger.ClosedTrades * 100m, 2)
                : 0m,
            GrossPnlUsd = Math.Round(ledger.GrossRealizedUsd, 8),
            FeesUsd = Math.Round(ledger.FeesUsd, 8),
            FundingPnlUsd = 0m,
            NetPnlUsd = Math.Round(net, 8),
            MaxDrawdownUsd = Math.Round(ledger.MaxDrawdownUsd, 8),
            MaxDrawdownPercent = ledger.MaxNotionalUsd > 0
                ? Math.Round(ledger.MaxDrawdownUsd / ledger.MaxNotionalUsd * 100m, 4)
                : 0m,
            MaxNotionalUsd = Math.Round(ledger.MaxNotionalUsd, 8),
            OpenPositionsAtEnd = openPositions,
            UnrealizedPnlAtEndUsd = Math.Round(unrealizedAtEnd, 8),
            CompletedCycles = state.CompletedCycles,
            StartTime = path[0].OpenTime,
            EndTime = path[^1].CloseTime,
            PathCandlesProcessed = path.Count
        };
    }

    // ───────── Internal state ─────────

    private sealed class DcaState
    {
        public bool InPosition { get; set; }
        public bool IsLong { get; set; }

        public decimal TotalQuantity { get; set; }
        public decimal TotalCost { get; set; }
        public decimal AverageEntryPrice { get; set; }
        public decimal CurrentTakeProfit { get; set; }
        public int DcaLevel { get; set; }
        public decimal LastDcaPrice { get; set; }

        public bool SkipNextCandle { get; set; }
        public int CompletedCycles { get; set; }

        // Resting DCA maker limit (Limit order type only).
        public bool DcaLimitActive { get; set; }
        public decimal DcaLimitPrice { get; set; }
        public decimal DcaLimitQuantity { get; set; }
    }
}
