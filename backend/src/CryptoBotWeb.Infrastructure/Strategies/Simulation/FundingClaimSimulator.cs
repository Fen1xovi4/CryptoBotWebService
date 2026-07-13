using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest simulator for the FundingClaim strategy. Mirrors
/// <see cref="FundingClaimHandler"/>'s Idle → InPosition machine:
///   • Idle: CheckBeforeFundingMinutes before a settlement, if |rate|% is within
///     [FcMinFundingRatePercent, FcMaxFundingRatePercent] → market-open on the side
///     that RECEIVES funding (rate>0 → Short, rate<0 → Long).
///   • Hold through the settlement and collect the payment. Do NOT auto-close: the
///     handler keeps the position and re-validates funding at the next check window;
///     it closes only when a stop-loss triggers (outside its grace window) or the
///     funding rate falls below the minimum / flips sign at that check.
///   • Stop-loss disabled within ±FcSlGraceMinutes of a funding event. On SL hit, the
///     symbol enters a global re-entry cooldown of FcSlCooldownHours.
///
/// Deterministic and pure. ctx.ConfigJson carries FundingClaimConfig fields PLUS the
/// workspace-level WorkspaceFundingClaimConfig "Fc*" fields merged at the top level.
///
/// Approximations vs. the live handler:
///   • The rate known at a check window is that FundingEvent's own Rate (predicted ≈ settled).
///   • FcLeverage affects margin only — ignored (PnL is notional-based).
///   • Auto-rotation / blacklist not modelled; single fixed symbol → SL cooldown is global.
///   • Funding is charged on the qty held at settlement, valued at that tick's mark price.
/// </summary>
public class FundingClaimSimulator : IStrategySimulator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.FundingClaim;

    private enum Phase { Idle, InPosition }

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();
        result.Warnings.AddRange(context.Warnings);

        var config = JsonSerializer.Deserialize<FundingClaimConfig>(context.ConfigJson, JsonOptions)
                     ?? new FundingClaimConfig();
        var ws = JsonSerializer.Deserialize<WorkspaceFundingClaimConfig>(context.ConfigJson, JsonOptions)
                 ?? new WorkspaceFundingClaimConfig();

        var candles = context.PathCandles;
        var events = context.FundingEvents;

        result.Summary.StartTime = candles.Count > 0 ? candles[0].OpenTime : default;
        result.Summary.EndTime = candles.Count > 0 ? candles[^1].CloseTime : default;
        result.Summary.PathCandlesProcessed = candles.Count;

        if (config.AutoRotateTicker)
            result.Warnings.Add("авто-ротация не моделируется — символ фиксирован");

        if (candles.Count == 0)
            return result;

        // ── Accumulators ──
        decimal realizedGross = 0m, feesTotal = 0m, fundingTotal = 0m;
        decimal maxNotional = 0m, maxDrawdown = 0m, peakEquity = 0m;
        bool peakInit = false;
        int cycleCount = 0, wins = 0, losses = 0;

        // ── Position / cycle state ──
        var phase = Phase.Idle;
        decimal posQty = 0m, posAvg = 0m;
        string? posSide = null;
        decimal entryFeeAccum = 0m, cycleFunding = 0m;
        DateTime? lastFundingPaidAt = null;
        DateTime? slCooldownUntil = null;   // global (single fixed symbol)
        int ne = 0;                          // next un-settled event index
        int evaluatedEvtIdx = -1;            // event index already committed/evaluated
        bool stopped = false;

        // ── Tick cursor ──
        DateTime curTime = default;
        decimal curPrice = 0m;
        decimal lastUnreal = 0m, lastRealizedNet = 0m, lastEquity = 0m;

        void OpenMarket(string dir)
        {
            var notional = ws.FcSizeUsdt;
            var qty = curPrice > 0m ? notional / curPrice : 0m;
            if (qty <= 0m) return;
            var fee = notional * context.TakerFeeRate;
            feesTotal += fee;
            posSide = dir;
            posQty = qty;
            posAvg = curPrice;
            entryFeeAccum = fee;
            cycleFunding = 0m;
            if (notional > maxNotional) maxNotional = notional;
            result.Trades.Add(new SimTrade
            {
                Time = curTime,
                Side = dir,
                Action = "Open",
                Price = curPrice,
                Quantity = qty,
                NotionalUsd = notional,
                FeeUsd = fee,
                PnlUsd = null,
                Reason = "funding claim entry"
            });
        }

        void CollectFunding(decimal rate)
        {
            if (posQty <= 0m) return;
            var notional = posQty * curPrice;
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

            posSide = null; posQty = 0m; posAvg = 0m;
            entryFeeAccum = 0m; cycleFunding = 0m;
            lastFundingPaidAt = null;
            evaluatedEvtIdx = -1;
            phase = Phase.Idle;
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
                    // Settle every funding event the tick has reached; collect while holding.
                    while (ne < events.Count && curTime >= events[ne].Timestamp)
                    {
                        if (phase == Phase.InPosition && posQty > 0m)
                        {
                            CollectFunding(events[ne].Rate);
                            lastFundingPaidAt = events[ne].Timestamp;
                        }
                        ne++;
                    }

                    FundingEventDto? up = ne < events.Count ? events[ne] : null;

                    if (phase == Phase.Idle)
                    {
                        if (slCooldownUntil.HasValue && slCooldownUntil.Value <= curTime)
                            slCooldownUntil = null;

                        bool cooled = !slCooldownUntil.HasValue || slCooldownUntil.Value <= curTime;
                        if (up != null && cooled)
                        {
                            bool inWindow = curTime >= up.Timestamp.AddMinutes(-config.CheckBeforeFundingMinutes);
                            if (inWindow)
                            {
                                var ratePct = Math.Abs(up.Rate * 100m);
                                bool rateOk = ratePct >= ws.FcMinFundingRatePercent
                                              && (ws.FcMaxFundingRatePercent <= 0m || ratePct <= ws.FcMaxFundingRatePercent);
                                if (rateOk)
                                {
                                    // Open on the side that receives funding.
                                    string dir = up.Rate > 0m ? "Short" : "Long";
                                    OpenMarket(dir);
                                    if (posQty > 0m)
                                    {
                                        evaluatedEvtIdx = ne; // committed to this event; don't re-check it
                                        phase = Phase.InPosition;
                                    }
                                }
                            }
                        }
                    }
                    else // InPosition
                    {
                        // ── Stop-loss with funding grace window ──
                        if (phase == Phase.InPosition && ws.FcStopLossPercent > 0m && posAvg > 0m)
                        {
                            bool inGrace = false;
                            if (ws.FcSlGraceMinutes > 0)
                            {
                                if (up != null)
                                {
                                    var minsUntil = (up.Timestamp - curTime).TotalMinutes;
                                    if (minsUntil >= 0 && minsUntil < ws.FcSlGraceMinutes) inGrace = true;
                                }
                                if (!inGrace && lastFundingPaidAt.HasValue)
                                {
                                    var minsSince = (curTime - lastFundingPaidAt.Value).TotalMinutes;
                                    if (minsSince >= 0 && minsSince < ws.FcSlGraceMinutes) inGrace = true;
                                }
                            }

                            decimal adverse = posSide == "Long"
                                ? (posAvg - curPrice) / posAvg * 100m
                                : (curPrice - posAvg) / posAvg * 100m;

                            if (adverse >= ws.FcStopLossPercent && !inGrace)
                            {
                                CloseCycle("StopLoss", "StopLoss");
                                if (ws.FcSlCooldownHours > 0)
                                    slCooldownUntil = curTime.AddHours(ws.FcSlCooldownHours);
                            }
                        }

                        // ── Check-window re-validation for the upcoming (not-yet-entered) event ──
                        if (phase == Phase.InPosition && up != null && ne != evaluatedEvtIdx)
                        {
                            bool inWindow = curTime >= up.Timestamp.AddMinutes(-config.CheckBeforeFundingMinutes);
                            if (inWindow)
                            {
                                var ratePct = Math.Abs(up.Rate * 100m);
                                bool signOk = (posSide == "Long" && up.Rate < 0m)
                                              || (posSide == "Short" && up.Rate > 0m);
                                bool rateOk = ratePct >= ws.FcMinFundingRatePercent;
                                if (!rateOk || !signOk)
                                    CloseCycle("Close", !signOk ? "FundingSignFlipped" : "FundingBelowMin");
                                else
                                    evaluatedEvtIdx = ne; // funding still good — hold through this event
                            }
                        }
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
