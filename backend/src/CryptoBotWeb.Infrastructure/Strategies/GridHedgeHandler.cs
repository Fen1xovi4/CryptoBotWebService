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
/// Lifecycle is a one-shot state machine:
///   NotStarted → HedgeOpening → GridArming → Active → (ExitingUp | ExitingDown) → Done
///
/// On Start: anchor at current market price, open the hedge short for the full planned grid
/// notional (× HedgeRatio × Beta), lay down limit buys across the entire range below the
/// anchor. Each grid fill becomes a batch with its own reduce-only take-profit limit.
///
/// Exit triggers (both close the whole bot — grid + hedge):
///   - price ≥ Anchor × (1 + UpperExitPercent/100) → ExitingUp (most grid in profit, hedge in loss)
///   - price ≤ Anchor × (1 − RangePercent/100)     → ExitingDown (stop-loss; hedge in profit)
///
/// After Done, the user must Stop → Start the bot to begin a fresh cycle. Cumulative
/// HedgeRealizedPnl / GridRealizedPnl / CompletedCycles persist across cycles.
///
/// V1 does NOT implement live re-anchoring, position-based reconcile, or per-tier auto-bump
/// (those battle-tested patterns from GridFloat can land in V1.1 if needed).
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
        config!.Tiers = config.Tiers
            .Where(t => t.UpToPercent > 0 && t.SizeUsdt > 0)
            .OrderBy(t => t.UpToPercent)
            .ToList();

        // SameTicker: HedgeSymbol mirrors GridSymbol automatically.
        var hedgeSymbol = (config.Mode == GridHedgeMode.SameTicker || string.IsNullOrWhiteSpace(config.HedgeSymbol))
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
                    // Bot completed this cycle. User must Stop → Start to begin a fresh one;
                    // the controller's Start branch resets Phase = NotStarted.
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
        if (config.Tiers == null || config.Tiers.Count == 0
            || config.Tiers.Any(t => t.UpToPercent <= 0 || t.SizeUsdt <= 0))
        { Log(strategy, "Error", "Tiers пуст или содержит невалидные UpToPercent/SizeUsdt"); return false; }
        if (config.DcaStepPercent <= 0 || config.TpStepPercent <= 0)
        { Log(strategy, "Error", "DcaStepPercent / TpStepPercent должны быть > 0"); return false; }
        if (config.HedgeRatio < 0 || config.HedgeRatio > 5)
        { Log(strategy, "Error", $"HedgeRatio вне разумного диапазона [0..5]: {config.HedgeRatio}"); return false; }
        if (config.Beta <= 0 || config.Beta > 10)
        { Log(strategy, "Error", $"Beta вне разумного диапазона (0..10]: {config.Beta}"); return false; }
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
            Log(strategy, "Info", $"📌 ANCHOR {config.GridSymbol} = {state.Anchor}");
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

        // Notional sizing: cover the entire grid (all tier levels × tier size) × HedgeRatio × β.
        var gridNotional = ComputeGridNotional(config);
        var hedgeNotional = gridNotional * config.HedgeRatio * config.Beta;
        if (hedgeNotional <= 0)
        {
            Log(strategy, "Error",
                $"hedgeNotional={hedgeNotional} (grid={gridNotional}, ratio={config.HedgeRatio}, β={config.Beta}) — невалидно");
            return;
        }

        // Best-effort leverage.
        try { await futures.SetLeverageAsync(hedgeSymbol, config.HedgeLeverage); }
        catch (NotSupportedException) { }
        catch (Exception ex)
        { _logger.LogWarning(ex, "GridHedge: SetLeverageAsync(hedge) failed for {Symbol}", hedgeSymbol); }

        state.Phase = GridHedgePhase.HedgeOpening;
        Log(strategy, "Info",
            $"🛡️ Открываю хедж SHORT {hedgeSymbol}: notional={hedgeNotional} USDT (grid={gridNotional} × ratio={config.HedgeRatio} × β={config.Beta}), lev={config.HedgeLeverage}");

        var result = await futures.OpenShortAsync(hedgeSymbol, hedgeNotional);
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
        if (levels.Count == 0)
        {
            Log(strategy, "Error",
                $"Сетка пуста: tier-конфиг и DcaStep дают 0 уровней внутри Range={config.RangePercent}%");
            strategy.Status = Core.Enums.StrategyStatus.Stopped;
            return;
        }

        // Already-placed levels (PendingBuys, in case ArmGrid is retried after a partial failure).
        var placedKeys = new HashSet<string>(
            state.PendingBuys.Select(p => FormatLevelKey(p.LevelPercent)),
            StringComparer.OrdinalIgnoreCase);
        // Already-filled levels (Batches, in case fills happened before ArmGrid completed).
        foreach (var b in state.Batches) placedKeys.Add(FormatLevelKey(b.LevelPercent));

        var placedThisTick = 0;
        var toPlace = levels.Where(l => !placedKeys.Contains(FormatLevelKey(l.offsetPct))).ToList();
        Log(strategy, "Info",
            $"🪜 Расставляю сетку: всего уровней={levels.Count}, осталось={toPlace.Count} " +
            $"(якорь={state.Anchor}, Range={config.RangePercent}%, default step={config.DcaStepPercent}%)");

        foreach (var (offsetPct, price, tier) in toPlace)
        {
            if (placedThisTick > 0) await Task.Delay(InterOrderDelayMs, ct);
            if (state.PlacementCooldownUntil.HasValue && state.PlacementCooldownUntil.Value > DateTime.UtcNow)
                break;

            var qty = tier.SizeUsdt / price;
            if (qty <= 0)
            {
                Log(strategy, "Warning", $"Уровень -{offsetPct}%: qty=0 (size={tier.SizeUsdt}, price={price}) — пропуск");
                continue;
            }

            OrderResultDto result;
            try { result = await gridLeg.PlaceLimitBuyAsync(price, qty); }
            catch (Exception ex)
            {
                state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
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
                placedThisTick++;
                Log(strategy, "Info",
                    $"🎯 -{offsetPct}%: BUY {Math.Round(qty, 6)} @ {Math.Round(price, 6)} (id={result.OrderId})");
            }
            else
            {
                state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
                Log(strategy, "Warning",
                    $"Уровень -{offsetPct}% не выставлен (cooldown {PlacementCooldownMinutes}мин): {result.ErrorMessage}");
                _logger.LogWarning("Strategy {Id}: GridHedge level -{Pct}% placement failed: {Error}",
                    strategy.Id, offsetPct, result.ErrorMessage);
                break;
            }
        }

        // Advance to Active only when every level we expected has a placed ID. Otherwise the
        // next tick will retry the remaining ones once the cooldown clears.
        var totalCovered = state.PendingBuys.Count + state.Batches.Count;
        if (totalCovered >= levels.Count)
        {
            state.Phase = GridHedgePhase.Active;
            Log(strategy, "Info",
                $"🟢 Сетка готова: {totalCovered}/{levels.Count} уровней. Phase=Active.");
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
                    // Filled with FilledQuantity == 0 — same Bybit V5 history glitch GridFloat
                    // handles. Drop the tracking; we won't re-arm in V1 (not a recurring grid).
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
        var tpStep = EffectiveTpStep(config, pending.LevelPercent);
        var tpPrice = fillPrice * (1m + tpStep / 100m);

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
            $"✅ Фил -{pending.LevelPercent}%: qty={Math.Round(fillQty, 6)} @ {Math.Round(fillPrice, 6)} → TP={Math.Round(tpPrice, 6)} (+{tpStep}%)");

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
                    RecordBatchClosure(strategy, state, gridLeg, batch, closePrice, status.FilledQuantity, "TpFill");
                    state.Batches.Remove(batch);
                    break;
                }

                case OrderLifecycleStatus.Cancelled:
                case OrderLifecycleStatus.Rejected:
                    if (status.FilledQuantity > 0)
                    {
                        var closePrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : batch.TpPrice;
                        RecordBatchClosure(strategy, state, gridLeg, batch, closePrice, status.FilledQuantity, "TpCancelledPartial");
                        state.Batches.Remove(batch);
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
        var upTrigger = state.Anchor * (1m + config.UpperExitPercent / 100m);
        var downTrigger = state.Anchor * (1m - config.RangePercent / 100m);

        if (price.Value >= upTrigger)
        {
            state.Phase = GridHedgePhase.ExitingUp;
            Log(strategy, "Info",
                $"⬆️ ВЕРХНИЙ ТРИГГЕР: цена {Math.Round(price.Value, 6)} ≥ {Math.Round(upTrigger, 6)} " +
                $"(anchor + {config.UpperExitPercent}%). Закрываю grid + hedge.");
        }
        else if (price.Value <= downTrigger)
        {
            state.Phase = GridHedgePhase.ExitingDown;
            Log(strategy, "Warning",
                $"⬇️ STOP-LOSS: цена {Math.Round(price.Value, 6)} ≤ {Math.Round(downTrigger, 6)} " +
                $"(anchor − {config.RangePercent}%). Аварийное закрытие grid + hedge.");
        }
    }

    // ────────────────────────── Phase: Exiting* ──────────────────────────

    private async Task CloseEverythingAsync(
        Strategy strategy, GridHedgeConfig config, GridHedgeState state,
        string hedgeSymbol, IGridLeg gridLeg, IFuturesExchangeService futures, CancellationToken ct)
    {
        Log(strategy, "Info", $"🚪 Закрытие: Phase={state.Phase}, открытых батчей={state.Batches.Count(b => !b.Closed)}, pendingBuys={state.PendingBuys.Count}, hedgeQty={state.HedgeQty}");

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

            // Use the most accurate close price we can get — exchange-reported FilledPrice if
            // available, otherwise live ticker.
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

            // Hedge PnL — for a short: (avgEntry − closePrice) × qty, minus fees on both legs.
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
            var hedgeFees = hedgeNotional * (futures.TakerFeeRate * 2m); // taker on open + taker on close
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
        state.CompletedCycles += 1;
        state.Phase = GridHedgePhase.Done;
        state.Anchor = 0;
        state.HedgeAnchor = 0;
        Log(strategy, "Info",
            $"🏁 Цикл #{state.CompletedCycles} завершён. " +
            $"Grid PnL за цикл (накопленный): ${Math.Round(state.GridRealizedPnl, 2)}, " +
            $"Hedge PnL (накопленный): ${Math.Round(state.HedgeRealizedPnl, 2)}. " +
            $"Чтобы запустить новый цикл — Stop → Start.");
    }

    // ────────────────────────── Helpers: grid math ──────────────────────────

    /// <summary>
    /// Walks each tier's offset range using the tier's effective DCA step (same algorithm as
    /// GridFloat.ComputeDcaLevels), returning the list of grid-buy levels. Returns
    /// (offsetPctFromAnchor, price, tier) tuples. Long-only.
    /// </summary>
    private static List<(decimal offsetPct, decimal price, GridFloatTier tier)> ComputeGridLevels(
        GridHedgeConfig config, decimal anchor)
    {
        var list = new List<(decimal, decimal, GridFloatTier)>();
        if (anchor <= 0 || config.Tiers.Count == 0) return list;

        // Hard cap — protect against misconfig generating hundreds of orders.
        const int safetyCeiling = 200;
        const decimal eps = 1e-9m;

        decimal prevTopPct = 0m;
        int k = 0;
        foreach (var tier in config.Tiers)
        {
            var stepPct = tier.DcaStepPercent is > 0 ? tier.DcaStepPercent.Value : config.DcaStepPercent;
            if (stepPct <= 0) continue;

            var offsetPct = prevTopPct + stepPct;
            while (offsetPct <= tier.UpToPercent + eps && k < safetyCeiling)
            {
                // Long-grid only: levels sit BELOW the anchor.
                var price = anchor * (1m - offsetPct / 100m);
                if (price <= 0) return list;

                // Cap at config.RangePercent — never place buys below the stop-loss boundary.
                if (offsetPct > config.RangePercent + eps) return list;

                k++;
                list.Add((offsetPct, price, tier));
                offsetPct += stepPct;
            }
            prevTopPct = tier.UpToPercent;
            if (k >= safetyCeiling) break;
        }
        return list;
    }

    /// <summary>
    /// Total USDT notional the grid will deploy if every level fills. Used as the basis for
    /// hedge sizing.
    /// </summary>
    private static decimal ComputeGridNotional(GridHedgeConfig config)
    {
        var anchor = 1m; // notional sizing is anchor-independent — each level's USDT is fixed
        var levels = ComputeGridLevels(config, anchor);
        return levels.Sum(l => l.tier.SizeUsdt);
    }

    /// <summary>
    /// Effective TP step% for a fill at a given offset% from anchor: the override from the
    /// tier containing that offset, falling back to the global TpStepPercent.
    /// </summary>
    private static decimal EffectiveTpStep(GridHedgeConfig config, decimal offsetPct)
    {
        if (config.Tiers.Count == 0) return config.TpStepPercent;
        var tier = config.Tiers.FirstOrDefault(t => offsetPct <= t.UpToPercent) ?? config.Tiers[^1];
        return tier.TpStepPercent is > 0 ? tier.TpStepPercent.Value : config.TpStepPercent;
    }

    private static string FormatLevelKey(decimal offsetPct) => Math.Round(offsetPct, 6).ToString("0.######");

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

    // Lets the state machine treat the grid leg the same way regardless of whether the user
    // chose SameTicker (spot) or CrossTicker (futures). Hedge always goes through the
    // IFuturesExchangeService directly — that's a single leg with explicit semantics.
    private interface IGridLeg
    {
        string Symbol { get; }
        decimal MakerFeeRate { get; }
        decimal TakerFeeRate { get; }
        Task<decimal?> GetTickerPriceAsync();
        Task<OrderResultDto> PlaceLimitBuyAsync(decimal price, decimal qty);
        Task<OrderResultDto> PlaceLimitSellAsync(decimal price, decimal qty);
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
        public Task<OrderResultDto> PlaceMarketSellAsync(decimal qty) => _futures.CloseLongAsync(Symbol, qty);
        public Task<bool> CancelOrderAsync(string orderId) => _futures.CancelOrderAsync(Symbol, orderId);
        public Task<OrderStatusDto?> GetOrderAsync(string orderId) => _futures.GetOrderAsync(Symbol, orderId);
    }
}
