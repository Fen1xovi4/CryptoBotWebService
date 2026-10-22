using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest simulator for the MaratG / EMA Bounce strategy. Ports the decision logic of
/// <see cref="EmaBounceHandler"/> onto the 1-minute price path:
///
/// - Counter + closed-candle entry logic runs on CLOSED candles of the config timeframe
///   (PathCandles aggregated via <see cref="CandleAggregator"/>).
/// - TP/SL is evaluated on the 1-minute path (the sim equivalent of the handler's 5s poll),
///   with the handler-rule "SL wins if TP and SL both fall inside the same 1m span".
/// - Intrabar entries fire during the forming timeframe bucket once the counter is armed, mirroring
///   <c>CheckIntrabarEntry</c>.
/// - Honors OnlyLong / OnlyShort, classic/stepped martingale and drawdown scaling, one position at a
///   time, and the wait-one-candle-after-close rule.
///
/// Fills apply fees per the shared spec (market fills = taker). Entries fill at the candle close and
/// TP/SL closes fill at the trigger level — the same prices the handler acts on.
/// </summary>
public class MaratGSimulator : IStrategySimulator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public virtual string StrategyType => StrategyTypes.MaratG;

    /// <summary>MaratGGrid: grid lots on top of the base (see <see cref="MaratGGridSimulator"/>).</summary>
    protected virtual bool WithGrid => false;

    public void ValidateConfig(string configJson)
    {
        EmaBounceConfig? config;
        try { config = JsonSerializer.Deserialize<EmaBounceConfig>(configJson, JsonOptions); }
        catch (JsonException ex) { throw new ArgumentException($"MaratG: некорректный configJson — {ex.Message}"); }
        if (config == null) throw new ArgumentException("MaratG: пустой configJson.");

        // Live bots get OrderSize from the workspace (betAmount) at start; the sim has no workspace,
        // so the request must carry it explicitly — otherwise every fill would have qty 0.
        if (config.OrderSize <= 0)
            throw new ArgumentException("MaratG: orderSize (сумма ставки, USDT) должен быть больше 0 — укажите его в конфиге симуляции.");
        if (config.IndicatorLength < 1)
            throw new ArgumentException("MaratG: период индикатора должен быть ≥ 1.");
        if (config.CandleCount < 1)
            throw new ArgumentException("MaratG: количество свечей должно быть ≥ 1.");
        if (config.TakeProfitPercent <= 0 || config.StopLossPercent <= 0)
            throw new ArgumentException("MaratG: Take Profit % и Stop Loss % должны быть больше 0.");
        if (config.OnlyLong && config.OnlyShort)
            throw new ArgumentException("MaratG: нельзя одновременно включить «только Long» и «только Short».");
        if (config.UseMartingale && config.MartingaleCoeff <= 0)
            throw new ArgumentException("MaratG: коэффициент мартингейла должен быть больше 0.");
        if (config.UseMartingale && config.UseSteppedMartingale && config.MartingaleStep < 1)
            throw new ArgumentException("MaratG: шаг ступенчатого мартингейла должен быть ≥ 1.");
        if (string.IsNullOrWhiteSpace(config.Timeframe))
            throw new ArgumentException("MaratG: не указан таймфрейм.");

        if (WithGrid)
        {
            if (config.GridLotSize <= 0)
                throw new ArgumentException("MaratG+Grid: сумма лота сетки (gridLotSize, USDT) должна быть больше 0.");
            if (config.GridStepPercent <= 0)
                throw new ArgumentException("MaratG+Grid: шаг сетки, % должен быть больше 0.");
            if (config.GridMaxLots < 0)
                throw new ArgumentException("MaratG+Grid: макс. докупок не может быть отрицательным.");
        }
    }

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();
        result.Warnings.AddRange(context.Warnings);

        var config = JsonSerializer.Deserialize<EmaBounceConfig>(context.ConfigJson, JsonOptions);
        if (config == null)
        {
            result.Warnings.Add("MaratG: config could not be deserialized.");
            return result;
        }

        var path = context.PathCandles;
        if (path.Count == 0)
        {
            result.Warnings.Add("MaratG: no path candles.");
            return result;
        }

        var ledger = new SimLedger(context.MakerFeeRate, context.TakerFeeRate);

        // ── Aggregate to the strategy timeframe and pre-compute the indicator line ──
        var span = SymbolHelper.GetTimeframeSpan(config.Timeframe);
        var agg = CandleAggregator.Aggregate(path, config.Timeframe);
        var closes = agg.Select(c => c.Close).ToArray();
        var ma = config.IndicatorType.Equals("SMA", StringComparison.OrdinalIgnoreCase)
            ? IndicatorCalculator.CalculateSma(closes, config.IndicatorLength)
            : IndicatorCalculator.CalculateEma(closes, config.IndicatorLength);

        for (int j = config.IndicatorLength - 1; j >= 0 && j < agg.Count; j++)
        {
            if (ma[j] != 0m)
                result.IndicatorValues.Add(new IndicatorPoint { Time = agg[j].OpenTime, Value = Math.Round(ma[j], 8) });
        }

        var state = new MgState { CurrentOrderSize = config.OrderSize, Grid = WithGrid };

        // bucketEnd[j] = the moment aggregated candle j is considered CLOSED (path time must pass it).
        var bucketEnd = new DateTime[agg.Count];
        for (int j = 0; j < agg.Count; j++)
            bucketEnd[j] = agg[j].OpenTime + span;

        int aggIdx = 0;

        // ── Walk the 1-minute path ──
        foreach (var c in path)
        {
            // 1) Hourly equity sample (mark-to-market at the candle open).
            if (c.OpenTime.Minute == 0)
                ledger.SampleEquity(c.OpenTime, Unrealized(state, c.Open));

            // 2) TP/SL over this 1m span (SL wins if both hit inside the span).
            if (state.Position != null)
            {
                if (state.Grid) RunGridTicks(config, state, c, ledger);
                else CheckTpSl(config, state, c, ledger);
            }

            // 3) Flush every timeframe bucket that has now closed at/before this candle's close.
            bool closedBucketThisCandle = false;
            while (aggIdx < agg.Count && bucketEnd[aggIdx] <= c.CloseTime)
            {
                ProcessClosedCandle(config, state, agg[aggIdx], ma[aggIdx], ledger);
                aggIdx++;
                closedBucketThisCandle = true;
            }

            // 4) Intrabar entry on the forming bucket (mutually exclusive with the closed-candle path,
            //    mirroring the handler's "new closed candle → closed path, else → intrabar" branch).
            if (!closedBucketThisCandle && state.CurrentMa != 0m && state.Position == null)
                CheckIntrabarEntry(config, state, c, ledger);
        }

        // Final equity sample at the last observed price.
        var lastPrice = path[^1].Close;
        ledger.SampleEquity(path[^1].CloseTime, Unrealized(state, lastPrice));

        result.Trades.AddRange(ledger.Trades);
        result.EquityCurve.AddRange(ledger.EquityCurve);
        BuildSummary(result, ledger, state, path, lastPrice);
        return result;
    }

    // ───────── Closed-candle counter + entry (ports EmaBounceHandler) ─────────

    private static void ProcessClosedCandle(EmaBounceConfig config, MgState state, CandleDto candle, decimal ma,
        SimLedger ledger)
    {
        if (ma == 0m) return; // indicator not warmed up yet
        state.CurrentMa = ma;

        if (!config.OnlyShort) ProcessLong(config, state, candle, ma);
        if (!config.OnlyLong) ProcessShort(config, state, candle, ma);

        if (!config.OnlyShort && ShouldOpenLong(config, state, candle, ma))
            OpenLong(config, state, candle.CloseTime, candle.Close, ma, ledger, "Entry");

        if (!config.OnlyLong && ShouldOpenShort(config, state, candle, ma))
            OpenShort(config, state, candle.CloseTime, candle.Close, ma, ledger, "Entry");
    }

    private static void ProcessLong(EmaBounceConfig config, MgState state, CandleDto candle, decimal ma)
    {
        if (state.WaitNextCandleAfterLongClose)
        {
            state.WaitNextCandleAfterLongClose = false;
            return;
        }
        if (state.Position != null) return;

        if (state.LongCounter >= config.CandleCount)
        {
            var offsetLine = ma + ma * config.OffsetPercent / 100m;
            if (candle.Low <= offsetLine) return; // entry will handle this
        }

        if (candle.Low > ma) state.LongCounter++;
        else state.LongCounter = 0;
    }

    private static void ProcessShort(EmaBounceConfig config, MgState state, CandleDto candle, decimal ma)
    {
        if (state.WaitNextCandleAfterShortClose)
        {
            state.WaitNextCandleAfterShortClose = false;
            return;
        }
        if (state.Position != null) return;

        if (state.ShortCounter >= config.CandleCount)
        {
            var offsetLine = ma - ma * config.OffsetPercent / 100m;
            if (candle.High >= offsetLine) return;
        }

        if (candle.High < ma) state.ShortCounter++;
        else state.ShortCounter = 0;
    }

    private static bool ShouldOpenLong(EmaBounceConfig config, MgState state, CandleDto candle, decimal ma)
    {
        if (state.Position != null) return false;
        if (state.LongCounter < config.CandleCount) return false;
        var offsetLine = ma + ma * config.OffsetPercent / 100m;
        return candle.Low <= offsetLine;
    }

    private static bool ShouldOpenShort(EmaBounceConfig config, MgState state, CandleDto candle, decimal ma)
    {
        if (state.Position != null) return false;
        if (state.ShortCounter < config.CandleCount) return false;
        var offsetLine = ma - ma * config.OffsetPercent / 100m;
        return candle.High >= offsetLine;
    }

    // ───────── Intrabar entry (ports CheckIntrabarEntry) ─────────

    private static void CheckIntrabarEntry(EmaBounceConfig config, MgState state, CandleDto forming, SimLedger ledger)
    {
        var longArmed = !config.OnlyShort && state.LongCounter >= config.CandleCount
                        && !state.WaitNextCandleAfterLongClose;
        var shortArmed = !config.OnlyLong && state.ShortCounter >= config.CandleCount
                         && !state.WaitNextCandleAfterShortClose;
        if (!longArmed && !shortArmed) return;

        var ma = state.CurrentMa; // EMA of the last closed timeframe candle (handler uses closed candles only)
        var offsetLong = ma + ma * config.OffsetPercent / 100m;
        var offsetShort = ma - ma * config.OffsetPercent / 100m;

        if (longArmed && forming.Low <= offsetLong)
        {
            OpenLong(config, state, forming.CloseTime, forming.Close, ma, ledger, "IntrabarEntry");
            return;
        }
        if (shortArmed && forming.High >= offsetShort)
        {
            OpenShort(config, state, forming.CloseTime, forming.Close, ma, ledger, "IntrabarEntry");
        }
    }

    // ───────── Position open / close ─────────

    private static void OpenLong(EmaBounceConfig config, MgState state, DateTime time, decimal entryPrice,
        decimal ma, SimLedger ledger, string reason)
    {
        var orderSize = GetCurrentOrderSize(config, state);
        state.CurrentOrderSize = orderSize;
        var qty = entryPrice > 0 ? orderSize / entryPrice : 0m;

        state.Position = new MgPosition
        {
            IsLong = true,
            EntryPrice = entryPrice,
            Quantity = qty,
            OrderSize = orderSize,
            TakeProfit = entryPrice * (1 + config.TakeProfitPercent / 100m),
            StopLoss = entryPrice * (1 - config.StopLossPercent / 100m)
        };
        state.LongCounter = 0;

        ledger.RecordOpen(time, "Long", "Open", entryPrice, qty, taker: true, reason);
        ledger.TrackOpenNotional(orderSize);
        if (state.Grid) StartGridCycle(config, state, time, entryPrice, ledger);
    }

    private static void OpenShort(EmaBounceConfig config, MgState state, DateTime time, decimal entryPrice,
        decimal ma, SimLedger ledger, string reason)
    {
        var orderSize = GetCurrentOrderSize(config, state);
        state.CurrentOrderSize = orderSize;
        var qty = entryPrice > 0 ? orderSize / entryPrice : 0m;

        state.Position = new MgPosition
        {
            IsLong = false,
            EntryPrice = entryPrice,
            Quantity = qty,
            OrderSize = orderSize,
            TakeProfit = entryPrice * (1 - config.TakeProfitPercent / 100m),
            StopLoss = entryPrice * (1 + config.StopLossPercent / 100m)
        };
        state.ShortCounter = 0;

        ledger.RecordOpen(time, "Short", "Open", entryPrice, qty, taker: true, reason);
        ledger.TrackOpenNotional(orderSize);
        if (state.Grid) StartGridCycle(config, state, time, entryPrice, ledger);
    }

    private static void CheckTpSl(EmaBounceConfig config, MgState state, CandleDto candle, SimLedger ledger)
    {
        var p = state.Position!;
        if (p.IsLong)
        {
            bool tpHit = candle.High >= p.TakeProfit;
            bool slHit = candle.Low <= p.StopLoss;
            if (slHit) CloseLong(config, state, candle.CloseTime, p.StopLoss, "StopLoss", ledger);
            else if (tpHit) CloseLong(config, state, candle.CloseTime, p.TakeProfit, "TakeProfit", ledger);
        }
        else
        {
            bool tpHit = candle.Low <= p.TakeProfit;
            bool slHit = candle.High >= p.StopLoss;
            if (slHit) CloseShort(config, state, candle.CloseTime, p.StopLoss, "StopLoss", ledger);
            else if (tpHit) CloseShort(config, state, candle.CloseTime, p.TakeProfit, "TakeProfit", ledger);
        }
    }

    private static void CloseLong(EmaBounceConfig config, MgState state, DateTime time, decimal closePrice,
        string reason, SimLedger ledger)
    {
        var p = state.Position!;
        var pnlPercent = (closePrice - p.EntryPrice) / p.EntryPrice * 100m;
        var gross = p.Quantity * (closePrice - p.EntryPrice);

        ledger.RecordClose(time, "Long", reason, closePrice, p.Quantity, taker: true, gross, pnlPercent, reason);
        UpdateMartingaleState(config, state, pnlPercent, p.OrderSize);

        if (state.Grid) CloseAllLots(config, state, time, closePrice, reason, ledger);
        state.Position = null;
        state.LongCounter = 0;
        state.WaitNextCandleAfterLongClose = true;
    }

    private static void CloseShort(EmaBounceConfig config, MgState state, DateTime time, decimal closePrice,
        string reason, SimLedger ledger)
    {
        var p = state.Position!;
        var pnlPercent = (p.EntryPrice - closePrice) / p.EntryPrice * 100m;
        var gross = p.Quantity * (p.EntryPrice - closePrice);

        ledger.RecordClose(time, "Short", reason, closePrice, p.Quantity, taker: true, gross, pnlPercent, reason);
        UpdateMartingaleState(config, state, pnlPercent, p.OrderSize);

        if (state.Grid) CloseAllLots(config, state, time, closePrice, reason, ledger);
        state.Position = null;
        state.ShortCounter = 0;
        state.WaitNextCandleAfterShortClose = true;
    }

    // ───────── Martingale (ports EmaBounceHandler.GetCurrentOrderSize/UpdateMartingaleState) ─────────

    private static decimal GetCurrentOrderSize(EmaBounceConfig config, MgState state)
    {
        if (!config.UseMartingale) return config.OrderSize;

        var baseSize = config.OrderSize;

        if (config.UseDrawdownScale && config.DrawdownBalance > 0)
        {
            var drawdownThreshold = config.DrawdownBalance * config.DrawdownPercent / 100m;
            var targetThreshold = config.DrawdownBalance * config.DrawdownTarget / 100m;

            if (state.RunningPnlDollar <= -drawdownThreshold)
            {
                var levels = (int)Math.Floor(-state.RunningPnlDollar / drawdownThreshold);
                baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, levels);
            }
            else if (state.RunningPnlDollar >= targetThreshold)
            {
                state.RunningPnlDollar = 0;
            }
        }
        else if (state.ConsecutiveLosses > 0)
        {
            if (config.UseSteppedMartingale && config.MartingaleStep > 0)
            {
                var steps = state.ConsecutiveLosses / config.MartingaleStep;
                if (steps > 0)
                    baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, steps);
            }
            else
            {
                baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, state.ConsecutiveLosses);
            }
        }

        return Math.Round(baseSize, 2);
    }

    private static void UpdateMartingaleState(EmaBounceConfig config, MgState state, decimal pnlPercent, decimal orderSize)
    {
        if (!config.UseMartingale) return;

        var pnlDollar = orderSize * pnlPercent / 100m;
        state.RunningPnlDollar += pnlDollar;

        if (pnlPercent > 0) state.ConsecutiveLosses = 0;
        else state.ConsecutiveLosses++;
    }

    // ───────── Equity / summary ─────────

    private static decimal Unrealized(MgState state, decimal price)
    {
        var p = state.Position;
        if (p == null) return 0m;
        var qty = p.Quantity;
        var cost = p.Quantity * p.EntryPrice;
        foreach (var lot in state.Lots)
        {
            qty += lot.Quantity;
            cost += lot.Quantity * lot.EntryPrice;
        }
        return p.IsLong ? qty * price - cost : cost - qty * price;
    }

    // ───────── Grid (MaratGGrid; ports EmaBounceHandler.Grid) ─────────
    //
    // The grid needs the order of prices inside a 1m candle, so while a base is open it walks the
    // 4-tick CandlePathHelper path instead of the candle's High/Low: base TP/SL first (SL wins),
    // then the grid in the live handler's per-poll order (one tick ≈ one poll): resting buys that
    // the tick reached fill, lot TP limits fill, the single level limit moves to the nearest free
    // level (not on the tick a level filled), the starter reopen limit.
    // Resting limits fill at their own price as maker (also on a gap — conservative); a level limit
    // placed when price is already past it fills at once at the tick price as taker. The starter
    // opens at market with the base (taker); base TP/SL closes everything at market (taker).

    private static void StartGridCycle(EmaBounceConfig config, MgState state, DateTime time, decimal entryPrice,
        SimLedger ledger)
    {
        state.Lots.Clear();
        state.LevelOrder = null;
        state.StarterOrder = null;
        state.StarterReopenAt = null;
        state.CycleLotSize = config.OrderSize > 0
            ? Math.Round(config.GridLotSize * state.CurrentOrderSize / config.OrderSize, 2)
            : config.GridLotSize;
        if (state.CycleLotSize <= 0 || config.GridStepPercent <= 0) return;

        OpenLot(config, state, time, entryPrice, 0, taker: true, ledger);
    }

    private static void RunGridTicks(EmaBounceConfig config, MgState state, CandleDto candle, SimLedger ledger)
    {
        foreach (var tick in CandlePathHelper.GetTicks(candle))
        {
            var p = state.Position;
            if (p == null) break;
            var price = tick.Price;

            if (p.IsLong)
            {
                if (price <= p.StopLoss) { CloseLong(config, state, tick.Time, p.StopLoss, "StopLoss", ledger); break; }
                if (price >= p.TakeProfit) { CloseLong(config, state, tick.Time, p.TakeProfit, "TakeProfit", ledger); break; }
            }
            else
            {
                if (price >= p.StopLoss) { CloseShort(config, state, tick.Time, p.StopLoss, "StopLoss", ledger); break; }
                if (price <= p.TakeProfit) { CloseShort(config, state, tick.Time, p.TakeProfit, "TakeProfit", ledger); break; }
            }

            GridStep(config, state, tick.Time, price, ledger);
        }
    }

    private static void GridStep(EmaBounceConfig config, MgState state, DateTime time, decimal price, SimLedger ledger)
    {
        var p = state.Position!;
        if (state.CycleLotSize <= 0 || config.GridStepPercent <= 0) return;
        var isLong = p.IsLong;
        bool BuyReached(decimal limit) => isLong ? price <= limit : price >= limit;
        var span = SymbolHelper.GetTimeframeSpan(config.Timeframe);
        var nextCandleOpen = new DateTime(time.Ticks - time.Ticks % span.Ticks, DateTimeKind.Utc) + span;

        // 1. Resting buys the tick reached fill at their own price.
        var levelFilled = false;
        if (state.LevelOrder is { } levelOrder && BuyReached(levelOrder.Price))
        {
            OpenLot(config, state, time, levelOrder.Price, levelOrder.Level, taker: false, ledger);
            state.LevelOrder = null;
            levelFilled = true;
        }
        if (state.StarterOrder is { } starterOrder && BuyReached(starterOrder.Price))
        {
            OpenLot(config, state, time, starterOrder.Price, 0, taker: false, ledger);
            state.StarterOrder = null;
        }

        // 2. Lot TP limits.
        foreach (var lot in state.Lots.Where(l => isLong ? price >= l.TakeProfit : price <= l.TakeProfit)
                     .OrderBy(l => l.Level).ToList())
        {
            CloseLot(config, state, time, lot, lot.TakeProfit, "GridTP", taker: false, ledger);
            if (lot.Level == 0) state.StarterReopenAt = nextCandleOpen;
        }

        // 3. The single level limit on the nearest free level against the base entry.
        var target = EmaBounceHandler.GetGridTargetLevel(EmaBounceHandler.GetGridMaxLevel(config),
            state.Lots.Select(l => l.Level));
        if (state.LevelOrder != null && state.LevelOrder.Level != target)
            state.LevelOrder = null;
        if (state.LevelOrder == null && target.HasValue && !levelFilled)
        {
            var level = EmaBounceHandler.GridLevelPrice(p.EntryPrice, target.Value, config.GridStepPercent, isLong);
            if (BuyReached(level))
                OpenLot(config, state, time, price, target.Value, taker: true, ledger); // marketable: fills now, better price
            else
                state.LevelOrder = new SimPending { Level = target.Value, Price = level };
        }

        // 4. Starter reopen: limit at the tick price from the next candle on, re-placed every
        //    candle open while unfilled.
        if (state.StarterOrder?.RepriceAt is { } repriceAt && time >= repriceAt)
        {
            state.StarterOrder = null;
            state.StarterReopenAt = time;
        }
        if (state.StarterOrder == null && state.StarterReopenAt.HasValue && time >= state.StarterReopenAt.Value
            && state.Lots.All(l => l.Level != 0))
        {
            state.StarterOrder = new SimPending { Level = 0, Price = price, RepriceAt = nextCandleOpen };
            state.StarterReopenAt = null;
        }
    }

    private static void OpenLot(EmaBounceConfig config, MgState state, DateTime time, decimal price, int level,
        bool taker, SimLedger ledger)
    {
        var p = state.Position!;
        if (price <= 0) return;
        var qty = state.CycleLotSize / price;
        var step = config.GridStepPercent / 100m;
        var fee = ledger.RecordOpen(time, p.IsLong ? "Long" : "Short", "GridOpen", price, qty, taker,
            $"GridL{level}", holdForPosition: false);
        state.Lots.Add(new SimLot
        {
            Level = level,
            EntryPrice = price,
            Quantity = qty,
            TakeProfit = p.IsLong ? price * (1 + step) : price * (1 - step),
            EntryFee = fee
        });
        ledger.TrackOpenNotional(p.OrderSize + state.Lots.Count * state.CycleLotSize);
    }

    private static void CloseLot(EmaBounceConfig config, MgState state, DateTime time, SimLot lot, decimal price,
        string reason, bool taker, SimLedger ledger)
    {
        var isLong = state.Position!.IsLong;
        var gross = isLong ? lot.Quantity * (price - lot.EntryPrice) : lot.Quantity * (lot.EntryPrice - price);
        var pnlPercent = isLong
            ? (price - lot.EntryPrice) / lot.EntryPrice * 100m
            : (lot.EntryPrice - price) / lot.EntryPrice * 100m;
        ledger.RecordClose(time, isLong ? "Long" : "Short", reason, price, lot.Quantity, taker, gross,
            pnlPercent, $"{reason} L{lot.Level}", ownEntryFee: lot.EntryFee);
        // Grid PnL feeds the drawdown-scaled martingale; the base outcome alone decides win/loss.
        if (config.UseMartingale) state.RunningPnlDollar += gross;
        state.Lots.Remove(lot);
    }

    private static void CloseAllLots(EmaBounceConfig config, MgState state, DateTime time, decimal price,
        string reason, SimLedger ledger)
    {
        foreach (var lot in state.Lots.ToList())
            CloseLot(config, state, time, lot, price, "Grid" + reason, taker: true, ledger);
        state.LevelOrder = null;
        state.StarterOrder = null;
        state.StarterReopenAt = null;
        state.BaseCloses++;
    }

    private static void BuildSummary(SimulationRunResult result, SimLedger ledger, MgState state,
        List<CandleDto> path, decimal lastPrice)
    {
        var net = ledger.GrossRealizedUsd - ledger.FeesUsd; // funding = 0
        var openPositions = state.Position != null ? 1 : 0;
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
            CompletedCycles = state.Grid ? state.BaseCloses : ledger.ClosedTrades,
            StartTime = path[0].OpenTime,
            EndTime = path[^1].CloseTime,
            PathCandlesProcessed = path.Count
        };
    }

    // ───────── Internal state ─────────

    private sealed class MgState
    {
        public int LongCounter { get; set; }
        public int ShortCounter { get; set; }
        public bool WaitNextCandleAfterLongClose { get; set; }
        public bool WaitNextCandleAfterShortClose { get; set; }

        public decimal CurrentMa { get; set; }          // EMA/SMA of the last closed timeframe candle
        public MgPosition? Position { get; set; }        // one position at a time (long OR short)

        // Martingale
        public decimal CurrentOrderSize { get; set; }
        public int ConsecutiveLosses { get; set; }
        public decimal RunningPnlDollar { get; set; }

        // Grid (MaratGGrid)
        public bool Grid { get; set; }
        public List<SimLot> Lots { get; } = new();
        public decimal CycleLotSize { get; set; }
        public DateTime? StarterReopenAt { get; set; }
        public SimPending? LevelOrder { get; set; }    // the one resting level limit
        public SimPending? StarterOrder { get; set; }  // starter reopen limit
        public int BaseCloses { get; set; }
    }

    private sealed class SimPending
    {
        public int Level { get; set; }
        public decimal Price { get; set; }
        public DateTime? RepriceAt { get; set; }
    }

    private sealed class SimLot
    {
        public int Level { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal EntryFee { get; set; }
    }

    private sealed class MgPosition
    {
        public bool IsLong { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal OrderSize { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal StopLoss { get; set; }
    }
}
