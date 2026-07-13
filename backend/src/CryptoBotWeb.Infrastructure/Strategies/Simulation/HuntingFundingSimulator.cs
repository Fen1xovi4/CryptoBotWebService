using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest simulator for the HuntingFunding strategy. Mirrors
/// <see cref="HuntingFundingHandler"/>'s phase machine per funding event:
/// WaitingForFunding → OrdersPlaced → InPosition → Cooldown.
///
/// Deterministic and pure — everything comes from <see cref="SimulationContext"/>:
///   • ctx.PathCandles  — 1m price path (4 ticks/candle via CandlePathHelper).
///   • ctx.FundingEvents — historical settlements (Rate = raw fraction, ascending).
///   • ctx.ConfigJson    — HuntingFundingConfig fields PLUS the workspace-level
///                         WorkspaceHuntingFundingConfig fields (fundingRateMin/Max)
///                         merged at the top level of the same JSON object.
///
/// Approximations vs. the live handler (documented in the task report):
///   • The rate known "before" a settlement is that FundingEvent's own Rate (the
///     predicted rate ≈ the settled rate).
///   • Auto-rotation / blacklist is NOT modelled — the symbol is fixed for the window.
///   • Funding is charged on the qty actually held at the settlement tick, valued at
///     that tick's mark price.
/// </summary>
public class HuntingFundingSimulator : IStrategySimulator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.HuntingFunding;

    private enum Phase { Waiting, Armed, InPosition }

    private sealed class OrderRec
    {
        public decimal Limit;
        public decimal Qty;
        public bool Buy;
        public bool Filled;
    }

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();
        result.Warnings.AddRange(context.Warnings);

        var config = JsonSerializer.Deserialize<HuntingFundingConfig>(context.ConfigJson, JsonOptions)
                     ?? new HuntingFundingConfig();
        var wsCfg = JsonSerializer.Deserialize<WorkspaceHuntingFundingConfig>(context.ConfigJson, JsonOptions)
                    ?? new WorkspaceHuntingFundingConfig();

        // Workspace magnitude window on |rate*100| (percent). Handler swaps if inverted.
        decimal rangeMin = wsCfg.FundingRateMin;
        decimal rangeMax = wsCfg.FundingRateMax;
        if (rangeMax < rangeMin) (rangeMin, rangeMax) = (rangeMax, rangeMin);

        var candles = context.PathCandles;
        var events = context.FundingEvents;

        result.Summary.StartTime = candles.Count > 0 ? candles[0].OpenTime : default;
        result.Summary.EndTime = candles.Count > 0 ? candles[^1].CloseTime : default;
        result.Summary.PathCandlesProcessed = candles.Count;

        if (config.AutoRotateTicker)
            result.Warnings.Add("авто-ротация не моделируется — символ фиксирован");

        if (candles.Count == 0)
            return result;

        // ── Accumulators (money in USD) ──
        decimal realizedGross = 0m;   // gross price PnL on closes, before fees/funding
        decimal feesTotal = 0m;       // all maker/taker fees
        decimal fundingTotal = 0m;    // funding payments collected
        decimal maxNotional = 0m;     // peak open notional
        decimal maxDrawdown = 0m;
        decimal peakEquity = 0m;
        bool peakInit = false;
        int cycleCount = 0, wins = 0, losses = 0;

        // ── Position / cycle state ──
        var phase = Phase.Waiting;
        var orders = new List<OrderRec>();
        decimal posQty = 0m, posUsdt = 0m, posAvg = 0m;
        string? posSide = null;
        decimal entryFeeAccum = 0m, cycleFunding = 0m;
        bool finalized = false, fundingCollected = false;
        DateTime settlementTime = default, timeLimitAt = default;
        int evtIdx = 0;        // next event to consider while Waiting
        int curEvtIdx = -1;    // event this cycle is armed against
        bool stopped = false;

        // ── Tick cursor (captured by local functions) ──
        DateTime curTime = default;
        decimal curPrice = 0m;
        decimal lastUnreal = 0m, lastRealizedNet = 0m, lastEquity = 0m;

        string? Eligible(decimal rate)
        {
            var ratePct = Math.Abs(rate * 100m);
            if (ratePct < rangeMin || ratePct > rangeMax) return null;
            if (rate < 0 && config.EnableLong && ratePct >= config.MinFundingLong) return "Long";
            if (rate > 0 && config.EnableShort && ratePct >= config.MinFundingShort) return "Short";
            return null;
        }

        void ResetCycle(bool advance)
        {
            posQty = 0m; posUsdt = 0m; posAvg = 0m; posSide = null;
            entryFeeAccum = 0m; cycleFunding = 0m;
            finalized = false; fundingCollected = false;
            orders.Clear();
            phase = Phase.Waiting;
            if (advance) evtIdx = curEvtIdx + 1;
            curEvtIdx = -1;
        }

        void FillOrder(OrderRec o)
        {
            o.Filled = true;
            var notional = o.Limit * o.Qty;
            var fee = notional * context.MakerFeeRate;
            feesTotal += fee;
            entryFeeAccum += fee;
            bool first = posQty == 0m;
            posQty += o.Qty;
            posUsdt += notional;
            posAvg = posUsdt / posQty;
            var openNotional = posAvg * posQty;
            if (openNotional > maxNotional) maxNotional = openNotional;
            result.Trades.Add(new SimTrade
            {
                Time = curTime,
                Side = posSide ?? "",
                Action = first ? "Open" : "Dca",
                Price = o.Limit,
                Quantity = o.Qty,
                NotionalUsd = notional,
                FeeUsd = fee,
                PnlUsd = null,
                Reason = "limit fill"
            });
        }

        void CollectFunding(decimal rate)
        {
            if (posQty <= 0m) return;
            var notional = posQty * curPrice;
            // Positive rate: longs pay. Long → −rate·notional, Short → +rate·notional.
            var payment = posSide == "Long" ? -rate * notional : rate * notional;
            fundingTotal += payment;
            cycleFunding += payment;
            result.Trades.Add(new SimTrade
            {
                Time = curTime,
                Side = posSide ?? "",
                Action = "Funding",
                Price = curPrice,
                Quantity = posQty,
                NotionalUsd = notional,
                FeeUsd = 0m,
                PnlUsd = payment,
                Reason = $"funding rate={rate:P4}"
            });
        }

        void CloseCycle(string action, string reason)
        {
            var side = posSide ?? "Long";
            var qty = posQty;
            var avg = posAvg;
            var grossPnl = side == "Long" ? (curPrice - avg) * qty : (avg - curPrice) * qty;
            var closeNotional = curPrice * qty;
            var closeFee = closeNotional * context.TakerFeeRate;
            realizedGross += grossPnl;
            feesTotal += closeFee;
            var pnlPct = avg > 0m
                ? (side == "Long" ? (curPrice - avg) / avg : (avg - curPrice) / avg) * 100m
                : 0m;
            result.Trades.Add(new SimTrade
            {
                Time = curTime,
                Side = side,
                Action = action,
                Price = curPrice,
                Quantity = qty,
                NotionalUsd = closeNotional,
                FeeUsd = closeFee,
                PnlUsd = grossPnl - closeFee,
                PnlPercent = Math.Round(pnlPct, 4),
                Reason = reason
            });
            var cycleNet = grossPnl - entryFeeAccum - closeFee + cycleFunding;
            if (cycleNet > 0m) wins++; else losses++;
            cycleCount++;
            ResetCycle(advance: true);
        }

        // ── Main tick walk ──
        foreach (var candle in candles)
        {
            var ticks = CandlePathHelper.GetTicks(candle);
            bool hourly = candle.OpenTime.Minute == 0;

            for (int ti = 0; ti < ticks.Length; ti++)
            {
                curTime = ticks[ti].Time;
                curPrice = ticks[ti].Price;

                if (!stopped)
                {
                    if (phase == Phase.Waiting)
                    {
                        // Drop events whose settlement already passed unarmed.
                        while (evtIdx < events.Count && events[evtIdx].Timestamp <= curTime)
                            evtIdx++;

                        if (evtIdx < events.Count)
                        {
                            var e = events[evtIdx];
                            var dir = Eligible(e.Rate);
                            // Tick resolution is ~15s (4 ticks per 1m candle). A lead window
                            // shorter than one tick (default SecondsBeforeFunding=10) can fall
                            // entirely between ticks, so the event would be dropped at the
                            // settlement tick without ever arming. Clamp the lead to one tick.
                            var leadSeconds = Math.Max(config.SecondsBeforeFunding, 15);
                            if (dir != null && curTime >= e.Timestamp.AddSeconds(-leadSeconds))
                            {
                                // Place the ladder of limit orders at the reference (current) price.
                                posSide = dir;
                                curEvtIdx = evtIdx;
                                settlementTime = e.Timestamp;
                                fundingCollected = false;
                                finalized = false;
                                orders.Clear();
                                foreach (var level in config.Levels)
                                {
                                    decimal limit = dir == "Long"
                                        ? curPrice * (1 - level.OffsetPercent / 100m)
                                        : curPrice * (1 + level.OffsetPercent / 100m);
                                    if (limit <= 0m) continue;
                                    var qty = level.SizeUsdt / limit;
                                    if (qty <= 0m) continue;
                                    orders.Add(new OrderRec { Limit = limit, Qty = qty, Buy = dir == "Long", Filled = false });
                                }
                                phase = Phase.Armed;
                            }
                        }
                    }

                    if (phase == Phase.Armed || phase == Phase.InPosition)
                    {
                        // Resting-limit fills (crossing) until finalized.
                        if (!finalized)
                        {
                            foreach (var o in orders)
                            {
                                if (o.Filled) continue;
                                bool cross = o.Buy ? curPrice <= o.Limit : curPrice >= o.Limit;
                                if (cross) FillOrder(o);
                            }
                        }

                        // Funding settlement — snapshot approximation. The ladder is placed
                        // seconds before funding precisely so fills land AT the settlement;
                        // 1m data can't resolve whether a touch happened at second 59 or 61.
                        // So the snapshot is taken at the END of the settlement minute
                        // (+45s = its last tick), crediting fills anywhere inside that minute.
                        // Checked after the fill loop so a fill on the snapshot tick counts.
                        if (curEvtIdx >= 0 && !fundingCollected
                            && curEvtIdx < events.Count && curTime >= events[curEvtIdx].Timestamp.AddSeconds(45))
                        {
                            CollectFunding(events[curEvtIdx].Rate);
                            fundingCollected = true;
                        }

                        // TP / SL on the averaged entry — active as soon as we hold anything
                        // (mirrors the handler's early-exit during OrdersPlaced).
                        if (posQty > 0m)
                        {
                            decimal tp = posSide == "Long"
                                ? posAvg * (1 + config.TakeProfitPercent / 100m)
                                : posAvg * (1 - config.TakeProfitPercent / 100m);
                            decimal sl = posSide == "Long"
                                ? posAvg * (1 - config.StopLossPercent / 100m)
                                : posAvg * (1 + config.StopLossPercent / 100m);
                            bool tpHit = posSide == "Long" ? curPrice >= tp : curPrice <= tp;
                            bool slHit = posSide == "Long" ? curPrice <= sl : curPrice >= sl;
                            if (tpHit) CloseCycle("TakeProfit", "TakeProfit");
                            else if (slHit) CloseCycle("StopLoss", "StopLoss");
                        }

                        // Finalization: all filled OR post-funding wait (settlement + 60s) elapsed.
                        if (phase != Phase.Waiting && !finalized)
                        {
                            bool allFilled = orders.Count > 0 && orders.All(o => o.Filled);
                            bool postFundingReady = curTime > settlementTime.AddSeconds(60);
                            if (allFilled || postFundingReady)
                            {
                                finalized = true;
                                if (posQty <= 0m)
                                {
                                    // Missed window — no fills. Skip to next event.
                                    ResetCycle(advance: true);
                                }
                                else
                                {
                                    phase = Phase.InPosition;
                                    timeLimitAt = settlementTime.AddMinutes(config.CloseAfterMinutes);
                                }
                            }
                        }

                        // Time-limit force close after funding.
                        if (phase == Phase.InPosition && posQty > 0m && curTime >= timeLimitAt)
                            CloseCycle("Close", "TimeLimit");
                    }

                    if (config.MaxCycles > 0 && cycleCount >= config.MaxCycles)
                        stopped = true;
                }

                // ── Equity / drawdown, every tick ──
                lastUnreal = posQty > 0m
                    ? (posSide == "Long" ? (curPrice - posAvg) * posQty : (posAvg - curPrice) * posQty)
                    : 0m;
                lastRealizedNet = realizedGross - feesTotal + fundingTotal;
                lastEquity = lastRealizedNet + lastUnreal;

                if (!peakInit) { peakEquity = lastEquity; peakInit = true; }
                if (lastEquity > peakEquity) peakEquity = lastEquity;
                var dd = peakEquity - lastEquity;
                if (dd > maxDrawdown) maxDrawdown = dd;

                if (hourly && ti == 0)
                    result.EquityCurve.Add(new EquityPoint
                    {
                        Time = curTime,
                        RealizedPnlUsd = Math.Round(lastRealizedNet, 8),
                        UnrealizedPnlUsd = Math.Round(lastUnreal, 8),
                        EquityUsd = Math.Round(lastEquity, 8)
                    });
            }
        }

        // Final equity sample.
        result.EquityCurve.Add(new EquityPoint
        {
            Time = curTime,
            RealizedPnlUsd = Math.Round(lastRealizedNet, 8),
            UnrealizedPnlUsd = Math.Round(lastUnreal, 8),
            EquityUsd = Math.Round(lastEquity, 8)
        });

        // ── Summary ──
        var s = result.Summary;
        s.TotalTrades = cycleCount;
        s.WinningTrades = wins;
        s.LosingTrades = losses;
        s.WinRate = cycleCount > 0 ? Math.Round((decimal)wins / cycleCount * 100m, 2) : 0m;
        s.GrossPnlUsd = Math.Round(realizedGross, 2);
        s.FeesUsd = Math.Round(feesTotal, 2);
        s.FundingPnlUsd = Math.Round(fundingTotal, 6);
        s.NetPnlUsd = Math.Round(realizedGross - feesTotal + fundingTotal, 2);
        s.MaxDrawdownUsd = Math.Round(maxDrawdown, 2);
        s.MaxDrawdownPercent = maxNotional > 0m ? Math.Round(maxDrawdown / maxNotional * 100m, 2) : 0m;
        s.MaxNotionalUsd = Math.Round(maxNotional, 2);
        s.OpenPositionsAtEnd = posQty > 0m ? 1 : 0;
        s.UnrealizedPnlAtEndUsd = Math.Round(lastUnreal, 2);
        s.CompletedCycles = cycleCount;

        return result;
    }
}
