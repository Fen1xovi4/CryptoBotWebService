using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest counterpart of <see cref="GridHedgeHandler"/>. Mirrors the phase machine:
///   NotStarted/HedgeOpening → open the static short hedge (HedgeNotionalUsdt, 0 = no hedge)
///   GridArming              → level-0 MARKET buy at the anchor + limit buys every DcaStepPercent
///                             from −step down to −RangePercent (BetUsdt each)
///   Active                  → each grid fill gets a reduce-only limit TP at +TpStepPercent;
///                             level-0 TP fill ladders the working anchor up and re-arms;
///                             deep-level TP fill re-arms the same absolute price.
///   ExitingUp / ExitingDown → whole-bot exits pinned to StartAnchor
///                             (price ≥ +UpperExitPercent → take-profit; price ≤ −RangePercent → stop-loss)
///
/// The live handler runs ONE cycle per Start, so after the exit closes everything the sim ends.
///
/// Tick model: the live handler is tick-driven (GetTicker every ~5s). The sim walks ctx.PathCandles
/// (1m ascending) with 4 sub-ticks per candle via <see cref="CandlePathHelper.GetTicks"/>; each
/// sub-tick is one "poll". Market fills take the tick price and the taker fee; resting limit orders
/// fill when the tick crosses (buy: tick ≤ limit, sell: tick ≥ limit) at the limit price with the
/// maker fee. Fees come from ctx.MakerFeeRate / ctx.TakerFeeRate.
///
/// Approximations (see engine report warnings): funding on the hedge leg is ignored; positionIdx /
/// PositionMode plumbing is irrelevant in the sim and skipped; exchange cooldowns / retry / failure
/// counters are dropped (sim placements always succeed); the level-0 market re-entry after a ladder
/// fills at the (TP) anchor rather than the next tick's live price; hedge in CrossTicker mode is
/// marked/closed at 1m candle resolution on the second series.
/// </summary>
public class GridHedgeSimulator : IStrategySimulator
{
    public string StrategyType => StrategyTypes.GridHedge;

    // Mirror GridHedgeHandler.JsonOptions exactly so a live bot's ConfigJson deserializes identically.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();

        GridHedgeConfig? config;
        try { config = JsonSerializer.Deserialize<GridHedgeConfig>(context.ConfigJson, JsonOptions); }
        catch (Exception ex)
        {
            result.Warnings.Add($"GridHedge: ConfigJson не распарсился: {ex.Message}");
            return result;
        }
        if (config == null) { result.Warnings.Add("GridHedge: ConfigJson = null"); return result; }

        if (config.RangePercent <= 0 || config.UpperExitPercent <= 0
            || config.DcaStepPercent <= 0 || config.TpStepPercent <= 0 || config.BetUsdt <= 0)
        {
            result.Warnings.Add("GridHedge: некорректная конфигурация (Range/Upper/Step/Tp/Bet должны быть > 0)");
            return result;
        }

        var candles = context.PathCandles;
        if (candles == null || candles.Count == 0)
        {
            result.Warnings.Add("GridHedge: пустой PathCandles — нечего симулировать");
            return result;
        }

        new Engine(context, config, result).Run();
        return result;
    }

    // ────────────────────────── Engine ──────────────────────────

    private sealed class Engine
    {
        private readonly SimulationContext _ctx;
        private readonly GridHedgeConfig _cfg;
        private readonly SimulationRunResult _res;
        private readonly List<CandleDto> _candles;
        private readonly bool _cross;
        private readonly Dictionary<DateTime, CandleDto> _secondMap = new();

        private readonly decimal _maker;
        private readonly decimal _taker;

        // ── running position state ──
        private decimal _anchor;        // working anchor (ladders up)
        private decimal _startAnchor;   // pinned anchor for exit triggers
        private decimal _hedgeQty;
        private decimal _hedgeEntry;
        private readonly List<OpenBatch> _batches = new();
        private readonly List<Pending> _pending = new();
        private bool _marketEntryOpened;

        // ── accounting ──
        private decimal _realizedGross;
        private decimal _totalFees;
        private int _cycles;
        private decimal _maxNotional;
        private bool _done;
        private string? _endReason;

        public Engine(SimulationContext ctx, GridHedgeConfig cfg, SimulationRunResult res)
        {
            _ctx = ctx;
            _cfg = cfg;
            _res = res;
            _candles = ctx.PathCandles;
            _maker = ctx.MakerFeeRate;
            _taker = ctx.TakerFeeRate;

            _cross = cfg.Mode == GridHedgeMode.CrossTicker;
            if (_cross)
            {
                if (ctx.SecondSymbolPathCandles == null || ctx.SecondSymbolPathCandles.Count == 0)
                {
                    _res.Warnings.Add(
                        "GridHedge CrossTicker: SecondSymbolPathCandles отсутствует — хедж оценивается по основной серии (приближение).");
                    _cross = false;
                }
                else
                {
                    foreach (var c in ctx.SecondSymbolPathCandles)
                        _secondMap[c.OpenTime] = c;
                }
            }

            if (cfg.PositionMode == GridHedgePositionMode.Hedge)
                _res.Warnings.Add("GridHedge: PositionMode влияет только на биржевую обвязку — в симуляции игнорируется.");
        }

        public void Run()
        {
            // ── Open: anchor at first tick, open hedge, arm grid (compressed into t=0). ──
            var first = _candles[0];
            var p0 = CandlePathHelper.GetTicks(first)[0].Price; // = first candle Open
            _anchor = p0;
            _startAnchor = p0;

            OpenHedge(first.OpenTime, p0);
            ArmGrid(first.OpenTime, p0);

            // ── Walk every sub-tick. ──
            foreach (var candle in _candles)
            {
                foreach (var tick in CandlePathHelper.GetTicks(candle))
                {
                    ProcessTick(candle, tick.Time, tick.Price);
                    if (_done) break;
                }

                if (!_done)
                    UpdateMaxNotional(candle);

                // Hourly equity boundary.
                if (candle.OpenTime.Minute == 0)
                    RecordEquity(candle);

                if (_done) break;
            }

            // Final equity point at the last processed candle.
            RecordEquity(_candles[^1], force: true);
            BuildSummary();
        }

        // ── hedge open (market short, taker) ──
        private void OpenHedge(DateTime time, decimal gridP0)
        {
            var hedgeP0 = HedgeOpenPrice(gridP0);
            _hedgeEntry = hedgeP0;

            if (_cfg.HedgeNotionalUsdt <= 0 || hedgeP0 <= 0)
            {
                _hedgeQty = 0;
                return;
            }

            _hedgeQty = _cfg.HedgeNotionalUsdt / hedgeP0;
            var fee = _cfg.HedgeNotionalUsdt * _taker;
            AddTrade(time, "Short", "HedgeOpen", hedgeP0, _hedgeQty, fee, grossPnl: null,
                $"Хедж SHORT notional={_cfg.HedgeNotionalUsdt}");
        }

        // ── grid arming: level-0 market buy + limit buys below anchor ──
        private void ArmGrid(DateTime time, decimal anchor)
        {
            if (_cfg.BetUsdt > 0 && !_marketEntryOpened)
            {
                var qty = _cfg.BetUsdt / anchor;
                var fee = _cfg.BetUsdt * _taker; // market → taker
                _batches.Add(new OpenBatch
                {
                    Entry = anchor,
                    Qty = qty,
                    Tp = anchor * (1m + _cfg.TpStepPercent / 100m),
                    LevelPct = 0m,
                    IsLevelZero = true
                });
                _marketEntryOpened = true;
                AddTrade(time, "Long", "Open", anchor, qty, fee, grossPnl: null, "MARKET entry level 0%");
            }

            foreach (var (offsetPct, price) in ComputeGridLevels(_cfg, anchor))
            {
                var qty = _cfg.BetUsdt / price;
                if (qty <= 0) continue;
                _pending.Add(new Pending { LimitPrice = price, Qty = qty, LevelPct = offsetPct });
            }
        }

        private void ProcessTick(CandleDto candle, DateTime time, decimal price)
        {
            // 1. resting limit-buy fills (tick ≤ limit).
            foreach (var pending in _pending.ToList())
            {
                if (price > pending.LimitPrice) continue;
                _pending.Remove(pending);
                var fillPrice = pending.LimitPrice; // resting limit fills at its price
                var fee = fillPrice * pending.Qty * _maker;
                _batches.Add(new OpenBatch
                {
                    Entry = fillPrice,
                    Qty = pending.Qty,
                    Tp = fillPrice * (1m + _cfg.TpStepPercent / 100m),
                    LevelPct = pending.LevelPct,
                    IsLevelZero = false
                });
                AddTrade(time, "Long", "Dca", fillPrice, pending.Qty, fee, grossPnl: null,
                    $"Grid fill -{pending.LevelPct}%");
            }

            // 2. TP-sell fills (tick ≥ tp).
            foreach (var batch in _batches.ToList())
            {
                if (price < batch.Tp) continue;
                _batches.Remove(batch);
                var closePrice = batch.Tp;
                var gross = (closePrice - batch.Entry) * batch.Qty;
                var fee = closePrice * batch.Qty * _maker; // TP is a resting limit → maker
                AddTrade(time, "Long", "TakeProfit", closePrice, batch.Qty, fee, gross,
                    $"TP -{batch.LevelPct}%");

                if (batch.IsLevelZero)
                    LadderUp(time, closePrice);
                else
                    ReArmDeep(batch.LevelPct, batch.Entry);
            }

            // 3. whole-bot exit triggers, pinned to the START anchor.
            var upTrigger = _startAnchor * (1m + _cfg.UpperExitPercent / 100m);
            var downTrigger = _startAnchor * (1m - _cfg.RangePercent / 100m);
            if (price >= upTrigger)
                CloseEverything(time, candle, price, "ExitingUp", "верхний триггер (take-profit)");
            else if (price <= downTrigger)
                CloseEverything(time, candle, price, "ExitingDown", "нижний триггер (stop-loss)");
        }

        // Level-0 TP fill → shift working anchor up, cancel pendings, re-arm a fresh generation.
        private void LadderUp(DateTime time, decimal newAnchor)
        {
            _anchor = newAnchor;
            _pending.Clear();            // cancel resting limits (no fee)
            _marketEntryOpened = false;
            ArmGrid(time, newAnchor);    // new market entry at the new anchor + fresh limit grid
        }

        // Deep-level TP fill → re-place a resting limit buy at the same absolute price.
        private void ReArmDeep(decimal levelPct, decimal price)
        {
            if (price <= 0 || _cfg.BetUsdt <= 0) return;
            var qty = _cfg.BetUsdt / price;
            if (qty <= 0) return;
            _pending.Add(new Pending { LimitPrice = price, Qty = qty, LevelPct = levelPct });
        }

        // Cancel pendings, market-close every open batch (taker) and the hedge (taker), end the cycle.
        private void CloseEverything(DateTime time, CandleDto candle, decimal price, string phase, string reason)
        {
            _pending.Clear();

            foreach (var batch in _batches.ToList())
            {
                _batches.Remove(batch);
                var gross = (price - batch.Entry) * batch.Qty;
                var fee = price * batch.Qty * _taker; // forced market close → taker
                AddTrade(time, "Long", "Close", price, batch.Qty, fee, gross,
                    $"{phase} force-close -{batch.LevelPct}%");
            }

            if (_hedgeQty > 0)
            {
                var hedgeClosePx = HedgeMark(candle, price);
                var gross = (_hedgeEntry - hedgeClosePx) * _hedgeQty; // short PnL
                var fee = hedgeClosePx * _hedgeQty * _taker;
                AddTrade(time, "Short", "HedgeClose", hedgeClosePx, _hedgeQty, fee, gross, $"{phase} hedge close");
                _hedgeQty = 0;
            }

            _cycles += 1;
            _endReason = phase;
            _done = true;
            _res.Warnings.Add($"GridHedge: цикл завершён ({phase}: {reason}) @ {Math.Round(price, 8)}. " +
                              "Стратегия выполняет один цикл на запуск — симуляция остановлена.");
        }

        // ── equity / notional ──

        private void RecordEquity(CandleDto candle, bool force = false)
        {
            // Avoid a duplicate final point if the last candle was already an hourly boundary.
            if (force && _res.EquityCurve.Count > 0 && _res.EquityCurve[^1].Time == candle.CloseTime)
                return;

            var gridMark = candle.Close;
            var hedgeMark = HedgeMark(candle, gridMark);

            var realized = _realizedGross - _totalFees;
            var unreal = 0m;
            foreach (var b in _batches) unreal += (gridMark - b.Entry) * b.Qty;
            if (_hedgeQty > 0) unreal += (_hedgeEntry - hedgeMark) * _hedgeQty;

            var point = new EquityPoint
            {
                Time = force ? candle.CloseTime : candle.OpenTime,
                RealizedPnlUsd = realized,
                UnrealizedPnlUsd = unreal,
                EquityUsd = realized + unreal
            };
            // If forcing a final point whose time collides with an existing hourly point, replace it.
            if (force && _res.EquityCurve.Count > 0 && _res.EquityCurve[^1].Time == point.Time)
                _res.EquityCurve[^1] = point;
            else
                _res.EquityCurve.Add(point);
        }

        private void UpdateMaxNotional(CandleDto candle)
        {
            var notional = 0m;
            foreach (var b in _batches) notional += b.Entry * b.Qty;
            if (_hedgeQty > 0) notional += _hedgeEntry * _hedgeQty;
            if (notional > _maxNotional) _maxNotional = notional;
        }

        private void BuildSummary()
        {
            var closed = _res.Trades.Where(t => t.PnlUsd.HasValue).ToList();
            var wins = closed.Count(t => t.PnlUsd!.Value > 0);
            var losses = closed.Count(t => t.PnlUsd!.Value <= 0);

            decimal peak = 0, maxDd = 0;
            var seenPeak = false;
            foreach (var p in _res.EquityCurve)
            {
                if (!seenPeak || p.EquityUsd > peak) { peak = p.EquityUsd; seenPeak = true; }
                var dd = peak - p.EquityUsd;
                if (dd > maxDd) maxDd = dd;
            }

            var lastMark = _candles[^1].Close;
            var lastHedgeMark = HedgeMark(_candles[^1], lastMark);
            var unrealEnd = 0m;
            foreach (var b in _batches) unrealEnd += (lastMark - b.Entry) * b.Qty;
            if (_hedgeQty > 0) unrealEnd += (_hedgeEntry - lastHedgeMark) * _hedgeQty;

            var openPositions = _batches.Count + (_hedgeQty > 0 ? 1 : 0);

            _res.Summary = new SimulationRunSummary
            {
                TotalTrades = _res.Trades.Count,
                WinningTrades = wins,
                LosingTrades = losses,
                WinRate = closed.Count > 0 ? Math.Round((decimal)wins / closed.Count * 100m, 2) : 0m,
                GrossPnlUsd = Math.Round(_realizedGross, 8),
                FeesUsd = Math.Round(_totalFees, 8),
                FundingPnlUsd = 0m, // funding on the hedge leg ignored (approximation)
                NetPnlUsd = Math.Round(_realizedGross - _totalFees, 8),
                MaxDrawdownUsd = Math.Round(maxDd, 8),
                MaxDrawdownPercent = _maxNotional > 0 ? Math.Round(maxDd / _maxNotional * 100m, 4) : 0m,
                MaxNotionalUsd = Math.Round(_maxNotional, 8),
                OpenPositionsAtEnd = openPositions,
                UnrealizedPnlAtEndUsd = Math.Round(unrealEnd, 8),
                CompletedCycles = _cycles,
                StartTime = _candles[0].OpenTime,
                EndTime = _candles[^1].CloseTime,
                PathCandlesProcessed = _candles.Count
            };

            if (!_done)
                _res.Warnings.Add(
                    $"GridHedge: данные закончились до срабатывания выхода — {openPositions} позиций осталось открыто, " +
                    "цикл не завершён (CompletedCycles учитывает только закрытые циклы).");
        }

        // ── price helpers ──

        private decimal HedgeOpenPrice(decimal gridP0)
        {
            if (!_cross) return gridP0;
            var firstOpen = _candles[0].OpenTime;
            return _secondMap.TryGetValue(firstOpen, out var c) ? c.Open : gridP0;
        }

        private decimal HedgeMark(CandleDto candle, decimal gridPrice)
        {
            if (!_cross) return gridPrice;
            return _secondMap.TryGetValue(candle.OpenTime, out var c) ? c.Close : gridPrice;
        }

        // ── accounting helper ──

        private void AddTrade(DateTime time, string side, string action, decimal price, decimal qty,
            decimal fee, decimal? grossPnl, string reason)
        {
            _totalFees += fee;
            decimal? pnl = null;
            if (grossPnl.HasValue)
            {
                _realizedGross += grossPnl.Value;
                pnl = grossPnl.Value - fee;
            }
            _res.Trades.Add(new SimTrade
            {
                Time = time,
                Side = side,
                Action = action,
                Price = price,
                Quantity = qty,
                NotionalUsd = price * qty,
                FeeUsd = fee,
                PnlUsd = pnl,
                Reason = reason
            });
        }

        // Uniform-step grid levels below the anchor (mirrors GridHedgeHandler.ComputeGridLevels).
        private static List<(decimal offsetPct, decimal price)> ComputeGridLevels(GridHedgeConfig cfg, decimal anchor)
        {
            var list = new List<(decimal, decimal)>();
            if (anchor <= 0 || cfg.DcaStepPercent <= 0 || cfg.RangePercent <= 0) return list;

            const int safetyCeiling = 500;
            const decimal eps = 1e-9m;

            var offsetPct = cfg.DcaStepPercent;
            var k = 0;
            while (offsetPct <= cfg.RangePercent + eps && k < safetyCeiling)
            {
                var price = anchor * (1m - offsetPct / 100m);
                if (price <= 0) break;
                list.Add((offsetPct, price));
                offsetPct += cfg.DcaStepPercent;
                k++;
            }
            return list;
        }
    }

    private sealed class OpenBatch
    {
        public decimal Entry;
        public decimal Qty;
        public decimal Tp;
        public decimal LevelPct;
        public bool IsLevelZero;
    }

    private sealed class Pending
    {
        public decimal LimitPrice;
        public decimal Qty;
        public decimal LevelPct;
    }
}
