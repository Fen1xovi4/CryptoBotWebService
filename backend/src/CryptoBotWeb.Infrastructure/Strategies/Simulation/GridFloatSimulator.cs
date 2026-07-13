using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest simulator for the GridFloat ("floating grid") strategy. Mirrors the live
/// <c>GridFloatHandler</c> tick loop against the 1-minute price path in the
/// <see cref="SimulationContext"/>.
///
/// Behaviours mirrored from the handler:
///   - Direction (Long/Short); a single market anchor entry at each cycle start, then a ladder
///     of DCA limit orders on the losing side, tiered by <c>GridFloatTier</c> (per-tier SizeUsdt,
///     DcaStepPercent, TpStepPercent with global fallbacks). Legacy BaseSizeUsdt + RangePercent
///     configs are normalised to a single tier (same NormalizeTiers logic).
///   - Every fill (anchor + each DCA) is an independent batch with its own reduce-only TP limit
///     at fill ± effective TpStepPercent. A batch TP fill realises PnL and frees the slot, which
///     the grid heal re-arms with a fresh DCA limit at the same level (classic-grid re-arm).
///   - When all batches are gone, one bar of config.Timeframe is waited (OpenAfterTime gate)
///     before a fresh market anchor. UseStaticRange freezes the first anchor's protective bound.
///   - TakeProfitEnabled + TakeProfitTargetUsd: when aggregate realised + mark-to-market
///     unrealised reaches the target, everything is closed at market and the sim stops.
///
/// Live-only plumbing deliberately skipped (exchange-failure handling, not strategy): order-ID
/// recovery / reconcile, phantom-batch detection, placement cooldown/backoff, exchange min-qty
/// and lot-step rounding, orphan-cancel sweeps, Bybit history glitch guards, Telegram signals.
///
/// The intrabar tick convention (4 ticks/candle) comes from <see cref="CandlePathHelper"/>; the
/// live handler is tick-driven (GetTicker every ~5s), so the 1-minute ticks are the sim
/// equivalent. Fills resolve deterministically in the tick order the path helper produces.
/// </summary>
public class GridFloatSimulator : IStrategySimulator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.GridFloat;

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();

        // Deserialize exactly like GridFloatHandler does (same options), then normalise tiers.
        GridFloatConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<GridFloatConfig>(context.ConfigJson, JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"GridFloat: не удалось разобрать ConfigJson: {ex.Message}");
            return result;
        }

        if (config == null || string.IsNullOrEmpty(config.Symbol))
        {
            result.Warnings.Add("GridFloat: некорректный config — symbol пуст.");
            return result;
        }

        NormalizeTiers(config);

        // Same validity gate as the handler (minus exchange-only checks). Invalid → empty run.
        if (config.Tiers.Count == 0
            || config.Tiers.Any(t => t.UpToPercent <= 0 || t.SizeUsdt <= 0)
            || config.Tiers.Any(t => t.DcaStepPercent is <= 0 || t.TpStepPercent is <= 0)
            || config.DcaStepPercent <= 0 || config.TpStepPercent <= 0
            || config.Tiers[^1].UpToPercent < EffectiveDcaStep(config, config.Tiers[^1])
            || config.Leverage < 1)
        {
            result.Warnings.Add("GridFloat: некорректные параметры конфигурации (Tiers/DcaStep/TpStep/Leverage) — прогон пуст.");
            return result;
        }

        var candles = context.PathCandles;
        if (candles.Count == 0)
        {
            result.Warnings.Add("GridFloat: пустой набор свечей — нечего симулировать.");
            return result;
        }

        var isLong = config.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var makerFee = context.MakerFeeRate;
        var takerFee = context.TakerFeeRate;
        var tfSpan = SymbolHelper.GetTimeframeSpan(config.Timeframe);
        var tfTicks = tfSpan.Ticks > 0 ? tfSpan.Ticks : TimeSpan.FromHours(1).Ticks;

        // Reuse the live state DTO so the copied ComputeDcaLevels helper works verbatim.
        var state = new GridFloatState { IsLong = isLong };

        // Running tallies.
        decimal grossRealized = 0m;   // realised PnL before any fees (gross, on closes)
        decimal feesTotal = 0m;       // every fill's fee (opens + dca + closes)
        decimal closeFeesTotal = 0m;  // fees on closing fills only (for TP-target parity w/ handler)
        decimal maxNotional = 0m;     // peak open notional (fill-price basis)
        int completedCycles = 0;
        DateTime? lastProcessedTfClose = null;
        DateTime? stopTime = null;

        decimal RealizedCash() => grossRealized - feesTotal;
        decimal Unrealized(decimal price) => state.Batches.Sum(b =>
            isLong ? b.Qty * (price - b.FillPrice) : b.Qty * (b.FillPrice - price));
        void TrackNotional()
        {
            var n = state.Batches.Sum(b => b.Qty * b.FillPrice);
            if (n > maxNotional) maxNotional = n;
        }

        // ── Anchor open (market at the closed timeframe bar's close) ──────────────
        void OpenAnchor(decimal closePrice, DateTime when)
        {
            var anchorSize = config.Tiers[0].SizeUsdt;
            var fillPrice = closePrice;
            if (fillPrice <= 0) return;
            var fillQty = anchorSize / fillPrice;
            if (fillQty <= 0) return;

            state.AnchorPrice = fillPrice;

            if (config.UseStaticRange && !state.StaticBoundsInitialized)
            {
                var maxRangePct = config.Tiers[^1].UpToPercent;
                if (isLong) state.StaticLowerBound = fillPrice * (1m - maxRangePct / 100m);
                else state.StaticUpperBound = fillPrice * (1m + maxRangePct / 100m);
                state.StaticBoundsInitialized = true;
            }

            var tpStep = EffectiveTpStep(config, fillPrice, fillPrice);
            var tpPrice = ComputeTp(fillPrice, tpStep, isLong);
            state.Batches.Add(new GridFloatBatch
            {
                LevelIdx = 0,
                FillPrice = fillPrice,
                Qty = fillQty,
                TpPrice = tpPrice,
                FilledAt = when,
            });

            var notional = fillQty * fillPrice;
            var fee = notional * takerFee; // anchor is a market (taker) fill
            feesTotal += fee;
            result.Trades.Add(new SimTrade
            {
                Time = when,
                Side = isLong ? "Long" : "Short",
                Action = "Open",
                Price = fillPrice,
                Quantity = fillQty,
                NotionalUsd = notional,
                FeeUsd = fee,
                PnlUsd = null,
                Reason = "Anchor",
            });
            TrackNotional();

            HealMissingDcas(); // seed the initial ladder immediately
        }

        // ── Re-arm every free grid slot inside the current range with a resting DCA ──
        void HealMissingDcas()
        {
            if (state.AnchorPrice <= 0) return;
            var levels = ComputeDcaLevels(config, state);
            if (levels.Count == 0) return;
            var occupied = new HashSet<int>();
            foreach (var b in state.Batches) occupied.Add(b.LevelIdx);
            foreach (var d in state.DcaOrders) occupied.Add(d.LevelIdx);

            foreach (var (idx, price, tier) in levels)
            {
                if (occupied.Contains(idx)) continue;
                if (price <= 0) continue;
                var qty = tier.SizeUsdt / price;
                if (qty <= 0) continue;
                state.DcaOrders.Add(new GridFloatDcaOrder { LevelIdx = idx, Price = price, Qty = qty });
            }
        }

        // ── Full close (all batches gone) → cooldown + cycle count ──
        void OnFullClose(DateTime when)
        {
            state.DcaOrders.Clear();
            state.AnchorPrice = 0;
            state.OpenAfterTime = when; // wait one bar of config.Timeframe before re-anchoring
            completedCycles++;
        }

        // ── Anchor gate run at each closed timeframe bar ──
        void AnchorCheck(DateTime tfClose, decimal closePrice)
        {
            if (lastProcessedTfClose.HasValue && tfClose <= lastProcessedTfClose.Value) return;
            lastProcessedTfClose = tfClose;

            var flat = state.Batches.Count == 0 && state.DcaOrders.Count == 0;

            if (state.OpenAfterTime.HasValue && flat)
            {
                if (tfClose <= state.OpenAfterTime.Value) return; // still inside cooldown
                state.OpenAfterTime = null;
            }

            if (state.Batches.Count == 0 && state.DcaOrders.Count == 0 && !state.OpenAfterTime.HasValue)
                OpenAnchor(closePrice, tfClose);
        }

        // ── Force close everything at market, then stop the sim ──
        void ForceCloseAll(decimal price, DateTime when)
        {
            foreach (var b in state.Batches.ToList())
            {
                var notional = b.Qty * price;
                var gross = isLong ? b.Qty * (price - b.FillPrice) : b.Qty * (b.FillPrice - price);
                var fee = notional * takerFee; // market close
                var net = gross - fee;
                var pnlPct = b.FillPrice > 0
                    ? (isLong ? (price - b.FillPrice) / b.FillPrice * 100m
                              : (b.FillPrice - price) / b.FillPrice * 100m)
                    : 0m;

                grossRealized += gross;
                feesTotal += fee;
                closeFeesTotal += fee;
                result.Trades.Add(new SimTrade
                {
                    Time = when,
                    Side = isLong ? "Long" : "Short",
                    Action = "Close",
                    Price = price,
                    Quantity = b.Qty,
                    NotionalUsd = notional,
                    FeeUsd = fee,
                    PnlUsd = net,
                    PnlPercent = pnlPct,
                    Reason = $"TakeProfitClose#{b.LevelIdx}",
                });
            }
            state.Batches.Clear();
            state.DcaOrders.Clear();
            state.AnchorPrice = 0;
        }

        // ── Process one intrabar tick: DCA fills, TP fills, re-arm, TP-target ──
        // Returns true if the TP target fired (caller must stop).
        bool ProcessTick(decimal price, DateTime when)
        {
            // 1. Resting DCA limits fill when the tick crosses the limit price.
            foreach (var dca in state.DcaOrders.ToList())
            {
                var crossed = isLong ? price <= dca.Price : price >= dca.Price;
                if (!crossed) continue;

                var fillPrice = dca.Price; // resting limit → fills at its own price
                var fillQty = dca.Qty;
                var notional = fillQty * fillPrice;
                var fee = notional * makerFee; // resting limit → maker
                feesTotal += fee;

                var tpStep = EffectiveTpStep(config, state.AnchorPrice, fillPrice);
                var tpPrice = ComputeTp(fillPrice, tpStep, isLong);
                state.Batches.Add(new GridFloatBatch
                {
                    LevelIdx = dca.LevelIdx,
                    FillPrice = fillPrice,
                    Qty = fillQty,
                    TpPrice = tpPrice,
                    FilledAt = when,
                });
                state.DcaOrders.Remove(dca);

                result.Trades.Add(new SimTrade
                {
                    Time = when,
                    Side = isLong ? "Long" : "Short",
                    Action = "Dca",
                    Price = fillPrice,
                    Quantity = fillQty,
                    NotionalUsd = notional,
                    FeeUsd = fee,
                    PnlUsd = null,
                    Reason = $"DCA#{dca.LevelIdx}",
                });
                TrackNotional();
            }

            // 2. Batch TPs fill when the tick crosses the TP price.
            foreach (var batch in state.Batches.ToList())
            {
                var crossed = isLong ? price >= batch.TpPrice : price <= batch.TpPrice;
                if (!crossed) continue;

                var closePrice = batch.TpPrice; // resting reduce-only limit → fills at its price
                var closeQty = batch.Qty;
                var notional = closeQty * closePrice;
                var gross = isLong ? closeQty * (closePrice - batch.FillPrice)
                                   : closeQty * (batch.FillPrice - closePrice);
                var fee = notional * makerFee; // resting limit → maker
                var net = gross - fee;
                var pnlPct = batch.FillPrice > 0
                    ? (isLong ? (closePrice - batch.FillPrice) / batch.FillPrice * 100m
                              : (batch.FillPrice - closePrice) / batch.FillPrice * 100m)
                    : 0m;

                grossRealized += gross;
                feesTotal += fee;
                closeFeesTotal += fee;

                result.Trades.Add(new SimTrade
                {
                    Time = when,
                    Side = isLong ? "Long" : "Short",
                    Action = "TakeProfit",
                    Price = closePrice,
                    Quantity = closeQty,
                    NotionalUsd = notional,
                    FeeUsd = fee,
                    PnlUsd = net,
                    PnlPercent = pnlPct,
                    Reason = $"TP#{batch.LevelIdx}",
                });

                state.Batches.Remove(batch);

                if (state.Batches.Count == 0)
                    OnFullClose(when);
            }

            // 3. Re-arm any freed slot inside the grid (classic-grid oscillation capture).
            HealMissingDcas();

            // 4. Take-profit target: aggregate realised (net, close-fee basis to mirror handler)
            //    plus mark-to-market unrealised. Fires a market close and stops the sim.
            if (config.TakeProfitEnabled && config.TakeProfitTargetUsd > 0m && state.Batches.Count > 0)
            {
                var realizedForTp = grossRealized - closeFeesTotal;
                var totalPnl = realizedForTp + Unrealized(price);
                if (totalPnl >= config.TakeProfitTargetUsd)
                {
                    ForceCloseAll(price, when);
                    stopTime = when;
                    result.Warnings.Add(
                        $"GridFloat: цель TakeProfit ${config.TakeProfitTargetUsd} достигнута — " +
                        $"позиции закрыты по рынку и симуляция остановлена в {when:yyyy-MM-dd HH:mm:ss} UTC.");
                    return true;
                }
            }

            return false;
        }

        // ── Equity sampling ──
        void SampleEquity(DateTime when, decimal price)
        {
            var realized = RealizedCash();
            var unreal = Unrealized(price);
            result.EquityCurve.Add(new EquityPoint
            {
                Time = when,
                RealizedPnlUsd = realized,
                UnrealizedPnlUsd = unreal,
                EquityUsd = realized + unreal,
            });
        }

        // ── Main path walk ──────────────────────────────────────────────────────
        CandleDto? prevCandle = null;
        long prevBucket = long.MinValue;
        int processed = 0;
        decimal lastTickPrice = candles[0].Open;
        DateTime lastTickTime = candles[0].OpenTime;
        bool stopped = false;

        foreach (var candle in candles)
        {
            processed++;
            var bucket = candle.OpenTime.Ticks / tfTicks;

            // A timeframe bar closed when we step into a new bucket → run the anchor gate on
            // the just-completed bar (its close price / close time). Mirrors the handler acting
            // on the last CLOSED timeframe candle.
            if (prevCandle != null && bucket != prevBucket)
                AnchorCheck(prevCandle.CloseTime, prevCandle.Close);

            // Equity sample at each hourly boundary candle (before its ticks).
            if (candle.OpenTime.Minute == 0)
                SampleEquity(candle.OpenTime, candle.Open);

            foreach (var pp in CandlePathHelper.GetTicks(candle))
            {
                lastTickPrice = pp.Price;
                lastTickTime = pp.Time;
                if (ProcessTick(pp.Price, pp.Time))
                {
                    stopped = true;
                    break;
                }
            }

            prevCandle = candle;
            prevBucket = bucket;
            if (stopped) break;
        }

        // Final equity point at the last observed tick.
        SampleEquity(lastTickTime, lastTickPrice);

        // ── Summary ──────────────────────────────────────────────────────────────
        var closedTrades = result.Trades
            .Where(t => t.Action is "TakeProfit" or "Close" && t.PnlUsd.HasValue)
            .ToList();
        var winning = closedTrades.Count(t => t.PnlUsd!.Value > 0m);
        var losing = closedTrades.Count(t => t.PnlUsd!.Value < 0m);
        var closedCount = winning + losing;

        // Max drawdown = deepest peak-to-trough of EquityUsd across the curve.
        decimal peak = decimal.MinValue;
        decimal maxDd = 0m;
        foreach (var pt in result.EquityCurve)
        {
            if (pt.EquityUsd > peak) peak = pt.EquityUsd;
            var dd = peak - pt.EquityUsd;
            if (dd > maxDd) maxDd = dd;
        }

        var endUnrealized = Unrealized(lastTickPrice);

        result.Summary = new SimulationRunSummary
        {
            TotalTrades = result.Trades.Count,
            WinningTrades = winning,
            LosingTrades = losing,
            WinRate = closedCount > 0 ? Math.Round((decimal)winning / closedCount * 100m, 2) : 0m,
            GrossPnlUsd = grossRealized,
            FeesUsd = feesTotal,
            FundingPnlUsd = 0m,
            NetPnlUsd = grossRealized - feesTotal, // + 0 funding
            MaxDrawdownUsd = maxDd,
            MaxDrawdownPercent = maxNotional > 0 ? Math.Round(maxDd / maxNotional * 100m, 4) : 0m,
            MaxNotionalUsd = maxNotional,
            OpenPositionsAtEnd = state.Batches.Count,
            UnrealizedPnlAtEndUsd = endUnrealized,
            CompletedCycles = completedCycles,
            StartTime = candles[0].OpenTime,
            EndTime = stopTime ?? candles[^1].CloseTime,
            PathCandlesProcessed = processed,
        };

        return result;
    }

    // ────────────────────────── Copied strategy helpers ──────────────────────────
    // Verbatim from GridFloatHandler so the sim computes grid levels / tiers / TP identically.

    private static List<(int idx, decimal price, GridFloatTier tier)> ComputeDcaLevels(
        GridFloatConfig config, GridFloatState state)
    {
        var list = new List<(int idx, decimal price, GridFloatTier tier)>();
        if (state.AnchorPrice <= 0 || config.Tiers.Count == 0) return list;

        const int safetyCeiling = 500;
        int k = 0;
        decimal prevTopPct = 0m;

        foreach (var tier in config.Tiers)
        {
            var stepPct = EffectiveDcaStep(config, tier);
            if (stepPct <= 0) continue;

            const decimal eps = 1e-9m;
            var offsetPct = prevTopPct + stepPct;
            while (offsetPct <= tier.UpToPercent + eps && k < safetyCeiling)
            {
                decimal price = state.IsLong
                    ? state.AnchorPrice * (1m - offsetPct / 100m)
                    : state.AnchorPrice * (1m + offsetPct / 100m);

                if (price <= 0) return list;

                if (config.UseStaticRange && state.StaticBoundsInitialized)
                {
                    if (state.IsLong && price < state.StaticLowerBound) return list;
                    if (!state.IsLong && price > state.StaticUpperBound) return list;
                }

                k++;
                list.Add((k, price, tier));
                offsetPct += stepPct;
            }

            prevTopPct = tier.UpToPercent;
            if (k >= safetyCeiling) break;
        }

        return list;
    }

    private static decimal EffectiveDcaStep(GridFloatConfig config, GridFloatTier tier)
        => tier.DcaStepPercent is > 0 ? tier.DcaStepPercent.Value : config.DcaStepPercent;

    private static decimal EffectiveTpStep(GridFloatConfig config, decimal anchorPrice, decimal fillPrice)
    {
        if (anchorPrice <= 0 || config.Tiers.Count == 0) return config.TpStepPercent;
        var offsetPct = Math.Abs(fillPrice - anchorPrice) / anchorPrice * 100m;
        var tier = config.Tiers.FirstOrDefault(t => offsetPct <= t.UpToPercent) ?? config.Tiers[^1];
        return tier.TpStepPercent is > 0 ? tier.TpStepPercent.Value : config.TpStepPercent;
    }

    private static void NormalizeTiers(GridFloatConfig config)
    {
        if ((config.Tiers == null || config.Tiers.Count == 0)
            && config.BaseSizeUsdt is > 0
            && config.RangePercent is > 0)
        {
            config.Tiers = new List<GridFloatTier>
            {
                new() { UpToPercent = config.RangePercent.Value, SizeUsdt = config.BaseSizeUsdt.Value }
            };
        }

        config.Tiers ??= new List<GridFloatTier>();
        config.Tiers = config.Tiers
            .Where(t => t.UpToPercent > 0 && t.SizeUsdt > 0)
            .OrderBy(t => t.UpToPercent)
            .ToList();
    }

    private static decimal ComputeTp(decimal fillPrice, decimal tpPercent, bool isLong)
        => isLong
            ? fillPrice * (1m + tpPercent / 100m)
            : fillPrice * (1m - tpPercent / 100m);
}
