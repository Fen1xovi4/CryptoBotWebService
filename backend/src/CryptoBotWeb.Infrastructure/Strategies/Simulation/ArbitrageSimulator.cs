using System.Globalization;
using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest counterpart of <see cref="ArbitrageHandler"/> (cross-exchange perp-perp arbitrage).
/// Mirrors the handler's tick semantics on two synchronized 1-minute price paths:
///
///   primary series (venue A) = <c>ctx.PathCandles</c>       — the strategy's AccountId
///   second  series (venue B) = <c>ctx.SecondSymbolPathCandles</c> — the SecondAccountId venue
///
/// Per tick: compute both directional spreads → close levels whose exit threshold is met
/// (deepest entry spread first) → open AT MOST ONE level (shallowest qualifying one). Direction is
/// locked by the first level that opens and released back to None when the last level closes
/// (CompletedCycles++), exactly like the live state machine.
///
///   entry spread % = (bid_expensive − ask_cheap) / ask_cheap × 100
///   exit  spread % = (ask_expensive − bid_cheap) / bid_cheap × 100
///
/// ── Path synchronization ──
/// The two series are matched by candle OpenTime (inner join): a minute present on only one venue
/// is skipped entirely, since a spread computed against a stale price is meaningless. Each matched
/// minute is walked with the shared 4-sub-tick convention (<see cref="CandlePathHelper.GetTicks"/>)
/// on BOTH candles; sub-tick k of A and sub-tick k of B carry the same timestamp (0/25/50/75% of
/// the minute), so the k-th pair is one simultaneous observation of both books.
///
/// ── Model limitations (results are OPTIMISTIC vs live) ──
/// 1. There is no bid/ask history: both sides of both books are approximated by the tick price
///    (book spread = 0). Consequently entry spread and exit spread are the SAME number for a given
///    direction, whereas live the bot must additionally cross two book spreads — live entries are
///    rarer and live exits trigger later than here.
/// 2. The sub-tick walk order depends on each candle's own direction (bullish: O→L→H→C, bearish:
///    O→H→L→C). On a minute where the two venues moved in opposite directions, an extreme of A is
///    paired with the opposite extreme of B, which overstates the momentary spread. Spread
///    excursions are therefore an upper bound.
/// 3. Fees: both legs are market orders → taker on every fill, charged at the SINGLE taker rate
///    carried by <see cref="SimulationContext.TakerFeeRate"/> (the primary account's rate or the
///    request override). The two venues may charge different rates live; the engine warns when
///    they differ.
/// 4. Leg risk is not modeled: sim fills always succeed, so there are no incomplete/naked levels,
///    no rollbacks and no ConsecutiveFailures stop. Order-rate throttling (InterOrderDelayMs),
///    leverage pinning and quantity rounding to exchange steps are likewise skipped.
/// 5. One level per SIM tick = one level per 15s (4 ticks/minute); live is one per 5s poll.
/// 6. Funding is NOT modeled in V1 (same as the live handler's recorded PnL).
///
/// Bookkeeping goes through <see cref="SimLedger"/>: each level opens with two RecordOpen fills
/// (cheap Long leg + expensive Short leg, both taker) and closes as ONE aggregated round trip
/// (RecordCloseMultiLeg, Side = "Both"), so win rate counts spread round trips rather than legs.
/// </summary>
public class ArbitrageSimulator : IStrategySimulator
{
    // Mirror ArbitrageHandler.JsonOptions exactly so a live bot's ConfigJson deserializes identically.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.FuturesArbitrage;

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();
        result.Warnings.AddRange(context.Warnings);

        ArbitrageConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ArbitrageConfig>(context.ConfigJson, JsonOptions);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"FuturesArbitrage: ConfigJson не распарсился: {ex.Message}");
            return result;
        }

        if (config == null)
        {
            result.Warnings.Add("FuturesArbitrage: ConfigJson = null");
            return result;
        }

        var error = Validate(config);
        if (error != null)
        {
            result.Warnings.Add($"FuturesArbitrage: некорректная конфигурация — {error}");
            return result;
        }

        if (context.PathCandles.Count == 0)
        {
            result.Warnings.Add("FuturesArbitrage: пустой PathCandles (первичная биржа) — нечего симулировать.");
            return result;
        }

        if (context.SecondSymbolPathCandles == null || context.SecondSymbolPathCandles.Count == 0)
        {
            result.Warnings.Add(
                "FuturesArbitrage: нет второй ценовой дорожки (SecondSymbolPathCandles) — " +
                "межбиржевой спред посчитать не по чему, симуляция не выполнена.");
            return result;
        }

        new Engine(context, config, result).Run();
        return result;
    }

    /// <summary>Config validation ported from ArbitrageHandler.ValidateAsync (config part only).</summary>
    private static string? Validate(ArbitrageConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Symbol)) return "Symbol пуст";
        if (config.Levels == null || config.Levels.Count == 0) return "список Levels пуст";
        if (config.Leverage < 1) return $"Leverage должен быть ≥ 1 (сейчас {config.Leverage})";

        for (var i = 0; i < config.Levels.Count; i++)
        {
            var lvl = config.Levels[i];
            if (lvl.ExitSpreadPercent < 0)
                return $"level[{i}]: ExitSpreadPercent должен быть ≥ 0 (сейчас {lvl.ExitSpreadPercent})";
            if (lvl.EntrySpreadPercent <= lvl.ExitSpreadPercent)
                return $"level[{i}]: EntrySpreadPercent ({lvl.EntrySpreadPercent}) должен быть > " +
                       $"ExitSpreadPercent ({lvl.ExitSpreadPercent})";
            if (lvl.NotionalUsdt <= 0)
                return $"level[{i}]: NotionalUsdt должен быть > 0 (сейчас {lvl.NotionalUsdt})";
        }

        return null;
    }

    // ────────────────────────── Engine ──────────────────────────

    private sealed class Engine
    {
        private readonly SimulationContext _ctx;
        private readonly ArbitrageConfig _cfg;
        private readonly SimulationRunResult _res;
        private readonly SimLedger _ledger;
        private readonly decimal _taker;

        // Level identity = position in the ascending-by-EntrySpreadPercent ordering (same as the handler).
        private readonly List<ArbitrageLevelConfig> _levels;
        private readonly List<LevelSlot> _slots = new();

        private ArbitrageDirection _direction = ArbitrageDirection.None;
        private int _cycles;

        public Engine(SimulationContext ctx, ArbitrageConfig cfg, SimulationRunResult res)
        {
            _ctx = ctx;
            _cfg = cfg;
            _res = res;
            _taker = ctx.TakerFeeRate;
            _ledger = new SimLedger(ctx.MakerFeeRate, ctx.TakerFeeRate);

            _levels = cfg.Levels.OrderBy(l => l.EntrySpreadPercent).ToList();
            for (var i = 0; i < _levels.Count; i++)
                _slots.Add(new LevelSlot { Index = i });
        }

        public void Run()
        {
            var path = BuildMatchedPath(out var skipped);
            if (path.Count == 0)
            {
                _res.Warnings.Add(
                    "FuturesArbitrage: ценовые дорожки двух бирж не пересекаются по времени (нет общих минут) — " +
                    "симулировать нечего.");
                return;
            }

            if (skipped > 0)
                _res.Warnings.Add(
                    $"FuturesArbitrage: {skipped} минут(ы) пропущено — на второй бирже нет свечи с тем же OpenTime " +
                    $"(симулировано {path.Count} общих минут).");

            _res.Warnings.Add(
                "FuturesArbitrage: истории bid/ask нет — обе стороны книги приняты равными цене тика (спред книги = 0), " +
                "поэтому спред входа и спред выхода совпадают; входов больше, а выходы срабатывают раньше, чем в живой торговле.");
            _res.Warnings.Add(
                $"FuturesArbitrage: обе ноги считаются по одной taker-ставке {Fmt(_taker * 100m, 4)}% — " +
                "у двух бирж комиссии могут отличаться.");

            foreach (var minute in path)
            {
                // Hourly equity boundary, marked at both venues' opens.
                if (minute.A.OpenTime.Minute == 0)
                    _ledger.SampleEquity(minute.A.OpenTime, Unrealized(minute.A.Open, minute.B.Open));

                var ticksA = CandlePathHelper.GetTicks(minute.A);
                var ticksB = CandlePathHelper.GetTicks(minute.B);
                var steps = Math.Min(ticksA.Length, ticksB.Length);

                for (var k = 0; k < steps; k++)
                    ProcessTick(ticksA[k].Time, ticksA[k].Price, ticksB[k].Price);

                _ledger.TrackOpenNotional(OpenNotional());
            }

            var last = path[^1];
            _ledger.SampleEquity(last.A.CloseTime, Unrealized(last.A.Close, last.B.Close));

            _res.Trades.AddRange(_ledger.Trades);
            _res.EquityCurve.AddRange(_ledger.EquityCurve);
            BuildSummary(path);
        }

        // ── one simultaneous observation of both venues ──
        private void ProcessTick(DateTime time, decimal priceA, decimal priceB)
        {
            if (priceA <= 0 || priceB <= 0) return; // degenerate candle — treat as a missed poll

            var entryPrimary = SpreadPercent(priceA, priceB);   // primary venue is the expensive one
            var entrySecondary = SpreadPercent(priceB, priceA); // second venue is the expensive one

            // Closing runs BEFORE opening: shrinking exposure always wins the tick (as in the handler).
            if (_direction != ArbitrageDirection.None)
                ProcessCloses(time, priceA, priceB);

            ProcessOpen(time, priceA, priceB, entryPrimary, entrySecondary);
        }

        // ── closing: deepest entry spread first, threshold per level ──
        private void ProcessCloses(DateTime time, decimal priceA, decimal priceB)
        {
            var exitSpread = ExitSpread(_direction, priceA, priceB);

            var candidates = _slots
                .Where(s => s.IsOpen)
                .OrderByDescending(s => _levels[s.Index].EntrySpreadPercent)
                .ToList();

            var closedAny = false;
            foreach (var slot in candidates)
            {
                if (exitSpread > _levels[slot.Index].ExitSpreadPercent) continue;
                CloseLevel(time, slot, priceA, priceB, exitSpread);
                closedAny = true;
            }

            // Flat again → the direction lock is released and the round trip is counted.
            if (closedAny && _slots.All(s => !s.IsOpen))
            {
                _direction = ArbitrageDirection.None;
                _cycles++;
            }
        }

        private void CloseLevel(DateTime time, LevelSlot slot, decimal priceA, decimal priceB, decimal exitSpread)
        {
            var (shortPrice, longPrice) = LegPrices(_direction, priceA, priceB);

            var gross = (slot.ShortEntry - shortPrice) * slot.ShortQty
                        + (longPrice - slot.LongEntry) * slot.LongQty;

            var closeNotional = shortPrice * slot.ShortQty + longPrice * slot.LongQty;
            var closeFee = closeNotional * _taker; // both legs close at market → taker
            var entryNotional = slot.ShortEntry * slot.ShortQty + slot.LongEntry * slot.LongQty;
            var pnlPercent = entryNotional > 0 ? gross / entryNotional * 100m : 0m;

            // The pair is one logical round trip, but it is closed by two fills on two venues:
            // report the quantity-weighted aggregate so Price × Quantity still equals NotionalUsd.
            var qty = slot.ShortQty + slot.LongQty;
            var price = qty > 0 ? closeNotional / qty : 0m;

            _ledger.RecordCloseMultiLeg(time, "Both", "Close", price, qty, closeNotional, closeFee,
                slot.EntryFees, gross, pnlPercent,
                $"Level #{slot.Index} exitSpread={Fmt(exitSpread)}% ≤ {Fmt(_levels[slot.Index].ExitSpreadPercent)}% " +
                $"(entered {Fmt(slot.EntrySpreadPercent)}%), short@{Fmt(shortPrice, 8)} / long@{Fmt(longPrice, 8)}");

            slot.Reset();
        }

        // ── opening: at most one level per tick, shallowest qualifying first ──
        private void ProcessOpen(DateTime time, decimal priceA, decimal priceB,
            decimal entryPrimary, decimal entrySecondary)
        {
            // Direction is locked while anything is open; otherwise the better side wins, and with
            // AllowBothDirections = false only PrimaryExpensive setups are taken (handler parity).
            var direction = _direction != ArbitrageDirection.None
                ? _direction
                : _cfg.AllowBothDirections && entrySecondary > entryPrimary
                    ? ArbitrageDirection.SecondaryExpensive
                    : ArbitrageDirection.PrimaryExpensive;

            var entrySpread = direction == ArbitrageDirection.PrimaryExpensive ? entryPrimary : entrySecondary;
            if (entrySpread <= 0) return;

            // _slots is ordered by Index, i.e. ascending EntrySpreadPercent.
            var next = _slots.FirstOrDefault(s => !s.IsOpen && entrySpread >= _levels[s.Index].EntrySpreadPercent);
            if (next == null) return;

            OpenLevel(time, next, direction, priceA, priceB, entrySpread);
        }

        private void OpenLevel(DateTime time, LevelSlot slot, ArbitrageDirection direction,
            decimal priceA, decimal priceB, decimal entrySpread)
        {
            var cfg = _levels[slot.Index];
            var (shortPrice, longPrice) = LegPrices(direction, priceA, priceB);
            if (shortPrice <= 0 || longPrice <= 0) return;

            // Fixed USDT notional per leg (each venue gets NotionalUsdt), like the live handler.
            var shortQty = cfg.NotionalUsdt / shortPrice;
            var longQty = cfg.NotionalUsdt / longPrice;
            if (shortQty <= 0 || longQty <= 0) return;

            var head = $"Level #{slot.Index} spread {Fmt(entrySpread)}% ≥ {Fmt(cfg.EntrySpreadPercent)}% ({direction})";
            _ledger.RecordOpen(time, "Long", "Open", longPrice, longQty, taker: true, $"{head} — cheap leg");
            _ledger.RecordOpen(time, "Short", "Open", shortPrice, shortQty, taker: true, $"{head} — expensive leg");

            slot.IsOpen = true;
            slot.ShortQty = shortQty;
            slot.LongQty = longQty;
            slot.ShortEntry = shortPrice;
            slot.LongEntry = longPrice;
            slot.EntrySpreadPercent = entrySpread;
            // Held per level: SimLedger's single-position fee pool cannot attribute entry fees when
            // several levels are open at once, so the pair carries its own opening fees to the close.
            slot.EntryFees = (shortPrice * shortQty + longPrice * longQty) * _taker;

            _direction = direction;
            _ledger.TrackOpenNotional(OpenNotional());
        }

        // ── spread helpers (bid = ask = tick price, see the class remarks) ──

        private static decimal SpreadPercent(decimal expensive, decimal cheap)
            => cheap > 0 ? (expensive - cheap) / cheap * 100m : 0m;

        // With a zero-width book this equals the entry spread of the same direction; kept as its own
        // helper so the live formula stays visible and a future bid/ask path can diverge here.
        private static decimal ExitSpread(ArbitrageDirection direction, decimal priceA, decimal priceB)
        {
            var (shortPrice, longPrice) = LegPrices(direction, priceA, priceB);
            return SpreadPercent(shortPrice, longPrice);
        }

        // Direction resolved into "which venue holds the short (expensive) leg".
        private static (decimal ShortPrice, decimal LongPrice) LegPrices(
            ArbitrageDirection direction, decimal priceA, decimal priceB)
            => direction == ArbitrageDirection.SecondaryExpensive ? (priceB, priceA) : (priceA, priceB);

        // ── equity / exposure ──

        private decimal Unrealized(decimal markA, decimal markB)
        {
            if (_direction == ArbitrageDirection.None) return 0m;

            var (shortMark, longMark) = LegPrices(_direction, markA, markB);
            var total = 0m;
            foreach (var slot in _slots)
            {
                if (!slot.IsOpen) continue;
                total += (slot.ShortEntry - shortMark) * slot.ShortQty
                         + (longMark - slot.LongEntry) * slot.LongQty;
            }
            return total;
        }

        private decimal OpenNotional()
        {
            var total = 0m;
            foreach (var slot in _slots)
            {
                if (!slot.IsOpen) continue;
                total += slot.ShortEntry * slot.ShortQty + slot.LongEntry * slot.LongQty;
            }
            return total;
        }

        private void BuildSummary(List<MatchedMinute> path)
        {
            var lastA = path[^1].A.Close;
            var lastB = path[^1].B.Close;
            var unrealizedAtEnd = Unrealized(lastA, lastB);
            var openLevels = _slots.Count(s => s.IsOpen);

            _res.Summary = new SimulationRunSummary
            {
                TotalTrades = _ledger.ClosedTrades,
                WinningTrades = _ledger.WinningTrades,
                LosingTrades = _ledger.LosingTrades,
                WinRate = _ledger.ClosedTrades > 0
                    ? Math.Round((decimal)_ledger.WinningTrades / _ledger.ClosedTrades * 100m, 2)
                    : 0m,
                GrossPnlUsd = Math.Round(_ledger.GrossRealizedUsd, 8),
                FeesUsd = Math.Round(_ledger.FeesUsd, 8),
                FundingPnlUsd = 0m, // funding is not modeled in V1 (same as the live handler)
                NetPnlUsd = Math.Round(_ledger.GrossRealizedUsd - _ledger.FeesUsd, 8),
                MaxDrawdownUsd = Math.Round(_ledger.MaxDrawdownUsd, 8),
                MaxDrawdownPercent = _ledger.MaxNotionalUsd > 0
                    ? Math.Round(_ledger.MaxDrawdownUsd / _ledger.MaxNotionalUsd * 100m, 4)
                    : 0m,
                MaxNotionalUsd = Math.Round(_ledger.MaxNotionalUsd, 8), // both legs of every open level
                OpenPositionsAtEnd = openLevels,                        // open LEVELS (two legs each)
                UnrealizedPnlAtEndUsd = Math.Round(unrealizedAtEnd, 8),
                CompletedCycles = _cycles,
                StartTime = path[0].A.OpenTime,
                EndTime = path[^1].A.CloseTime,
                PathCandlesProcessed = path.Count
            };

            if (openLevels > 0)
                _res.Warnings.Add(
                    $"FuturesArbitrage: данные закончились со спредом выше порога выхода — {openLevels} уровень(ей) " +
                    "остался открыт, его PnL учтён только как нереализованный.");
        }

        // ── path synchronization: inner join of the two 1m series on OpenTime ──
        private List<MatchedMinute> BuildMatchedPath(out int skipped)
        {
            var second = new Dictionary<DateTime, CandleDto>(_ctx.SecondSymbolPathCandles!.Count);
            foreach (var c in _ctx.SecondSymbolPathCandles!)
                second[c.OpenTime] = c;

            var list = new List<MatchedMinute>(Math.Min(_ctx.PathCandles.Count, second.Count));
            skipped = 0;

            foreach (var a in _ctx.PathCandles)
            {
                if (second.TryGetValue(a.OpenTime, out var b))
                    list.Add(new MatchedMinute(a, b));
                else
                    skipped++; // a minute that exists on only one venue cannot price a spread
            }

            return list;
        }

        private static string Fmt(decimal value, int decimals = 4) =>
            Math.Round(value, decimals).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One minute present on BOTH venues: A = primary series, B = second series.</summary>
    private readonly record struct MatchedMinute(CandleDto A, CandleDto B);

    /// <summary>Runtime state of one spread level (sim counterpart of ArbitrageLevelState).</summary>
    private sealed class LevelSlot
    {
        public int Index;
        public bool IsOpen;

        public decimal ShortQty;
        public decimal LongQty;
        public decimal ShortEntry;
        public decimal LongEntry;
        public decimal EntrySpreadPercent;

        /// <summary>Taker fees paid opening BOTH legs — carried to the close for the round-trip net.</summary>
        public decimal EntryFees;

        public void Reset()
        {
            IsOpen = false;
            ShortQty = 0;
            LongQty = 0;
            ShortEntry = 0;
            LongEntry = 0;
            EntrySpreadPercent = 0;
            EntryFees = 0;
        }
    }
}
