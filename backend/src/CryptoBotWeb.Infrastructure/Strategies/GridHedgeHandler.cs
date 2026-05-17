using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

/// <summary>
/// Grid + Hedge strategy.
///
/// One Bybit account, two operating modes:
///   - SameTicker: spot grid (long limits) + futures short on the SAME symbol.
///   - CrossTicker: futures grid (long limits) on one symbol + futures short on a different
///                  correlated symbol (e.g. ETH grid hedged by BTC short).
///
/// Lifecycle: NotStarted → HedgeOpening → GridArming → Active → (ExitingUp | ExitingDown) → Done.
///
/// On Start: anchor at current market price, open the hedge short of HedgeNotionalUsdt, then
/// arm the grid:
///   1. Open a level-0 MARKET BUY of BetUsdt at the anchor with its own TP at
///      anchor × (1 + TpStepPercent/100).
///   2. Lay down uniform-step limit buys of BetUsdt at −DcaStep%, −2·DcaStep%, …, −Range% below
///      the anchor. Each limit fill becomes its own batch with its own TP at
///      fill_price × (1 + TpStepPercent/100).
///
/// Ladder-up: when the level-0 batch's TP fills, the WORKING anchor shifts up to the TP fill
/// price, any still-pending limit buys are cancelled and the cycle re-enters GridArming.
/// Filled deep batches keep their own TPs. The START anchor (StartAnchor) stays pinned —
/// exit triggers are evaluated against StartAnchor, NOT the laddering Anchor, so stop-loss
/// and upper take-profit remain at the prices set when the bot was first started.
///
/// Exit triggers (both close the whole bot — evaluated against the START anchor):
///   - price ≥ StartAnchor × (1 + UpperExitPercent/100) → ExitingUp (most grid in profit, hedge in loss)
///   - price ≤ StartAnchor × (1 − RangePercent/100)     → ExitingDown (stop-loss; hedge in profit)
///
/// After Done, Stop → Start begins a fresh cycle at the current market price. Cumulative
/// HedgeRealizedPnl / GridRealizedPnl / CompletedCycles persist across cycles.
/// </summary>
public class GridHedgeHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ~13 req/sec — same throttle GridFloat uses for batched placements.
    private const int InterOrderDelayMs = 75;
    private const int PlacementCooldownMinutes = 5;

    // After this many consecutive ArmGrid ticks without ANY successful placement, the handler
    // assumes the grid leg is permanently refusing (Bybit Spot regulatory restriction, missing
    // scope, invalid symbol, etc.) and rolls back the hedge so the user isn't left with a
    // naked short on the exchange. 3 ticks ≈ 15 minutes given the 5-minute cooldown.
    private const int MaxConsecutiveArmFailures = 3;

    // Substrings in exchange error messages that indicate a permanent refusal — abort
    // immediately on the first occurrence instead of waiting for the failure counter to fill.
    private static readonly string[] FatalErrorSubstrings =
    [
        "regulatory restriction",
        "regulatory restrictions",
        "not available to you",
        "API key permission",
        "invalid api key",
    ];

    public string StrategyType => StrategyTypes.GridHedge;

    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _factory;
    private readonly ILogger<GridHedgeHandler> _logger;

    public GridHedgeHandler(AppDbContext db, IExchangeServiceFactory factory, ILogger<GridHedgeHandler> logger)
    {
        _db = db;
        _factory = factory;
        _logger = logger;
    }

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService futures, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<GridHedgeConfig>(strategy.ConfigJson, JsonOptions);
        if (!ValidateConfig(strategy, config)) return;

        // SameTicker: HedgeSymbol mirrors GridSymbol automatically.
        var hedgeSymbol = (config!.Mode == GridHedgeMode.SameTicker || string.IsNullOrWhiteSpace(config.HedgeSymbol))
            ? config.GridSymbol
            : config.HedgeSymbol;

        var state = JsonSerializer.Deserialize<GridHedgeState>(strategy.StateJson, JsonOptions)
                    ?? new GridHedgeState();

        // Resolve the grid leg through an adapter so the state machine doesn't care whether
        // we're hitting Bybit spot or Bybit futures for the long-grid side.
        ISpotExchangeService? spotForDispose = null;
        IGridLeg gridLeg;
        try
        {
            if (config.Mode == GridHedgeMode.SameTicker)
            {
                spotForDispose = _factory.CreateSpot(strategy.Account);
                gridLeg = new SpotGridLeg(spotForDispose, config.GridSymbol);
            }
            else
            {
                gridLeg = new FuturesGridLeg(futures, config.GridSymbol);
            }
        }
        catch (NotSupportedException ex)
        {
            Log(strategy, "Error",
                $"Биржа не поддерживает spot-режим для SameTicker GridHedge: {ex.Message}. Используйте CrossTicker или Bybit.");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            await _db.SaveChangesAsync(ct);
            return;
        }

        try
        {
            switch (state.Phase)
            {
                case GridHedgePhase.NotStarted:
                case GridHedgePhase.HedgeOpening:
                    await OpenHedgeAsync(strategy, config, state, hedgeSymbol, gridLeg, futures, ct);
                    break;

                case GridHedgePhase.GridArming:
                    await ArmGridAsync(strategy, config, state, gridLeg, futures, ct);
                    break;

                case GridHedgePhase.Active:
                    await PollPendingBuysAsync(strategy, config, state, gridLeg, ct);
                    await PollTpFillsAsync(strategy, config, state, gridLeg, ct);
                    await CheckExitTriggersAsync(strategy, config, state, gridLeg, ct);
                    break;

                case GridHedgePhase.ExitingUp:
                case GridHedgePhase.ExitingDown:
                    await CloseEverythingAsync(strategy, config, state, hedgeSymbol, gridLeg, futures, ct);
                    break;

                case GridHedgePhase.Done:
                    // Cycle complete. User must Stop → Start to begin a fresh one; the
                    // controller's Start branch resets Phase = NotStarted.
                    break;
            }
        }
        finally
        {
            spotForDispose?.Dispose();
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }
    }

    // ────────────────────────── Validation ──────────────────────────

    private bool ValidateConfig(Strategy strategy, GridHedgeConfig? config)
    {
        if (config == null) { Log(strategy, "Error", "ConfigJson не распарсился"); return false; }
        if (string.IsNullOrWhiteSpace(config.GridSymbol))
        { Log(strategy, "Error", "GridSymbol пуст"); return false; }
        if (config.Mode == GridHedgeMode.CrossTicker && string.IsNullOrWhiteSpace(config.HedgeSymbol))
        { Log(strategy, "Error", "CrossTicker mode требует HedgeSymbol"); return false; }
        if (config.RangePercent <= 0)
        { Log(strategy, "Error", $"RangePercent должен быть > 0, текущее: {config.RangePercent}"); return false; }
        if (config.UpperExitPercent <= 0)
        { Log(strategy, "Error", $"UpperExitPercent должен быть > 0, текущее: {config.UpperExitPercent}"); return false; }
        if (config.DcaStepPercent <= 0 || config.TpStepPercent <= 0)
        { Log(strategy, "Error", "DcaStepPercent / TpStepPercent должны быть > 0"); return false; }
        if (config.BetUsdt <= 0)
        { Log(strategy, "Error", $"BetUsdt должен быть > 0, текущее: {config.BetUsdt}"); return false; }
        if (config.HedgeNotionalUsdt < 0)
        { Log(strategy, "Error", $"HedgeNotionalUsdt не может быть отрицательным: {config.HedgeNotionalUsdt}"); return false; }
        if (config.HedgeLeverage < 1 || config.GridLeverage < 1)
        { Log(strategy, "Error", "Leverage должен быть ≥ 1"); return false; }
        return true;
    }

    // ────────────────────────── Phase: HedgeOpening ──────────────────────────

    private async Task OpenHedgeAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state,
        string hedgeSymbol, IGridLeg gridLeg, IFuturesExchangeService futures, CancellationToken ct)
    {
        // Anchor + hedge-anchor capture (idempotent — only first time through).
        if (state.Anchor <= 0)
        {
            decimal? gridPrice;
            try { gridPrice = await gridLeg.GetTickerPriceAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridHedge: GetTickerPriceAsync(grid) failed for {Symbol}", config.GridSymbol);
                return;
            }
            if (gridPrice is null or <= 0)
            { Log(strategy, "Warning", $"Не удалось получить цену {config.GridSymbol} — пропуск тика"); return; }
            state.Anchor = gridPrice.Value;
            state.StartAnchor = gridPrice.Value;
            Log(strategy, "Info",
                $"📌 ANCHOR {config.GridSymbol} = {state.Anchor} " +
                $"(triggers: ↑{Math.Round(state.StartAnchor * (1 + config.UpperExitPercent / 100m), 6)} / " +
                $"↓{Math.Round(state.StartAnchor * (1 - config.RangePercent / 100m), 6)})");
        }

        if (state.HedgeAnchor <= 0)
        {
            decimal? hedgePrice;
            try { hedgePrice = await futures.GetTickerPriceAsync(hedgeSymbol); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridHedge: GetTickerPriceAsync(hedge) failed for {Symbol}", hedgeSymbol);
                return;
            }
            if (hedgePrice is null or <= 0)
            { Log(strategy, "Warning", $"Не удалось получить цену {hedgeSymbol} — пропуск тика"); return; }
            state.HedgeAnchor = hedgePrice.Value;
        }

        if (state.HedgeQty > 0)
        {
            // Hedge already opened on a previous tick — advance.
            state.Phase = GridHedgePhase.GridArming;
            return;
        }

        // HedgeNotionalUsdt comes from the form's recommendation calculation (or user override).
        // 0 = no hedge → skip opening, go straight to GridArming.
        if (config.HedgeNotionalUsdt <= 0)
        {
            Log(strategy, "Info", "HedgeNotionalUsdt = 0 — пропускаю открытие хеджа, иду к расстановке сетки.");
            state.Phase = GridHedgePhase.GridArming;
            return;
        }

        // Best-effort leverage.
        try { await futures.SetLeverageAsync(hedgeSymbol, config.HedgeLeverage); }
        catch (NotSupportedException) { }
        catch (Exception ex)
        { _logger.LogWarning(ex, "GridHedge: SetLeverageAsync(hedge) failed for {Symbol}", hedgeSymbol); }

        state.Phase = GridHedgePhase.HedgeOpening;
        Log(strategy, "Info",
            $"🛡️ Открываю хедж SHORT {hedgeSymbol}: notional={config.HedgeNotionalUsdt} USDT, lev={config.HedgeLeverage}");

        var result = await futures.OpenShortAsync(hedgeSymbol, config.HedgeNotionalUsdt);
        if (!result.Success || result.FilledQuantity is not > 0)
        {
            Log(strategy, "Error",
                $"❌ Не удалось открыть хедж SHORT {hedgeSymbol}: {result.ErrorMessage}. Останусь в HedgeOpening, перепроверю на след. тике.");
            state.PlacementCooldownUntil = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        state.HedgeQty = result.FilledQuantity.Value;
        state.HedgeAvgEntry = result.FilledPrice ?? state.HedgeAnchor;
        state.HedgeOpenOrderId = result.OrderId;
        state.Phase = GridHedgePhase.GridArming;

        RecordTrade(strategy, hedgeSymbol, "Sell", state.HedgeQty, state.HedgeAvgEntry,
            result.OrderId, "HedgeOpen");

        Log(strategy, "Info",
            $"✅ Хедж открыт: qty={state.HedgeQty} @ {state.HedgeAvgEntry} (id={result.OrderId}). Перехожу к расстановке сетки.");
    }

    // ────────────────────────── Phase: GridArming ──────────────────────────

    private async Task ArmGridAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state,
        IGridLeg gridLeg, IFuturesExchangeService futures, CancellationToken ct)
    {
        if (state.PlacementCooldownUntil.HasValue && state.PlacementCooldownUntil.Value > DateTime.UtcNow)
            return;

        // Set leverage on the grid leg too — only meaningful in CrossTicker (futures grid).
        if (config.Mode == GridHedgeMode.CrossTicker)
        {
            try { await futures.SetLeverageAsync(config.GridSymbol, config.GridLeverage); }
            catch (NotSupportedException) { }
            catch (Exception ex)
            { _logger.LogWarning(ex, "GridHedge: SetLeverageAsync(grid) failed for {Symbol}", config.GridSymbol); }
        }

        var levels = ComputeGridLevels(config, state.Anchor);
        // Note: an empty `levels` list is only fatal if MarketEntryOpened is also already true.
        // Otherwise we still want to open the level-0 market batch — the limit grid being empty
        // is a config issue (DcaStep > Range) that the user can fix without losing the entry.
        var madeProgressThisTick = false;
        string? lastErrorMessage = null;

        // ───────── Level-0 MARKET buy ─────────
        // First step of the strategy is a market buy at the anchor. It owns its own TP at
        // anchor × (1 + TpStep%). When that TP fills, PollTpFillsAsync triggers LadderUpAsync
        // which shifts the anchor up and re-enters this method to re-arm a fresh generation.
        // MarketEntryOpened guards against duplicate opens on retry-from-cooldown.
        if (!state.MarketEntryOpened && config.BetUsdt > 0)
        {
            OrderResultDto? marketResult = null;
            try { marketResult = await gridLeg.PlaceMarketBuyAsync(config.BetUsdt); }
            catch (Exception ex)
            {
                state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
                lastErrorMessage = ex.Message;
                _logger.LogError(ex, "GridHedge: market entry threw for {Symbol}", config.GridSymbol);
                Log(strategy, "Error", $"MARKET ENTRY (level 0%) исключение (cooldown {PlacementCooldownMinutes}мин): {ex.Message}");
            }

            if (marketResult != null)
            {
                if (marketResult.Success && marketResult.FilledQuantity is > 0)
                {
                    var entryPrice = marketResult.FilledPrice is > 0 ? marketResult.FilledPrice!.Value : state.Anchor;
                    var tpPrice = entryPrice * (1m + config.TpStepPercent / 100m);
                    var batch = new GridHedgeBatch
                    {
                        BuyOrderId = marketResult.OrderId ?? string.Empty,
                        LevelPercent = 0m,
                        FilledPrice = entryPrice,
                        FilledQty = marketResult.FilledQuantity.Value,
                        TpPrice = tpPrice,
                        FilledAt = DateTime.UtcNow
                    };
                    state.Batches.Add(batch);
                    state.MarketEntryOpened = true;
                    madeProgressThisTick = true;

                    RecordTrade(strategy, gridLeg.Symbol, "Buy", batch.FilledQty, entryPrice,
                        marketResult.OrderId, "MarketEntry@0%");

                    Log(strategy, "Info",
                        $"🚀 MARKET ENTRY (level 0%): BUY {Math.Round(batch.FilledQty, 6)} @ {Math.Round(entryPrice, 6)} " +
                        $"→ TP={Math.Round(tpPrice, 6)} (+{config.TpStepPercent}%)");

                    await PlaceBatchTpAsync(strategy, state, gridLeg, batch);
                }
                else
                {
                    state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
                    lastErrorMessage = marketResult.ErrorMessage;
                    Log(strategy, "Error",
                        $"❌ MARKET ENTRY (level 0%) не открыт (cooldown {PlacementCooldownMinutes}мин): {marketResult.ErrorMessage}");
                    _logger.LogWarning("Strategy {Id}: GridHedge market entry failed: {Error}",
                        strategy.Id, marketResult.ErrorMessage);
                }
            }
        }

        // ───────── Limit grid below anchor ─────────
        // Dedup by absolute price (not by LevelPercent) so a partial retry after cooldown
        // skips already-placed limits, while a re-entry after ladder-shift treats the new
        // anchor's levels as fresh (their absolute prices differ from any old batch's).
        // Only PendingBuys are considered — Batches at LevelPercent != 0 hold historical
        // absolute prices that must not block new placements at different prices.
        if (!state.PlacementCooldownUntil.HasValue || state.PlacementCooldownUntil.Value <= DateTime.UtcNow)
        {
            var placedPrices = new HashSet<string>(
                state.PendingBuys.Select(p => FormatPriceKey(p.Price)),
                StringComparer.OrdinalIgnoreCase);

            var toPlace = levels.Where(l => !placedPrices.Contains(FormatPriceKey(l.price))).ToList();
            if (levels.Count > 0)
            {
                Log(strategy, "Info",
                    $"🪜 Расставляю сетку: всего уровней={levels.Count}, осталось={toPlace.Count} " +
                    $"(якорь={state.Anchor}, Range={config.RangePercent}%, step={config.DcaStepPercent}%, bet=${config.BetUsdt})");
            }

            var placedLimitsThisTick = 0;
            foreach (var (offsetPct, price) in toPlace)
            {
                if (placedLimitsThisTick > 0 || madeProgressThisTick) await Task.Delay(InterOrderDelayMs, ct);
                if (state.PlacementCooldownUntil.HasValue && state.PlacementCooldownUntil.Value > DateTime.UtcNow)
                    break;

                var qty = config.BetUsdt / price;
                if (qty <= 0)
                {
                    Log(strategy, "Warning", $"Уровень -{offsetPct}%: qty=0 (bet={config.BetUsdt}, price={price}) — пропуск");
                    continue;
                }

                OrderResultDto result;
                try { result = await gridLeg.PlaceLimitBuyAsync(price, qty); }
                catch (Exception ex)
                {
                    state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
                    lastErrorMessage = ex.Message;
                    _logger.LogError(ex, "GridHedge: PlaceLimitBuy threw for {Symbol} @-{Pct}%", config.GridSymbol, offsetPct);
                    Log(strategy, "Error", $"Уровень -{offsetPct}% исключение (cooldown {PlacementCooldownMinutes}мин): {ex.Message}");
                    break;
                }

                if (result.Success && !string.IsNullOrEmpty(result.OrderId))
                {
                    state.PendingBuys.Add(new GridHedgePendingBuy
                    {
                        OrderId = result.OrderId,
                        Price = result.FilledPrice ?? price,
                        Qty = result.FilledQuantity ?? qty,
                        LevelPercent = offsetPct
                    });
                    placedLimitsThisTick++;
                    madeProgressThisTick = true;
                    Log(strategy, "Info",
                        $"🎯 -{offsetPct}%: BUY {Math.Round(qty, 6)} @ {Math.Round(price, 6)} (id={result.OrderId})");
                }
                else
                {
                    state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
                    lastErrorMessage = result.ErrorMessage;
                    Log(strategy, "Warning",
                        $"Уровень -{offsetPct}% не выставлен (cooldown {PlacementCooldownMinutes}мин): {result.ErrorMessage}");
                    _logger.LogWarning("Strategy {Id}: GridHedge level -{Pct}% placement failed: {Error}",
                        strategy.Id, offsetPct, result.ErrorMessage);
                    break;
                }
            }
        }

        // ───────── Auto-rollback safety ─────────
        // No progress this tick (no market entry, no limit placed) AND there was work to do
        // → increment failure counter. Fatal exchange errors short-circuit to immediate
        // rollback so the hedge isn't left naked.
        var hadWorkThisTick = (!state.MarketEntryOpened && config.BetUsdt > 0)
                              || state.PendingBuys.Count + (state.MarketEntryOpened ? 1 : 0) < levels.Count + 1;
        if (!madeProgressThisTick && hadWorkThisTick)
        {
            state.GridArmingFailureCount++;
            var isFatalError = !string.IsNullOrEmpty(lastErrorMessage)
                && FatalErrorSubstrings.Any(s => lastErrorMessage!.Contains(s, StringComparison.OrdinalIgnoreCase));

            if (isFatalError || state.GridArmingFailureCount >= MaxConsecutiveArmFailures)
            {
                Log(strategy, "Error",
                    isFatalError
                        ? $"🚨 Фатальная ошибка биржи при расстановке сетки: \"{lastErrorMessage}\". " +
                          "Откатываю хедж и останавливаю бота."
                        : $"🚨 Сетка не выставляется {state.GridArmingFailureCount} тика подряд. " +
                          $"Откатываю хедж и останавливаю бота. Последняя ошибка: \"{lastErrorMessage}\".");
                state.Phase = GridHedgePhase.ExitingDown;
                state.PlacementCooldownUntil = null;
                return;
            }
        }
        else if (madeProgressThisTick)
        {
            state.GridArmingFailureCount = 0;
        }

        // Advance to Active when market entry is in place AND every limit level is covered.
        // Empty `levels` is fine — that just means the grid has only the market entry slot.
        var allLimitsPlaced = state.PendingBuys.Count >= levels.Count;
        if (state.MarketEntryOpened && allLimitsPlaced)
        {
            state.Phase = GridHedgePhase.Active;
            state.GridArmingFailureCount = 0;
            Log(strategy, "Info",
                $"🟢 Сетка готова: market entry + {state.PendingBuys.Count}/{levels.Count} лимитных уровней. Phase=Active.");
        }
    }

    // ────────────────────────── Phase: Active — poll pending buys ──────────────────────────

    private async Task PollPendingBuysAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state,
        IGridLeg gridLeg, CancellationToken ct)
    {
        if (state.PendingBuys.Count == 0) return;
        var snapshot = state.PendingBuys.ToList();

        foreach (var pending in snapshot)
        {
            OrderStatusDto? status;
            try { status = await gridLeg.GetOrderAsync(pending.OrderId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridHedge: poll-pending GetOrder failed for {OrderId}", pending.OrderId);
                continue;
            }
            if (status == null) continue;

            switch (status.Status)
            {
                case OrderLifecycleStatus.Filled when status.FilledQuantity > 0:
                {
                    var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : pending.Price;
                    var fillQty = status.FilledQuantity;
                    await AdoptBuyFillAsync(strategy, config, state, gridLeg, pending, fillQty, fillPrice);
                    state.PendingBuys.Remove(pending);
                    break;
                }

                case OrderLifecycleStatus.Cancelled:
                case OrderLifecycleStatus.Rejected:
                    if (status.FilledQuantity > 0)
                    {
                        var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : pending.Price;
                        await AdoptBuyFillAsync(strategy, config, state, gridLeg, pending, status.FilledQuantity, fillPrice);
                    }
                    else
                    {
                        Log(strategy, "Warning",
                            $"Лимит -{pending.LevelPercent}% отменён/отклонён без филла — слот пропущен в этом цикле");
                    }
                    state.PendingBuys.Remove(pending);
                    break;

                case OrderLifecycleStatus.Filled:
                    // Filled with FilledQuantity == 0 — Bybit V5 history glitch (same as GridFloat).
                    Log(strategy, "Warning",
                        $"Лимит -{pending.LevelPercent}%: Filled но qty=0 — игнорирую (Bybit history glitch)");
                    state.PendingBuys.Remove(pending);
                    break;

                default:
                    break; // Open / PartiallyFilled / Unknown → wait
            }
        }
    }

    private async Task AdoptBuyFillAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state, IGridLeg gridLeg,
        GridHedgePendingBuy pending, decimal fillQty, decimal fillPrice)
    {
        var tpPrice = fillPrice * (1m + config.TpStepPercent / 100m);

        var batch = new GridHedgeBatch
        {
            BuyOrderId = pending.OrderId,
            LevelPercent = pending.LevelPercent,
            FilledPrice = fillPrice,
            FilledQty = fillQty,
            TpPrice = tpPrice,
            FilledAt = DateTime.UtcNow
        };
        state.Batches.Add(batch);

        RecordTrade(strategy, gridLeg.Symbol, "Buy", fillQty, fillPrice, pending.OrderId,
            $"GridFill@-{pending.LevelPercent}%");

        Log(strategy, "Info",
            $"✅ Фил -{pending.LevelPercent}%: qty={Math.Round(fillQty, 6)} @ {Math.Round(fillPrice, 6)} → TP={Math.Round(tpPrice, 6)} (+{config.TpStepPercent}%)");

        await PlaceBatchTpAsync(strategy, state, gridLeg, batch);
    }

    private async Task PlaceBatchTpAsync(Strategy strategy, GridHedgeState state, IGridLeg gridLeg, GridHedgeBatch batch)
    {
        if (batch.FilledQty <= 0 || batch.TpPrice <= 0) return;

        OrderResultDto result;
        try { result = await gridLeg.PlaceLimitSellAsync(batch.TpPrice, batch.FilledQty); }
        catch (Exception ex)
        {
            batch.TpOrderId = null;
            _logger.LogWarning(ex, "GridHedge: TP placement threw for batch @-{Pct}%", batch.LevelPercent);
            Log(strategy, "Error", $"TP батча -{batch.LevelPercent}% исключение: {ex.Message}");
            return;
        }

        if (result.Success && !string.IsNullOrEmpty(result.OrderId))
        {
            batch.TpOrderId = result.OrderId;
            Log(strategy, "Info",
                $"🎯 TP -{batch.LevelPercent}%: SELL {Math.Round(batch.FilledQty, 6)} @ {Math.Round(batch.TpPrice, 6)} (id={result.OrderId})");
        }
        else
        {
            batch.TpOrderId = null;
            Log(strategy, "Error",
                $"TP батча -{batch.LevelPercent}% не выставлен: {result.ErrorMessage}");
        }
    }

    // ────────────────────────── Phase: Active — poll TP fills ──────────────────────────

    private async Task PollTpFillsAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state,
        IGridLeg gridLeg, CancellationToken ct)
    {
        if (state.Batches.Count == 0) return;
        var snapshot = state.Batches.ToList();

        foreach (var batch in snapshot)
        {
            if (batch.Closed) continue;
            if (string.IsNullOrEmpty(batch.TpOrderId))
            {
                // TP missing — heal it on the next placement opportunity.
                await PlaceBatchTpAsync(strategy, state, gridLeg, batch);
                continue;
            }

            OrderStatusDto? status;
            try { status = await gridLeg.GetOrderAsync(batch.TpOrderId!); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridHedge: poll-TP GetOrder failed for {OrderId}", batch.TpOrderId);
                continue;
            }
            if (status == null) continue;

            switch (status.Status)
            {
                case OrderLifecycleStatus.Filled when status.FilledQuantity > 0:
                {
                    var closePrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : batch.TpPrice;
                    var isLevelZero = batch.LevelPercent == 0m;
                    RecordBatchClosure(strategy, state, gridLeg, batch, closePrice, status.FilledQuantity, "TpFill");
                    state.Batches.Remove(batch);

                    if (isLevelZero)
                    {
                        await LadderUpAsync(strategy, state, gridLeg, closePrice, ct);
                        return; // Stop polling — Phase is now GridArming, next tick re-arms.
                    }
                    break;
                }

                case OrderLifecycleStatus.Cancelled:
                case OrderLifecycleStatus.Rejected:
                    if (status.FilledQuantity > 0)
                    {
                        var closePrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : batch.TpPrice;
                        var isLevelZero = batch.LevelPercent == 0m;
                        RecordBatchClosure(strategy, state, gridLeg, batch, closePrice, status.FilledQuantity, "TpCancelledPartial");
                        state.Batches.Remove(batch);

                        if (isLevelZero)
                        {
                            await LadderUpAsync(strategy, state, gridLeg, closePrice, ct);
                            return;
                        }
                    }
                    else
                    {
                        Log(strategy, "Warning",
                            $"TP батча -{batch.LevelPercent}% отменён без филла — переставлю на следующем тике");
                        batch.TpOrderId = null;
                    }
                    break;

                case OrderLifecycleStatus.Filled:
                    Log(strategy, "Warning",
                        $"TP батча -{batch.LevelPercent}%: Filled но qty=0 — игнорирую, переставлю");
                    batch.TpOrderId = null;
                    break;

                default:
                    break; // still resting / partial
            }
        }
    }

    // ────────────────────────── Ladder-up after level-0 TP fill ──────────────────────────

    /// <summary>
    /// Triggered when the level-0 (market entry) batch's TP fills. Shifts the anchor up to
    /// the TP fill price, cancels every still-pending limit buy, and flips Phase back to
    /// GridArming so the next tick opens a fresh market entry at the new anchor and re-places
    /// the limit grid relative to it. Filled deep-level batches stay untouched — they keep
    /// their own TPs and remain in state.Batches with their original LevelPercent (which now
    /// references the OLD anchor; this is why the limit-grid dedup keys by absolute price).
    /// </summary>
    private async Task LadderUpAsync(
        Strategy strategy, GridHedgeState state, IGridLeg gridLeg, decimal newAnchor, CancellationToken ct)
    {
        var oldAnchor = state.Anchor;
        state.Anchor = newAnchor;

        Log(strategy, "Info",
            $"⬆️ LADDER UP: TP level-0 закрылся @ {Math.Round(newAnchor, 6)}. " +
            $"Якорь {Math.Round(oldAnchor, 6)} → {Math.Round(newAnchor, 6)}. " +
            $"Отменяю pending лимитки ({state.PendingBuys.Count}) и перевыставлю сетку на след. тике.");

        foreach (var pending in state.PendingBuys.ToList())
        {
            try { await gridLeg.CancelOrderAsync(pending.OrderId); }
            catch (Exception ex)
            { _logger.LogWarning(ex, "GridHedge ladder-up: cancel pending failed for {OrderId}", pending.OrderId); }
        }
        state.PendingBuys.Clear();
        state.MarketEntryOpened = false;
        state.GridArmingFailureCount = 0;
        state.PlacementCooldownUntil = null;
        state.Phase = GridHedgePhase.GridArming;
    }

    private void RecordBatchClosure(Strategy strategy, GridHedgeState state, IGridLeg gridLeg,
        GridHedgeBatch batch, decimal closePrice, decimal closeQty, string reason)
    {
        var notional = batch.FilledPrice * closeQty;
        var pnlPct = (closePrice - batch.FilledPrice) / batch.FilledPrice * 100m;
        var grossPnl = notional * pnlPct / 100m;
        // Buy was a maker limit, TP exit is a maker limit on TP fills / taker on forced market
        // close. Approximate uniformly with maker+maker for TP fills, maker+taker for forced
        // market closures.
        var feeRate = reason == "ForceMarketClose"
            ? gridLeg.MakerFeeRate + gridLeg.TakerFeeRate
            : gridLeg.MakerFeeRate * 2m;
        var commission = notional * feeRate;
        var netPnl = grossPnl - commission;

        batch.Closed = true;
        batch.RealizedPnl = netPnl;
        state.GridRealizedPnl += netPnl;

        RecordTrade(strategy, gridLeg.Symbol, "Sell", closeQty, closePrice, batch.TpOrderId,
            $"{reason}@-{batch.LevelPercent}%", pnlDollar: netPnl, commission: commission);

        Log(strategy, "Info",
            $"💰 Закрыт батч -{batch.LevelPercent}% ({reason}): {Math.Round(closeQty, 6)} @ {Math.Round(closePrice, 6)}, " +
            $"вход={Math.Round(batch.FilledPrice, 6)}, PnL=${Math.Round(netPnl, 2)} ({Math.Round(pnlPct, 3)}%)");
    }

    // ────────────────────────── Phase: Active — exit triggers ──────────────────────────

    private async Task CheckExitTriggersAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state,
        IGridLeg gridLeg, CancellationToken ct)
    {
        decimal? price;
        try { price = await gridLeg.GetTickerPriceAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridHedge: exit-trigger price probe failed for {Symbol}", config.GridSymbol);
            return;
        }
        if (price is null or <= 0) return;

        state.LastPrice = price.Value;
        // Triggers are pinned to the START anchor, not the (laddering) working anchor — so the
        // stop-loss and upper take-profit stay at the prices set when the bot was started.
        // Fall back to Anchor if StartAnchor is missing (state from before this field existed).
        var triggerAnchor = state.StartAnchor > 0 ? state.StartAnchor : state.Anchor;
        var upTrigger = triggerAnchor * (1m + config.UpperExitPercent / 100m);
        var downTrigger = triggerAnchor * (1m - config.RangePercent / 100m);

        if (price.Value >= upTrigger)
        {
            state.Phase = GridHedgePhase.ExitingUp;
            Log(strategy, "Info",
                $"⬆️ ВЕРХНИЙ ТРИГГЕР: цена {Math.Round(price.Value, 6)} ≥ {Math.Round(upTrigger, 6)} " +
                $"(start anchor + {config.UpperExitPercent}%). Закрываю grid + hedge.");
        }
        else if (price.Value <= downTrigger)
        {
            state.Phase = GridHedgePhase.ExitingDown;
            Log(strategy, "Warning",
                $"⬇️ STOP-LOSS: цена {Math.Round(price.Value, 6)} ≤ {Math.Round(downTrigger, 6)} " +
                $"(start anchor − {config.RangePercent}%). Аварийное закрытие grid + hedge.");
        }
    }

    // ────────────────────────── Phase: Exiting* ──────────────────────────

    private async Task CloseEverythingAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state,
        string hedgeSymbol, IGridLeg gridLeg, IFuturesExchangeService futures, CancellationToken ct)
    {
        Log(strategy, "Info",
            $"🚪 Закрытие: Phase={state.Phase}, открытых батчей={state.Batches.Count(b => !b.Closed)}, " +
            $"pendingBuys={state.PendingBuys.Count}, hedgeQty={state.HedgeQty}");

        // 1. Cancel every pending limit buy.
        foreach (var pending in state.PendingBuys.ToList())
        {
            try { await gridLeg.CancelOrderAsync(pending.OrderId); }
            catch (Exception ex)
            { _logger.LogWarning(ex, "GridHedge: cancel pending failed for {OrderId}", pending.OrderId); }
        }
        state.PendingBuys.Clear();

        // 2. Cancel every batch's TP, then market-sell the batch's qty on the grid leg.
        foreach (var batch in state.Batches.ToList())
        {
            if (batch.Closed) { state.Batches.Remove(batch); continue; }

            if (!string.IsNullOrEmpty(batch.TpOrderId))
            {
                try { await gridLeg.CancelOrderAsync(batch.TpOrderId!); }
                catch (Exception ex)
                { _logger.LogWarning(ex, "GridHedge: cancel TP failed for {OrderId}", batch.TpOrderId); }
                batch.TpOrderId = null;
            }

            OrderResultDto sell;
            try { sell = await gridLeg.PlaceMarketSellAsync(batch.FilledQty); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GridHedge: force-close market sell threw for batch @-{Pct}%", batch.LevelPercent);
                Log(strategy, "Error", $"Принудительное закрытие батча -{batch.LevelPercent}% исключение: {ex.Message}");
                continue;
            }

            if (!sell.Success)
            {
                Log(strategy, "Error", $"Принудительное закрытие батча -{batch.LevelPercent}% не удалось: {sell.ErrorMessage}");
                continue;
            }

            var closePrice = sell.FilledPrice ?? state.LastPrice ?? batch.FilledPrice;
            RecordBatchClosure(strategy, state, gridLeg, batch, closePrice, batch.FilledQty, "ForceMarketClose");
            state.Batches.Remove(batch);
        }

        // 3. Close the hedge short — buy back HedgeQty.
        if (state.HedgeQty > 0)
        {
            OrderResultDto hedgeClose;
            try { hedgeClose = await futures.CloseShortAsync(hedgeSymbol, state.HedgeQty); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GridHedge: hedge close threw for {Symbol}", hedgeSymbol);
                Log(strategy, "Error", $"Закрытие хеджа {hedgeSymbol} исключение: {ex.Message}. Останусь в Phase={state.Phase} для ретрая.");
                return;
            }

            if (!hedgeClose.Success)
            {
                Log(strategy, "Error",
                    $"Закрытие хеджа {hedgeSymbol} не удалось: {hedgeClose.ErrorMessage}. Останусь в Phase={state.Phase} для ретрая.");
                return;
            }

            decimal? closePxNullable;
            try { closePxNullable = await futures.GetTickerPriceAsync(hedgeSymbol); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridHedge: hedge close-price probe failed for {Symbol}", hedgeSymbol);
                closePxNullable = state.HedgeAvgEntry;
            }
            var closePx = closePxNullable ?? state.HedgeAvgEntry;
            var hedgeNotional = state.HedgeAvgEntry * state.HedgeQty;
            var hedgeGross = (state.HedgeAvgEntry - closePx) * state.HedgeQty;
            var hedgeFees = hedgeNotional * (futures.TakerFeeRate * 2m);
            var hedgeNet = hedgeGross - hedgeFees;

            state.HedgeRealizedPnl += hedgeNet;

            RecordTrade(strategy, hedgeSymbol, "Buy", state.HedgeQty, closePx,
                hedgeClose.OrderId, "HedgeClose", pnlDollar: hedgeNet, commission: hedgeFees);

            Log(strategy, "Info",
                $"🛡️ Хедж закрыт: BUY {state.HedgeQty} @ {Math.Round(closePx, 6)} (вход={state.HedgeAvgEntry}), " +
                $"PnL=${Math.Round(hedgeNet, 2)}");

            state.HedgeQty = 0;
            state.HedgeAvgEntry = 0;
            state.HedgeOpenOrderId = null;
        }

        // 4. Finalize cycle.
        var wasRollback = state.GridArmingFailureCount > 0;
        state.CompletedCycles += 1;
        state.Phase = GridHedgePhase.Done;
        state.Anchor = 0;
        state.StartAnchor = 0;
        state.HedgeAnchor = 0;
        state.GridArmingFailureCount = 0;

        Log(strategy, "Info",
            $"🏁 Цикл #{state.CompletedCycles} завершён. " +
            $"Grid PnL за цикл (накопленный): ${Math.Round(state.GridRealizedPnl, 2)}, " +
            $"Hedge PnL (накопленный): ${Math.Round(state.HedgeRealizedPnl, 2)}. " +
            (wasRollback ? "Бот остановлен из-за невозможности выставить сетку." : "Чтобы запустить новый цикл — Stop → Start."));

        if (wasRollback)
        {
            // Rollback path — don't leave the bot in Running state spinning on Done; the user
            // needs to fix whatever blocked the grid (API scope, region, etc.) before retrying.
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
        }
    }

    // ────────────────────────── Helpers: grid math ──────────────────────────

    /// <summary>
    /// Walks the lower range with uniform DcaStepPercent stride. Returns (offsetPctFromAnchor,
    /// price) tuples for every grid-buy level. Long-only.
    /// </summary>
    private static List<(decimal offsetPct, decimal price)> ComputeGridLevels(
        GridHedgeConfig config, decimal anchor)
    {
        var list = new List<(decimal, decimal)>();
        if (anchor <= 0 || config.DcaStepPercent <= 0 || config.RangePercent <= 0) return list;

        const int safetyCeiling = 500;
        const decimal eps = 1e-9m;

        var offsetPct = config.DcaStepPercent;
        var k = 0;
        while (offsetPct <= config.RangePercent + eps && k < safetyCeiling)
        {
            var price = anchor * (1m - offsetPct / 100m);
            if (price <= 0) break;
            list.Add((offsetPct, price));
            offsetPct += config.DcaStepPercent;
            k++;
        }
        return list;
    }

    // Dedup key for grid limit placements. Uses absolute price (not LevelPercent offset) so
    // post-ladder-shift placements at the new anchor don't collide with historical batches
    // that were filled relative to the old anchor.
    private static string FormatPriceKey(decimal price) =>
        Math.Round(price, 8).ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    // ────────────────────────── Helpers: persistence ──────────────────────────

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

    private static void SaveState(Strategy strategy, GridHedgeState state)
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

    // ────────────────────────── Grid-leg adapter ──────────────────────────

    private interface IGridLeg
    {
        string Symbol { get; }
        decimal MakerFeeRate { get; }
        decimal TakerFeeRate { get; }
        Task<decimal?> GetTickerPriceAsync();
        Task<OrderResultDto> PlaceLimitBuyAsync(decimal price, decimal qty);
        Task<OrderResultDto> PlaceLimitSellAsync(decimal price, decimal qty);
        Task<OrderResultDto> PlaceMarketBuyAsync(decimal notionalUsdt);
        Task<OrderResultDto> PlaceMarketSellAsync(decimal qty);
        Task<bool> CancelOrderAsync(string orderId);
        Task<OrderStatusDto?> GetOrderAsync(string orderId);
    }

    private sealed class SpotGridLeg : IGridLeg
    {
        private readonly ISpotExchangeService _spot;
        public string Symbol { get; }
        public decimal MakerFeeRate => _spot.MakerFeeRate;
        public decimal TakerFeeRate => _spot.TakerFeeRate;
        public SpotGridLeg(ISpotExchangeService spot, string symbol) { _spot = spot; Symbol = symbol; }
        public Task<decimal?> GetTickerPriceAsync() => _spot.GetTickerPriceAsync(Symbol);
        public Task<OrderResultDto> PlaceLimitBuyAsync(decimal price, decimal qty)
            => _spot.PlaceLimitBuyAsync(Symbol, price, qty);
        public Task<OrderResultDto> PlaceLimitSellAsync(decimal price, decimal qty)
            => _spot.PlaceLimitSellAsync(Symbol, price, qty);
        public Task<OrderResultDto> PlaceMarketBuyAsync(decimal notionalUsdt)
            => _spot.PlaceMarketBuyAsync(Symbol, notionalUsdt);
        public Task<OrderResultDto> PlaceMarketSellAsync(decimal qty)
            => _spot.PlaceMarketSellAsync(Symbol, qty);
        public Task<bool> CancelOrderAsync(string orderId) => _spot.CancelOrderAsync(Symbol, orderId);
        public Task<OrderStatusDto?> GetOrderAsync(string orderId) => _spot.GetOrderAsync(Symbol, orderId);
    }

    private sealed class FuturesGridLeg : IGridLeg
    {
        private readonly IFuturesExchangeService _futures;
        public string Symbol { get; }
        public decimal MakerFeeRate => _futures.MakerFeeRate;
        public decimal TakerFeeRate => _futures.TakerFeeRate;
        public FuturesGridLeg(IFuturesExchangeService futures, string symbol) { _futures = futures; Symbol = symbol; }
        public Task<decimal?> GetTickerPriceAsync() => _futures.GetTickerPriceAsync(Symbol);
        public Task<OrderResultDto> PlaceLimitBuyAsync(decimal price, decimal qty)
            => _futures.PlaceLimitOrderAsync(Symbol, "Buy", price, qty, reduceOnly: false);
        public Task<OrderResultDto> PlaceLimitSellAsync(decimal price, decimal qty)
            => _futures.PlaceLimitOrderAsync(Symbol, "Sell", price, qty, reduceOnly: true);
        public Task<OrderResultDto> PlaceMarketBuyAsync(decimal notionalUsdt)
            => _futures.OpenLongAsync(Symbol, notionalUsdt);
        public Task<OrderResultDto> PlaceMarketSellAsync(decimal qty) => _futures.CloseLongAsync(Symbol, qty);
        public Task<bool> CancelOrderAsync(string orderId) => _futures.CancelOrderAsync(Symbol, orderId);
        public Task<OrderStatusDto?> GetOrderAsync(string orderId) => _futures.GetOrderAsync(Symbol, orderId);
    }
}
