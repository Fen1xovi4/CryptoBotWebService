using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest counterpart of <see cref="SmartGridHedgeHandler"/> — symmetric geometric grid + a
/// static short hedge (skim variants A/B). Mirrors the handler's phase machine:
///   Opening  (t=0, P0 = first tick): initial long qInit = LotUsd/P0 (positionIdx=1) + static
///            short hedge Q_hedge (QHedgeOverride, else <see cref="SymmetricHedgeOptimizer"/>);
///            geometric grid rungs at P0·(1±Step)^k; D_k (k=1..NDown-1) recyclable DCA buy/sell
///            pairs; U_k (k=1..NUp-1) skim cells per SkimMode.
///   Active   → poll fills, re-arm pairs, run OneShot trims, watch the boundaries.
///   HardClose→ HBreak (=U_NUp) / LBreak (=D_NDown) touch, or per-cycle TakeProfit: cancel all,
///            market-close both legs (taker), CompletedCycles++, record end reason. AutoRestart
///            re-anchors at the current price and continues; AutoRestart=false (or TakeProfit)
///            stops the simulation.
///
/// Fees: this strategy takes fees from ITS OWN config (MakerFeeBps / TakerFeeBps), NOT from
/// ctx.MakerFeeRate / ctx.TakerFeeRate — mirrored here; the ctx fee rates are ignored (noted as a
/// warning). Maker fills = maker bps, market/taker fills = taker bps.
///
/// Tick model: walk ctx.PathCandles (1m ascending) with 4 sub-ticks/candle via
/// <see cref="CandlePathHelper.GetTicks"/>; each sub-tick is one poll. Limit fills: buy when
/// tick ≤ price, sell when tick ≥ price, at the limit price. Boundary checks run first each tick.
///
/// Approximations (surfaced as warnings): funding on the static hedge is ignored; positionIdx /
/// hedge-mode plumbing is skipped; opening/auto-restart cooldowns are dropped (re-anchor is
/// immediate at the trigger tick's price); exchange lot/price rounding is not applied.
/// </summary>
public class SmartGridHedgeSimulator : IStrategySimulator
{
    public string StrategyType => StrategyTypes.SmartGridHedge;

    // Mirror SmartGridHedgeHandler.JsonOptions exactly.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();

        SmartGridHedgeConfig? config;
        try { config = JsonSerializer.Deserialize<SmartGridHedgeConfig>(context.ConfigJson, JsonOptions); }
        catch (Exception ex)
        {
            result.Warnings.Add($"SmartGridHedge: ConfigJson не распарсился: {ex.Message}");
            return result;
        }
        if (config == null) { result.Warnings.Add("SmartGridHedge: ConfigJson = null"); return result; }

        if (config.Step <= 0m || config.Step >= 1m || config.NUp < 1 || config.NDown < 1 || config.LotUsd <= 0m)
        {
            result.Warnings.Add("SmartGridHedge: некорректная конфигурация (Step∈(0,1), NUp/NDown≥1, LotUsd>0)");
            return result;
        }

        var candles = context.PathCandles;
        if (candles == null || candles.Count == 0)
        {
            result.Warnings.Add("SmartGridHedge: пустой PathCandles — нечего симулировать");
            return result;
        }

        new Engine(context, config, result).Run();
        return result;
    }

    // ────────────────────────── Engine ──────────────────────────

    private sealed class Engine
    {
        private readonly SmartGridHedgeConfig _cfg;
        private readonly SimulationRunResult _res;
        private readonly List<CandleDto> _candles;

        private readonly decimal _maker;
        private readonly decimal _taker;

        // ── cycle state ──
        private decimal _p0;
        private decimal _hBreak;
        private decimal _lBreak;
        private decimal _qInit;
        private decimal _pAvgInit;
        private decimal _qHedge;
        private decimal _hedgeEntry;
        private readonly List<DcaCell> _dca = new();
        private readonly List<SkimCell> _skim = new();

        // ── per-cycle running totals (drive the TakeProfit check) ──
        private decimal _cycleGridRealized;
        private decimal _cycleHedgeRealized;
        private decimal _cycleFees;

        // ── cumulative accounting ──
        private decimal _realizedGross;
        private decimal _totalFees;
        private int _cycles;
        private decimal _maxNotional;
        private bool _done;

        public Engine(SimulationContext ctx, SmartGridHedgeConfig cfg, SimulationRunResult res)
        {
            _cfg = cfg;
            _res = res;
            _candles = ctx.PathCandles;
            _maker = cfg.MakerFeeBps / 10_000m; // config-sourced, ctx fees ignored (per handler)
            _taker = cfg.TakerFeeBps / 10_000m;

            _res.Warnings.Add(
                "SmartGridHedge: комиссии берутся из конфига стратегии (MakerFeeBps/TakerFeeBps), " +
                "ctx.MakerFeeRate/ctx.TakerFeeRate игнорируются — как в live-хендлере.");
        }

        public void Run()
        {
            var first = _candles[0];
            var p0 = CandlePathHelper.GetTicks(first)[0].Price; // first candle Open
            OpenCycle(first.OpenTime, p0);

            foreach (var candle in _candles)
            {
                foreach (var tick in CandlePathHelper.GetTicks(candle))
                {
                    ProcessTick(tick.Time, tick.Price);
                    if (_done) break;
                }

                if (!_done) UpdateMaxNotional();
                if (candle.OpenTime.Minute == 0) RecordEquity(candle);
                if (_done) break;
            }

            RecordEquity(_candles[^1], force: true);
            BuildSummary();
        }

        // ── Opening / re-anchor: open qInit + hedge + lay grid at anchor P0 ──
        private void OpenCycle(DateTime time, decimal p0)
        {
            _p0 = p0;
            _pAvgInit = p0;
            _hBreak = p0 * Pow(1m + _cfg.Step, _cfg.NUp);
            _lBreak = p0 * Pow(1m - _cfg.Step, _cfg.NDown);

            _cycleGridRealized = 0m;
            _cycleHedgeRealized = 0m;
            _cycleFees = 0m;
            _dca.Clear();
            _skim.Clear();

            // 1. Initial long (market, taker) on positionIdx=1.
            _qInit = _cfg.LotUsd / p0;
            var longFee = _qInit * p0 * _taker;
            AddTrade(time, "Long", "Open", p0, _qInit, longFee, grossPnl: null, "Initial long qInit");

            // 2. Static short hedge (market, taker) on positionIdx=2.
            var qHedge = _cfg.QHedgeOverride is > 0m
                ? _cfg.QHedgeOverride.Value
                : SafeOptimize(p0);
            var hedgeNotional = qHedge * p0;
            if (hedgeNotional <= 0m)
            {
                _qHedge = 0m;
                _hedgeEntry = p0; // sentinel — pure grid, no hedge
                _res.Warnings.Add("SmartGridHedge: Q_hedge ≤ 0 — цикл идёт как чистая сетка без хеджа.");
            }
            else
            {
                _qHedge = qHedge;
                _hedgeEntry = p0;
                var hedgeFee = _qHedge * p0 * _taker;
                AddTrade(time, "Short", "HedgeOpen", p0, _qHedge, hedgeFee, grossPnl: null, "Static short hedge");
            }

            // 3. DCA cells k = 1..NDown-1 (recyclable buy/sell pairs), buy resting.
            for (var k = 1; k <= _cfg.NDown - 1; k++)
            {
                var dk = p0 * Pow(1m - _cfg.Step, k);
                var dkPrev = p0 * Pow(1m - _cfg.Step, k - 1);
                if (dk <= 0m) continue;
                _dca.Add(new DcaCell { K = k, BuyPrice = dk, SellPrice = dkPrev, Paired = false, QtyCoins = 0m });
            }

            // 4. Skim cells k = 1..NUp-1.
            for (var k = 1; k <= _cfg.NUp - 1; k++)
            {
                var uk = p0 * Pow(1m + _cfg.Step, k);
                var ukPrev = p0 * Pow(1m + _cfg.Step, k - 1);
                if (uk <= 0m) continue;
                _skim.Add(new SkimCell { K = k, SellPrice = uk, CoverPrice = ukPrev, Paired = false, FiredOnceShot = false, ShortQtyCoins = 0m });
            }
        }

        private decimal SafeOptimize(decimal p0)
        {
            try
            {
                return SymmetricHedgeOptimizer.Optimize(
                    p0, _cfg.Step, _cfg.NUp, _cfg.NDown, _cfg.LotUsd,
                    _cfg.SkimMode, _cfg.MakerFeeBps, _cfg.TakerFeeBps).QHedgeCoins;
            }
            catch (Exception ex)
            {
                _res.Warnings.Add($"SmartGridHedge: оптимизатор Q_hedge бросил исключение ({ex.Message}) — хедж = 0.");
                return 0m;
            }
        }

        private void ProcessTick(DateTime time, decimal mark)
        {
            // 1. Boundary — checked before any polling (handler fast-path).
            if (mark >= _hBreak) { HardClose(time, mark, "HBreak"); return; }
            if (mark <= _lBreak) { HardClose(time, mark, "LBreak"); return; }

            // 2. Per-cycle take-profit (realized net + mark-to-market unrealized on every open leg).
            if (_cfg.TakeProfitEnabled && _cfg.TakeProfitTargetUsd > 0m)
            {
                var (longUnreal, shortUnreal) = Unrealized(mark);
                var cycleRealizedNet = _cycleGridRealized + _cycleHedgeRealized - _cycleFees;
                if (cycleRealizedNet + longUnreal + shortUnreal >= _cfg.TakeProfitTargetUsd)
                {
                    HardClose(time, mark, "TakeProfit");
                    return;
                }
            }

            // 3. DCA cells.
            foreach (var cell in _dca)
            {
                if (!cell.Paired)
                {
                    // Buy resting at D_k — fills on tick ≤ BuyPrice.
                    if (mark > cell.BuyPrice) continue;
                    var qty = _cfg.LotUsd / cell.BuyPrice;
                    var fee = qty * cell.BuyPrice * _maker;
                    cell.QtyCoins = qty;
                    cell.Paired = true;
                    AddTrade(time, "Long", "Dca", cell.BuyPrice, qty, fee, grossPnl: null, $"DCA buy k={cell.K}");
                }
                else
                {
                    // Paired sell resting at D_{k-1} — fills on tick ≥ SellPrice.
                    if (mark < cell.SellPrice) continue;
                    var gross = cell.QtyCoins * (cell.SellPrice - cell.BuyPrice);
                    var fee = cell.QtyCoins * cell.SellPrice * _maker;
                    _cycleGridRealized += gross;
                    AddTrade(time, "Long", "TakeProfit", cell.SellPrice, cell.QtyCoins, fee, gross, $"DCA sell k={cell.K}");
                    cell.Paired = false; // re-arm buy at D_k
                    cell.QtyCoins = 0m;
                }
            }

            // 4. Skim cells.
            if (_cfg.SkimMode == SmartGridSkimMode.OneShot)
                TickOneShot(time, mark);
            else
                TickRecycleSkim(time, mark);
        }

        private void TickOneShot(DateTime time, decimal mark)
        {
            foreach (var cell in _skim.OrderBy(c => c.K))
            {
                if (cell.FiredOnceShot) continue;
                if (mark < cell.SellPrice) continue;
                if (_qInit <= 0m) break;

                var excessUsd = _qInit * cell.SellPrice - _cfg.LotUsd;
                if (excessUsd <= 0m) { cell.FiredOnceShot = true; continue; }

                var trimCoins = excessUsd / cell.SellPrice;
                if (trimCoins <= 0m || trimCoins > _qInit) trimCoins = _qInit;

                var fillPx = cell.SellPrice; // market trim on cross
                var gross = trimCoins * (fillPx - _pAvgInit);
                var fee = trimCoins * fillPx * _taker; // market → taker
                _qInit -= trimCoins;
                if (_qInit < 0m) _qInit = 0m;
                _cycleGridRealized += gross;
                cell.FiredOnceShot = true;
                AddTrade(time, "Long", "Skim", fillPx, trimCoins, fee, gross, $"OneShot trim k={cell.K}");
            }
        }

        private void TickRecycleSkim(DateTime time, decimal mark)
        {
            foreach (var cell in _skim)
            {
                if (!cell.Paired)
                {
                    // Short (sell) resting at U_k — fills on tick ≥ SellPrice.
                    if (mark < cell.SellPrice) continue;
                    var shortQty = RecycleShortQty(cell.SellPrice);
                    if (shortQty <= 0m) continue;
                    var fee = shortQty * cell.SellPrice * _maker;
                    cell.ShortQtyCoins = shortQty;
                    cell.Paired = true;
                    AddTrade(time, "Short", "Skim", cell.SellPrice, shortQty, fee, grossPnl: null, $"Skim short k={cell.K}");
                }
                else
                {
                    // Cover (buy) resting at U_{k-1} — fills on tick ≤ CoverPrice.
                    if (mark > cell.CoverPrice) continue;
                    var gross = cell.ShortQtyCoins * (cell.SellPrice - cell.CoverPrice);
                    var fee = cell.ShortQtyCoins * cell.CoverPrice * _maker;
                    _cycleGridRealized += gross;
                    AddTrade(time, "Short", "TakeProfit", cell.CoverPrice, cell.ShortQtyCoins, fee, gross, $"Skim cover k={cell.K}");
                    cell.Paired = false; // re-arm short at U_k
                    cell.ShortQtyCoins = 0m;
                }
            }
        }

        private decimal RecycleShortQty(decimal uk) => _cfg.SkimMode switch
        {
            SmartGridSkimMode.FullRecycle => _cfg.LotUsd / uk,
            SmartGridSkimMode.ExcessRecycle => _cfg.LotUsd * _cfg.Step / uk,
            _ => 0m
        };

        // Cancel all, market-close both aggregate legs (taker), finalize cycle. Auto-restart or stop.
        private void HardClose(DateTime time, decimal closePx, string reason)
        {
            // LONG side aggregate: qInit + filled DCA cells.
            var dcaPairedLong = _dca.Where(c => c.Paired).Sum(c => c.QtyCoins);
            var totalLong = _qInit + dcaPairedLong;
            if (totalLong > 0m)
            {
                var gross = _qInit * (closePx - _pAvgInit);
                foreach (var cell in _dca.Where(c => c.Paired))
                    gross += cell.QtyCoins * (closePx - cell.BuyPrice);
                var fee = totalLong * closePx * _taker;
                _cycleGridRealized += gross;
                AddTrade(time, "Long", "Close", closePx, totalLong, fee, gross, $"HardClose long ({reason})");
            }

            // SHORT side aggregate: qHedge + paired skim shorts.
            var skimPairedShort = _skim.Where(c => c.Paired).Sum(c => c.ShortQtyCoins);
            var totalShort = _qHedge + skimPairedShort;
            if (totalShort > 0m)
            {
                var hedgeGross = _qHedge * (_hedgeEntry - closePx);
                var skimGross = 0m;
                foreach (var cell in _skim.Where(c => c.Paired))
                    skimGross += cell.ShortQtyCoins * (cell.SellPrice - closePx);
                var fee = totalShort * closePx * _taker;
                _cycleHedgeRealized += hedgeGross;
                _cycleGridRealized += skimGross;
                AddTrade(time, "Short", "Close", closePx, totalShort, fee, hedgeGross + skimGross, $"HardClose short ({reason})");
            }

            _cycles += 1;

            // Reset per-cell/cycle position state.
            _dca.Clear();
            _skim.Clear();
            _qInit = 0m;
            _qHedge = 0m;
            _hedgeEntry = 0m;
            _p0 = 0m;
            _hBreak = 0m;
            _lBreak = 0m;
            _pAvgInit = 0m;

            var isTakeProfit = reason == "TakeProfit";
            if (!isTakeProfit && _cfg.AutoRestart)
            {
                // Re-anchor a fresh cycle at the current price (cooldown skipped in sim).
                OpenCycle(time, closePx);
                _res.Warnings.Add($"SmartGridHedge: цикл #{_cycles} закрыт ({reason}) @ {Math.Round(closePx, 8)} — AutoRestart, новый цикл на новой цене.");
            }
            else
            {
                _done = true;
                _res.Warnings.Add($"SmartGridHedge: цикл #{_cycles} закрыт ({reason}) @ {Math.Round(closePx, 8)} — " +
                                  (isTakeProfit ? "TakeProfit достигнут, бот остановлен." : "AutoRestart=false, симуляция остановлена."));
            }
        }

        // ── equity / notional ──

        private (decimal longUnreal, decimal shortUnreal) Unrealized(decimal mark)
        {
            var longUnreal = _qInit * (mark - _pAvgInit);
            foreach (var c in _dca)
                if (c.Paired && c.QtyCoins > 0m) longUnreal += c.QtyCoins * (mark - c.BuyPrice);

            var shortUnreal = _qHedge * (_hedgeEntry - mark);
            foreach (var c in _skim)
                if (c.Paired && c.ShortQtyCoins > 0m) shortUnreal += c.ShortQtyCoins * (c.SellPrice - mark);

            return (longUnreal, shortUnreal);
        }

        private void RecordEquity(CandleDto candle, bool force = false)
        {
            var mark = candle.Close;
            var (longUnreal, shortUnreal) = Unrealized(mark);
            var realized = _realizedGross - _totalFees;
            var unreal = longUnreal + shortUnreal;

            var point = new EquityPoint
            {
                Time = force ? candle.CloseTime : candle.OpenTime,
                RealizedPnlUsd = realized,
                UnrealizedPnlUsd = unreal,
                EquityUsd = realized + unreal
            };

            if (force && _res.EquityCurve.Count > 0 && _res.EquityCurve[^1].Time == point.Time)
                _res.EquityCurve[^1] = point;
            else
                _res.EquityCurve.Add(point);
        }

        private void UpdateMaxNotional()
        {
            var notional = _qInit * _pAvgInit + _qHedge * _hedgeEntry;
            foreach (var c in _dca) if (c.Paired) notional += c.QtyCoins * c.BuyPrice;
            foreach (var c in _skim) if (c.Paired) notional += c.ShortQtyCoins * c.SellPrice;
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
            var (lu, su) = Unrealized(lastMark);
            var unrealEnd = lu + su;

            var openPositions = (_qInit > 0m ? 1 : 0) + _dca.Count(c => c.Paired)
                                + (_qHedge > 0m ? 1 : 0) + _skim.Count(c => c.Paired);

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
        }

        // ── accounting helper ──

        private void AddTrade(DateTime time, string side, string action, decimal price, decimal qty,
            decimal fee, decimal? grossPnl, string reason)
        {
            _totalFees += fee;
            _cycleFees += fee;
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

        // (1 + x)^n via repeated multiplication — matches SmartGridHedgeHandler.Pow / optimizer.
        private static decimal Pow(decimal baseValue, int exponent)
        {
            var result = 1m;
            for (var i = 0; i < exponent; i++) result *= baseValue;
            return result;
        }
    }

    private sealed class DcaCell
    {
        public int K;
        public decimal BuyPrice;   // D_k
        public decimal SellPrice;  // D_{k-1}
        public bool Paired;        // false = buy resting; true = holding, sell resting
        public decimal QtyCoins;
    }

    private sealed class SkimCell
    {
        public int K;
        public decimal SellPrice;  // U_k
        public decimal CoverPrice; // U_{k-1}
        public bool Paired;        // recycle: false = short resting; true = cover resting
        public bool FiredOnceShot; // OneShot only
        public decimal ShortQtyCoins;
    }
}
