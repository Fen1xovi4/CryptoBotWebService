using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

/// <summary>
/// SmartGridHedge — symmetric geometric grid + static short hedge (variants A/B of the skim cycle).
///
/// Geometric ladder anchored at P0 (mark price at cycle start):
///   U_k = P0 * (1 + Step)^k for k = 1..NUp    — upper rungs (skim)
///   D_k = P0 * (1 - Step)^k for k = 1..NDown  — lower rungs (DCA)
///   HBreak = U_NUp, LBreak = D_NDown          — hard-close boundaries
///
/// At t=0:
///   • Market LONG of LotUsd at P0 on positionIdx=1 (the "initial long", qInit coins).
///   • Market SHORT of QHedge coins at P0 on positionIdx=2 (the "static hedge"); size comes
///     from <see cref="SymmetricHedgeOptimizer"/> unless the user overrides via Config.
///   • DCA layer: paired buy/sell limits between consecutive D_k rungs (always recycle).
///   • Skim layer (recycle modes only): paired sell/buy limits between consecutive U_k rungs.
///     OneShot mode does NOT pre-place upper limits — instead it watches the mark and trims
///     the excess from qInit on first cross of each U_k.
///
/// Boundary hit → HardClosing → cancel all + market-close both sides → optionally re-anchor
/// at the new mark price (AutoRestart) or Closed (user must Stop+Start).
///
/// Requires Bybit account in hedge mode. The 5-second worker tick is the entire timer surface;
/// limit fills are observed by polling, not via websocket.
/// </summary>
public class SmartGridHedgeHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ~13 req/sec — same throttle GridHedge uses for batched placements.
    private const int InterOrderDelayMs = 75;

    // Cooldown applied after a failed Opening step. Keeps us out of a tight retry loop while
    // still re-trying soon (the worker loop runs every 5s anyway, so 30s ≈ 6 ticks).
    private const int OpeningRetryCooldownSeconds = 30;

    // Cooldown applied between successive AutoRestart cycles so we don't immediately re-open
    // in the same tick that just hard-closed.
    private const int AutoRestartCooldownSeconds = 30;

    // Summary log cadence in Active phase — once every N ticks (≈ N * 5s).
    private const int SummaryEveryNTicks = 12;

    // Substrings in exchange error messages that indicate a permanent refusal — surface them
    // as Error logs so the user can act, but never crash the loop.
    private static readonly string[] FatalErrorSubstrings =
    [
        "regulatory restriction",
        "regulatory restrictions",
        "not available to you",
        "API key permission",
        "invalid api key",
    ];

    public string StrategyType => StrategyTypes.SmartGridHedge;

    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _factory;
    private readonly ILogger<SmartGridHedgeHandler> _logger;

    // Per-process tick counter used only to throttle the periodic summary log. Not persisted
    // — restarting the worker just resets the cadence which is harmless.
    private int _summaryTickCounter;

    public SmartGridHedgeHandler(AppDbContext db, IExchangeServiceFactory factory, ILogger<SmartGridHedgeHandler> logger)
    {
        _db = db;
        _factory = factory;
        _logger = logger;
    }

    // ────────────────────────── ProcessAsync ──────────────────────────

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService futures, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<SmartGridHedgeConfig>(strategy.ConfigJson, JsonOptions);
        if (!ValidateConfig(strategy, config))
        {
            await _db.SaveChangesAsync(ct);
            return;
        }

        var state = JsonSerializer.Deserialize<SmartGridHedgeState>(strategy.StateJson, JsonOptions)
                    ?? new SmartGridHedgeState();

        // ───────── Hedge-mode probe ─────────
        // SmartGridHedge requires positionIdx=1 / positionIdx=2 coexistence on the same symbol
        // — only Bybit supports this (Bitget/BingX accounts in this project are pinned one-way).
        if (!futures.IsHedgeModeSupported)
        {
            Log(strategy, "Error",
                "SmartGridHedge требует Bybit-аккаунт с поддержкой hedge-режима. Останавливаю стратегию.");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        bool? hedgeEnabled;
        try
        {
            hedgeEnabled = await futures.IsHedgeModeEnabledAsync(config!.Symbol);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmartGridHedge: IsHedgeModeEnabledAsync threw for {Symbol}", config!.Symbol);
            Log(strategy, "Warning",
                $"Не удалось проверить hedge-режим для {config.Symbol}: {ex.Message}. Пропускаю тик.");
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        if (hedgeEnabled == false)
        {
            Log(strategy, "Error",
                "Аккаунт в one-way режиме. Переключите Bybit в hedge-режим (Mode: Hedge) " +
                "в UI биржи, затем перезапустите стратегию.");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }
        // hedgeEnabled == null → probe failed (network blip / endpoint down). Log already
        // surfaced from the catch block above wouldn't have fired here — but the IFuturesExchangeService
        // contract allows null without throwing. Treat as transient: log and skip the tick.
        if (hedgeEnabled == null)
        {
            Log(strategy, "Warning",
                $"Hedge-режим не подтверждён для {config.Symbol} (биржа не ответила). Пробую снова на след. тике.");
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            switch (state.Phase)
            {
                case SmartGridHedgePhase.NotStarted:
                case SmartGridHedgePhase.Opening:
                    await OpenCycleAsync(strategy, config, state, futures, ct);
                    break;

                case SmartGridHedgePhase.Active:
                    await TickActiveAsync(strategy, config, state, futures, ct);
                    break;

                case SmartGridHedgePhase.HardClosing:
                    await HardCloseAsync(strategy, config, state, futures, isManual: false, ct);
                    break;

                case SmartGridHedgePhase.Closed:
                    // Wait for user. No-op.
                    break;
            }
        }
        catch (Exception ex)
        {
            // Critical safety net — the worker contract is that ProcessAsync NEVER throws past
            // this boundary. Persist whatever we have and log; the next tick will retry.
            _logger.LogError(ex, "SmartGridHedge: unhandled exception in Phase={Phase} for strategy {Id}",
                state.Phase, strategy.Id);
            Log(strategy, "Error",
                $"Внутренняя ошибка в фазе {state.Phase}: {ex.Message}. Тик прерван, состояние сохранено.");
        }
        finally
        {
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }
    }

    // ────────────────────────── Manual force-close ──────────────────────────

    /// <summary>
    /// Manual "Close" button entry point. Cancels every order on the symbol and market-closes
    /// both the long-side aggregate (qInit + filled DCA cells) and the short-side aggregate
    /// (qHedge + paired skim cells). Forces Phase = Closed regardless of AutoRestart.
    /// </summary>
    public async Task<SmartGridHedgeForceCloseResult> ForceCloseAsync(
        Strategy strategy, IFuturesExchangeService futures, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<SmartGridHedgeConfig>(strategy.ConfigJson, JsonOptions);
        if (config == null || string.IsNullOrWhiteSpace(config.Symbol))
            return new SmartGridHedgeForceCloseResult(false, "Некорректная конфигурация SmartGridHedge", 0m, 0m);

        var state = JsonSerializer.Deserialize<SmartGridHedgeState>(strategy.StateJson, JsonOptions)
                    ?? new SmartGridHedgeState();

        if (!futures.IsHedgeModeSupported)
            return new SmartGridHedgeForceCloseResult(false,
                "Биржа не поддерживает hedge-режим", 0m, 0m);

        var longCoinsBefore = state.QInitCoins + state.DcaCells.Where(c => c.Paired).Sum(c => c.QtyCoins);
        var shortCoinsBefore = state.QHedgeCoins + state.SkimCells.Where(c => c.Paired).Sum(c => c.ShortQtyCoins);

        if (longCoinsBefore == 0m && shortCoinsBefore == 0m && state.DcaCells.Count == 0 && state.SkimCells.Count == 0)
            return new SmartGridHedgeForceCloseResult(false, "Нет открытых позиций или ордеров", 0m, 0m);

        try
        {
            Log(strategy, "Info",
                $"🛑 Ручное закрытие SmartGridHedge: long≈{Math.Round(longCoinsBefore, 8)}, " +
                $"short≈{Math.Round(shortCoinsBefore, 8)}, dcaCells={state.DcaCells.Count}, skimCells={state.SkimCells.Count}");

            state.LastCycleEndReason = "Manual";
            await HardCloseAsync(strategy, config, state, futures, isManual: true, ct);
            // HardCloseAsync sets Phase = Opening (AutoRestart) or Closed; manual close always
            // overrides to Closed regardless of AutoRestart.
            state.Phase = SmartGridHedgePhase.Closed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SmartGridHedge: ForceCloseAsync threw for strategy {Id}", strategy.Id);
            Log(strategy, "Error", $"Ошибка при ручном закрытии: {ex.Message}");
            return new SmartGridHedgeForceCloseResult(false, $"Ошибка: {ex.Message}", 0m, 0m);
        }
        finally
        {
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        return new SmartGridHedgeForceCloseResult(true,
            "Позиции закрыты, ордера отменены", longCoinsBefore, shortCoinsBefore);
    }

    // ────────────────────────── Validation ──────────────────────────

    private bool ValidateConfig(Strategy strategy, SmartGridHedgeConfig? config)
    {
        if (config == null)
        {
            Log(strategy, "Error", "ConfigJson не распарсился");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return false;
        }
        if (string.IsNullOrWhiteSpace(config.Symbol))
        {
            Log(strategy, "Error", "Symbol пуст");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return false;
        }
        if (config.Step <= 0m || config.Step >= 1m)
        {
            Log(strategy, "Error", $"Step должен быть в (0, 1), текущее: {config.Step}");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return false;
        }
        if (config.NUp < 1)
        {
            Log(strategy, "Error", $"NUp должен быть ≥ 1, текущее: {config.NUp}");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return false;
        }
        if (config.NDown < 1)
        {
            Log(strategy, "Error", $"NDown должен быть ≥ 1, текущее: {config.NDown}");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return false;
        }
        if (config.LotUsd <= 0m)
        {
            Log(strategy, "Error", $"LotUsd должен быть > 0, текущее: {config.LotUsd}");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return false;
        }
        if (config.Leverage < 1)
        {
            Log(strategy, "Error", $"Leverage должен быть ≥ 1, текущее: {config.Leverage}");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return false;
        }
        return true;
    }

    // ────────────────────────── Phase: Opening ──────────────────────────

    /// <summary>
    /// Anchor the cycle, open the initial long + static hedge short, and lay down the DCA
    /// (and recycle-mode skim) limit orders. If any sub-step fails we set a short cooldown
    /// and keep the partial state — the next tick re-enters Opening and skips already-done
    /// steps based on idempotency markers (state.P0, state.QInitCoins, state.QHedgeCoins,
    /// cell.BuyOrderId / cell.ShortOrderId presence).
    /// </summary>
    private async Task OpenCycleAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, CancellationToken ct)
    {
        // Honour any cooldown set by a previous failure (or by HardClose AutoRestart).
        if (state.LastTickAt.HasValue && state.Phase == SmartGridHedgePhase.Opening
            && state.LastTickAt.Value.AddSeconds(OpeningRetryCooldownSeconds) > DateTime.UtcNow
            && state.P0 > 0m && (state.QInitCoins == 0m || state.QHedgeCoins == 0m))
        {
            // Mid-Opening cooldown — wait for the cooldown window to elapse.
            return;
        }

        state.Phase = SmartGridHedgePhase.Opening;

        // ───────── 1. Capture P0 (idempotent — only first time through) ─────────
        if (state.P0 <= 0m)
        {
            decimal? mark;
            try { mark = await futures.GetTickerPriceAsync(config.Symbol); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SmartGridHedge: GetTickerPriceAsync(open) failed for {Symbol}", config.Symbol);
                Log(strategy, "Warning",
                    $"Не удалось получить цену {config.Symbol}: {ex.Message}. Пропуск тика.");
                return;
            }
            if (mark is null or <= 0m)
            {
                Log(strategy, "Warning", $"Цена {config.Symbol} = null/0 — пропуск тика");
                return;
            }

            state.P0 = mark.Value;
            state.PAvgInit = mark.Value;
            state.HBreak = state.P0 * Pow(1m + config.Step, config.NUp);
            state.LBreak = state.P0 * Pow(1m - config.Step, config.NDown);
            state.CycleStartedAt = DateTime.UtcNow;
            state.CycleGridRealized = 0m;
            state.CycleHedgeRealized = 0m;
            state.CycleFees = 0m;
            state.LastCycleEndReason = null;

            Log(strategy, "Info",
                $"📌 P0 {config.Symbol} = {Math.Round(state.P0, 8)} | " +
                $"HBreak = {Math.Round(state.HBreak, 8)} (+{Math.Round((state.HBreak / state.P0 - 1m) * 100m, 4)}%) | " +
                $"LBreak = {Math.Round(state.LBreak, 8)} (-{Math.Round((1m - state.LBreak / state.P0) * 100m, 4)}%) | " +
                $"SkimMode = {config.SkimMode}");
        }

        // ───────── 2. Best-effort leverage ─────────
        try { await futures.SetLeverageAsync(config.Symbol, config.Leverage); }
        catch (NotSupportedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmartGridHedge: SetLeverageAsync failed for {Symbol}", config.Symbol);
        }

        // ───────── 3. Compute Q_hedge in coins ─────────
        if (state.QHedgeCoins == 0m)
        {
            if (config.QHedgeOverride.HasValue && config.QHedgeOverride.Value > 0m)
            {
                state.QHedgeCoins = config.QHedgeOverride.Value;
                Log(strategy, "Info",
                    $"🎚️ Q_hedge override: {Math.Round(state.QHedgeCoins, 8)} coins (user-supplied)");
            }
            else
            {
                try
                {
                    var opt = SymmetricHedgeOptimizer.Optimize(
                        state.P0, config.Step, config.NUp, config.NDown,
                        config.LotUsd, config.SkimMode, config.MakerFeeBps, config.TakerFeeBps);
                    state.QHedgeCoins = opt.QHedgeCoins;
                    Log(strategy, "Info",
                        $"🎚️ Q_hedge (optimizer): {Math.Round(state.QHedgeCoins, 8)} coins, " +
                        $"WCL ≈ ${Math.Round(opt.WorstCaseLoss, 2)}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SmartGridHedge: optimizer threw for strategy {Id}", strategy.Id);
                    Log(strategy, "Error",
                        $"Не удалось рассчитать Q_hedge оптимизатором: {ex.Message}. Остановка.");
                    strategy.Status = Core.Enums.StrategyStatus.Stopped;
                    return;
                }
            }
        }

        // ───────── 4. Open initial long (positionIdx=1, MARKET) ─────────
        if (state.QInitCoins == 0m)
        {
            OrderResultDto longResult;
            try { longResult = await futures.OpenHedgeLongAsync(config.Symbol, config.LotUsd); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SmartGridHedge: OpenHedgeLongAsync threw for {Symbol}", config.Symbol);
                Log(strategy, "Error",
                    $"Исключение при открытии initial long: {ex.Message}. Cooldown {OpeningRetryCooldownSeconds}с.");
                state.LastTickAt = DateTime.UtcNow;
                return;
            }
            if (!longResult.Success || longResult.FilledQuantity is not > 0m)
            {
                var fatal = IsFatal(longResult.ErrorMessage);
                Log(strategy, fatal ? "Error" : "Warning",
                    $"❌ Initial long не открыт: {longResult.ErrorMessage}. " +
                    (fatal ? "Фатальная ошибка биржи — останавливаю." : $"Cooldown {OpeningRetryCooldownSeconds}с."));
                if (fatal) strategy.Status = Core.Enums.StrategyStatus.Stopped;
                state.LastTickAt = DateTime.UtcNow;
                return;
            }

            state.QInitCoins = longResult.FilledQuantity!.Value;
            // PAvgInit stays pinned to P0 (state spec); the FilledPrice is only logged.
            var longFillPx = longResult.FilledPrice ?? state.P0;
            var longFee = state.QInitCoins * longFillPx * futures.TakerFeeRate;
            state.CycleFees += longFee;
            state.TotalFees += longFee;

            RecordTrade(strategy, config.Symbol, "Buy", state.QInitCoins, longFillPx,
                longResult.OrderId, "InitLongOpen", commission: longFee);
            Log(strategy, "Info",
                $"✅ Initial LONG opened: qty = {Math.Round(state.QInitCoins, 8)} @ {Math.Round(longFillPx, 8)} " +
                $"(id = {longResult.OrderId}, fee ≈ ${Math.Round(longFee, 4)})");
        }

        // ───────── 5. Open static hedge short (positionIdx=2, MARKET) ─────────
        // Hedge open is gated on a separate marker so a long-success + hedge-failure on the
        // same tick doesn't re-open the long when we retry.
        if (state.HedgeEntryPrice == 0m)
        {
            var hedgeNotional = state.QHedgeCoins * state.P0;
            if (hedgeNotional <= 0m)
            {
                // Optimizer returned Q_hedge = 0 (degenerate config, e.g. denom = 0). Skip the
                // hedge open entirely — the cycle runs as a pure grid. Mark HedgeEntryPrice = -1
                // as a sentinel so we don't re-enter this branch.
                Log(strategy, "Warning",
                    "Q_hedge = 0 → пропускаю открытие хеджа, цикл идёт как чистая сетка.");
                state.HedgeEntryPrice = state.P0; // sentinel: non-zero, equal to P0
            }
            else
            {
                OrderResultDto hedgeResult;
                try { hedgeResult = await futures.OpenHedgeShortAsync(config.Symbol, hedgeNotional); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SmartGridHedge: OpenHedgeShortAsync threw for {Symbol}", config.Symbol);
                    Log(strategy, "Error",
                        $"Исключение при открытии хеджа: {ex.Message}. Cooldown {OpeningRetryCooldownSeconds}с.");
                    state.LastTickAt = DateTime.UtcNow;
                    return;
                }
                if (!hedgeResult.Success || hedgeResult.FilledQuantity is not > 0m)
                {
                    var fatal = IsFatal(hedgeResult.ErrorMessage);
                    Log(strategy, fatal ? "Error" : "Warning",
                        $"❌ Хедж SHORT не открыт: {hedgeResult.ErrorMessage}. " +
                        (fatal ? "Фатальная ошибка биржи — останавливаю." : $"Cooldown {OpeningRetryCooldownSeconds}с."));
                    if (fatal) strategy.Status = Core.Enums.StrategyStatus.Stopped;
                    state.LastTickAt = DateTime.UtcNow;
                    return;
                }

                // Use the exchange-reported filled qty as the authoritative size (it may differ
                // from our notional/price quote by lot-size rounding).
                state.QHedgeCoins = hedgeResult.FilledQuantity!.Value;
                state.HedgeEntryPrice = hedgeResult.FilledPrice ?? state.P0;
                var hedgeFee = state.QHedgeCoins * state.HedgeEntryPrice * futures.TakerFeeRate;
                state.CycleFees += hedgeFee;
                state.TotalFees += hedgeFee;

                RecordTrade(strategy, config.Symbol, "Sell", state.QHedgeCoins, state.HedgeEntryPrice,
                    hedgeResult.OrderId, "HedgeOpen", commission: hedgeFee);
                Log(strategy, "Info",
                    $"🛡️ Static SHORT hedge opened: qty = {Math.Round(state.QHedgeCoins, 8)} @ " +
                    $"{Math.Round(state.HedgeEntryPrice, 8)} (id = {hedgeResult.OrderId}, fee ≈ ${Math.Round(hedgeFee, 4)})");
            }
        }

        // ───────── 6. Lay down DCA buy-limits for k = 1..NDown-1 ─────────
        await EnsureDcaCellsAsync(strategy, config, state, futures, ct);

        // ───────── 7. Lay down skim sells for k = 1..NUp-1 (recycle modes only) ─────────
        if (config.SkimMode != SmartGridSkimMode.OneShot)
        {
            await EnsureRecycleSkimCellsAsync(strategy, config, state, futures, ct);
        }
        else
        {
            // OneShot — pre-allocate the cells so we can mark FiredOnceShot on each cross
            // without re-creating them. No orders are placed; trim fires reactively in Active.
            EnsureOneShotSkimCells(state, config);
        }

        // ───────── 8. Advance to Active ─────────
        state.Phase = SmartGridHedgePhase.Active;
        state.LastTickAt = DateTime.UtcNow;
        Log(strategy, "Info",
            $"🟢 SmartGridHedge ARMED: cells DCA={state.DcaCells.Count}, " +
            $"skim={state.SkimCells.Count} ({config.SkimMode}). Phase = Active.");
    }

    /// <summary>
    /// Ensures every DCA cell (k = 1..NDown-1) has a Buy limit on the books. Idempotent:
    /// cells with a non-null BuyOrderId are skipped (they're either resting or already
    /// paired with a sell). Throttles placements with InterOrderDelayMs.
    /// </summary>
    private async Task EnsureDcaCellsAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, CancellationToken ct)
    {
        var first = true;
        for (var k = 1; k <= config.NDown - 1; k++)
        {
            var cell = state.DcaCells.FirstOrDefault(c => c.K == k);
            if (cell != null && !string.IsNullOrEmpty(cell.BuyOrderId)) continue;

            var dk = state.P0 * Pow(1m - config.Step, k);
            var dkPrev = state.P0 * Pow(1m - config.Step, k - 1);
            if (dk <= 0m) continue;
            var qty = config.LotUsd / dk;
            if (qty <= 0m) continue;

            if (!first) await Task.Delay(InterOrderDelayMs, ct);
            first = false;

            OrderResultDto result;
            try
            {
                result = await futures.PlaceLimitHedgeOrderAsync(
                    config.Symbol, "Buy", "Long", dk, qty, reduceOnly: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SmartGridHedge: DCA limit placement threw k={K}", k);
                Log(strategy, "Warning",
                    $"DCA k = {k} @ {Math.Round(dk, 8)} исключение: {ex.Message}. Повтор на след. тике.");
                state.LastTickAt = DateTime.UtcNow;
                return;
            }
            if (!result.Success || string.IsNullOrEmpty(result.OrderId))
            {
                var fatal = IsFatal(result.ErrorMessage);
                Log(strategy, fatal ? "Error" : "Warning",
                    $"DCA k = {k} не выставлен: {result.ErrorMessage}");
                if (fatal) { strategy.Status = Core.Enums.StrategyStatus.Stopped; return; }
                state.LastTickAt = DateTime.UtcNow;
                return;
            }

            if (cell == null)
            {
                cell = new SmartGridDcaCell
                {
                    K = k,
                    BuyPrice = dk,
                    SellPrice = dkPrev,
                    BuyOrderId = result.OrderId,
                    Paired = false,
                    QtyCoins = 0m
                };
                state.DcaCells.Add(cell);
            }
            else
            {
                cell.BuyPrice = dk;
                cell.SellPrice = dkPrev;
                cell.BuyOrderId = result.OrderId;
                cell.Paired = false;
                cell.QtyCoins = 0m;
            }
            Log(strategy, "Info",
                $"📉 DCA k = {k}: BUY {Math.Round(qty, 8)} @ {Math.Round(dk, 8)} → " +
                $"paired SELL target = {Math.Round(dkPrev, 8)} (id = {result.OrderId})");
        }
    }

    /// <summary>
    /// Ensures every recycle-mode skim cell (k = 1..NUp-1) has a Sell limit on the books.
    /// </summary>
    private async Task EnsureRecycleSkimCellsAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, CancellationToken ct)
    {
        var first = true;
        for (var k = 1; k <= config.NUp - 1; k++)
        {
            var cell = state.SkimCells.FirstOrDefault(c => c.K == k);
            if (cell != null && !string.IsNullOrEmpty(cell.ShortOrderId)) continue;

            var uk = state.P0 * Pow(1m + config.Step, k);
            var ukPrev = state.P0 * Pow(1m + config.Step, k - 1);
            if (uk <= 0m) continue;
            var shortQty = config.SkimMode switch
            {
                SmartGridSkimMode.FullRecycle => config.LotUsd / uk,
                SmartGridSkimMode.ExcessRecycle => config.LotUsd * config.Step / uk,
                _ => 0m
            };
            if (shortQty <= 0m) continue;

            if (!first) await Task.Delay(InterOrderDelayMs, ct);
            first = false;

            OrderResultDto result;
            try
            {
                result = await futures.PlaceLimitHedgeOrderAsync(
                    config.Symbol, "Sell", "Short", uk, shortQty, reduceOnly: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SmartGridHedge: skim limit placement threw k={K}", k);
                Log(strategy, "Warning",
                    $"Skim k = {k} @ {Math.Round(uk, 8)} исключение: {ex.Message}. Повтор на след. тике.");
                state.LastTickAt = DateTime.UtcNow;
                return;
            }
            if (!result.Success || string.IsNullOrEmpty(result.OrderId))
            {
                var fatal = IsFatal(result.ErrorMessage);
                Log(strategy, fatal ? "Error" : "Warning",
                    $"Skim k = {k} не выставлен: {result.ErrorMessage}");
                if (fatal) { strategy.Status = Core.Enums.StrategyStatus.Stopped; return; }
                state.LastTickAt = DateTime.UtcNow;
                return;
            }

            if (cell == null)
            {
                cell = new SmartGridSkimCell
                {
                    K = k,
                    SellPrice = uk,
                    CoverPrice = ukPrev,
                    ShortOrderId = result.OrderId,
                    Paired = false,
                    ShortQtyCoins = shortQty
                };
                state.SkimCells.Add(cell);
            }
            else
            {
                cell.SellPrice = uk;
                cell.CoverPrice = ukPrev;
                cell.ShortOrderId = result.OrderId;
                cell.Paired = false;
                cell.ShortQtyCoins = shortQty;
            }
            Log(strategy, "Info",
                $"📈 Skim k = {k}: SELL {Math.Round(shortQty, 8)} @ {Math.Round(uk, 8)} → " +
                $"paired COVER target = {Math.Round(ukPrev, 8)} (id = {result.OrderId})");
        }
    }

    /// <summary>
    /// OneShot bookkeeping: pre-create the skim cell records (no orders placed) so the
    /// Active tick can flip FiredOnceShot on each first cross of U_k.
    /// </summary>
    private static void EnsureOneShotSkimCells(SmartGridHedgeState state, SmartGridHedgeConfig config)
    {
        for (var k = 1; k <= config.NUp - 1; k++)
        {
            var cell = state.SkimCells.FirstOrDefault(c => c.K == k);
            var uk = state.P0 * Pow(1m + config.Step, k);
            var ukPrev = state.P0 * Pow(1m + config.Step, k - 1);
            if (cell == null)
            {
                state.SkimCells.Add(new SmartGridSkimCell
                {
                    K = k,
                    SellPrice = uk,
                    CoverPrice = ukPrev,
                    FiredOnceShot = false
                });
            }
            else
            {
                cell.SellPrice = uk;
                cell.CoverPrice = ukPrev;
            }
        }
    }

    // ────────────────────────── Phase: Active ──────────────────────────

    private async Task TickActiveAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, CancellationToken ct)
    {
        // ───────── 1. Fetch mark price ─────────
        decimal? markN;
        try { markN = await futures.GetTickerPriceAsync(config.Symbol); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmartGridHedge: GetTickerPriceAsync(active) failed for {Symbol}", config.Symbol);
            Log(strategy, "Warning", $"Не удалось получить цену {config.Symbol}: {ex.Message}. Пропуск тика.");
            return;
        }
        if (markN is null or <= 0m)
        {
            Log(strategy, "Warning", $"Цена {config.Symbol} = null/0 — пропуск тика");
            return;
        }
        var mark = markN.Value;
        state.LastMarkPrice = mark;
        state.LastTickAt = DateTime.UtcNow;

        // ───────── 2. Boundary check (fast-path before any polling) ─────────
        if (mark >= state.HBreak)
        {
            state.Phase = SmartGridHedgePhase.HardClosing;
            state.LastCycleEndReason = "HBreak";
            Log(strategy, "Warning",
                $"⬆️ HBREAK: mark {Math.Round(mark, 8)} ≥ {Math.Round(state.HBreak, 8)}. " +
                "Закрываю всё на следующем тике (HardClosing).");
            return;
        }
        if (mark <= state.LBreak)
        {
            state.Phase = SmartGridHedgePhase.HardClosing;
            state.LastCycleEndReason = "LBreak";
            Log(strategy, "Warning",
                $"⬇️ LBREAK: mark {Math.Round(mark, 8)} ≤ {Math.Round(state.LBreak, 8)}. " +
                "Закрываю всё на следующем тике (HardClosing).");
            return;
        }

        // ───────── 3. Poll DCA cells ─────────
        await PollDcaCellsAsync(strategy, config, state, futures, ct);

        // ───────── 4. Poll skim cells ─────────
        if (config.SkimMode == SmartGridSkimMode.OneShot)
        {
            await TickOneShotSkimAsync(strategy, config, state, futures, mark, ct);
        }
        else
        {
            await PollRecycleSkimCellsAsync(strategy, config, state, futures, ct);
        }

        // ───────── 5. Periodic summary ─────────
        _summaryTickCounter++;
        if (_summaryTickCounter % SummaryEveryNTicks == 0)
        {
            var openDcaPairs = state.DcaCells.Count(c => c.Paired);
            var openSkimPairs = state.SkimCells.Count(c => c.Paired);
            var hedgeUnreal = state.QHedgeCoins * (state.HedgeEntryPrice - mark);
            Log(strategy, "Info",
                $"📊 mark = {Math.Round(mark, 8)} | cycle realized = ${Math.Round(state.CycleGridRealized, 2)} | " +
                $"hedge unrealized = ${Math.Round(hedgeUnreal, 2)} | " +
                $"DCA pairs = {openDcaPairs}/{state.DcaCells.Count}, skim pairs = {openSkimPairs}/{state.SkimCells.Count}");
        }
    }

    private async Task PollDcaCellsAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, CancellationToken ct)
    {
        // Snapshot — handlers may mutate cell state mid-iteration but never add/remove entries.
        var cells = state.DcaCells.ToList();
        foreach (var cell in cells)
        {
            // ───── Step A: Buy not yet paired → poll fill ─────
            if (cell.BuyOrderId != null && !cell.Paired)
            {
                OrderStatusDto? status;
                try { status = await futures.GetOrderAsync(config.Symbol, cell.BuyOrderId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SmartGridHedge: DCA buy poll threw k={K}", cell.K);
                    continue;
                }
                if (status == null) continue;

                if (status.Status == OrderLifecycleStatus.Filled && status.FilledQuantity > 0m)
                {
                    var fillQty = status.FilledQuantity;
                    var fillPx = status.AverageFilledPrice > 0m ? status.AverageFilledPrice : cell.BuyPrice;
                    cell.QtyCoins = fillQty;
                    cell.Paired = true;
                    var fee = fillQty * fillPx * futures.MakerFeeRate;
                    state.CycleFees += fee;
                    state.TotalFees += fee;

                    RecordTrade(strategy, config.Symbol, "Buy", fillQty, fillPx,
                        cell.BuyOrderId, $"DcaBuyFill@k={cell.K}", commission: fee);
                    Log(strategy, "Info",
                        $"✅ DCA k = {cell.K} BUY filled: {Math.Round(fillQty, 8)} @ {Math.Round(fillPx, 8)} → " +
                        $"placing paired SELL @ {Math.Round(cell.SellPrice, 8)}");

                    // Place the paired reduce-only Sell limit on positionSide=Long.
                    OrderResultDto sellResult;
                    try
                    {
                        sellResult = await futures.PlaceLimitHedgeOrderAsync(
                            config.Symbol, "Sell", "Long", cell.SellPrice, cell.QtyCoins, reduceOnly: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SmartGridHedge: DCA paired sell threw k={K}", cell.K);
                        Log(strategy, "Warning",
                            $"DCA k = {cell.K} paired SELL исключение: {ex.Message}. Повтор на след. тике.");
                        // Leave Paired = true with SellOrderId = null — we'll heal on next tick.
                        continue;
                    }
                    if (sellResult.Success && !string.IsNullOrEmpty(sellResult.OrderId))
                    {
                        cell.SellOrderId = sellResult.OrderId;
                    }
                    else
                    {
                        Log(strategy, "Warning",
                            $"DCA k = {cell.K} paired SELL не выставлен: {sellResult.ErrorMessage}. " +
                            "Повтор на след. тике.");
                    }
                }
                else if (status.Status == OrderLifecycleStatus.Cancelled || status.Status == OrderLifecycleStatus.Rejected)
                {
                    Log(strategy, "Warning",
                        $"DCA k = {cell.K} BUY отменён/отклонён без филла — перевыставлю в конце тика");
                    cell.BuyOrderId = null;
                }
                continue;
            }

            // ───── Step B: Buy paired with Sell → poll sell fill ─────
            if (cell.SellOrderId != null && cell.Paired)
            {
                OrderStatusDto? status;
                try { status = await futures.GetOrderAsync(config.Symbol, cell.SellOrderId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SmartGridHedge: DCA sell poll threw k={K}", cell.K);
                    continue;
                }
                if (status == null) continue;

                if (status.Status == OrderLifecycleStatus.Filled && status.FilledQuantity > 0m)
                {
                    var fillPx = status.AverageFilledPrice > 0m ? status.AverageFilledPrice : cell.SellPrice;
                    var realized = cell.QtyCoins * (fillPx - cell.BuyPrice);
                    var fee = cell.QtyCoins * fillPx * futures.MakerFeeRate;
                    state.CycleGridRealized += realized;
                    state.GridRealizedPnl += realized;
                    state.CycleFees += fee;
                    state.TotalFees += fee;

                    RecordTrade(strategy, config.Symbol, "Sell", cell.QtyCoins, fillPx,
                        cell.SellOrderId, $"DcaSellFill@k={cell.K}", pnlDollar: realized - fee, commission: fee);
                    Log(strategy, "Info",
                        $"💰 DCA k = {cell.K} SELL filled @ {Math.Round(fillPx, 8)}: " +
                        $"realized = ${Math.Round(realized, 4)} (gross) − fee ${Math.Round(fee, 4)}");

                    // RE-ARM: place a new Buy limit at BuyPrice (qty = LotUsd / BuyPrice).
                    cell.SellOrderId = null;
                    cell.Paired = false;
                    cell.QtyCoins = 0m;

                    var rearmQty = config.LotUsd / cell.BuyPrice;
                    OrderResultDto rearmResult;
                    try
                    {
                        rearmResult = await futures.PlaceLimitHedgeOrderAsync(
                            config.Symbol, "Buy", "Long", cell.BuyPrice, rearmQty, reduceOnly: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SmartGridHedge: DCA re-arm BUY threw k={K}", cell.K);
                        Log(strategy, "Warning",
                            $"DCA k = {cell.K} re-arm BUY исключение: {ex.Message}. " +
                            "Перевыставлю позже (EnsureDcaCellsAsync).");
                        cell.BuyOrderId = null;
                        continue;
                    }
                    if (rearmResult.Success && !string.IsNullOrEmpty(rearmResult.OrderId))
                    {
                        cell.BuyOrderId = rearmResult.OrderId;
                        Log(strategy, "Info",
                            $"🔁 DCA k = {cell.K} re-armed: BUY {Math.Round(rearmQty, 8)} @ " +
                            $"{Math.Round(cell.BuyPrice, 8)} (id = {rearmResult.OrderId})");
                    }
                    else
                    {
                        Log(strategy, "Warning",
                            $"DCA k = {cell.K} re-arm BUY не выставлен: {rearmResult.ErrorMessage}");
                        cell.BuyOrderId = null;
                    }
                }
                else if (status.Status == OrderLifecycleStatus.Cancelled || status.Status == OrderLifecycleStatus.Rejected)
                {
                    Log(strategy, "Warning",
                        $"DCA k = {cell.K} paired SELL отменён без филла — перевыставлю на след. тике");
                    cell.SellOrderId = null;
                }
            }
        }

        // Heal any cells that lost their BuyOrderId mid-flight (cancellation, exchange refusal).
        await EnsureDcaCellsAsync(strategy, config, state, futures, ct);
    }

    /// <summary>
    /// OneShot skim: on first cross of U_k, market-trim the excess from qInit. Each cell fires
    /// exactly once per cycle.
    /// </summary>
    private async Task TickOneShotSkimAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, decimal mark, CancellationToken ct)
    {
        // Iterate ordered by K ascending so a single tick that crosses multiple rungs fires
        // them in price order — and updates qInit between fires so excessUsd reflects the
        // residual long size, not the original.
        var cells = state.SkimCells.OrderBy(c => c.K).ToList();
        foreach (var cell in cells)
        {
            if (cell.FiredOnceShot) continue;
            if (mark < cell.SellPrice) continue;
            if (state.QInitCoins <= 0m) break;

            var excessUsd = state.QInitCoins * cell.SellPrice - config.LotUsd;
            if (excessUsd <= 0m)
            {
                // Nothing to trim (qInit already at or below LotUsd notional at U_k). Mark
                // the cell fired so we don't re-evaluate every tick.
                cell.FiredOnceShot = true;
                continue;
            }
            var trimCoins = excessUsd / cell.SellPrice;
            if (trimCoins <= 0m || trimCoins > state.QInitCoins) trimCoins = state.QInitCoins;

            OrderResultDto trimResult;
            try { trimResult = await futures.CloseHedgeLongAsync(config.Symbol, trimCoins); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SmartGridHedge: OneShot trim threw k={K}", cell.K);
                Log(strategy, "Warning",
                    $"OneShot k = {cell.K} trim исключение: {ex.Message}. Повтор на след. тике.");
                continue;
            }
            if (!trimResult.Success || trimResult.FilledQuantity is not > 0m)
            {
                Log(strategy, "Warning",
                    $"OneShot k = {cell.K} trim не исполнен: {trimResult.ErrorMessage}. Повтор на след. тике.");
                continue;
            }

            var trimmed = trimResult.FilledQuantity!.Value;
            var fillPx = trimResult.FilledPrice ?? cell.SellPrice;
            var realized = trimmed * (fillPx - state.PAvgInit);
            var fee = trimmed * fillPx * futures.TakerFeeRate;

            state.QInitCoins -= trimmed;
            if (state.QInitCoins < 0m) state.QInitCoins = 0m;
            state.CycleGridRealized += realized;
            state.GridRealizedPnl += realized;
            state.CycleFees += fee;
            state.TotalFees += fee;
            cell.FiredOnceShot = true;

            RecordTrade(strategy, config.Symbol, "Sell", trimmed, fillPx,
                trimResult.OrderId, $"OneShotTrim@k={cell.K}", pnlDollar: realized - fee, commission: fee);
            Log(strategy, "Info",
                $"✂️ OneShot k = {cell.K} trim @ {Math.Round(fillPx, 8)}: " +
                $"sold {Math.Round(trimmed, 8)}, realized = ${Math.Round(realized, 4)} (gross), " +
                $"qInit → {Math.Round(state.QInitCoins, 8)}");
        }
    }

    private async Task PollRecycleSkimCellsAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, CancellationToken ct)
    {
        var cells = state.SkimCells.ToList();
        foreach (var cell in cells)
        {
            // ───── Step A: Short not yet paired → poll fill ─────
            if (cell.ShortOrderId != null && !cell.Paired)
            {
                OrderStatusDto? status;
                try { status = await futures.GetOrderAsync(config.Symbol, cell.ShortOrderId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SmartGridHedge: skim short poll threw k={K}", cell.K);
                    continue;
                }
                if (status == null) continue;

                if (status.Status == OrderLifecycleStatus.Filled && status.FilledQuantity > 0m)
                {
                    cell.Paired = true;
                    // Trust exchange-reported fill qty for the cover sizing.
                    cell.ShortQtyCoins = status.FilledQuantity;
                    var fillPx = status.AverageFilledPrice > 0m ? status.AverageFilledPrice : cell.SellPrice;
                    var fee = cell.ShortQtyCoins * fillPx * futures.MakerFeeRate;
                    state.CycleFees += fee;
                    state.TotalFees += fee;

                    RecordTrade(strategy, config.Symbol, "Sell", cell.ShortQtyCoins, fillPx,
                        cell.ShortOrderId, $"SkimShortFill@k={cell.K}", commission: fee);
                    Log(strategy, "Info",
                        $"✅ Skim k = {cell.K} SHORT filled: {Math.Round(cell.ShortQtyCoins, 8)} @ " +
                        $"{Math.Round(fillPx, 8)} → placing COVER BUY @ {Math.Round(cell.CoverPrice, 8)}");

                    OrderResultDto coverResult;
                    try
                    {
                        coverResult = await futures.PlaceLimitHedgeOrderAsync(
                            config.Symbol, "Buy", "Short", cell.CoverPrice, cell.ShortQtyCoins, reduceOnly: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SmartGridHedge: skim cover BUY threw k={K}", cell.K);
                        Log(strategy, "Warning",
                            $"Skim k = {cell.K} cover BUY исключение: {ex.Message}. Повтор на след. тике.");
                        continue;
                    }
                    if (coverResult.Success && !string.IsNullOrEmpty(coverResult.OrderId))
                    {
                        cell.CoverOrderId = coverResult.OrderId;
                    }
                    else
                    {
                        Log(strategy, "Warning",
                            $"Skim k = {cell.K} cover BUY не выставлен: {coverResult.ErrorMessage}. " +
                            "Повтор на след. тике.");
                    }
                }
                else if (status.Status == OrderLifecycleStatus.Cancelled || status.Status == OrderLifecycleStatus.Rejected)
                {
                    Log(strategy, "Warning",
                        $"Skim k = {cell.K} SHORT отменён без филла — перевыставлю в конце тика");
                    cell.ShortOrderId = null;
                }
                continue;
            }

            // ───── Step B: Short paired with Cover → poll cover fill ─────
            if (cell.CoverOrderId != null && cell.Paired)
            {
                OrderStatusDto? status;
                try { status = await futures.GetOrderAsync(config.Symbol, cell.CoverOrderId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SmartGridHedge: skim cover poll threw k={K}", cell.K);
                    continue;
                }
                if (status == null) continue;

                if (status.Status == OrderLifecycleStatus.Filled && status.FilledQuantity > 0m)
                {
                    var fillPx = status.AverageFilledPrice > 0m ? status.AverageFilledPrice : cell.CoverPrice;
                    var realized = cell.ShortQtyCoins * (cell.SellPrice - fillPx);
                    var fee = cell.ShortQtyCoins * fillPx * futures.MakerFeeRate;
                    state.CycleGridRealized += realized;
                    state.GridRealizedPnl += realized;
                    state.CycleFees += fee;
                    state.TotalFees += fee;

                    RecordTrade(strategy, config.Symbol, "Buy", cell.ShortQtyCoins, fillPx,
                        cell.CoverOrderId, $"SkimCoverFill@k={cell.K}", pnlDollar: realized - fee, commission: fee);
                    Log(strategy, "Info",
                        $"💰 Skim k = {cell.K} COVER filled @ {Math.Round(fillPx, 8)}: " +
                        $"realized = ${Math.Round(realized, 4)} (gross) − fee ${Math.Round(fee, 4)}");

                    // RE-ARM: new sell limit at SellPrice. Recompute qty by mode (the math
                    // gives the same answer since SellPrice is fixed within a cycle, but be
                    // explicit so a future Step/LotUsd hot-swap would propagate correctly).
                    var rearmQty = config.SkimMode switch
                    {
                        SmartGridSkimMode.FullRecycle => config.LotUsd / cell.SellPrice,
                        SmartGridSkimMode.ExcessRecycle => config.LotUsd * config.Step / cell.SellPrice,
                        _ => 0m
                    };
                    cell.CoverOrderId = null;
                    cell.Paired = false;

                    if (rearmQty <= 0m)
                    {
                        cell.ShortOrderId = null;
                        Log(strategy, "Warning",
                            $"Skim k = {cell.K} re-arm qty = 0 — пропуск, ячейка пуста до конца цикла");
                        continue;
                    }

                    OrderResultDto rearmResult;
                    try
                    {
                        rearmResult = await futures.PlaceLimitHedgeOrderAsync(
                            config.Symbol, "Sell", "Short", cell.SellPrice, rearmQty, reduceOnly: false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SmartGridHedge: skim re-arm SHORT threw k={K}", cell.K);
                        Log(strategy, "Warning",
                            $"Skim k = {cell.K} re-arm SHORT исключение: {ex.Message}");
                        cell.ShortOrderId = null;
                        continue;
                    }
                    if (rearmResult.Success && !string.IsNullOrEmpty(rearmResult.OrderId))
                    {
                        cell.ShortOrderId = rearmResult.OrderId;
                        cell.ShortQtyCoins = rearmQty;
                        Log(strategy, "Info",
                            $"🔁 Skim k = {cell.K} re-armed: SELL {Math.Round(rearmQty, 8)} @ " +
                            $"{Math.Round(cell.SellPrice, 8)} (id = {rearmResult.OrderId})");
                    }
                    else
                    {
                        Log(strategy, "Warning",
                            $"Skim k = {cell.K} re-arm SHORT не выставлен: {rearmResult.ErrorMessage}");
                        cell.ShortOrderId = null;
                    }
                }
                else if (status.Status == OrderLifecycleStatus.Cancelled || status.Status == OrderLifecycleStatus.Rejected)
                {
                    Log(strategy, "Warning",
                        $"Skim k = {cell.K} COVER отменён без филла — перевыставлю на след. тике");
                    cell.CoverOrderId = null;
                }
            }
        }

        // Heal any cells that lost their ShortOrderId mid-flight.
        await EnsureRecycleSkimCellsAsync(strategy, config, state, futures, ct);
    }

    // ────────────────────────── Phase: HardClosing ──────────────────────────

    /// <summary>
    /// Atomic close-everything. Used both by boundary triggers (HBreak/LBreak) and by manual
    /// ForceCloseAsync. Cancels all orders on the symbol, then market-closes the LONG-side
    /// aggregate (qInit + filled DCA cells) and the SHORT-side aggregate (qHedge + paired
    /// skim cells), then either re-anchors a fresh cycle (AutoRestart) or stops (Closed).
    /// </summary>
    private async Task HardCloseAsync(
        Strategy strategy, SmartGridHedgeConfig config, SmartGridHedgeState state,
        IFuturesExchangeService futures, bool isManual, CancellationToken ct)
    {
        Log(strategy, "Info",
            $"🚪 HardClose (reason = {state.LastCycleEndReason ?? (isManual ? "Manual" : "?")}): " +
            $"qInit = {Math.Round(state.QInitCoins, 8)}, qHedge = {Math.Round(state.QHedgeCoins, 8)}, " +
            $"dcaCells = {state.DcaCells.Count}, skimCells = {state.SkimCells.Count}");

        // ───────── 1. Cancel all orders on the symbol ─────────
        // Belt-and-suspenders — cancels both positionIdx=1 and positionIdx=2 resting orders.
        try { await futures.CancelAllOrdersAsync(config.Symbol); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmartGridHedge: CancelAllOrders failed for {Symbol}", config.Symbol);
            Log(strategy, "Warning", $"CancelAllOrders {config.Symbol} исключение: {ex.Message} (продолжаю)");
        }

        // Use the latest mark for boundary-close accounting. Fall back to LBreak/HBreak if the
        // probe fails (we already crossed it, so it's a reasonable estimate).
        decimal closePx;
        try
        {
            var probed = await futures.GetTickerPriceAsync(config.Symbol);
            closePx = probed is > 0m ? probed.Value
                : state.LastCycleEndReason == "HBreak" ? state.HBreak
                : state.LastCycleEndReason == "LBreak" ? state.LBreak
                : state.LastMarkPrice ?? state.P0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmartGridHedge: close-price probe failed for {Symbol}", config.Symbol);
            closePx = state.LastMarkPrice ?? state.P0;
        }

        // ───────── 2. Close LONG side ─────────
        var dcaPairedLong = state.DcaCells.Where(c => c.Paired).Sum(c => c.QtyCoins);
        var totalLong = state.QInitCoins + dcaPairedLong;
        if (totalLong > 0m)
        {
            OrderResultDto longClose;
            try { longClose = await futures.CloseHedgeLongAsync(config.Symbol, totalLong); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SmartGridHedge: long close threw for {Symbol}", config.Symbol);
                Log(strategy, "Error",
                    $"Закрытие LONG исключение: {ex.Message}. Останусь в HardClosing для ретрая.");
                return;
            }
            if (!longClose.Success)
            {
                Log(strategy, "Error",
                    $"Закрытие LONG не удалось: {longClose.ErrorMessage}. Останусь в HardClosing для ретрая.");
                return;
            }

            var longFillPx = longClose.FilledPrice ?? closePx;
            var longRealized = 0m;
            // Initial long piece (entry = PAvgInit, anchored to P0)
            if (state.QInitCoins > 0m)
            {
                longRealized += state.QInitCoins * (longFillPx - state.PAvgInit);
            }
            // DCA paired cells (entry = each cell's BuyPrice)
            foreach (var cell in state.DcaCells.Where(c => c.Paired))
            {
                longRealized += cell.QtyCoins * (longFillPx - cell.BuyPrice);
            }
            var longFee = totalLong * longFillPx * futures.TakerFeeRate;
            state.CycleGridRealized += longRealized;
            state.GridRealizedPnl += longRealized;
            state.CycleFees += longFee;
            state.TotalFees += longFee;

            RecordTrade(strategy, config.Symbol, "Sell", totalLong, longFillPx,
                longClose.OrderId, "HardCloseLong",
                pnlDollar: longRealized - longFee, commission: longFee);
            Log(strategy, "Info",
                $"🚪 LONG closed: {Math.Round(totalLong, 8)} @ {Math.Round(longFillPx, 8)} | " +
                $"realized = ${Math.Round(longRealized, 4)} (gross), fee = ${Math.Round(longFee, 4)}");
        }

        // ───────── 3. Close SHORT side ─────────
        var skimPairedShort = state.SkimCells.Where(c => c.Paired).Sum(c => c.ShortQtyCoins);
        var totalShort = state.QHedgeCoins + skimPairedShort;
        if (totalShort > 0m)
        {
            OrderResultDto shortClose;
            try { shortClose = await futures.CloseHedgeShortAsync(config.Symbol, totalShort); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SmartGridHedge: short close threw for {Symbol}", config.Symbol);
                Log(strategy, "Error",
                    $"Закрытие SHORT исключение: {ex.Message}. Останусь в HardClosing для ретрая.");
                return;
            }
            if (!shortClose.Success)
            {
                Log(strategy, "Error",
                    $"Закрытие SHORT не удалось: {shortClose.ErrorMessage}. Останусь в HardClosing для ретрая.");
                return;
            }

            var shortFillPx = shortClose.FilledPrice ?? closePx;
            // The static hedge piece is booked to HedgeRealizedPnl, not GridRealizedPnl.
            var hedgeRealized = 0m;
            if (state.QHedgeCoins > 0m)
            {
                hedgeRealized = state.QHedgeCoins * (state.HedgeEntryPrice - shortFillPx);
            }
            // Skim-cell paired shorts: realized on the grid side (entry = SellPrice).
            var skimGridRealized = 0m;
            foreach (var cell in state.SkimCells.Where(c => c.Paired))
            {
                skimGridRealized += cell.ShortQtyCoins * (cell.SellPrice - shortFillPx);
            }
            var shortFee = totalShort * shortFillPx * futures.TakerFeeRate;
            state.CycleGridRealized += skimGridRealized;
            state.GridRealizedPnl += skimGridRealized;
            state.CycleHedgeRealized += hedgeRealized;
            state.HedgeRealizedPnl += hedgeRealized;
            state.CycleFees += shortFee;
            state.TotalFees += shortFee;

            RecordTrade(strategy, config.Symbol, "Buy", totalShort, shortFillPx,
                shortClose.OrderId, "HardCloseShort",
                pnlDollar: hedgeRealized + skimGridRealized - shortFee, commission: shortFee);
            Log(strategy, "Info",
                $"🚪 SHORT closed: {Math.Round(totalShort, 8)} @ {Math.Round(shortFillPx, 8)} | " +
                $"hedge realized = ${Math.Round(hedgeRealized, 4)}, " +
                $"skim realized = ${Math.Round(skimGridRealized, 4)}, fee = ${Math.Round(shortFee, 4)}");
        }

        // ───────── 4. Finalize cycle & zero out per-cell state ─────────
        state.CompletedCycles += 1;
        state.DcaCells.Clear();
        state.SkimCells.Clear();
        state.QInitCoins = 0m;
        state.QHedgeCoins = 0m;
        state.HedgeEntryPrice = 0m;
        state.P0 = 0m;
        state.HBreak = 0m;
        state.LBreak = 0m;
        state.PAvgInit = 0m;

        Log(strategy, "Info",
            $"🏁 Cycle #{state.CompletedCycles} closed ({state.LastCycleEndReason}). " +
            $"Cycle grid PnL = ${Math.Round(state.CycleGridRealized, 2)}, " +
            $"cycle hedge PnL = ${Math.Round(state.CycleHedgeRealized, 2)}, " +
            $"cycle fees = ${Math.Round(state.CycleFees, 2)} | " +
            $"Total grid = ${Math.Round(state.GridRealizedPnl, 2)}, " +
            $"total hedge = ${Math.Round(state.HedgeRealizedPnl, 2)}, " +
            $"total fees = ${Math.Round(state.TotalFees, 2)}.");

        // ───────── 5. Auto-restart branch ─────────
        if (!isManual && config.AutoRestart)
        {
            state.Phase = SmartGridHedgePhase.Opening;
            // Tag LastTickAt so the Opening cooldown gate keeps us out for AutoRestartCooldownSeconds.
            // OpenCycleAsync's cooldown check only kicks in when P0 > 0 and a position is still
            // partially open — so we instead just log it. The 5-second worker tick will naturally
            // pace the re-open; a fresh P0 capture happens next tick.
            state.LastTickAt = DateTime.UtcNow.AddSeconds(-OpeningRetryCooldownSeconds + AutoRestartCooldownSeconds);
            Log(strategy, "Info",
                $"♻️ AutoRestart: открываю новый цикл через ~{AutoRestartCooldownSeconds}с на новой цене.");
        }
        else
        {
            state.Phase = SmartGridHedgePhase.Closed;
            Log(strategy, "Info",
                isManual
                    ? "✋ Ручное закрытие: Phase = Closed. Stop → Start чтобы начать новый цикл."
                    : "🛑 AutoRestart = false: Phase = Closed. Stop → Start чтобы начать новый цикл.");
        }
    }

    // ────────────────────────── Helpers ──────────────────────────

    /// <summary>(1 + x)^n via repeated multiplication — exact for our n ≤ 200.</summary>
    private static decimal Pow(decimal baseValue, int exponent)
    {
        if (exponent < 0) throw new ArgumentOutOfRangeException(nameof(exponent));
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= baseValue;
        return result;
    }

    private static bool IsFatal(string? error) =>
        !string.IsNullOrEmpty(error)
        && FatalErrorSubstrings.Any(s => error!.Contains(s, StringComparison.OrdinalIgnoreCase));

    private void RecordTrade(Strategy strategy, string symbol, string side, decimal quantity, decimal price,
        string? orderId, string status, decimal? pnlDollar = null, decimal? commission = null)
    {
        _db.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            AccountId = strategy.AccountId,
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

    private static void SaveState(Strategy strategy, SmartGridHedgeState state)
    {
        strategy.StateJson = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private void Log(Strategy strategy, string level, string message)
    {
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

/// <summary>
/// Result envelope returned by <see cref="SmartGridHedgeHandler.ForceCloseAsync"/>. Carries
/// the aggregate long/short coin counts that were closed so the controller can echo them to
/// the UI/log.
/// </summary>
public record SmartGridHedgeForceCloseResult(
    bool Ok,
    string Message,
    decimal LongCoinsClosed,
    decimal ShortCoinsClosed);
