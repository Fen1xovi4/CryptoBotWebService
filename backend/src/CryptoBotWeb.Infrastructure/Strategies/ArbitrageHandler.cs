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
/// Execution: both legs are sized to ONE base quantity (valid on both venues' lot steps) from
/// the books already in hand and sent concurrently — no ticker or contract lookups in between
/// (instrument rules come from a cache). Every fill is read back from the exchange: price,
/// quantity and fee are booked as filled, never as quoted. OrderMode picks market orders or
/// limit ImmediateOrCancel at the quote ± MaxSlippagePercent.
///
/// Leg risk is the core hazard here: the two legs sit on different exchanges and cannot fill
/// atomically, and an IOC leg may fill partly or not at all. After every open or close the
/// level is squared to the matched quantity at once — the fuller leg's excess is closed at
/// market, reduce-only; a lone filled leg is closed entirely (rollback). If that trim fails,
/// the lopsided level stays in state and the unwind pass keeps retrying it — exposure is never
/// dropped from bookkeeping.
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

    // A lopsided level whose trim failed is retried no faster than this.
    private const int RebalanceRetrySeconds = 30;

    // An IOC close that filled nothing waits this long before re-firing both legs.
    private const int IocCloseRetrySeconds = 2;

    // Leg-size differences below this share of the larger leg are lot-step noise, not exposure
    // worth an order (and usually below the exchange's minimum order size anyway).
    private const decimal MinImbalanceFraction = 0.02m;

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
            await ProcessClosesAsync(strategy, config, levels, state, legs, ct);
        }

        if (!CheckFailureLimit(strategy, config, state)) return;

        await ProcessOpenAsync(strategy, config, levels, state, ctx, entryPrimary, entrySecondary, ct);

        CheckFailureLimit(strategy, config, state);
    }

    // ────────────────────────── Closing ──────────────────────────

    private async Task ProcessClosesAsync(Strategy strategy, ArbitrageConfig config,
        List<ArbitrageLevelConfig> levels, ArbitrageState state, LegPair legs, CancellationToken ct)
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
            var (flat, net) = await CloseLevelAsync(strategy, level, legs, reason, state, null, ct);
            if (flat)
            {
                closedAny = true;
                Log(strategy, "Warning",
                    $"Level #{level.Index}: incomplete pair unwound ({reason}), net={Fmt(net)} USDT");
            }
        }

        // ── Pass 1b: lopsided levels. Both legs present but of materially different size (a
        // trim that failed earlier) — the excess is a naked position, so it is trimmed at market.
        // Throttled per level: a trim the exchange keeps rejecting must not fire every second.
        foreach (var level in state.Levels.Where(l => l.IsOpen && l.ShortQty > 0 && l.LongQty > 0).ToList())
        {
            if (level.RebalanceAttemptAt.HasValue &&
                (DateTime.UtcNow - level.RebalanceAttemptAt.Value).TotalSeconds < RebalanceRetrySeconds)
                continue;
            if (!await IsMaterialImbalanceAsync(level, legs)) continue;

            await RebalanceLevelAsync(strategy, level, legs, "LegTrim", state, ct);
        }

        // ── Pass 2: threshold closes. Deepest levels (highest entry spread) first — they are the
        // ones that were opened last and carry the most spread risk if the divergence resumes.
        var exitSpread = legs.ExitSpreadPercent;
        var limitIoc = ArbitrageOrderModes.IsLimitIoc(config.OrderMode);

        var candidates = state.Levels
            .Where(l => l.IsOpen && l.Index < levels.Count && l.ShortQty > 0 && l.LongQty > 0)
            .OrderByDescending(l => levels[l.Index].EntrySpreadPercent)
            .ToList();

        foreach (var level in candidates)
        {
            var cfg = levels[level.Index];
            if (exitSpread > cfg.ExitSpreadPercent) continue;

            // An IOC close that filled nothing backs off briefly instead of re-firing both legs
            // every tick into a book that just moved away.
            if (limitIoc && level.CloseAttemptAt.HasValue &&
                (DateTime.UtcNow - level.CloseAttemptAt.Value).TotalSeconds < IocCloseRetrySeconds)
                continue;

            var (flat, net) = await CloseLevelAsync(strategy, level, legs, "SpreadExit", state,
                limitIoc ? config.MaxSlippagePercent : null, ct);
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
            else if (net != 0m)
            {
                Log(strategy, "Info",
                    $"Level #{level.Index}: partly closed at exitSpread={Fmt(exitSpread, 4)}% " +
                    $"(IOC), net so far {Fmt(net)} USDT — the remaining " +
                    $"{Fmt(level.ShortQty, 8)}/{Fmt(level.LongQty, 8)} stays open");
            }
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
    /// Closes whatever both legs of <paramref name="level"/> still carry, sending the two orders
    /// concurrently (reduce-only) and only then reading the fills back. With
    /// <paramref name="iocSlippagePercent"/> null the closes are market orders and always go
    /// through; with a value they are limit IOC at the quote ± slippage and may fill partly.
    /// Whatever filled is booked; if the two legs end up of different size, the excess is
    /// trimmed at market right away so the level never sits lopsided. Returns whether the level
    /// ended flat plus the net PnL booked by this call.
    /// </summary>
    private async Task<(bool Flat, decimal NetPnl)> CloseLevelAsync(Strategy strategy,
        ArbitrageLevelState level, LegPair legs, string status, ArbitrageState state,
        decimal? iocSlippagePercent, CancellationToken ct)
    {
        var ioc = iocSlippagePercent.HasValue;
        decimal? shortLimit = null, longLimit = null;
        if (ioc)
        {
            var (shortRules, longRules) = await GetRulesAsync(legs);
            // Buying the short back: pay up to ask + slippage. Selling the long: down to bid − slippage.
            shortLimit = LimitPrice(legs.ShortBook?.AskPrice, iocSlippagePercent!.Value, isBuy: true, shortRules);
            longLimit = LimitPrice(legs.LongBook?.BidPrice, iocSlippagePercent!.Value, isBuy: false, longRules);
            if (shortLimit == null || longLimit == null)
            {
                // No book / no tick size → a safe limit cannot be priced; let the next tick retry.
                return (false, 0m);
            }
            level.CloseAttemptAt = DateTime.UtcNow;
        }

        var shortQty = level.ShortQty;
        var longQty = level.LongQty;

        var shortTask = shortQty > 0
            ? AsNullable(legs.ShortExchange.PlaceTakerOrderAsync(legs.ShortSymbol, "Buy", shortQty, shortLimit, reduceOnly: true))
            : Task.FromResult<OrderResultDto?>(null);
        var longTask = longQty > 0
            ? AsNullable(legs.LongExchange.PlaceTakerOrderAsync(legs.LongSymbol, "Sell", longQty, longLimit, reduceOnly: true))
            : Task.FromResult<OrderResultDto?>(null);
        await Task.WhenAll(shortTask, longTask);
        var shortResult = await shortTask;
        var longResult = await longTask;

        ReportRejection(strategy, level, state, shortResult, $"short close on {legs.ShortSymbol}");
        ReportRejection(strategy, level, state, longResult, $"long close on {legs.LongSymbol}");

        var shortFillTask = shortResult?.Success == true
            ? AsNullable(ConfirmFillAsync(strategy, legs.ShortExchange, legs.ShortSymbol, shortResult.OrderId,
                shortLimit ?? PositivePrice(null, legs.ShortBook?.AskPrice, level.ShortEntryPrice),
                shortQty, legs.ShortFeeRate, "short close", ioc, ct))
            : Task.FromResult<Fill?>(null);
        var longFillTask = longResult?.Success == true
            ? AsNullable(ConfirmFillAsync(strategy, legs.LongExchange, legs.LongSymbol, longResult.OrderId,
                longLimit ?? PositivePrice(null, legs.LongBook?.BidPrice, level.LongEntryPrice),
                longQty, legs.LongFeeRate, "long close", ioc, ct))
            : Task.FromResult<Fill?>(null);
        await Task.WhenAll(shortFillTask, longFillTask);

        decimal net = 0m;
        if (await shortFillTask is { Quantity: > 0 } sf)
            net += BookClosedLeg(strategy, level, legs, isShort: true, sf, shortQty, shortResult!.OrderId, status, state);
        if (await longFillTask is { Quantity: > 0 } lf)
            net += BookClosedLeg(strategy, level, legs, isShort: false, lf, longQty, longResult!.OrderId, status, state);

        // A partial IOC close (or one leg rejected) leaves the legs lopsided — square them now.
        if (level.ShortQty > 0 && level.LongQty > 0 && level.ShortQty != level.LongQty &&
            await IsMaterialImbalanceAsync(level, legs))
        {
            net += await RebalanceLevelAsync(strategy, level, legs, "LegTrim", state, ct);
        }

        var flat = level.ShortQty <= 0 && level.LongQty <= 0;
        if (flat)
        {
            level.IsOpen = false;
            level.OpenedAt = null;
            level.CloseAttemptAt = null;
            level.RebalanceAttemptAt = null;
        }
        return (flat, net);
    }

    /// <summary>
    /// Books one closed (or trimmed) leg from its confirmed fill: records the closing Trade and
    /// takes the filled quantity — and the matching share of the entry fee — off the level.
    /// PnlDollar on the closing Trade is net of BOTH fees: the entry fee was booked on the
    /// opening Trade's Commission, the closing Trade carries the exit fee only (same convention
    /// as the other handlers). Whatever did not fill stays on the level.
    /// </summary>
    private decimal BookClosedLeg(Strategy strategy, ArbitrageLevelState level, LegPair legs,
        bool isShort, Fill fill, decimal requestedQty, string? orderId, string status, ArbitrageState state)
    {
        var legQty = isShort ? level.ShortQty : level.LongQty;
        if (legQty <= 0) return 0m;

        var entryPrice = isShort ? level.ShortEntryPrice : level.LongEntryPrice;
        var entryFeeCarried = isShort ? level.ShortEntryFee : level.LongEntryFee;
        var feeRate = isShort ? legs.ShortFeeRate : legs.LongFeeRate;

        var closedQty = Math.Min(Math.Min(fill.Quantity, requestedQty), legQty);
        if (closedQty <= 0) return 0m;

        var gross = isShort
            ? (entryPrice - fill.Price) * closedQty
            : (fill.Price - entryPrice) * closedQty;

        // Entry fee: what the exchange actually charged on the open, pro-rated to the closed
        // part; levels from before entry fees were recorded fall back to the rate.
        var entryFee = entryFeeCarried > 0
            ? entryFeeCarried * (closedQty / legQty)
            : entryPrice * closedQty * feeRate;
        // A partial fill's fee covers only what filled; scale the order's fee to closedQty when
        // the order filled more than this level owned (never happens with reduce-only, defensive).
        var exitFee = fill.Quantity > 0 ? fill.Fee * (closedQty / fill.Quantity) : 0m;
        var legNet = gross - entryFee - exitFee;

        RecordTrade(strategy, isShort ? legs.ShortAccountId : legs.LongAccountId,
            isShort ? legs.ShortSymbol : legs.LongSymbol, isShort ? "Buy" : "Sell",
            closedQty, fill.Price, orderId, status, legNet, exitFee);
        state.RealizedPnlUsdt += legNet;

        var remaining = legQty - closedQty;
        if (isShort)
        {
            level.ShortQty = remaining;
            level.ShortEntryFee = remaining > 0 ? Math.Max(0m, entryFeeCarried - entryFee) : 0m;
        }
        else
        {
            level.LongQty = remaining;
            level.LongEntryFee = remaining > 0 ? Math.Max(0m, entryFeeCarried - entryFee) : 0m;
        }
        return legNet;
    }

    /// <summary>
    /// Squares a lopsided level: market-closes (reduce-only) the excess of the larger leg so both
    /// legs carry the same quantity. Used right after an open or close whose legs filled
    /// differently, and by the unwind pass for a trim that failed earlier. A failure is counted
    /// towards MaxConsecutiveFailures and retried no faster than every RebalanceRetrySeconds.
    /// </summary>
    private async Task<decimal> RebalanceLevelAsync(Strategy strategy, ArbitrageLevelState level,
        LegPair legs, string status, ArbitrageState state, CancellationToken ct)
    {
        var diff = level.ShortQty - level.LongQty;
        if (diff == 0m) return 0m;

        level.RebalanceAttemptAt = DateTime.UtcNow;
        var isShort = diff > 0;
        var qty = Math.Abs(diff);
        var exchange = isShort ? legs.ShortExchange : legs.LongExchange;
        var symbol = isShort ? legs.ShortSymbol : legs.LongSymbol;

        var result = await exchange.PlaceTakerOrderAsync(symbol, isShort ? "Buy" : "Sell", qty,
            limitPrice: null, reduceOnly: true);
        if (!result.Success)
        {
            state.ConsecutiveFailures++;
            Log(strategy, "Error",
                $"⚠️ Level #{level.Index}: could not trim the {Fmt(qty, 8)} excess " +
                $"{(isShort ? "SHORT" : "LONG")} on {symbol}: {Trim(result.ErrorMessage)} — the level is " +
                $"lopsided ({Fmt(level.ShortQty, 8)} short / {Fmt(level.LongQty, 8)} long), retry in " +
                $"{RebalanceRetrySeconds}s (failures {state.ConsecutiveFailures})");
            _logger.LogError("Arbitrage {Id}: trim failed for level {Lvl} ({Qty} {Sym}): {Err}",
                strategy.Id, level.Index, qty, symbol, result.ErrorMessage);
            return 0m;
        }

        var estPrice = PositivePrice(null,
            isShort ? legs.ShortBook?.AskPrice : legs.LongBook?.BidPrice,
            isShort ? level.ShortEntryPrice : level.LongEntryPrice);
        var fill = await ConfirmFillAsync(strategy, exchange, symbol, result.OrderId, estPrice, qty,
            isShort ? legs.ShortFeeRate : legs.LongFeeRate, isShort ? "short trim" : "long trim",
            immediateOrCancel: false, ct);

        var net = BookClosedLeg(strategy, level, legs, isShort, fill, qty, result.OrderId, status, state);
        Log(strategy, "Warning",
            $"Level #{level.Index}: legs filled unevenly — trimmed {Fmt(Math.Min(fill.Quantity, qty), 8)} " +
            $"{(isShort ? "SHORT" : "LONG")} {symbol} at {Fmt(fill.Price, 8)}, net={Fmt(net)} USDT; level now " +
            $"{Fmt(level.ShortQty, 8)} short / {Fmt(level.LongQty, 8)} long");

        if (level.ShortQty <= 0 && level.LongQty <= 0)
        {
            level.IsOpen = false;
            level.OpenedAt = null;
            level.RebalanceAttemptAt = null;
        }
        return net;
    }

    /// <summary>
    /// True when the two legs differ by enough to be worth an order: at least the larger leg's
    /// exchange minimum, and at least MinImbalanceFraction of the larger leg. Smaller differences
    /// come from the two venues' lot steps and are a negligible directional exposure — trying to
    /// trim them would only collect "below minimum order size" rejections.
    /// </summary>
    private static async Task<bool> IsMaterialImbalanceAsync(ArbitrageLevelState level, LegPair legs)
    {
        var diff = Math.Abs(level.ShortQty - level.LongQty);
        if (diff == 0m) return false;

        var larger = Math.Max(level.ShortQty, level.LongQty);
        if (larger > 0 && diff / larger < MinImbalanceFraction) return false;

        var exchange = level.ShortQty > level.LongQty ? legs.ShortExchange : legs.LongExchange;
        var symbol = level.ShortQty > level.LongQty ? legs.ShortSymbol : legs.LongSymbol;
        var rules = await SafeRulesAsync(exchange, symbol);
        return rules == null || diff >= Math.Max(rules.MinQty, rules.QtyStep);
    }

    private void ReportRejection(Strategy strategy, ArbitrageLevelState level, ArbitrageState state,
        OrderResultDto? result, string what)
    {
        if (result == null || result.Success) return;
        state.ConsecutiveFailures++;
        Log(strategy, "Warning",
            $"Level #{level.Index}: {what} rejected: {Trim(result.ErrorMessage)} (failures {state.ConsecutiveFailures})");
        _logger.LogWarning("Arbitrage {Id}: {What} rejected for level {Lvl}: {Err}",
            strategy.Id, what, level.Index, result.ErrorMessage);
    }

    private static async Task<T?> AsNullable<T>(Task<T> task) where T : class => await task;

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

        await TryOpenLevelAsync(strategy, config, next, levels[next.Index], legs, direction, entrySpread, state, ct);
    }

    /// <summary>
    /// Opens one delta-neutral pair: SHORT on the expensive venue and LONG on the cheap one, for
    /// the SAME base quantity, both orders sent concurrently.
    ///
    /// Concurrency is the point: sending the legs one after the other — each preceded by its own
    /// ticker and contract lookups — left about a second between them, and on a moving book that
    /// second cost more than the spread being captured. Quantity is sized once from the book
    /// already in hand and rounded to a step both venues accept, so the legs match exactly.
    ///
    /// Leg risk: the two venues cannot fill atomically, and in LimitIoc mode either leg may fill
    /// partly or not at all. Whatever happens, the fills are read back and the level is squared
    /// to the matched quantity at once (<see cref="RebalanceLevelAsync"/>): the excess of the
    /// fuller leg is closed at market, reduce-only. One leg filled and the other not is the
    /// limiting case — the filled leg is closed entirely and the level is not opened. If even
    /// that trim fails, the lopsided level is kept in state (never dropped from bookkeeping) and
    /// the unwind pass keeps retrying it.
    /// </summary>
    private async Task TryOpenLevelAsync(Strategy strategy, ArbitrageConfig config, ArbitrageLevelState level,
        ArbitrageLevelConfig cfg, LegPair legs, ArbitrageDirection direction, decimal entrySpread,
        ArbitrageState state, CancellationToken ct)
    {
        // Stamped before the orders go out, not after they succeed: a level that keeps getting
        // rejected (or keeps missing in IOC mode) must back off too.
        state.LastLevelOpenedAt = DateTime.UtcNow;

        var askLong = legs.LongBook?.AskPrice ?? 0m;
        var bidShort = legs.ShortBook?.BidPrice ?? 0m;
        if (askLong <= 0 || bidShort <= 0) return;

        var (shortRules, longRules) = await GetRulesAsync(legs);
        if (shortRules == null || longRules == null)
        {
            Log(strategy, "Warning",
                $"Level #{level.Index}: contract rules (lot step / tick) unavailable for " +
                $"{(shortRules == null ? legs.ShortSymbol : legs.LongSymbol)} — not opening this tick");
            return;
        }

        // One quantity for both legs: sized off the cheap leg's ask, floored to a step that is
        // valid on BOTH venues, so the pair is delta-neutral to the unit.
        var qty = CommonQuantity(cfg.NotionalUsdt / askLong, shortRules.QtyStep, longRules.QtyStep);
        var minQty = Math.Max(shortRules.MinQty, longRules.MinQty);
        if (qty <= 0 || qty < minQty)
        {
            state.ConsecutiveFailures++;
            Log(strategy, "Error",
                $"Level #{level.Index}: ${Fmt(cfg.NotionalUsdt, 2)} buys {Fmt(qty, 8)} {legs.LongSymbol}, below the " +
                $"exchange minimum {Fmt(minQty, 8)} — raise the level's notional (failures {state.ConsecutiveFailures})");
            return;
        }

        var limitIoc = ArbitrageOrderModes.IsLimitIoc(config.OrderMode);
        decimal? longLimit = null, shortLimit = null;
        if (limitIoc)
        {
            longLimit = LimitPrice(askLong, config.MaxSlippagePercent, isBuy: true, longRules);
            shortLimit = LimitPrice(bidShort, config.MaxSlippagePercent, isBuy: false, shortRules);
            if (longLimit == null || shortLimit == null) return;
        }

        // ── Both legs at once. ──
        var longTask = legs.LongExchange.PlaceTakerOrderAsync(legs.LongSymbol, "Buy", qty, longLimit, reduceOnly: false);
        var shortTask = legs.ShortExchange.PlaceTakerOrderAsync(legs.ShortSymbol, "Sell", qty, shortLimit, reduceOnly: false);
        await Task.WhenAll(longTask, shortTask);
        var longResult = await longTask;
        var shortResult = await shortTask;

        ReportRejection(strategy, level, state, longResult, $"long open on {legs.LongSymbol}");
        ReportRejection(strategy, level, state, shortResult, $"short open on {legs.ShortSymbol}");
        if (!longResult.Success && !shortResult.Success) return;   // nothing filled, no exposure

        var longFillTask = longResult.Success
            ? ConfirmFillAsync(strategy, legs.LongExchange, legs.LongSymbol, longResult.OrderId,
                longLimit ?? askLong, qty, legs.LongFeeRate, "long", limitIoc, ct)
            : Task.FromResult(new Fill(askLong, 0m, 0m, Confirmed: true));
        var shortFillTask = shortResult.Success
            ? ConfirmFillAsync(strategy, legs.ShortExchange, legs.ShortSymbol, shortResult.OrderId,
                shortLimit ?? bidShort, qty, legs.ShortFeeRate, "short", limitIoc, ct)
            : Task.FromResult(new Fill(bidShort, 0m, 0m, Confirmed: true));
        await Task.WhenAll(longFillTask, shortFillTask);
        var longFill = await longFillTask;
        var shortFill = await shortFillTask;

        if (longFill.Quantity <= 0 && shortFill.Quantity <= 0)
        {
            // IOC miss on both legs: the book moved away before the orders landed. No exposure.
            NoteIocMiss(strategy, state, level, entrySpread);
            return;
        }

        if (longFill.Quantity > 0)
            RecordTrade(strategy, legs.LongAccountId, legs.LongSymbol, "Buy", longFill.Quantity,
                longFill.Price, longResult.OrderId, "Filled", commission: longFill.Fee);
        if (shortFill.Quantity > 0)
            RecordTrade(strategy, legs.ShortAccountId, legs.ShortSymbol, "Sell", shortFill.Quantity,
                shortFill.Price, shortResult.OrderId, "Filled", commission: shortFill.Fee);

        // The level owns exactly what filled — matched or not — before any squaring, so a failed
        // trim still leaves every unit of exposure in bookkeeping.
        level.IsOpen = true;
        level.LongQty = longFill.Quantity;
        level.ShortQty = shortFill.Quantity;
        level.LongEntryPrice = longFill.Price;
        level.ShortEntryPrice = shortFill.Price;
        level.LongEntryFee = longFill.Fee;
        level.ShortEntryFee = shortFill.Fee;
        level.EntrySpreadPercent = entrySpread;
        level.OpenedAt = DateTime.UtcNow;
        level.CloseAttemptAt = null;
        level.RebalanceAttemptAt = null;
        state.Direction = direction;

        var lopsided = longFill.Quantity != shortFill.Quantity;
        if (lopsided)
        {
            Log(strategy, "Warning",
                $"Level #{level.Index}: legs filled unevenly — LONG {Fmt(longFill.Quantity, 8)} / SHORT " +
                $"{Fmt(shortFill.Quantity, 8)} of {Fmt(qty, 8)}{(limitIoc ? " (IOC)" : "")} — squaring the pair at market");

            // A single filled leg is always trimmed (it IS the whole exposure); a small mismatch
            // between two filled legs only if it is material.
            if (IsSingleLegged(level) || await IsMaterialImbalanceAsync(level, legs))
                await RebalanceLevelAsync(strategy, level, legs, "LegTrim", state, ct);
        }

        if (!level.IsOpen || level.ShortQty <= 0 || level.LongQty <= 0)
        {
            // Nothing matched: the filled leg was rolled back (or is still being unwound).
            if (!level.IsOpen && state.Levels.All(l => !l.IsOpen))
                state.Direction = ArbitrageDirection.None;
            if (!level.IsOpen)
                Log(strategy, "Warning",
                    $"Level #{level.Index}: only one leg filled — rolled back, level not opened " +
                    $"(realized {Fmt(state.RealizedPnlUsdt)} USDT)");
            return;
        }

        state.ConsecutiveFailures = 0;

        // The spread the fills actually locked in, as opposed to the one the books promised.
        var filledSpread = level.LongEntryPrice > 0
            ? (level.ShortEntryPrice - level.LongEntryPrice) / level.LongEntryPrice * 100m
            : 0m;

        Log(strategy, "Info",
            $"Level #{level.Index} opened @ spread {Fmt(entrySpread, 4)}% (threshold {Fmt(cfg.EntrySpreadPercent, 4)}%, " +
            $"filled at {Fmt(filledSpread, 4)}%{(limitIoc ? $", IOC ±{Fmt(config.MaxSlippagePercent, 3)}%" : "")}): " +
            $"SHORT {legs.ShortSymbol} {Fmt(level.ShortQty, 8)} @ {Fmt(level.ShortEntryPrice, 8)} / " +
            $"LONG {legs.LongSymbol} {Fmt(level.LongQty, 8)} @ {Fmt(level.LongEntryPrice, 8)}, " +
            $"fees {Fmt(shortFill.Fee + longFill.Fee, 4)} USDT, " +
            $"notional=${Fmt(cfg.NotionalUsdt, 2)}/leg, exit at ≤{Fmt(cfg.ExitSpreadPercent, 4)}%");
        _logger.LogInformation(
            "Arbitrage {Id}: level {Lvl} opened, dir={Dir}, spread={Spread}%, filled={Filled}%",
            strategy.Id, level.Index, direction, Math.Round(entrySpread, 4), Math.Round(filledSpread, 4));
    }

    /// <summary>
    /// Counts IOC opens that filled nothing and reports them in batches — at one attempt every
    /// few seconds while the quoted spread sits above a threshold, a line per miss would bury
    /// the log.
    /// </summary>
    private void NoteIocMiss(Strategy strategy, ArbitrageState state, ArbitrageLevelState level, decimal entrySpread)
    {
        state.IocMissCount++;
        var now = DateTime.UtcNow;
        if (state.IocMissLoggedAt.HasValue &&
            now - state.IocMissLoggedAt.Value < TimeSpan.FromMinutes(QuoteFallbackWarnMinutes))
            return;

        Log(strategy, "Info",
            $"IOC open missed on both legs ({state.IocMissCount} miss(es) in the last {QuoteFallbackWarnMinutes} min; " +
            $"latest: level #{level.Index} at quoted {Fmt(entrySpread, 4)}%) — the book moved past the slippage limit " +
            $"before the orders landed, nothing was opened");
        state.IocMissLoggedAt = now;
        state.IocMissCount = 0;
    }

    // ────────────────────────── Order sizing helpers ──────────────────────────

    private static async Task<(InstrumentRulesDto? Short, InstrumentRulesDto? Long)> GetRulesAsync(LegPair legs)
    {
        var s = SafeRulesAsync(legs.ShortExchange, legs.ShortSymbol);
        var l = SafeRulesAsync(legs.LongExchange, legs.LongSymbol);
        await Task.WhenAll(s, l);
        return (await s, await l);
    }

    private static async Task<InstrumentRulesDto?> SafeRulesAsync(IFuturesExchangeService exchange, string symbol)
    {
        try { return await exchange.GetInstrumentRulesAsync(symbol); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Largest quantity ≤ <paramref name="raw"/> that is a whole multiple of both lot steps.
    /// Steps are almost always powers of ten, where this is simply the coarser one.
    /// </summary>
    internal static decimal CommonQuantity(decimal raw, decimal stepA, decimal stepB)
    {
        if (raw <= 0) return 0m;
        if (stepA <= 0) stepA = stepB;
        if (stepB <= 0) stepB = stepA;
        if (stepA <= 0) return raw;

        var coarse = Math.Max(stepA, stepB);
        var fine = Math.Min(stepA, stepB);
        var q = Math.Floor(raw / coarse) * coarse;
        if (coarse % fine == 0m) return q;

        // Non-nested steps (rare): walk down in coarse steps until fine divides too.
        for (var i = 0; i < 1000 && q > 0; i++, q -= coarse)
            if (q % fine == 0m) return q;
        return 0m;
    }

    /// <summary>
    /// Limit price for an IOC leg: the quote pushed by the slippage allowance, rounded to the
    /// tick AWAY from the quote (buy up, sell down) so rounding never eats the allowance.
    /// Null when there is no quote or no tick to round to.
    /// </summary>
    internal static decimal? LimitPrice(decimal? quote, decimal slippagePercent, bool isBuy, InstrumentRulesDto? rules)
    {
        if (quote is not > 0 || rules == null || rules.PriceStep <= 0) return null;
        var slip = Math.Max(0m, slippagePercent) / 100m;
        var raw = isBuy ? quote.Value * (1m + slip) : quote.Value * (1m - slip);
        var ticks = raw / rules.PriceStep;
        var rounded = (isBuy ? Math.Ceiling(ticks) : Math.Floor(ticks)) * rules.PriceStep;
        return rounded > 0 ? rounded : null;
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
                var (flat, net) = await CloseLevelAsync(strategy, level, legs, "ForceClose", state, null, ct);
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
        string legName, bool immediateOrCancel, CancellationToken ct)
    {
        var estimate = new Fill(estPrice, estQty, estPrice * estQty * feeRate, Confirmed: false);
        if (string.IsNullOrEmpty(orderId)) return estimate;

        string? failure = null;
        for (var attempt = 1; attempt <= FillConfirmAttempts; attempt++)
        {
            try
            {
                var order = await exchange.GetOrderAsync(symbol, orderId);
                // An IOC order is final as soon as the exchange has processed it: whatever did not
                // fill was cancelled, so "partially filled" is its terminal state too.
                var settled = order?.Status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled
                                  or OrderLifecycleStatus.Rejected
                              || (immediateOrCancel && order?.Status == OrderLifecycleStatus.PartiallyFilled);

                if (order != null && order.FilledQuantity > 0 && order.AverageFilledPrice > 0 &&
                    (settled || attempt == FillConfirmAttempts))
                {
                    var fee = order.Fee ?? order.AverageFilledPrice * order.FilledQuantity * feeRate;
                    return new Fill(order.AverageFilledPrice, order.FilledQuantity, fee, Confirmed: true);
                }

                // Settled with nothing filled — a legitimate IOC outcome (or a rejected market
                // order): zero exposure, confirmed.
                if (order != null && settled && order.FilledQuantity <= 0)
                    return new Fill(estPrice, 0m, 0m, Confirmed: true);

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
