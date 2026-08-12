using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

/// <summary>
/// Cross-exchange perp-perp arbitrage (FuturesArbitrage).
///
/// Two futures accounts on two DIFFERENT exchanges: the primary (Strategy.AccountId, the client
/// the worker hands to ProcessAsync) and the secondary (Strategy.SecondAccountId, whose client
/// this handler builds itself). The bot watches the executable spread between the same contract
/// on both venues and opens a delta-neutral pair when it widens: SHORT on the expensive venue,
/// LONG on the cheap one — both market orders, one-way position mode.
///
///   entry spread % = (bid_expensive − ask_cheap) / ask_cheap × 100
///   exit  spread % = (ask_expensive − bid_cheap) / bid_cheap × 100   (cost to unwind)
///
/// Levels are a "spread grid": each opens independently once the entry spread reaches its
/// EntrySpreadPercent and closes independently once the exit spread falls to its
/// ExitSpreadPercent. All open levels share one direction (which venue is expensive), fixed by
/// the first level that opens and released back to None when the last level closes
/// (CompletedCycles++).
///
/// Per tick (worker's 5s loop): set leverage once → read both books → compute spreads →
/// unwind incomplete levels → threshold-close → open AT MOST ONE level. The one-level-per-tick
/// cap keeps a violent divergence from firing the whole ladder as market orders in one burst.
///
/// Leg risk is the core hazard here: the two legs sit on different exchanges and cannot fill
/// atomically. The long (cheap) leg goes first because it is the one we can always unwind with
/// a plain reduce-only market close; if the short leg is then rejected, the long is rolled back
/// immediately. If even the rollback fails, the naked leg is written into level state so the
/// next tick's unwind pass keeps retrying it — it is never dropped from bookkeeping.
///
/// After MaxConsecutiveFailures consecutive order failures the strategy stops itself WITHOUT
/// touching open positions (a broken order path is exactly when blind market closes are most
/// dangerous) and logs which levels are still live so the user can settle them manually.
///
/// Funding is not modeled in V1 — it lands on the exchange balances, outside recorded PnL.
/// </summary>
public class ArbitrageHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ~13 req/sec — same throttle the grid handlers use between consecutive placements.
    private const int InterOrderDelayMs = 75;

    // Used when the config leaves MaxConsecutiveFailures at 0 (older/hand-written configs).
    private const int DefaultMaxFailures = 3;

    public string StrategyType => StrategyTypes.FuturesArbitrage;

    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _factory;
    private readonly ILogger<ArbitrageHandler> _logger;

    public ArbitrageHandler(AppDbContext db, IExchangeServiceFactory factory, ILogger<ArbitrageHandler> logger)
    {
        _db = db;
        _factory = factory;
        _logger = logger;
    }

    // ────────────────────────── Tick entry point ──────────────────────────

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<ArbitrageConfig>(strategy.ConfigJson, JsonOptions);

        // Validation stops the bot itself (and saves) on failure — a misconfigured arbitrage bot
        // must not keep firing market orders across two accounts.
        var secondAccount = await ValidateAsync(strategy, config, ct);
        if (secondAccount == null) return;

        var state = JsonSerializer.Deserialize<ArbitrageState>(strategy.StateJson, JsonOptions)
                    ?? new ArbitrageState();

        IFuturesExchangeService secondExchange;
        try
        {
            secondExchange = _factory.CreateFutures(secondAccount);
        }
        catch (Exception ex)
        {
            Log(strategy, "Error", $"Failed to build the secondary exchange client: {Trim(ex.Message)}");
            _logger.LogError(ex, "Arbitrage {Id}: CreateFutures failed for second account {Acc}",
                strategy.Id, secondAccount.Id);
            strategy.Status = StrategyStatus.Stopped;
            await _db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            await RunTickAsync(strategy, config!, state, exchange, secondExchange, secondAccount, ct);
        }
        finally
        {
            secondExchange.Dispose();
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task RunTickAsync(Strategy strategy, ArbitrageConfig config, ArbitrageState state,
        IFuturesExchangeService primaryExchange, IFuturesExchangeService secondExchange,
        ExchangeAccount secondAccount, CancellationToken ct)
    {
        // Level identity = position in the ascending-by-EntrySpreadPercent ordering. The config
        // list itself is never mutated; we sort a copy defensively every tick.
        var levels = config.Levels.OrderBy(l => l.EntrySpreadPercent).ToList();
        SyncLevelStates(state, levels.Count);

        var symbolA = config.Symbol;
        var symbolB = string.IsNullOrWhiteSpace(config.SecondSymbol) ? config.Symbol : config.SecondSymbol!;

        if (!state.LeverageSet)
        {
            await TrySetLeverageAsync(strategy, primaryExchange, symbolA, config.Leverage, "primary");
            await TrySetLeverageAsync(strategy, secondExchange, symbolB, config.Leverage, "secondary");
            state.LeverageSet = true;
        }

        var bookA = await SafeBookAsync(primaryExchange, symbolA);
        var bookB = await SafeBookAsync(secondExchange, symbolB);
        state.LastCheckAt = DateTime.UtcNow;

        // A missing/degenerate book is a transient fetch failure, not a trading signal — skip the
        // tick silently (the caller's finally still persists LastCheckAt). It deliberately does
        // NOT count towards ConsecutiveFailures, which tracks order failures only.
        if (bookA == null || bookB == null) return;

        var ctx = new ArbContext
        {
            PrimaryExchange = primaryExchange,
            SecondaryExchange = secondExchange,
            PrimarySymbol = symbolA,
            SecondarySymbol = symbolB,
            PrimaryAccountId = strategy.AccountId,
            SecondaryAccountId = secondAccount.Id,
            PrimaryBook = bookA,
            SecondaryBook = bookB
        };

        var primaryExpensive = BuildLegs(ctx, ArbitrageDirection.PrimaryExpensive);
        var secondaryExpensive = BuildLegs(ctx, ArbitrageDirection.SecondaryExpensive);
        var entryPrimary = primaryExpensive.EntrySpreadPercent;
        var entrySecondary = secondaryExpensive.EntrySpreadPercent;

        // Monitoring value is signed: positive = primary venue is the expensive one.
        state.LastSpreadPercent = entryPrimary >= entrySecondary ? entryPrimary : -entrySecondary;

        // ── Closing runs BEFORE opening: shrinking exposure always wins the tick. ──
        if (state.Direction != ArbitrageDirection.None)
        {
            var legs = BuildLegs(ctx, state.Direction);
            await ProcessClosesAsync(strategy, levels, state, legs, ct);
        }

        if (!CheckFailureLimit(strategy, config, state)) return;

        await ProcessOpenAsync(strategy, config, levels, state, ctx, entryPrimary, entrySecondary, ct);

        CheckFailureLimit(strategy, config, state);
    }

    // ────────────────────────── Closing ──────────────────────────

    private async Task ProcessClosesAsync(Strategy strategy, List<ArbitrageLevelConfig> levels,
        ArbitrageState state, LegPair legs, CancellationToken ct)
    {
        // Bookkeeping-only levels (flagged open but carrying no quantity) need no orders.
        foreach (var empty in state.Levels.Where(l => l.IsOpen && l.ShortQty <= 0 && l.LongQty <= 0).ToList())
            empty.IsOpen = false;

        var closedAny = false;

        // ── Pass 1: incomplete levels. A level holding exactly one leg is NOT delta-neutral —
        // it is a naked directional position from a half-filled open or a half-filled close, so
        // it is unwound at market immediately, regardless of the spread. Levels whose config
        // entry disappeared (config edited while open) are unwound the same way.
        var incomplete = state.Levels
            .Where(l => l.IsOpen && (IsSingleLegged(l) || l.Index >= levels.Count))
            .OrderBy(l => l.Index)
            .ToList();

        foreach (var level in incomplete)
        {
            var reason = IsStaleLevel(level, levels.Count) ? "StaleUnwind" : "LegUnwind";
            var (flat, net) = await CloseLevelAsync(strategy, level, legs, reason, state, ct);
            if (flat)
            {
                closedAny = true;
                Log(strategy, "Warning",
                    $"Level #{level.Index}: incomplete pair unwound ({reason}), net={Fmt(net)} USDT");
            }
            await Task.Delay(InterOrderDelayMs, ct);
        }

        // ── Pass 2: threshold closes. Deepest levels (highest entry spread) first — they are the
        // ones that were opened last and carry the most spread risk if the divergence resumes.
        var exitSpread = legs.ExitSpreadPercent;

        var candidates = state.Levels
            .Where(l => l.IsOpen && l.Index < levels.Count && l.ShortQty > 0 && l.LongQty > 0)
            .OrderByDescending(l => levels[l.Index].EntrySpreadPercent)
            .ToList();

        foreach (var level in candidates)
        {
            var cfg = levels[level.Index];
            if (exitSpread > cfg.ExitSpreadPercent) continue;

            var (flat, net) = await CloseLevelAsync(strategy, level, legs, "SpreadExit", state, ct);
            if (flat)
            {
                closedAny = true;
                state.ConsecutiveFailures = 0;
                Log(strategy, "Info",
                    $"Level #{level.Index} closed: exitSpread={Fmt(exitSpread, 4)}% ≤ {Fmt(cfg.ExitSpreadPercent, 4)}% " +
                    $"(entered at {Fmt(level.EntrySpreadPercent, 4)}%), net={Fmt(net)} USDT, " +
                    $"total={Fmt(state.RealizedPnlUsdt)} USDT");
                _logger.LogInformation("Arbitrage {Id}: level {Lvl} closed, net={Net}",
                    strategy.Id, level.Index, Math.Round(net, 4));
            }
            await Task.Delay(InterOrderDelayMs, ct);
        }

        // Flat again → the direction lock is released and the round trip is counted.
        if (closedAny && state.Levels.All(l => !l.IsOpen))
        {
            state.Direction = ArbitrageDirection.None;
            state.CompletedCycles++;
            Log(strategy, "Info",
                $"All levels closed — cycle #{state.CompletedCycles} complete, " +
                $"realized={Fmt(state.RealizedPnlUsdt)} USDT");
        }
    }

    /// <summary>
    /// Market-closes whichever legs of <paramref name="level"/> still carry quantity and records
    /// one Trade per closed leg (each on its own account). A leg that closes successfully has its
    /// quantity zeroed immediately, so a failure on the other leg only leaves the survivor to be
    /// retried by the next tick's unwind pass. Returns whether the level ended flat plus the net
    /// PnL booked by this call.
    /// </summary>
    private async Task<(bool Flat, decimal NetPnl)> CloseLevelAsync(Strategy strategy,
        ArbitrageLevelState level, LegPair legs, string status, ArbitrageState state, CancellationToken ct)
    {
        decimal net = 0m;

        if (level.ShortQty > 0)
        {
            var qty = level.ShortQty;
            var result = await legs.ShortExchange.CloseShortAsync(legs.ShortSymbol, qty);
            if (result.Success)
            {
                var exit = PositivePrice(result.FilledPrice, legs.ShortBook?.AskPrice, level.ShortEntryPrice);
                var gross = (level.ShortEntryPrice - exit) * qty;
                // Entry fee was already booked on the opening Trade's Commission; the closing
                // Trade carries the exit fee only, while PnlDollar is net of BOTH (same
                // convention as the other handlers: PnlDollar is always the net number).
                var entryFee = level.ShortEntryPrice * qty * legs.ShortExchange.TakerFeeRate;
                var exitFee = exit * qty * legs.ShortExchange.TakerFeeRate;
                var legNet = gross - entryFee - exitFee;

                RecordTrade(strategy, legs.ShortAccountId, legs.ShortSymbol, "Buy", qty, exit,
                    result.OrderId, status, legNet, exitFee);
                state.RealizedPnlUsdt += legNet;
                net += legNet;
                level.ShortQty = 0;
            }
            else
            {
                state.ConsecutiveFailures++;
                Log(strategy, "Warning",
                    $"Level #{level.Index}: short close on {legs.ShortSymbol} failed: {Trim(result.ErrorMessage)} " +
                    $"— retry next tick (failures {state.ConsecutiveFailures})");
                _logger.LogWarning("Arbitrage {Id}: short close failed for level {Lvl}: {Err}",
                    strategy.Id, level.Index, result.ErrorMessage);
            }

            await Task.Delay(InterOrderDelayMs, ct);
        }

        if (level.LongQty > 0)
        {
            var qty = level.LongQty;
            var result = await legs.LongExchange.CloseLongAsync(legs.LongSymbol, qty);
            if (result.Success)
            {
                var exit = PositivePrice(result.FilledPrice, legs.LongBook?.BidPrice, level.LongEntryPrice);
                var gross = (exit - level.LongEntryPrice) * qty;
                var entryFee = level.LongEntryPrice * qty * legs.LongExchange.TakerFeeRate;
                var exitFee = exit * qty * legs.LongExchange.TakerFeeRate;
                var legNet = gross - entryFee - exitFee;

                RecordTrade(strategy, legs.LongAccountId, legs.LongSymbol, "Sell", qty, exit,
                    result.OrderId, status, legNet, exitFee);
                state.RealizedPnlUsdt += legNet;
                net += legNet;
                level.LongQty = 0;
            }
            else
            {
                state.ConsecutiveFailures++;
                Log(strategy, "Warning",
                    $"Level #{level.Index}: long close on {legs.LongSymbol} failed: {Trim(result.ErrorMessage)} " +
                    $"— retry next tick (failures {state.ConsecutiveFailures})");
                _logger.LogWarning("Arbitrage {Id}: long close failed for level {Lvl}: {Err}",
                    strategy.Id, level.Index, result.ErrorMessage);
            }
        }

        var flat = level.ShortQty <= 0 && level.LongQty <= 0;
        if (flat)
        {
            level.IsOpen = false;
            level.OpenedAt = null;
        }
        return (flat, net);
    }

    // ────────────────────────── Opening ──────────────────────────

    private async Task ProcessOpenAsync(Strategy strategy, ArbitrageConfig config,
        List<ArbitrageLevelConfig> levels, ArbitrageState state, ArbContext ctx,
        decimal entryPrimary, decimal entrySecondary, CancellationToken ct)
    {
        // Direction is locked while anything is open: every level of a cycle must sit on the same
        // side, otherwise the "pair" legs would net each other out across venues.
        ArbitrageDirection direction;
        if (state.Direction != ArbitrageDirection.None)
        {
            direction = state.Direction;
        }
        else if (config.AllowBothDirections && entrySecondary > entryPrimary)
        {
            direction = ArbitrageDirection.SecondaryExpensive;
        }
        else
        {
            direction = ArbitrageDirection.PrimaryExpensive;
        }

        var legs = BuildLegs(ctx, direction);
        var entrySpread = legs.EntrySpreadPercent;
        if (entrySpread <= 0) return;

        // Shallowest qualifying level first, and AT MOST ONE per tick — a spread that blows
        // through several thresholds at once still gets filled one level per 5s, which both
        // rate-limits the venues and gives the operator a chance to react.
        var next = state.Levels
            .Where(l => !l.IsOpen && l.Index < levels.Count)
            .OrderBy(l => l.Index)
            .FirstOrDefault(l => entrySpread >= levels[l.Index].EntrySpreadPercent);
        if (next == null) return;

        await TryOpenLevelAsync(strategy, next, levels[next.Index], legs, direction, entrySpread, state, ct);
    }

    /// <summary>
    /// Opens one delta-neutral pair: LONG on the cheap venue first, then SHORT on the expensive
    /// one. The order matters — the long is the leg we can unwind unconditionally, so if the
    /// short is rejected we roll the long back immediately instead of sitting on naked exposure.
    /// </summary>
    private async Task TryOpenLevelAsync(Strategy strategy, ArbitrageLevelState level,
        ArbitrageLevelConfig cfg, LegPair legs, ArbitrageDirection direction, decimal entrySpread,
        ArbitrageState state, CancellationToken ct)
    {
        var longResult = await legs.LongExchange.OpenLongAsync(legs.LongSymbol, cfg.NotionalUsdt);
        if (!longResult.Success)
        {
            // Nothing filled — no exposure, no rollback needed.
            state.ConsecutiveFailures++;
            Log(strategy, "Warning",
                $"Level #{level.Index}: long leg on {legs.LongSymbol} rejected: {Trim(longResult.ErrorMessage)} " +
                $"(failures {state.ConsecutiveFailures}) — level not opened");
            _logger.LogWarning("Arbitrage {Id}: long open failed for level {Lvl}: {Err}",
                strategy.Id, level.Index, longResult.ErrorMessage);
            return;
        }

        var longPrice = PositivePrice(longResult.FilledPrice, legs.LongBook?.AskPrice, 0m);
        var longQty = longResult.FilledQuantity ?? (longPrice > 0 ? cfg.NotionalUsdt / longPrice : 0m);
        var longFee = longPrice * longQty * legs.LongExchange.TakerFeeRate;

        // Booked as soon as it fills so the ledger reflects reality even if the pair never
        // completes (rollback path below records the matching close).
        RecordTrade(strategy, legs.LongAccountId, legs.LongSymbol, "Buy", longQty, longPrice,
            longResult.OrderId, "Filled", commission: longFee);

        await Task.Delay(InterOrderDelayMs, ct);

        var shortResult = await legs.ShortExchange.OpenShortAsync(legs.ShortSymbol, cfg.NotionalUsdt);
        if (!shortResult.Success)
        {
            state.ConsecutiveFailures++;
            Log(strategy, "Error",
                $"Level #{level.Index}: short leg on {legs.ShortSymbol} rejected: {Trim(shortResult.ErrorMessage)} " +
                $"— rolling back the {Fmt(longQty, 8)} long leg (failures {state.ConsecutiveFailures})");
            _logger.LogError("Arbitrage {Id}: short open failed for level {Lvl}: {Err}",
                strategy.Id, level.Index, shortResult.ErrorMessage);

            await RollbackLongAsync(strategy, level, legs, direction, longQty, longPrice, entrySpread, state);
            return;
        }

        var shortPrice = PositivePrice(shortResult.FilledPrice, legs.ShortBook?.BidPrice, 0m);
        var shortQty = shortResult.FilledQuantity ?? (shortPrice > 0 ? cfg.NotionalUsdt / shortPrice : 0m);
        var shortFee = shortPrice * shortQty * legs.ShortExchange.TakerFeeRate;

        RecordTrade(strategy, legs.ShortAccountId, legs.ShortSymbol, "Sell", shortQty, shortPrice,
            shortResult.OrderId, "Filled", commission: shortFee);

        level.IsOpen = true;
        level.ShortQty = shortQty;
        level.LongQty = longQty;
        level.ShortEntryPrice = shortPrice;
        level.LongEntryPrice = longPrice;
        level.EntrySpreadPercent = entrySpread;
        level.OpenedAt = DateTime.UtcNow;

        state.Direction = direction;
        state.ConsecutiveFailures = 0;

        Log(strategy, "Info",
            $"Level #{level.Index} opened @ spread {Fmt(entrySpread, 4)}% (threshold {Fmt(cfg.EntrySpreadPercent, 4)}%): " +
            $"SHORT {legs.ShortSymbol} {Fmt(shortQty, 8)} @ {Fmt(shortPrice, 8)} / " +
            $"LONG {legs.LongSymbol} {Fmt(longQty, 8)} @ {Fmt(longPrice, 8)}, " +
            $"notional=${Fmt(cfg.NotionalUsdt, 2)}/leg, exit at ≤{Fmt(cfg.ExitSpreadPercent, 4)}%");
        _logger.LogInformation(
            "Arbitrage {Id}: level {Lvl} opened, dir={Dir}, spread={Spread}%",
            strategy.Id, level.Index, direction, Math.Round(entrySpread, 4));
    }

    /// <summary>
    /// Unwinds a long leg whose short counterpart was rejected. If the rollback itself fails the
    /// leg is written into level state (long only, IsOpen) and the direction is locked, so the
    /// next tick's incomplete-level pass keeps retrying the close — a naked position is never
    /// silently dropped from bookkeeping.
    /// </summary>
    private async Task RollbackLongAsync(Strategy strategy, ArbitrageLevelState level, LegPair legs,
        ArbitrageDirection direction, decimal longQty, decimal longPrice, decimal entrySpread,
        ArbitrageState state)
    {
        if (longQty <= 0) return;

        var result = await legs.LongExchange.CloseLongAsync(legs.LongSymbol, longQty);
        if (result.Success)
        {
            var exit = PositivePrice(result.FilledPrice, legs.LongBook?.BidPrice, longPrice);
            var gross = (exit - longPrice) * longQty;
            var entryFee = longPrice * longQty * legs.LongExchange.TakerFeeRate;
            var exitFee = exit * longQty * legs.LongExchange.TakerFeeRate;
            var net = gross - entryFee - exitFee;

            RecordTrade(strategy, legs.LongAccountId, legs.LongSymbol, "Sell", longQty, exit,
                result.OrderId, "LegRollback", net, exitFee);
            state.RealizedPnlUsdt += net;

            Log(strategy, "Warning",
                $"Level #{level.Index}: long leg rolled back at {Fmt(exit, 8)}, cost={Fmt(net)} USDT — level not opened");
            return;
        }

        level.IsOpen = true;
        level.LongQty = longQty;
        level.LongEntryPrice = longPrice;
        level.ShortQty = 0;
        level.ShortEntryPrice = 0;
        level.EntrySpreadPercent = entrySpread;
        level.OpenedAt = DateTime.UtcNow;
        state.Direction = direction;

        Log(strategy, "Error",
            $"⚠️ Level #{level.Index}: rollback FAILED ({Trim(result.ErrorMessage)}) — naked LONG " +
            $"{Fmt(longQty, 8)} {legs.LongSymbol} left on the exchange, unwind will be retried every tick");
        _logger.LogError("Arbitrage {Id}: rollback failed for level {Lvl}, naked long {Qty} {Sym}",
            strategy.Id, level.Index, longQty, legs.LongSymbol);
    }

    // ────────────────────────── Manual force-close (controller entry) ──────────────────────────

    /// <summary>
    /// Manual "Close" entry point. Market-closes every open level on both venues, records the
    /// closing Trades with PnL, releases the direction lock and persists state. Builds its own
    /// secondary client exactly like ProcessAsync does.
    /// </summary>
    public async Task ForceCloseAsync(Strategy strategy, IFuturesExchangeService primaryExchange, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<ArbitrageConfig>(strategy.ConfigJson, JsonOptions);
        var state = JsonSerializer.Deserialize<ArbitrageState>(strategy.StateJson, JsonOptions)
                    ?? new ArbitrageState();

        if (config == null || string.IsNullOrWhiteSpace(config.Symbol))
        {
            Log(strategy, "Error", "Force close aborted — ConfigJson is invalid");
            await _db.SaveChangesAsync(ct);
            return;
        }

        var openLevels = state.Levels
            .Where(l => l.IsOpen && (l.ShortQty > 0 || l.LongQty > 0))
            .OrderBy(l => l.Index)
            .ToList();

        if (openLevels.Count == 0)
        {
            foreach (var level in state.Levels) level.IsOpen = false;
            state.Direction = ArbitrageDirection.None;
            Log(strategy, "Info", "Force close: nothing to close — state reset to flat");
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Without a direction we cannot tell which venue holds the short and which the long —
        // guessing would fire reduce-only closes at the wrong exchange.
        if (state.Direction == ArbitrageDirection.None)
        {
            Log(strategy, "Error",
                $"Force close aborted — {openLevels.Count} level(s) carry quantity but direction is None; " +
                "close the positions manually on the exchanges");
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (!strategy.SecondAccountId.HasValue)
        {
            Log(strategy, "Error", "Force close aborted — SecondAccountId is not set");
            await _db.SaveChangesAsync(ct);
            return;
        }

        var secondAccount = await LoadSecondAccountAsync(strategy.SecondAccountId.Value, ct);
        if (secondAccount == null)
        {
            Log(strategy, "Error", "Force close aborted — secondary exchange account not found");
            await _db.SaveChangesAsync(ct);
            return;
        }

        IFuturesExchangeService secondExchange;
        try
        {
            secondExchange = _factory.CreateFutures(secondAccount);
        }
        catch (Exception ex)
        {
            Log(strategy, "Error", $"Force close aborted — cannot build secondary client: {Trim(ex.Message)}");
            await _db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var symbolA = config.Symbol;
            var symbolB = string.IsNullOrWhiteSpace(config.SecondSymbol) ? config.Symbol : config.SecondSymbol!;

            // Books are best-effort here: they only price the PnL, and a close must go through
            // even when the book endpoint is down (fill price / entry price take over).
            var ctx = new ArbContext
            {
                PrimaryExchange = primaryExchange,
                SecondaryExchange = secondExchange,
                PrimarySymbol = symbolA,
                SecondarySymbol = symbolB,
                PrimaryAccountId = strategy.AccountId,
                SecondaryAccountId = secondAccount.Id,
                PrimaryBook = await SafeBookAsync(primaryExchange, symbolA),
                SecondaryBook = await SafeBookAsync(secondExchange, symbolB)
            };

            var legs = BuildLegs(ctx, state.Direction);

            Log(strategy, "Info", $"🛑 Force close: {openLevels.Count} open level(s), direction={state.Direction}");

            decimal total = 0m;
            var flatCount = 0;
            foreach (var level in openLevels)
            {
                var (flat, net) = await CloseLevelAsync(strategy, level, legs, "ForceClose", state, ct);
                total += net;
                if (flat) flatCount++;
                await Task.Delay(InterOrderDelayMs, ct);
            }

            if (state.Levels.All(l => !l.IsOpen))
            {
                state.Direction = ArbitrageDirection.None;
                state.CompletedCycles++;
            }

            var leftovers = state.Levels.Count(l => l.IsOpen);
            Log(strategy, leftovers == 0 ? "Info" : "Warning",
                $"Force close finished: {flatCount}/{openLevels.Count} level(s) flat, net={Fmt(total)} USDT, " +
                $"realized={Fmt(state.RealizedPnlUsdt)} USDT" +
                (leftovers > 0 ? $", {leftovers} level(s) still carry quantity — check the exchanges" : ""));
            _logger.LogInformation("Arbitrage {Id}: force close, {Flat}/{Total} flat, net={Net}",
                strategy.Id, flatCount, openLevels.Count, Math.Round(total, 4));
        }
        finally
        {
            secondExchange.Dispose();
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }
    }

    // ────────────────────────── Validation ──────────────────────────

    /// <summary>
    /// Validates config + both accounts. Returns the secondary account on success; on failure it
    /// logs, sets Status = Stopped, saves and returns null (the caller just returns).
    /// </summary>
    private async Task<ExchangeAccount?> ValidateAsync(Strategy strategy, ArbitrageConfig? config, CancellationToken ct)
    {
        string? error = null;

        if (config == null) error = "ConfigJson failed to parse";
        else if (string.IsNullOrWhiteSpace(config.Symbol)) error = "Symbol is empty";
        else if (config.Levels == null || config.Levels.Count == 0) error = "Levels list is empty";
        else if (config.Leverage < 1) error = $"Leverage must be ≥ 1 (current: {config.Leverage})";
        else
        {
            for (var i = 0; i < config.Levels.Count; i++)
            {
                var lvl = config.Levels[i];
                if (lvl.ExitSpreadPercent < 0)
                { error = $"level[{i}]: ExitSpreadPercent must be ≥ 0 (current: {lvl.ExitSpreadPercent})"; break; }
                if (lvl.EntrySpreadPercent <= lvl.ExitSpreadPercent)
                {
                    error = $"level[{i}]: EntrySpreadPercent ({lvl.EntrySpreadPercent}) must be > " +
                            $"ExitSpreadPercent ({lvl.ExitSpreadPercent})";
                    break;
                }
                if (lvl.NotionalUsdt <= 0)
                { error = $"level[{i}]: NotionalUsdt must be > 0 (current: {lvl.NotionalUsdt})"; break; }
            }
        }

        ExchangeAccount? secondAccount = null;

        if (error == null)
        {
            if (!strategy.SecondAccountId.HasValue)
            {
                error = "SecondAccountId is not set — cross-exchange arbitrage needs two accounts";
            }
            else if (strategy.SecondAccountId.Value == strategy.AccountId)
            {
                error = "SecondAccountId equals AccountId — both legs would land on the same account";
            }
            else
            {
                secondAccount = await LoadSecondAccountAsync(strategy.SecondAccountId.Value, ct);
                if (secondAccount == null)
                    error = $"Secondary exchange account {strategy.SecondAccountId.Value} not found";
                else if (!secondAccount.IsActive)
                    error = $"Secondary exchange account '{secondAccount.Name}' is inactive";
            }
        }

        if (error == null)
        {
            // Read the primary account off our own context instead of the (possibly stale, possibly
            // unloaded) navigation — the entity reaches us from the worker's outer scope.
            var primaryAccount = await _db.ExchangeAccounts
                .FirstOrDefaultAsync(a => a.Id == strategy.AccountId, ct);

            if (primaryAccount == null)
                error = "Primary exchange account not found";
            else if (primaryAccount.ExchangeType == ExchangeType.Dzengi ||
                     secondAccount!.ExchangeType == ExchangeType.Dzengi)
                error = "Dzengi is not supported by FuturesArbitrage — pick Bybit / Bitget / BingX accounts";
            else if (primaryAccount.ExchangeType == secondAccount!.ExchangeType)
                error = $"Both accounts are on {primaryAccount.ExchangeType} — " +
                        "cross-exchange arbitrage requires two different exchanges";
        }

        if (error == null) return secondAccount;

        Log(strategy, "Error", $"Invalid configuration — {error}. Strategy stopped.");
        _logger.LogError("Arbitrage {Id}: invalid configuration — {Error}", strategy.Id, error);
        strategy.Status = StrategyStatus.Stopped;
        await _db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Stops the bot once ConsecutiveFailures hits the configured ceiling. Open positions are
    /// deliberately left untouched — the failure counter usually means the order path itself is
    /// broken, which is the worst moment to fire more market orders. Returns false when stopped.
    /// </summary>
    private bool CheckFailureLimit(Strategy strategy, ArbitrageConfig config, ArbitrageState state)
    {
        var max = config.MaxConsecutiveFailures > 0 ? config.MaxConsecutiveFailures : DefaultMaxFailures;
        if (state.ConsecutiveFailures < max) return true;

        var open = state.Levels.Where(l => l.IsOpen).ToList();
        var detail = open.Count == 0
            ? "no open levels"
            : string.Join("; ", open.Select(l =>
                $"#{l.Index} short={Fmt(l.ShortQty, 8)}@{Fmt(l.ShortEntryPrice, 8)} " +
                $"long={Fmt(l.LongQty, 8)}@{Fmt(l.LongEntryPrice, 8)}"));

        Log(strategy, "Error",
            $"Stopped after {state.ConsecutiveFailures} consecutive order failures (limit {max}). " +
            $"Positions were NOT touched — open levels: {detail}");
        _logger.LogError("Arbitrage {Id}: stopped after {Fails} consecutive failures, {Open} level(s) still open",
            strategy.Id, state.ConsecutiveFailures, open.Count);

        strategy.Status = StrategyStatus.Stopped;
        return false;
    }

    // ────────────────────────── Exchange helpers ──────────────────────────

    private Task<ExchangeAccount?> LoadSecondAccountAsync(Guid accountId, CancellationToken ct) =>
        // The proxy graph MUST be included: ExchangeServiceFactory's proxy selection reads
        // AccountProxies/Proxy off the entity and would otherwise run the leg direct.
        _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

    /// <summary>
    /// Best-effort leverage pin, clamped to the symbol's risk-limit maximum. Never fatal: the bot
    /// sizes by fixed notional, so a stale leverage only risks an order rejection, which the
    /// leg-risk path already handles.
    /// </summary>
    private async Task TrySetLeverageAsync(Strategy strategy, IFuturesExchangeService exchange,
        string symbol, int leverage, string legName)
    {
        try
        {
            var maxLev = await exchange.GetMaxLeverageAsync(symbol);
            var target = maxLev.HasValue && maxLev.Value > 0 ? Math.Min(leverage, maxLev.Value) : leverage;

            var ok = await exchange.SetLeverageAsync(symbol, target);
            if (!ok)
                Log(strategy, "Warning",
                    $"Could not set {target}x leverage on the {legName} leg ({symbol}) — using the account's current value");
            else
                _logger.LogDebug("Arbitrage {Id}: {Leg} leverage set to {Lev}x for {Symbol}",
                    strategy.Id, legName, target, symbol);
        }
        catch (NotSupportedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Arbitrage {Id}: SetLeverage failed on the {Leg} leg ({Symbol})",
                strategy.Id, legName, symbol);
        }
    }

    /// <summary>Top-of-book read that swallows transient failures — null means "skip this tick".</summary>
    private async Task<BookTickerDto?> SafeBookAsync(IFuturesExchangeService exchange, string symbol)
    {
        try
        {
            var book = await exchange.GetBookTickerAsync(symbol);
            if (book == null || book.BidPrice <= 0 || book.AskPrice <= 0)
            {
                _logger.LogDebug("Arbitrage: empty/degenerate book for {Symbol}", symbol);
                return null;
            }
            return book;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Arbitrage: GetBookTickerAsync failed for {Symbol}", symbol);
            return null;
        }
    }

    // ────────────────────────── Leg binding ──────────────────────────

    // Per-tick view of both venues. Nothing here is persisted — it is rebuilt every tick from
    // config + freshly fetched books.
    private sealed class ArbContext
    {
        public IFuturesExchangeService PrimaryExchange = null!;
        public IFuturesExchangeService SecondaryExchange = null!;
        public string PrimarySymbol = string.Empty;
        public string SecondarySymbol = string.Empty;
        public Guid PrimaryAccountId;
        public Guid SecondaryAccountId;
        public BookTickerDto? PrimaryBook;
        public BookTickerDto? SecondaryBook;
    }

    // Direction resolved into "which venue is the short (expensive) side". Every order in the
    // handler is expressed against this binding, so the direction sign lives in exactly one place.
    private sealed class LegPair
    {
        public IFuturesExchangeService ShortExchange = null!;
        public string ShortSymbol = string.Empty;
        public Guid ShortAccountId;
        public BookTickerDto? ShortBook;

        public IFuturesExchangeService LongExchange = null!;
        public string LongSymbol = string.Empty;
        public Guid LongAccountId;
        public BookTickerDto? LongBook;

        // Executable entry: sell the expensive venue at its bid, buy the cheap one at its ask.
        public decimal EntrySpreadPercent =>
            ShortBook != null && LongBook != null && LongBook.AskPrice > 0
                ? (ShortBook.BidPrice - LongBook.AskPrice) / LongBook.AskPrice * 100m
                : 0m;

        // Cost to unwind right now: buy back the short at its ask, sell the long at its bid.
        public decimal ExitSpreadPercent =>
            ShortBook != null && LongBook != null && LongBook.BidPrice > 0
                ? (ShortBook.AskPrice - LongBook.BidPrice) / LongBook.BidPrice * 100m
                : 0m;
    }

    private static LegPair BuildLegs(ArbContext ctx, ArbitrageDirection direction) =>
        direction == ArbitrageDirection.SecondaryExpensive
            ? new LegPair
            {
                ShortExchange = ctx.SecondaryExchange,
                ShortSymbol = ctx.SecondarySymbol,
                ShortAccountId = ctx.SecondaryAccountId,
                ShortBook = ctx.SecondaryBook,
                LongExchange = ctx.PrimaryExchange,
                LongSymbol = ctx.PrimarySymbol,
                LongAccountId = ctx.PrimaryAccountId,
                LongBook = ctx.PrimaryBook
            }
            : new LegPair
            {
                ShortExchange = ctx.PrimaryExchange,
                ShortSymbol = ctx.PrimarySymbol,
                ShortAccountId = ctx.PrimaryAccountId,
                ShortBook = ctx.PrimaryBook,
                LongExchange = ctx.SecondaryExchange,
                LongSymbol = ctx.SecondarySymbol,
                LongAccountId = ctx.SecondaryAccountId,
                LongBook = ctx.SecondaryBook
            };

    // ────────────────────────── State helpers ──────────────────────────

    /// <summary>
    /// Keeps one state slot per configured level. Slots whose config entry disappeared are dropped
    /// only when closed — an open one still owns real positions and is unwound by the close pass.
    /// </summary>
    private static void SyncLevelStates(ArbitrageState state, int levelCount)
    {
        for (var i = 0; i < levelCount; i++)
        {
            if (state.Levels.All(l => l.Index != i))
                state.Levels.Add(new ArbitrageLevelState { Index = i });
        }

        state.Levels.RemoveAll(l => l.Index >= levelCount && !l.IsOpen);
        state.Levels.Sort((a, b) => a.Index.CompareTo(b.Index));
    }

    // Exactly one leg carries quantity → the level is directional, not delta-neutral.
    private static bool IsSingleLegged(ArbitrageLevelState level) =>
        (level.ShortQty > 0) != (level.LongQty > 0);

    // The level's config entry was removed while it was open (user edited the ladder live).
    private static bool IsStaleLevel(ArbitrageLevelState level, int levelCount) => level.Index >= levelCount;

    // Prefer the exchange's fill price, then the book price we quoted against, then the fallback.
    private static decimal PositivePrice(decimal? filled, decimal? book, decimal fallback)
    {
        if (filled.HasValue && filled.Value > 0) return filled.Value;
        if (book.HasValue && book.Value > 0) return book.Value;
        return fallback;
    }

    private static string Fmt(decimal value, int decimals = 4) =>
        Math.Round(value, decimals).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Trim(string? message, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(message)) return "unknown error";
        return message.Length <= max ? message : message[..max];
    }

    // ────────────────────────── Helpers: persistence ──────────────────────────

    // accountId is explicit (unlike the single-account handlers): the two legs live on two
    // different exchange accounts, so each Trade must be attributed to the account that filled it.
    private void RecordTrade(Strategy strategy, Guid accountId, string symbol, string side,
        decimal quantity, decimal price, string? orderId, string status,
        decimal? pnlDollar = null, decimal? commission = null)
    {
        _db.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            AccountId = accountId,
            ExchangeOrderId = orderId ?? "",
            Symbol = symbol,
            Side = side,
            Quantity = quantity,
            Price = price,
            Status = status,
            ExecutedAt = DateTime.UtcNow,
            PnlDollar = pnlDollar,
            Commission = commission
        });
    }

    private static void SaveState(Strategy strategy, ArbitrageState state)
    {
        strategy.StateJson = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private void Log(Strategy strategy, string level, string message)
    {
        // strategy_logs.message is capped at 1000 chars — a wide ladder can overflow the
        // failure/force-close summaries, so truncate instead of letting SaveChanges throw.
        if (message.Length > 1000) message = message[..1000];

        _db.StrategyLogs.Add(new StrategyLog
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            Level = level,
            Message = message,
            CreatedAt = DateTime.UtcNow
        });
    }
}
