using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Helpers;
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
/// Per tick (ArbitrageFastLoopService, 1s — this strategy is excluded from the shared 5s loop):
/// set leverage once → read both books off the websocket cache → compute spreads → unwind
/// incomplete levels → threshold-close → open AT MOST ONE level. The one-level-per-tick cap is
/// paired with MinSecondsBetweenOpens so a violent divergence still cannot fire the whole ladder
/// as market orders in one burst, whatever the loop interval happens to be.
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

    // Back-off between leverage-pin retries after a leg rejects the change.
    private const int LeverageRetryMinutes = 15;

    // Minimum spacing between two level openings, in seconds. Decoupled from the loop interval on
    // purpose — see ProcessOpenAsync.
    private const int MinSecondsBetweenOpens = 5;

    // Reading a market order back: how many times, and how far apart, to ask the exchange for the
    // fill before giving up and booking the estimate. Market fills are immediate; the retries only
    // cover the exchange's own bookkeeping lag between "accepted" and "visible as Filled".
    private const int FillConfirmAttempts = 3;
    private const int FillConfirmDelayMs = 200;

    // Account fee rates are re-read from the exchanges this often (tiers change rarely), and a
    // failed read is retried after this many minutes while the published constant fills in.
    private const int FeeRateRefreshHours = 24;
    private const int FeeRateRetryMinutes = 15;

    public string StrategyType => StrategyTypes.FuturesArbitrage;

    // A stream quote older than this is not trusted for a trading decision — we fall back to a
    // REST snapshot for that tick. Streams push every few hundred ms at most, so anything past
    // two seconds means the socket is silent, not that the market is quiet.
    private const int MaxQuoteAgeMs = 2000;

    // How often the "running on REST, stream is down" warning may reach the strategy log.
    private const int QuoteFallbackWarnMinutes = 10;

    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _factory;
    private readonly IQuoteStreamService _quotes;
    private readonly ILogger<ArbitrageHandler> _logger;

    public ArbitrageHandler(AppDbContext db, IExchangeServiceFactory factory,
        IQuoteStreamService quotes, ILogger<ArbitrageHandler> logger)
    {
        _db = db;
        _factory = factory;
        _quotes = quotes;
        _logger = logger;
    }

    // ────────────────────────── Tick entry point ──────────────────────────

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<ArbitrageConfig>(strategy.ConfigJson, JsonOptions);

        // Validation stops the bot itself (and saves) on failure — a misconfigured arbitrage bot
        // must not keep firing market orders across two accounts.
        var accounts = await ValidateAsync(strategy, config, ct);
        if (accounts == null) return;
        var (primaryAccount, secondAccount) = accounts.Value;

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
            await RunTickAsync(strategy, config!, state, exchange, secondExchange,
                primaryAccount, secondAccount, ct);
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
        ExchangeAccount primaryAccount, ExchangeAccount secondAccount, CancellationToken ct)
    {
        // Level identity = position in the ascending-by-EntrySpreadPercent ordering. The config
        // list itself is never mutated; we sort a copy defensively every tick.
        var levels = config.Levels.OrderBy(l => l.EntrySpreadPercent).ToList();
        SyncLevelStates(state, levels.Count);

        var symbolA = config.Symbol;
        var symbolB = string.IsNullOrWhiteSpace(config.SecondSymbol) ? config.Symbol : config.SecondSymbol!;

        if (!state.LeverageSet &&
            (state.LeverageRetryAt == null || DateTime.UtcNow >= state.LeverageRetryAt.Value))
        {
            if (!state.LeveragePrimarySet)
                state.LeveragePrimarySet =
                    await TrySetLeverageAsync(strategy, primaryExchange, symbolA, config.Leverage, "primary");

            if (!state.LeverageSecondarySet)
                state.LeverageSecondarySet =
                    await TrySetLeverageAsync(strategy, secondExchange, symbolB, config.Leverage, "secondary");

            state.LeverageSet = state.LeveragePrimarySet && state.LeverageSecondarySet;

            // A failed pin is not fatal (sizing is by notional), but leaving it unset forever means
            // every order runs on whatever leverage the account happens to carry — so retry slowly.
            state.LeverageRetryAt = state.LeverageSet
                ? null
                : DateTime.UtcNow.AddMinutes(LeverageRetryMinutes);
        }

        await ResolveFeeRatesAsync(strategy, state, primaryExchange, symbolA, secondExchange, symbolB);

        var (bookA, bookB) = await ReadBooksAsync(strategy, state,
            primaryExchange, primaryAccount, symbolA,
            secondExchange, secondAccount, symbolB, ct);
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
            SecondaryBook = bookB,
            PrimaryFeeRate = state.PrimaryTakerFeeRate ?? primaryExchange.TakerFeeRate,
            SecondaryFeeRate = state.SecondaryTakerFeeRate ?? secondExchange.TakerFeeRate
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
        // Both closes are sent first and only then read back: confirming the short's fill before
        // sending the long would hold a naked long for the confirmation round-trips.
        OrderResultDto? shortResult = null;
        OrderResultDto? longResult = null;

        if (level.ShortQty > 0)
        {
            shortResult = await legs.ShortExchange.CloseShortAsync(legs.ShortSymbol, level.ShortQty);
            if (!shortResult.Success)
            {
                state.ConsecutiveFailures++;
                Log(strategy, "Warning",
                    $"Level #{level.Index}: short close on {legs.ShortSymbol} failed: {Trim(shortResult.ErrorMessage)} " +
                    $"— retry next tick (failures {state.ConsecutiveFailures})");
                _logger.LogWarning("Arbitrage {Id}: short close failed for level {Lvl}: {Err}",
                    strategy.Id, level.Index, shortResult.ErrorMessage);
            }

            await Task.Delay(InterOrderDelayMs, ct);
        }

        if (level.LongQty > 0)
        {
            longResult = await legs.LongExchange.CloseLongAsync(legs.LongSymbol, level.LongQty);
            if (!longResult.Success)
            {
                state.ConsecutiveFailures++;
                Log(strategy, "Warning",
                    $"Level #{level.Index}: long close on {legs.LongSymbol} failed: {Trim(longResult.ErrorMessage)} " +
                    $"— retry next tick (failures {state.ConsecutiveFailures})");
                _logger.LogWarning("Arbitrage {Id}: long close failed for level {Lvl}: {Err}",
                    strategy.Id, level.Index, longResult.ErrorMessage);
            }
        }

        decimal net = 0m;
        if (shortResult?.Success == true)
            net += await BookClosedLegAsync(strategy, level, legs, isShort: true, shortResult, status, state, ct);
        if (longResult?.Success == true)
            net += await BookClosedLegAsync(strategy, level, legs, isShort: false, longResult, status, state, ct);

        var flat = level.ShortQty <= 0 && level.LongQty <= 0;
        if (flat)
        {
            level.IsOpen = false;
            level.OpenedAt = null;
        }
        return (flat, net);
    }

    /// <summary>
    /// Books one successfully closed leg: reads the real fill back, records the closing Trade and
    /// moves the leg's quantity (and the entry fee it carried) off the level. PnlDollar on the
    /// closing Trade is net of BOTH fees — the entry fee was booked on the opening Trade's
    /// Commission, the closing Trade carries the exit fee only, same convention as the other
    /// handlers. A partial fill leaves the remainder on the level for the next unwind pass.
    /// </summary>
    private async Task<decimal> BookClosedLegAsync(Strategy strategy, ArbitrageLevelState level,
        LegPair legs, bool isShort, OrderResultDto result, string status, ArbitrageState state,
        CancellationToken ct)
    {
        var exchange = isShort ? legs.ShortExchange : legs.LongExchange;
        var symbol = isShort ? legs.ShortSymbol : legs.LongSymbol;
        var feeRate = isShort ? legs.ShortFeeRate : legs.LongFeeRate;
        var qty = isShort ? level.ShortQty : level.LongQty;
        var entryPrice = isShort ? level.ShortEntryPrice : level.LongEntryPrice;
        var entryFeeCarried = isShort ? level.ShortEntryFee : level.LongEntryFee;

        // Estimate = the book price the close was priced against (the side we cross).
        var estimate = PositivePrice(result.FilledPrice,
            isShort ? legs.ShortBook?.AskPrice : legs.LongBook?.BidPrice, entryPrice);

        var fill = await ConfirmFillAsync(strategy, exchange, symbol, result.OrderId,
            estimate, qty, feeRate, isShort ? "short close" : "long close", ct);

        var closedQty = fill.Confirmed ? Math.Min(fill.Quantity, qty) : qty;
        var gross = isShort
            ? (entryPrice - fill.Price) * closedQty
            : (fill.Price - entryPrice) * closedQty;

        // Entry fee: what the exchange actually charged when the level opened (pro-rated if this
        // close is partial); levels from before entry fees were recorded fall back to the rate.
        var entryFee = entryFeeCarried > 0
            ? entryFeeCarried * (closedQty / qty)
            : entryPrice * closedQty * feeRate;
        var exitFee = fill.Fee;
        var legNet = gross - entryFee - exitFee;

        RecordTrade(strategy, isShort ? legs.ShortAccountId : legs.LongAccountId, symbol,
            isShort ? "Buy" : "Sell", closedQty, fill.Price, result.OrderId, status, legNet, exitFee);
        state.RealizedPnlUsdt += legNet;

        var remaining = qty - closedQty;
        if (isShort)
        {
            level.ShortQty = remaining;
            level.ShortEntryFee = remaining > 0 ? entryFeeCarried - entryFee : 0m;
        }
        else
        {
            level.LongQty = remaining;
            level.LongEntryFee = remaining > 0 ? entryFeeCarried - entryFee : 0m;
        }

        if (remaining > 0)
            Log(strategy, "Warning",
                $"Level #{level.Index}: {(isShort ? "short" : "long")} close on {symbol} filled " +
                $"{Fmt(closedQty, 8)} of {Fmt(qty, 8)} — the rest is unwound next tick");

        return legNet;
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
        // through several thresholds at once still gets filled one level at a time, which both
        // rate-limits the venues and gives the operator a chance to react.
        //
        // The pace is enforced in seconds, not in ticks: the loop moved from 5s to 1s, and without
        // this the same divergence would fire the whole ladder five times faster than the design
        // this cap came from.
        if (state.LastLevelOpenedAt.HasValue &&
            (DateTime.UtcNow - state.LastLevelOpenedAt.Value).TotalSeconds < MinSecondsBetweenOpens)
            return;

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
        // Stamped before the orders go out, not after they succeed: a level that keeps getting
        // rejected must back off too, otherwise the 1s loop would retry the same failing open
        // every second against both venues.
        state.LastLevelOpenedAt = DateTime.UtcNow;

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

        // The service's FilledPrice/FilledQuantity are the ticker price it sized against and the
        // quantity it sent — placeholders until the fill is read back below. The read-back waits
        // until BOTH legs are placed: confirming the long first would widen the window in which
        // the pair is naked.
        var longEstPrice = PositivePrice(longResult.FilledPrice, legs.LongBook?.AskPrice, 0m);
        var longEstQty = longResult.FilledQuantity ?? (longEstPrice > 0 ? cfg.NotionalUsdt / longEstPrice : 0m);

        await Task.Delay(InterOrderDelayMs, ct);

        var shortResult = await legs.ShortExchange.OpenShortAsync(legs.ShortSymbol, cfg.NotionalUsdt);
        if (!shortResult.Success)
        {
            state.ConsecutiveFailures++;
            Log(strategy, "Error",
                $"Level #{level.Index}: short leg on {legs.ShortSymbol} rejected: {Trim(shortResult.ErrorMessage)} " +
                $"— rolling back the {Fmt(longEstQty, 8)} long leg (failures {state.ConsecutiveFailures})");
            _logger.LogError("Arbitrage {Id}: short open failed for level {Lvl}: {Err}",
                strategy.Id, level.Index, shortResult.ErrorMessage);

            // The rollback must close what was REALLY filled, so the long is read back first.
            var nakedLong = await ConfirmFillAsync(strategy, legs.LongExchange, legs.LongSymbol,
                longResult.OrderId, longEstPrice, longEstQty, legs.LongFeeRate, "long", ct);
            RecordTrade(strategy, legs.LongAccountId, legs.LongSymbol, "Buy", nakedLong.Quantity,
                nakedLong.Price, longResult.OrderId, "Filled", commission: nakedLong.Fee);

            await RollbackLongAsync(strategy, level, legs, direction, nakedLong, entrySpread, state, ct);
            return;
        }

        var shortEstPrice = PositivePrice(shortResult.FilledPrice, legs.ShortBook?.BidPrice, 0m);
        var shortEstQty = shortResult.FilledQuantity ?? (shortEstPrice > 0 ? cfg.NotionalUsdt / shortEstPrice : 0m);

        var longFill = await ConfirmFillAsync(strategy, legs.LongExchange, legs.LongSymbol,
            longResult.OrderId, longEstPrice, longEstQty, legs.LongFeeRate, "long", ct);
        var shortFill = await ConfirmFillAsync(strategy, legs.ShortExchange, legs.ShortSymbol,
            shortResult.OrderId, shortEstPrice, shortEstQty, legs.ShortFeeRate, "short", ct);

        var longPrice = longFill.Price;
        var longQty = longFill.Quantity;
        var shortPrice = shortFill.Price;
        var shortQty = shortFill.Quantity;

        RecordTrade(strategy, legs.LongAccountId, legs.LongSymbol, "Buy", longQty, longPrice,
            longResult.OrderId, "Filled", commission: longFill.Fee);
        RecordTrade(strategy, legs.ShortAccountId, legs.ShortSymbol, "Sell", shortQty, shortPrice,
            shortResult.OrderId, "Filled", commission: shortFill.Fee);

        level.IsOpen = true;
        level.ShortQty = shortQty;
        level.LongQty = longQty;
        level.ShortEntryPrice = shortPrice;
        level.LongEntryPrice = longPrice;
        level.ShortEntryFee = shortFill.Fee;
        level.LongEntryFee = longFill.Fee;
        level.EntrySpreadPercent = entrySpread;
        level.OpenedAt = DateTime.UtcNow;

        // The spread the fills actually locked in, as opposed to the one the books promised. On a
        // thin or stale book the two drift apart — this is the number that tells the user whether
        // the entry threshold is doing its job.
        var filledSpread = longPrice > 0 ? (shortPrice - longPrice) / longPrice * 100m : 0m;

        state.Direction = direction;
        state.ConsecutiveFailures = 0;

        Log(strategy, "Info",
            $"Level #{level.Index} opened @ spread {Fmt(entrySpread, 4)}% (threshold {Fmt(cfg.EntrySpreadPercent, 4)}%, " +
            $"filled at {Fmt(filledSpread, 4)}%): " +
            $"SHORT {legs.ShortSymbol} {Fmt(shortQty, 8)} @ {Fmt(shortPrice, 8)} / " +
            $"LONG {legs.LongSymbol} {Fmt(longQty, 8)} @ {Fmt(longPrice, 8)}, " +
            $"fees {Fmt(shortFill.Fee + longFill.Fee, 4)} USDT, " +
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
        ArbitrageDirection direction, Fill longFill, decimal entrySpread, ArbitrageState state,
        CancellationToken ct)
    {
        var longQty = longFill.Quantity;
        var longPrice = longFill.Price;
        if (longQty <= 0) return;

        var result = await legs.LongExchange.CloseLongAsync(legs.LongSymbol, longQty);
        if (result.Success)
        {
            var estimate = PositivePrice(result.FilledPrice, legs.LongBook?.BidPrice, longPrice);
            var exitFill = await ConfirmFillAsync(strategy, legs.LongExchange, legs.LongSymbol,
                result.OrderId, estimate, longQty, legs.LongFeeRate, "long rollback", ct);

            var gross = (exitFill.Price - longPrice) * longQty;
            var net = gross - longFill.Fee - exitFill.Fee;

            RecordTrade(strategy, legs.LongAccountId, legs.LongSymbol, "Sell", longQty, exitFill.Price,
                result.OrderId, "LegRollback", net, exitFill.Fee);
            state.RealizedPnlUsdt += net;

            Log(strategy, "Warning",
                $"Level #{level.Index}: long leg rolled back at {Fmt(exitFill.Price, 8)}, cost={Fmt(net)} USDT — level not opened");
            return;
        }

        level.IsOpen = true;
        level.LongQty = longQty;
        level.LongEntryPrice = longPrice;
        level.LongEntryFee = longFill.Fee;
        level.ShortQty = 0;
        level.ShortEntryPrice = 0;
        level.ShortEntryFee = 0;
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
                SecondaryBook = await SafeBookAsync(secondExchange, symbolB),
                PrimaryFeeRate = state.PrimaryTakerFeeRate ?? primaryExchange.TakerFeeRate,
                SecondaryFeeRate = state.SecondaryTakerFeeRate ?? secondExchange.TakerFeeRate
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
    private async Task<(ExchangeAccount Primary, ExchangeAccount Secondary)?> ValidateAsync(
        Strategy strategy, ArbitrageConfig? config, CancellationToken ct)
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

        ExchangeAccount? primaryAccount = null;

        if (error == null)
        {
            // Read the primary account off our own context instead of the (possibly stale, possibly
            // unloaded) navigation — the entity reaches us from the worker's outer scope. The proxy
            // graph is included because the quote streams pick their proxy off this entity.
            primaryAccount = await _db.ExchangeAccounts
                .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
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

        if (error == null) return (primaryAccount!, secondAccount!);

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
    /// leg-risk path already handles. Returns true when the leg ends up on the target leverage
    /// (including "already there" rejections); false means the caller should retry later.
    /// </summary>
    private async Task<bool> TrySetLeverageAsync(Strategy strategy, IFuturesExchangeService exchange,
        string symbol, int leverage, string legName)
    {
        try
        {
            var maxLev = await exchange.GetMaxLeverageAsync(symbol);
            var target = maxLev.HasValue && maxLev.Value > 0 ? Math.Min(leverage, maxLev.Value) : leverage;

            if (target < leverage)
                Log(strategy, "Warning",
                    $"{legName} leg ({symbol}): requested {leverage}x exceeds the exchange risk limit " +
                    $"({maxLev}x) — using {target}x");

            var result = await exchange.SetLeverageDetailedAsync(symbol, target);
            if (result.Success)
            {
                _logger.LogDebug("Arbitrage {Id}: {Leg} leverage {Lev}x for {Symbol} ({State})",
                    strategy.Id, legName, target, symbol, result.AlreadySet ? "already set" : "set");
                return true;
            }

            // The exchange's own text is the only thing that explains WHY — never drop it.
            Log(strategy, "Warning",
                $"Could not set {target}x leverage on the {legName} leg ({symbol}): " +
                $"{Trim(result.Error ?? "unknown error")}. Using the account's current value; " +
                $"retrying in {LeverageRetryMinutes} min");
            _logger.LogWarning("Arbitrage {Id}: SetLeverage rejected on the {Leg} leg ({Symbol}): {Error}",
                strategy.Id, legName, symbol, result.Error);
            return false;
        }
        catch (NotSupportedException)
        {
            // Exchange service doesn't expose leverage control — nothing to retry.
            return true;
        }
        catch (Exception ex)
        {
            Log(strategy, "Warning",
                $"Leverage pin failed on the {legName} leg ({symbol}): {Trim(ex.Message)}. " +
                $"Retrying in {LeverageRetryMinutes} min");
            _logger.LogWarning(ex, "Arbitrage {Id}: SetLeverage failed on the {Leg} leg ({Symbol})",
                strategy.Id, legName, symbol);
            return false;
        }
    }

    // ────────────────────────── Fees and fills ──────────────────────────

    /// <summary>
    /// Asks both exchanges what taker rate they really charge these accounts, once per start and
    /// then daily. Until an exchange answers, its service's published standard rate is used — the
    /// gap matters: a Bybit account was observed paying 0.11% where the constant says 0.055%,
    /// which alone flipped five "profitable" cycles negative.
    /// </summary>
    private async Task ResolveFeeRatesAsync(Strategy strategy, ArbitrageState state,
        IFuturesExchangeService primaryExchange, string symbolA,
        IFuturesExchangeService secondExchange, string symbolB)
    {
        var now = DateTime.UtcNow;
        var due = state.FeeRatesResolvedAt == null ||
                  now - state.FeeRatesResolvedAt.Value > TimeSpan.FromHours(FeeRateRefreshHours);
        if (!due) return;
        if (state.FeeRatesRetryAt.HasValue && now < state.FeeRatesRetryAt.Value) return;

        var primary = await TryGetFeeRateAsync(strategy, primaryExchange, symbolA, "primary");
        var secondary = await TryGetFeeRateAsync(strategy, secondExchange, symbolB, "secondary");

        var changed = (primary.HasValue && primary != state.PrimaryTakerFeeRate) ||
                      (secondary.HasValue && secondary != state.SecondaryTakerFeeRate);
        if (primary.HasValue) state.PrimaryTakerFeeRate = primary;
        if (secondary.HasValue) state.SecondaryTakerFeeRate = secondary;

        if (primary.HasValue && secondary.HasValue)
        {
            state.FeeRatesResolvedAt = now;
            state.FeeRatesRetryAt = null;
        }
        else
        {
            // Keep whatever did resolve, retry the rest soon; the constant covers the gap.
            state.FeeRatesRetryAt = now.AddMinutes(FeeRateRetryMinutes);
        }

        if (changed)
        {
            var a = state.PrimaryTakerFeeRate ?? primaryExchange.TakerFeeRate;
            var b = state.SecondaryTakerFeeRate ?? secondExchange.TakerFeeRate;
            Log(strategy, "Info",
                $"Taker fee rates from the exchanges: primary {Fmt(a * 100m, 4)}% / secondary {Fmt(b * 100m, 4)}% " +
                $"— a round trip costs {Fmt((a + b) * 2m * 100m, 4)}% of notional before any spread");
        }
    }

    private async Task<decimal?> TryGetFeeRateAsync(Strategy strategy, IFuturesExchangeService exchange,
        string symbol, string legName)
    {
        try
        {
            var rate = await exchange.GetTakerFeeRateAsync(symbol);
            if (rate.HasValue) return rate;

            _logger.LogDebug("Arbitrage {Id}: {Leg} leg ({Symbol}) did not report a taker fee rate",
                strategy.Id, legName, symbol);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Arbitrage {Id}: fee rate lookup failed on the {Leg} leg ({Symbol})",
                strategy.Id, legName, symbol);
            return null;
        }
    }

    // One leg's execution as the exchange reports it. Confirmed=false means the exchange could not
    // be read back and the numbers are the handler's own estimate.
    private sealed record Fill(decimal Price, decimal Quantity, decimal Fee, bool Confirmed);

    /// <summary>
    /// Reads a market order back from the exchange: average fill price, filled quantity and the
    /// fee actually charged. The service's own FilledPrice is only the ticker price it sized
    /// against — on a thin book the real fill differs from it by more than the spread this bot
    /// chases, and booking the estimate produced PnL that did not exist on the exchange.
    /// Falls back to the estimate (price × qty × rate) when the order cannot be read back, and
    /// says so in the strategy log so the user knows that leg's PnL is approximate.
    /// </summary>
    private async Task<Fill> ConfirmFillAsync(Strategy strategy, IFuturesExchangeService exchange,
        string symbol, string? orderId, decimal estPrice, decimal estQty, decimal feeRate,
        string legName, CancellationToken ct)
    {
        var estimate = new Fill(estPrice, estQty, estPrice * estQty * feeRate, Confirmed: false);
        if (string.IsNullOrEmpty(orderId)) return estimate;

        string? failure = null;
        for (var attempt = 1; attempt <= FillConfirmAttempts; attempt++)
        {
            try
            {
                var order = await exchange.GetOrderAsync(symbol, orderId);
                var settled = order?.Status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled
                              or OrderLifecycleStatus.Rejected;

                if (order != null && order.FilledQuantity > 0 && order.AverageFilledPrice > 0 &&
                    (settled || attempt == FillConfirmAttempts))
                {
                    var fee = order.Fee ?? order.AverageFilledPrice * order.FilledQuantity * feeRate;
                    return new Fill(order.AverageFilledPrice, order.FilledQuantity, fee, Confirmed: true);
                }

                failure = order == null ? "order not found" : $"status={order.Status}, filled={order.FilledQuantity}";
            }
            catch (NotSupportedException)
            {
                failure = "exchange service has no order lookup";
                break;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            if (attempt < FillConfirmAttempts) await Task.Delay(FillConfirmDelayMs, ct);
        }

        Log(strategy, "Warning",
            $"{legName} leg ({symbol}): could not read order {orderId} back ({Trim(failure, 80)}) — " +
            $"this leg is booked at the ticker estimate {Fmt(estPrice, 8)} × {Fmt(estQty, 8)}, " +
            $"fee {Fmt(estimate.Fee, 4)} USDT");
        _logger.LogWarning("Arbitrage {Id}: fill read-back failed for {Symbol} order {OrderId}: {Reason}",
            strategy.Id, symbol, orderId, failure);
        return estimate;
    }

    /// <summary>
    /// Both legs' top-of-book for this tick, preferring the websocket streams.
    ///
    /// Why this shape matters: the spread is a comparison between two venues, so the two books
    /// must describe the same instant. Reading them sequentially over REST put hundreds of
    /// milliseconds between the snapshots and manufactured spreads that never existed. Stream
    /// quotes are read from memory, so both sides are effectively simultaneous.
    ///
    /// A stream that is missing or stale (see <see cref="MaxQuoteAgeMs"/>) is NOT trusted — that
    /// leg falls back to REST, and when both do they are fetched concurrently to keep the time
    /// skew as small as REST allows. Falling back rather than skipping is deliberate: a socket
    /// outage must never freeze a bot that holds open positions and needs to exit.
    /// </summary>
    private async Task<(BookTickerDto? Primary, BookTickerDto? Secondary)> ReadBooksAsync(
        Strategy strategy, ArbitrageState state,
        IFuturesExchangeService primaryExchange, ExchangeAccount primaryAccount, string symbolA,
        IFuturesExchangeService secondExchange, ExchangeAccount secondAccount, string symbolB,
        CancellationToken ct)
    {
        // Idempotent keep-alive: starts the streams on the first tick, refreshes their idle
        // timers afterwards. Never blocks on the handshake.
        await _quotes.EnsureSubscribedAsync(primaryAccount, symbolA, ct);
        await _quotes.EnsureSubscribedAsync(secondAccount, symbolB, ct);

        var now = DateTime.UtcNow;
        var streamA = FreshQuote(primaryAccount.ExchangeType, symbolA, now);
        var streamB = FreshQuote(secondAccount.ExchangeType, symbolB, now);

        if (streamA != null && streamB != null)
        {
            state.QuoteSource = "stream";
            return (streamA, streamB);
        }

        state.QuoteSource = streamA == null && streamB == null ? "rest" : "mixed";

        // Report the leg that is actually missing (the primary if both are).
        if (streamA == null)
            WarnQuoteFallback(strategy, state, primaryAccount.ExchangeType, symbolA, now);
        else
            WarnQuoteFallback(strategy, state, secondAccount.ExchangeType, symbolB, now);

        var taskA = streamA != null
            ? Task.FromResult<BookTickerDto?>(streamA)
            : SafeBookAsync(primaryExchange, symbolA);
        var taskB = streamB != null
            ? Task.FromResult<BookTickerDto?>(streamB)
            : SafeBookAsync(secondExchange, symbolB);

        await Task.WhenAll(taskA, taskB);
        return (taskA.Result, taskB.Result);
    }

    /// <summary>Stream quote, or null when there is none yet, it is degenerate, or it is stale.</summary>
    private BookTickerDto? FreshQuote(Core.Enums.ExchangeType exchange, string symbol, DateTime nowUtc)
    {
        var quote = _quotes.TryGetQuote(exchange, symbol);
        if (quote == null || !quote.IsValid || quote.AgeMs(nowUtc) > MaxQuoteAgeMs) return null;

        return new BookTickerDto
        {
            Symbol = symbol,
            BidPrice = quote.Bid,
            AskPrice = quote.Ask
        };
    }

    /// <summary>
    /// Tells the user the bot is running on REST because a stream is down — throttled, because
    /// this would otherwise write to the strategy log on every 5s tick.
    /// </summary>
    private void WarnQuoteFallback(Strategy strategy, ArbitrageState state,
        Core.Enums.ExchangeType exchange, string symbol, DateTime nowUtc)
    {
        state.QuoteFallbackTicks++;

        if (state.QuoteFallbackWarnedAt.HasValue &&
            nowUtc - state.QuoteFallbackWarnedAt.Value < TimeSpan.FromMinutes(QuoteFallbackWarnMinutes))
            return;

        // Everything needed to tell the three causes apart without adding logging on the hot path:
        // a quiet book (connected, updates rising, quote a few seconds old), a dead socket (age in
        // minutes) and a stream that never started (no quote at all, or an error text).
        var quote = _quotes.TryGetQuote(exchange, symbol);
        var status = _quotes.GetStatus(exchange, symbol);
        var age = quote == null ? "never" : $"{quote.AgeMs(nowUtc) / 1000:F1}s ago";
        var detail = $"connected={(status?.Connected ?? false ? "yes" : "no")}, " +
                     $"updates={status?.UpdateCount ?? 0}, last quote {age}" +
                     (status?.LastError is { } err ? $", error: {Trim(err, 80)}" : "");

        Log(strategy, "Warning",
            $"No fresh websocket quote for {exchange} {symbol} — {state.QuoteFallbackTicks} tick(s) on " +
            $"REST polling in the last {QuoteFallbackWarnMinutes} min ({detail})");

        state.QuoteFallbackWarnedAt = nowUtc;
        state.QuoteFallbackTicks = 0;
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

        // Effective taker rates: the exchange-reported account rate when known, else the
        // service's published constant.
        public decimal PrimaryFeeRate;
        public decimal SecondaryFeeRate;
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

        public decimal ShortFeeRate;
        public decimal LongFeeRate;

        // Executable entry: sell the expensive venue at its bid, buy the cheap one at its ask.
        public decimal EntrySpreadPercent =>
            ShortBook != null && LongBook != null
                ? ArbitrageSpreadMath.EntrySpreadPercent(ShortBook.BidPrice, LongBook.AskPrice)
                : 0m;

        // Cost to unwind right now: buy back the short at its ask, sell the long at its bid.
        public decimal ExitSpreadPercent =>
            ShortBook != null && LongBook != null
                ? ArbitrageSpreadMath.ExitSpreadPercent(ShortBook.AskPrice, LongBook.BidPrice)
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
                ShortFeeRate = ctx.SecondaryFeeRate,
                LongExchange = ctx.PrimaryExchange,
                LongSymbol = ctx.PrimarySymbol,
                LongAccountId = ctx.PrimaryAccountId,
                LongBook = ctx.PrimaryBook,
                LongFeeRate = ctx.PrimaryFeeRate
            }
            : new LegPair
            {
                ShortExchange = ctx.PrimaryExchange,
                ShortSymbol = ctx.PrimarySymbol,
                ShortAccountId = ctx.PrimaryAccountId,
                ShortBook = ctx.PrimaryBook,
                ShortFeeRate = ctx.PrimaryFeeRate,
                LongExchange = ctx.SecondaryExchange,
                LongSymbol = ctx.SecondarySymbol,
                LongAccountId = ctx.SecondaryAccountId,
                LongBook = ctx.SecondaryBook,
                LongFeeRate = ctx.SecondaryFeeRate
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
