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
/// Floating-grid strategy ("Grid Float").
///
/// On the close of the first candle after a flat state, opens a single market entry of
/// BaseSizeUsdt — this fill price becomes the anchor. Immediately lays N DCA limit orders on
/// the losing side of the anchor at fixed step% spacing, capped by RangePercent. Each fill
/// (anchor and DCAs alike) becomes a separate "batch", and each batch gets its own
/// reduce-only TP limit at fill_price ± TpStepPercent (NOT the average — each batch is closed
/// independently at its own +1%).
///
/// When a batch's TP fills, the slot is re-armed: a fresh DCA limit is placed at the same
/// grid level. Oscillations therefore generate multiple TP fills off the same anchor.
///
/// When ALL batches are gone (full position closed by accumulating TP fills), the bot:
///   1. cancels every remaining DCA limit on the exchange,
///   2. waits one closed bar (cooldown),
///   3. opens a brand-new anchor on the next close.
///
/// Range modes:
///   - Dynamic (default): each new anchor recenters the grid → always floor(Range/Step) slots.
///   - Static: the lower (Long) / upper (Short) bound is frozen at the FIRST anchor of the
///     bot session; subsequent anchors place as many slots as fit before the frozen bound
///     (so the slot count drifts: 9, 10, 11, 12…). Cleared on bot Stop+Start.
///
/// No stop-loss. One direction per bot. Bounds for short are mirrored.
/// </summary>
public class GridFloatHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const int PlacementCooldownMinutes = 5;

    // Small delay between sequential REST placements to stay inside per-key rate limits
    // (Bybit ~10/sec on linear post-order, Bitget ~10/sec, BingX ~5/sec). 75ms between calls
    // caps the burst at ~13 req/sec which fits every exchange we support. Applied in
    // PlaceInitialDcaLadder, HealMissingDcas and HealMissingTps — the three sites that can
    // legitimately fire dozens of orders back-to-back (initial ladder on a fresh anchor,
    // re-arm after restart, or TP heal after restart).
    private const int InterOrderDelayMs = 75;

    public string StrategyType => StrategyTypes.GridFloat;

    private readonly AppDbContext _db;
    private readonly ILogger<GridFloatHandler> _logger;
    private readonly ITelegramSignalService _telegramSignalService;

    public GridFloatHandler(AppDbContext db, ILogger<GridFloatHandler> logger,
        ITelegramSignalService telegramSignalService)
    {
        _db = db;
        _logger = logger;
        _telegramSignalService = telegramSignalService;
    }

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<GridFloatConfig>(strategy.ConfigJson, JsonOptions);
        if (config == null || string.IsNullOrEmpty(config.Symbol))
        {
            Log(strategy, "Error", "Некорректный config — symbol пуст");
            return;
        }

        // Migrate legacy single-tier configs (BaseSizeUsdt + RangePercent) → Tiers list and
        // sort. Mutates `config.Tiers` in-place; idempotent on already-tiered configs.
        NormalizeTiers(config);

        // Per-tier overrides (if present) must be strictly positive; null = inherit the global
        // DcaStepPercent / TpStepPercent default.
        if (config.Tiers.Count == 0 || config.Tiers.Any(t => t.UpToPercent <= 0 || t.SizeUsdt <= 0)
            || config.Tiers.Any(t => t.DcaStepPercent is <= 0 || t.TpStepPercent is <= 0)
            || config.DcaStepPercent <= 0 || config.TpStepPercent <= 0
            || config.Tiers[^1].UpToPercent < EffectiveDcaStep(config, config.Tiers[^1])
            || config.Leverage < 1)
        {
            Log(strategy, "Error",
                $"Некорректные параметры: Tiers=[{string.Join(", ", config.Tiers.Select(t => $"upTo={t.UpToPercent}%/size={t.SizeUsdt}/dca={t.DcaStepPercent?.ToString() ?? "-"}/tp={t.TpStepPercent?.ToString() ?? "-"}"))}], " +
                $"DcaStep={config.DcaStepPercent}%, TpStep={config.TpStepPercent}%, Leverage={config.Leverage}");
            return;
        }

        var isLongConfig = config.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var state = JsonSerializer.Deserialize<GridFloatState>(strategy.StateJson, JsonOptions)
                    ?? new GridFloatState();

        // 1. Restart re-sync (once per worker boot).
        if (!state.StateInitialized)
        {
            await SyncFromExchangeOnStartup(strategy, config, state, exchange, isLongConfig, ct);
            state.StateInitialized = true;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 2. Poll pending DCA limits → detected fills become new batches with their own TPs.
        if (state.DcaOrders.Count > 0)
        {
            await PollDcaFills(strategy, config, state, exchange, ct);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 3. Poll batch TPs → detected fills remove the batch and re-arm the DCA at that level.
        if (state.Batches.Count > 0)
        {
            await PollTpFills(strategy, config, state, exchange, ct);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 3b. Position-based reconcile — defensive backstop catching what Poll* missed.
        // Bybit DOES implement GetOrderAsync (V5 GetOrders + GetOrderHistory fallback) but
        // on worker restart its history endpoint can return status=Filled with
        // FilledQuantity=0 for still-active orders — Poll* now drops those as glitches
        // (re-places the TP/DCA) instead of phantom-closing. Reconcile then walks the
        // exchange position to catch real fills Poll* might still have missed:
        //   exchange.qty < state.qty → missed TP fill(s) → close batches.
        //   exchange.qty > state.qty → missed DCA fill(s) → adopt DCA orders as batches.
        // Runs whenever EITHER batches or DCA orders exist.
        if (state.Batches.Count > 0 || state.DcaOrders.Count > 0)
        {
            await ReconcileBatchesFromPosition(strategy, config, state, exchange, ct);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 4. Heal: place missing TPs for batches that lost theirs (placement glitch or restart).
        if (state.Batches.Count > 0)
        {
            await HealMissingTps(strategy, config, state, exchange);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 5. Heal: re-arm DCA limits for any free slot inside the current grid range.
        if (state.AnchorPrice > 0)
        {
            await HealMissingDcas(strategy, config, state, exchange);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 6. Fetch candles. Need at least 2 closed bars to drive the "wait next bar" cooldown.
        var candles = await exchange.GetKlinesAsync(config.Symbol, config.Timeframe, 5);
        if (candles.Count < 1) return;

        var lastClosed = GetLastClosedCandle(candles);
        if (lastClosed == null) return;

        state.LastPrice = lastClosed.Close;

        // 7. No new closed candle → just persist whatever heal/poll updated.
        if (state.LastProcessedCandleTime.HasValue
            && lastClosed.CloseTime <= state.LastProcessedCandleTime.Value)
        {
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        state.LastProcessedCandleTime = lastClosed.CloseTime;

        // 8. One-bar cooldown after full close. OpenAfterTime is set by OnFullClose to the
        // moment of full closure; the next anchor opens on the first closed candle whose
        // CloseTime is strictly after that instant — which is the candle following the bar in
        // which the closure was detected. Matches user spec: "ждём закрытия следующего бара".
        if (state.OpenAfterTime.HasValue
            && state.Batches.Count == 0
            && state.DcaOrders.Count == 0)
        {
            if (lastClosed.CloseTime <= state.OpenAfterTime.Value)
            {
                // Still inside (or before) the bar where the closure happened — keep waiting.
                SaveState(strategy, state);
                await _db.SaveChangesAsync(ct);
                return;
            }
            // First candle past the cooldown gate — clear the gate and proceed to anchor open.
            Log(strategy, "Info",
                $"Кулдаун снят (закрылся бар после полного закрытия в {state.OpenAfterTime.Value:HH:mm:ss}) — открываю новый якорь");
            state.OpenAfterTime = null;
        }

        // 9. Open new anchor if we're flat.
        if (state.Batches.Count == 0 && state.DcaOrders.Count == 0 && !state.OpenAfterTime.HasValue)
        {
            await OpenAnchor(strategy, config, state, exchange, lastClosed, isLongConfig, ct);
            if (state.AnchorPrice > 0)
                await PlaceInitialDcaLadder(strategy, config, state, exchange);
        }

        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
    }

    // ────────────────────────── Anchor / DCA placement ──────────────────────────

    private async Task OpenAnchor(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, CandleDto candle, bool isLong, CancellationToken ct)
    {
        // Pre-placement exchange-minimum guard. If tier 0's USDT size is below the symbol's
        // minimum order notional at the current price, either auto-bump the tier (if the gap
        // is ≤ 2×) or stop the bot. Saves users from infinite "Qty 0 < min X" loops on
        // symbols whose lot size makes the configured tier unviable.
        if (!await EnsureTierFitsExchangeMinimum(strategy, config, exchange, config.Tiers[0], candle.Close, levelIdx: 0))
            return;

        // Best-effort leverage. Some exchanges error if it's already set to the same value
        // — we swallow those because they don't block trading.
        try { await exchange.SetLeverageAsync(config.Symbol, config.Leverage); }
        catch (NotSupportedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat: SetLeverageAsync failed (continuing) for {Symbol}", config.Symbol);
        }

        // Anchor (level 0) sits at offset 0% from itself, which is inside the first tier.
        // Re-read tier.SizeUsdt after the guard — it may have been bumped just above.
        var anchorSize = config.Tiers[0].SizeUsdt;
        var maxRangePct = config.Tiers[^1].UpToPercent;

        Log(strategy, "Info",
            $"📈 ANCHOR {config.Direction}: {config.Symbol}, anchorSize={anchorSize}USDT, close={candle.Close}, " +
            $"tiers=[{string.Join(", ", config.Tiers.Select(t => $"≤{t.UpToPercent}%:${t.SizeUsdt}/dca{t.DcaStepPercent?.ToString() ?? config.DcaStepPercent.ToString()}%/tp{t.TpStepPercent?.ToString() ?? config.TpStepPercent.ToString()}%"))}], " +
            $"default step={config.DcaStepPercent}%, tp={config.TpStepPercent}%, maxRange=±{maxRangePct}%");

        var result = isLong
            ? await exchange.OpenLongAsync(config.Symbol, anchorSize)
            : await exchange.OpenShortAsync(config.Symbol, anchorSize);

        if (!result.Success || result.FilledQuantity is not > 0)
        {
            Log(strategy, "Error", $"Ошибка ANCHOR: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: GridFloat ANCHOR failed: {Error}", strategy.Id, result.ErrorMessage);
            return;
        }

        var fillQty = result.FilledQuantity.Value;
        var fillPrice = result.FilledPrice ?? candle.Close;

        state.IsLong = isLong;
        state.AnchorPrice = fillPrice;

        if (config.UseStaticRange && !state.StaticBoundsInitialized)
        {
            // First anchor of the bot session — freeze the protected bound at the largest
            // tier's UpToPercent (the outermost edge of the configured grid).
            if (isLong)
                state.StaticLowerBound = fillPrice * (1m - maxRangePct / 100m);
            else
                state.StaticUpperBound = fillPrice * (1m + maxRangePct / 100m);
            state.StaticBoundsInitialized = true;
            Log(strategy, "Info",
                $"📌 STATIC RANGE: " +
                (isLong ? $"lower={Math.Round(state.StaticLowerBound, 8)}" : $"upper={Math.Round(state.StaticUpperBound, 8)}"));
        }

        // Anchor sits at offset 0% → falls inside tier 0; uses its TP override if set.
        var anchorTpStep = EffectiveTpStep(config, fillPrice, fillPrice);
        var tpPrice = ComputeTp(fillPrice, anchorTpStep, isLong);
        var batch = new GridFloatBatch
        {
            LevelIdx = 0, // anchor
            FillPrice = fillPrice,
            Qty = fillQty,
            TpPrice = tpPrice,
            FilledAt = DateTime.UtcNow,
        };
        state.Batches.Add(batch);

        RecordTrade(strategy, config.Symbol, isLong ? "Buy" : "Sell", fillQty, fillPrice, result.OrderId, "Entry");

        Log(strategy, "Info",
            $"✅ ANCHOR open: qty={fillQty} @ {fillPrice}, batch TP={Math.Round(tpPrice, 8)}");

        await PlaceBatchTpLimit(strategy, config, state, exchange, batch);

        await _telegramSignalService.SendOpenPositionSignalAsync(strategy, config.Symbol, config.Direction,
            anchorSize, fillPrice, tpPrice, stopLoss: null, ct);
    }

    private async Task PlaceInitialDcaLadder(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange)
    {
        var levels = ComputeDcaLevels(config, state);
        if (levels.Count == 0)
        {
            Log(strategy, "Warning", "Сетка пуста: 0 уровней (тиры/Step или статическая граница слишком близки)");
            return;
        }

        Log(strategy, "Info",
            $"Расставляю DCA-сетку: {levels.Count} уровней (" +
            string.Join(", ", levels.Take(5).Select(l => $"#{l.idx}@{Math.Round(l.price, 8)}({l.tier.SizeUsdt}$)")) +
            (levels.Count > 5 ? "…" : "") + ")");

        for (int i = 0; i < levels.Count; i++)
        {
            if (i > 0) await Task.Delay(InterOrderDelayMs);
            var (idx, price, tier) = levels[i];
            await PlaceDcaLimit(strategy, config, state, exchange, idx, price, tier);
        }
    }

    private async Task PlaceDcaLimit(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, int levelIdx, decimal price, GridFloatTier tier)
    {
        if (state.PlacementCooldownUntil.HasValue && state.PlacementCooldownUntil.Value > DateTime.UtcNow)
            return;

        // Pre-placement exchange-minimum guard: bump tier size or stop the bot if the
        // configured size is below the symbol's minimum notional. Re-reads tier.SizeUsdt
        // below so the bump takes effect on this same placement.
        if (!await EnsureTierFitsExchangeMinimum(strategy, config, exchange, tier, price, levelIdx))
            return;

        var qty = tier.SizeUsdt / price;
        if (qty <= 0)
        {
            Log(strategy, "Warning", $"DCA #{levelIdx}: qty=0 (price={price}, size={tier.SizeUsdt}) — пропуск");
            return;
        }

        var side = state.IsLong ? "Buy" : "Sell";
        try
        {
            var result = await exchange.PlaceLimitOrderAsync(config.Symbol, side, price, qty, reduceOnly: false);
            if (result.Success && !string.IsNullOrEmpty(result.OrderId))
            {
                state.DcaOrders.Add(new GridFloatDcaOrder
                {
                    LevelIdx = levelIdx,
                    Price = price,
                    Qty = qty,
                    OrderId = result.OrderId,
                });
                Log(strategy, "Info",
                    $"🎯 DCA #{levelIdx} лимит: {side} {qty} @ {Math.Round(price, 8)} (id={result.OrderId})");
                return;
            }

            // Suspected min-notional / margin issue → cooldown rather than busy-loop.
            state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
            Log(strategy, "Warning",
                $"DCA #{levelIdx} не выставлен (cooldown {PlacementCooldownMinutes}мин): {result.ErrorMessage}");
            _logger.LogWarning("Strategy {Id}: DCA #{Lvl} placement failed: {Error}",
                strategy.Id, levelIdx, result.ErrorMessage);
        }
        catch (NotSupportedException)
        {
            Log(strategy, "Error", "Биржа не поддерживает PlaceLimitOrderAsync — стратегия не может работать");
        }
        catch (Exception ex)
        {
            state.PlacementCooldownUntil = DateTime.UtcNow.AddMinutes(PlacementCooldownMinutes);
            _logger.LogError(ex, "Strategy {Id}: DCA #{Lvl} placement threw", strategy.Id, levelIdx);
            Log(strategy, "Error", $"DCA #{levelIdx} исключение (cooldown {PlacementCooldownMinutes}мин): {ex.Message}");
        }
    }

    private async Task PlaceBatchTpLimit(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, GridFloatBatch batch)
    {
        if (batch.Qty <= 0 || batch.TpPrice <= 0) return;

        if (exchange.UsesSoftTakeProfit)
        {
            // Soft-TP exchanges (Dzengi): we don't place a resting order; TP is enforced by
            // the on-cross safety net during a polling tick.
            Log(strategy, "Info",
                $"Soft TP for batch #{batch.LevelIdx} (биржа без reduce-only limit): qty={batch.Qty} target={Math.Round(batch.TpPrice, 8)}");
            return;
        }

        // Cleanup guard: drop batches whose qty is below the exchange minimum (legacy zombies
        // from partial-fill reconcile adoptions before Fix #5 was deployed). Without this they
        // would loop in HealMissingTps forever logging "Qty 0 < min N".
        try
        {
            var (qtyStep, minQty) = await exchange.GetSymbolInfoAsync(config.Symbol);
            if (minQty > 0 && batch.Qty < minQty)
            {
                Log(strategy, "Warning",
                    $"🧹 Удаляю sub-min батч #{batch.LevelIdx}: qty={batch.Qty} < биржевой минимум {minQty}. " +
                    $"Это легаси из partial-fill reconcile до Fix #5. PnL по нему не реализуется.");
                state.Batches.Remove(batch);
                return;
            }
        }
        catch (NotSupportedException) { /* exchange doesn't expose, fall through to normal flow */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat TP-place: GetSymbolInfoAsync probe failed for {Symbol}", config.Symbol);
        }

        var closeSide = state.IsLong ? "Sell" : "Buy";
        try
        {
            var result = await exchange.PlaceLimitOrderAsync(config.Symbol, closeSide, batch.TpPrice, batch.Qty, reduceOnly: true);
            if (result.Success && !string.IsNullOrEmpty(result.OrderId))
            {
                batch.TpOrderId = result.OrderId;
                Log(strategy, "Info",
                    $"🎯 TP батча #{batch.LevelIdx}: {closeSide} {batch.Qty} @ {Math.Round(batch.TpPrice, 8)} (id={result.OrderId})");
                return;
            }

            batch.TpOrderId = null;
            Log(strategy, "Error",
                $"TP батча #{batch.LevelIdx} не выставлен: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: TP batch #{Lvl} placement failed: {Error}",
                strategy.Id, batch.LevelIdx, result.ErrorMessage);
        }
        catch (NotSupportedException)
        {
            batch.TpOrderId = null;
            Log(strategy, "Warning",
                "Биржа не поддерживает PlaceLimitOrderAsync — TP не выставится");
        }
        catch (Exception ex)
        {
            batch.TpOrderId = null;
            _logger.LogError(ex, "Strategy {Id}: TP batch #{Lvl} placement threw", strategy.Id, batch.LevelIdx);
            Log(strategy, "Error", $"TP батча #{batch.LevelIdx} исключение: {ex.Message}");
        }
    }

    // ────────────────────────── Fill detection ──────────────────────────

    private async Task PollDcaFills(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        // Snapshot — we mutate state.DcaOrders inside the loop.
        var pending = state.DcaOrders.ToList();
        foreach (var dca in pending)
        {
            OrderStatusDto? status;
            try { status = await exchange.GetOrderAsync(config.Symbol, dca.OrderId); }
            catch (NotSupportedException) { continue; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridFloat: DCA poll GetOrderAsync failed for {OrderId}", dca.OrderId);
                continue;
            }
            if (status == null) continue;

            switch (status.Status)
            {
                case OrderLifecycleStatus.Filled when status.FilledQuantity > 0:
                case OrderLifecycleStatus.PartiallyFilled when status.FilledQuantity > 0:
                {
                    // PartiallyFilled is unusual for a single-level DCA fill but we accept it
                    // pragmatically — adopt whatever filled, leave the remainder to either
                    // complete or get cancelled (we'll catch the cancellation on a later poll).
                    if (status.Status == OrderLifecycleStatus.PartiallyFilled)
                        continue; // wait for full Filled / Cancelled to avoid double-adoption

                    var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : dca.Price;
                    var fillQty = status.FilledQuantity > 0 ? status.FilledQuantity : dca.Qty;
                    await AdoptDcaFill(strategy, config, state, exchange, dca, fillQty, fillPrice, ct);
                    state.DcaOrders.Remove(dca);
                    break;
                }
                case OrderLifecycleStatus.Filled:
                {
                    // Filled but FilledQuantity == 0. Observed on Bybit V5 for stale order ids
                    // returned by GetOrderHistory after a worker restart — Bybit returns
                    // status=Filled with FilledQuantity=0 and AveragePrice=0. Adopting this
                    // as a real fill would create a phantom batch with batch.Qty fabricated
                    // from dca.Qty. Drop the order from tracking and re-arm the slot.
                    Log(strategy, "Warning",
                        $"DCA #{dca.LevelIdx}: Filled но FilledQuantity=0 — игнорирую (Bybit history glitch), слот переустановится");
                    state.DcaOrders.Remove(dca);
                    break;
                }
                case OrderLifecycleStatus.Cancelled:
                case OrderLifecycleStatus.Rejected:
                {
                    if (status.FilledQuantity > 0)
                    {
                        // Partial fill before cancellation — adopt the partial as a batch.
                        var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : dca.Price;
                        await AdoptDcaFill(strategy, config, state, exchange, dca, status.FilledQuantity, fillPrice, ct);
                    }
                    else
                    {
                        Log(strategy, "Warning",
                            $"DCA #{dca.LevelIdx} лимит отменён/отклонён без филла — слот переустановится на следующем тике");
                    }
                    state.DcaOrders.Remove(dca);
                    break;
                }
                default:
                    break; // Open / Unknown → keep waiting
            }
        }
    }

    private async Task AdoptDcaFill(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, GridFloatDcaOrder dca, decimal fillQty, decimal fillPrice,
        CancellationToken ct)
    {
        // TP step uses the tier in which this fill's offset% (from anchor) lies. Falls back
        // to config.TpStepPercent if the tier doesn't define its own override.
        var tpStep = EffectiveTpStep(config, state.AnchorPrice, fillPrice);
        var tpPrice = ComputeTp(fillPrice, tpStep, state.IsLong);
        var batch = new GridFloatBatch
        {
            LevelIdx = dca.LevelIdx,
            FillPrice = fillPrice,
            Qty = fillQty,
            TpPrice = tpPrice,
            FilledAt = DateTime.UtcNow,
        };
        state.Batches.Add(batch);

        RecordTrade(strategy, config.Symbol, state.IsLong ? "Buy" : "Sell", fillQty, fillPrice,
            dca.OrderId, $"DCA#{dca.LevelIdx}");

        Log(strategy, "Info",
            $"✅ DCA #{dca.LevelIdx} filled: qty={fillQty} @ {fillPrice} → batch TP={Math.Round(tpPrice, 8)}");
        _logger.LogInformation(
            "Strategy {Id}: GridFloat DCA#{Lvl} {Dir} qty={Qty} @ {Price}",
            strategy.Id, dca.LevelIdx, config.Direction, fillQty, fillPrice);

        await PlaceBatchTpLimit(strategy, config, state, exchange, batch);

        await _telegramSignalService.SendDcaSignalAsync(strategy, config.Symbol, config.Direction,
            dca.LevelIdx, fillQty * fillPrice, fillPrice, tpPrice, ct);
    }

    private async Task PollTpFills(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        var snapshot = state.Batches.ToList();
        foreach (var batch in snapshot)
        {
            if (string.IsNullOrEmpty(batch.TpOrderId)) continue;

            OrderStatusDto? status;
            try { status = await exchange.GetOrderAsync(config.Symbol, batch.TpOrderId!); }
            catch (NotSupportedException) { continue; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridFloat: TP poll GetOrderAsync failed for {OrderId}", batch.TpOrderId);
                continue;
            }
            if (status == null) continue;

            switch (status.Status)
            {
                case OrderLifecycleStatus.Filled when status.FilledQuantity > 0:
                    await RecordTpFill(strategy, config, state, exchange, batch,
                        status.AverageFilledPrice > 0 ? status.AverageFilledPrice : batch.TpPrice,
                        status.FilledQuantity, ct);
                    break;
                case OrderLifecycleStatus.Filled:
                    // Filled but FilledQuantity == 0. Observed on Bybit V5: after worker restart,
                    // GetOrderHistory for a still-active TP can return status=Filled with
                    // FilledQuantity=0 and AveragePrice=0 (suspected V5 history glitch).
                    // Adopting it as a real TP fill would phantom-close batch.Qty (using
                    // batch.TpPrice as closePrice) and leak +pnl + leftover qty on the exchange.
                    // Drop the stale id; HealMissingTps re-places a fresh TP for the same batch.
                    Log(strategy, "Warning",
                        $"TP батча #{batch.LevelIdx}: Filled но FilledQuantity=0 — игнорирую (Bybit history glitch), переставлю");
                    batch.TpOrderId = null;
                    break;
                case OrderLifecycleStatus.Cancelled:
                case OrderLifecycleStatus.Rejected:
                    if (status.FilledQuantity > 0)
                    {
                        // Cancelled with partial fill — treat as TP fill of the partial. Edge
                        // case; full closure of an oddly-partial batch is fine because we
                        // remove the whole batch (rare, log loudly).
                        Log(strategy, "Warning",
                            $"TP батча #{batch.LevelIdx} отменён с частичным филлом {status.FilledQuantity} — фиксирую как закрытие");
                        await RecordTpFill(strategy, config, state, exchange, batch,
                            status.AverageFilledPrice > 0 ? status.AverageFilledPrice : batch.TpPrice,
                            status.FilledQuantity, ct);
                    }
                    else
                    {
                        // TP order vanished/cancelled with no fill (manual cancel via UI?) —
                        // clear id so the heal step re-places it on this same tick.
                        Log(strategy, "Warning",
                            $"TP батча #{batch.LevelIdx} отменён без филла — будет переустановлен");
                        batch.TpOrderId = null;
                    }
                    break;
                default:
                    break;
            }
        }
    }

    private async Task RecordTpFill(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, GridFloatBatch batch, decimal closePrice, decimal closeQty,
        CancellationToken ct)
    {
        var notional = batch.FillPrice * closeQty;
        var pnlPct = state.IsLong
            ? (closePrice - batch.FillPrice) / batch.FillPrice * 100m
            : (batch.FillPrice - closePrice) / batch.FillPrice * 100m;
        var grossPnl = notional * pnlPct / 100m;
        // Open was market (taker) on anchor; DCA was a maker limit; close was a maker limit.
        // For anchor batches a taker+maker model is more accurate; for DCA batches it's
        // maker+maker. Approximate uniformly with (maker+maker) since most batches will be DCAs.
        var commission = notional * (exchange.MakerFeeRate * 2m);
        var netPnl = grossPnl - commission;

        RecordTrade(strategy, config.Symbol, state.IsLong ? "Sell" : "Buy", closeQty, closePrice,
            batch.TpOrderId, $"TakeProfit#{batch.LevelIdx}", pnlDollar: netPnl, commission: commission);

        state.RealizedPnlDollar += netPnl;

        Log(strategy, "Info",
            $"💰 TP батча #{batch.LevelIdx} филлд: qty={closeQty} @ {closePrice}, " +
            $"вход={batch.FillPrice}, PnL={Math.Round(pnlPct, 4)}% (${Math.Round(netPnl, 2)})");
        _logger.LogInformation(
            "Strategy {Id}: GridFloat TP batch#{Lvl} {Dir} qty={Qty} @ {Price} netPnL={Pnl}",
            strategy.Id, batch.LevelIdx, config.Direction, closeQty, closePrice, netPnl);

        state.Batches.Remove(batch);

        // If this batch was the anchor (level 0) and grid still has DCAs ↓ resting, the next
        // poll's HealMissingDcas will re-arm whatever's missing — including level 0 if it's
        // free. Anchor *price* stays put until the whole position closes.

        // Full closure check: no batches AND no live DCAs left? Cancel anything stale and arm
        // the cooldown. We DON'T cancel DCAs proactively when a single TP fills — the slot
        // gets re-armed by HealMissingDcas (classic-grid behavior).
        if (state.Batches.Count == 0)
        {
            await OnFullClose(strategy, config, state, exchange, ct);
        }

        await _telegramSignalService.SendPositionClosedSignalAsync(
            strategy, config.Symbol, config.Direction, netPnl, pnlPct, ct);
    }

    /// <summary>
    /// Reconciles state against the live exchange position in BOTH directions. This is the
    /// PRIMARY fill-detection mechanism on Bybit (which doesn't implement
    /// IFuturesExchangeService.GetOrderAsync at all — Poll* methods silently no-op there),
    /// and a backstop on exchanges that do support GetOrderAsync but sometimes return null
    /// or Unknown for orders that have been moved to history after a fill.
    ///
    /// (1) exchangeQty &lt; sum(state.Batches.Qty) → some batches' TPs closed on the exchange
    ///     but PollTpFills didn't detect. Close batches in TP-cross order.
    ///
    /// (2) exchangeQty &gt; sum(state.Batches.Qty) → some DCA limits filled on the exchange
    ///     but PollDcaFills didn't detect. Adopt DCA orders in price-cross order as new
    ///     batches and place their dedicated TPs.
    ///
    /// One GetPositionAsync call (plus a 2-second confirmation probe) covers both cases.
    /// </summary>
    private async Task ReconcileBatchesFromPosition(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange, CancellationToken ct)
    {
        // We can reconcile if either side has something to lose/gain.
        if (state.Batches.Count == 0 && state.DcaOrders.Count == 0) return;

        PositionDto? pos;
        try { pos = await exchange.GetPositionAsync(config.Symbol, config.Direction); }
        catch (NotSupportedException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat reconcile: GetPositionAsync failed for {Symbol}", config.Symbol);
            return;
        }

        var exchangeQty = pos?.Quantity ?? 0m;
        var stateQty = state.Batches.Sum(b => b.Qty);

        // Tolerance band: max of 0.1% of position size OR the symbol's minimum order quantity.
        // The min-qty floor (Fix #5 follow-up) prevents perpetual re-adoption of sub-minimum
        // dust (e.g. 59 of a 100-min-qty symbol left over from a historical partial fill).
        // Such dust can't be re-traded anyway — adopt → drop → re-place → adopt loops otherwise.
        var (qtyStep, minQty) = (0m, 0m);
        try { (qtyStep, minQty) = await exchange.GetSymbolInfoAsync(config.Symbol); }
        catch (NotSupportedException) { /* fall through, minQty = 0 */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat reconcile: GetSymbolInfoAsync failed for {Symbol}", config.Symbol);
        }
        var noiseFloor = Math.Max(Math.Max(stateQty, exchangeQty) * 0.001m, minQty);
        var rawDelta = exchangeQty - stateQty;
        if (Math.Abs(rawDelta) <= noiseFloor) return; // in-sync within tolerance / dust ignored

        // Second probe to filter out transient exchange empty/stale snapshots.
        try { await Task.Delay(2000, ct); }
        catch (OperationCanceledException) { return; }

        PositionDto? pos2;
        try { pos2 = await exchange.GetPositionAsync(config.Symbol, config.Direction); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat reconcile: second-probe failed for {Symbol}", config.Symbol);
            return;
        }
        var exchangeQty2 = pos2?.Quantity ?? 0m;
        var rawDelta2 = exchangeQty2 - stateQty;
        // Only act if BOTH probes agree on the direction of the discrepancy.
        if (Math.Sign(rawDelta) != Math.Sign(rawDelta2) || Math.Abs(rawDelta2) <= noiseFloor)
        {
            Log(strategy, "Info",
                $"Reconcile: 1st={Math.Round(exchangeQty, 8)} 2nd={Math.Round(exchangeQty2, 8)} vs state={Math.Round(stateQty, 8)} — несогласованность, пропуск (вероятно транзитный снапшот).");
            return;
        }

        // Use the reading closer to state (smaller absolute delta) — conservative.
        exchangeQty = Math.Abs(rawDelta) < Math.Abs(rawDelta2) ? exchangeQty : exchangeQty2;
        var delta = exchangeQty - stateQty;

        // Fix #3: always prefer a fresh ticker over state.LastPrice for the cross-check.
        // state.LastPrice is the previous CLOSED candle's close, which lags the live
        // price by up to candle-interval seconds. A stale LastPrice below all batch TPs
        // makes ReconcileMissedTpFills skip every batch (`crossed` returns false) and
        // emit a misleading "Возможно ручное частичное закрытие извне" warning, even
        // when the TP fill is happening in real time on the exchange. Fall back to
        // state.LastPrice only if the ticker call fails.
        decimal price = 0m;
        try
        {
            var ticker = await exchange.GetTickerPriceAsync(config.Symbol);
            if (ticker is not (null or <= 0)) price = ticker.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat reconcile: GetTickerPriceAsync failed for {Symbol}", config.Symbol);
        }
        if (price <= 0) price = state.LastPrice ?? 0m;

        if (delta < 0)
            await ReconcileMissedTpFills(strategy, config, state, exchange, exchangeQty, stateQty, price, ct);
        else
            await ReconcileMissedDcaFills(strategy, config, state, exchange, exchangeQty, stateQty, price, ct);
    }

    /// <summary>
    /// exchangeQty &lt; stateQty: some batches' TPs closed on the exchange but we missed
    /// the fill detection. Close batches in TP-cross-by-price order.
    /// </summary>
    private async Task ReconcileMissedTpFills(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange,
        decimal exchangeQty, decimal stateQty, decimal price, CancellationToken ct)
    {
        var qtyDelta = stateQty - exchangeQty;
        var exchangeIsFlat = exchangeQty <= stateQty * 0.001m;

        Log(strategy, "Warning",
            $"🔎 RECONCILE TP: state qty={Math.Round(stateQty, 8)} vs exchange qty={Math.Round(exchangeQty, 8)} " +
            $"(дельта={Math.Round(qtyDelta, 8)}, цена={Math.Round(price, 8)}). " +
            (exchangeIsFlat
                ? "Биржа: позиции нет — закрываю все батчи (TP реально сработал на high бара)."
                : "Закрываю батчи, чьи TP пересечены ценой."));

        var sortedBatches = state.IsLong
            ? state.Batches.OrderBy(b => b.TpPrice).ToList()
            : state.Batches.OrderByDescending(b => b.TpPrice).ToList();

        foreach (var batch in sortedBatches)
        {
            if (qtyDelta <= stateQty * 0.001m) break;

            if (!exchangeIsFlat)
            {
                var crossed = price > 0 && (state.IsLong ? price >= batch.TpPrice : price <= batch.TpPrice);
                if (!crossed)
                {
                    Log(strategy, "Warning",
                        $"Reconcile TP: батч #{batch.LevelIdx} TP={Math.Round(batch.TpPrice, 8)} не пересечён ценой " +
                        $"{Math.Round(price, 8)} — частичное закрытие извне, пропускаю.");
                    continue;
                }
            }

            await RecordTpFill(strategy, config, state, exchange, batch, batch.TpPrice, batch.Qty, ct);
            qtyDelta -= batch.Qty;
        }

        if (qtyDelta > stateQty * 0.01m)
        {
            Log(strategy, "Warning",
                $"После reconcile TP остаток qtyDelta={Math.Round(qtyDelta, 8)} — не удалось найти подходящий пересечённый батч. " +
                "Возможно ручное частичное закрытие извне.");
        }
    }

    /// <summary>
    /// exchangeQty &gt; stateQty: one or more DCA limits filled on the exchange but
    /// PollDcaFills didn't catch it. The exchange is authoritative — a BUY limit
    /// orders only fill at-or-below their limit price, so if the exchange shows extra qty,
    /// SOMETHING did cross the limit at some point (even if our cached lastPrice = bar-close
    /// has since rebounded above the limit). Adopt DCAs in cross-by-price order without
    /// requiring a live price-cross check — for Long, the largest dca.Price fires first as
    /// price drops; for Short, mirror image.
    /// </summary>
    private async Task ReconcileMissedDcaFills(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange,
        decimal exchangeQty, decimal stateQty, decimal price, CancellationToken ct)
    {
        var qtyExcess = exchangeQty - stateQty;
        Log(strategy, "Warning",
            $"🔎 RECONCILE DCA: state qty={Math.Round(stateQty, 8)} vs exchange qty={Math.Round(exchangeQty, 8)} " +
            $"(биржа БОЛЬШЕ на {Math.Round(qtyExcess, 8)}, цена={Math.Round(price, 8)}). " +
            "Адаптирую DCA-уровни в порядке близости к якорю.");

        // Long: DCAs sit below anchor; fire when price ≤ dca.Price. The highest DCA prices
        // (closest to anchor) fire first as price drops.
        // Short: mirror image — DCAs sit above; lowest prices (closest to anchor) fire first.
        var sortedDcas = state.IsLong
            ? state.DcaOrders.OrderByDescending(d => d.Price).ToList()
            : state.DcaOrders.OrderBy(d => d.Price).ToList();

        foreach (var dca in sortedDcas)
        {
            if (qtyExcess <= Math.Max(stateQty, dca.Qty) * 0.001m) break;

            // No price-cross check here: the exchange has authoritatively confirmed a BUY
            // limit fill happened (excess qty > 0). lastPrice is the close of the most
            // recent bar and may already have rebounded above the limit since the fill
            // happened on a bar's low — checking against it produces false negatives. Trust
            // the exchange; adopt the most-likely DCA.
            //
            // Use min(dca.Qty, qtyExcess) — exchanges round limit-order qty to lot-size on
            // fill, so the actual filled qty may be slightly less than the placed qty (e.g.
            // we placed 2598.04 and the exchange filled 2500). Adopting more than the excess
            // would create a state qty > exchange qty, triggering a phantom TP reconcile
            // every tick after this one.
            var adoptQty = Math.Min(dca.Qty, qtyExcess);
            await AdoptDcaFill(strategy, config, state, exchange, dca, adoptQty, dca.Price, ct);
            state.DcaOrders.Remove(dca);
            qtyExcess -= adoptQty;
        }

        if (qtyExcess > stateQty * 0.01m)
        {
            Log(strategy, "Warning",
                $"После reconcile DCA остаток qtyExcess={Math.Round(qtyExcess, 8)} — нет больше DCA-уровней для адаптации. " +
                "Возможно ручное открытие извне или повреждение state.");
        }
    }

    private async Task OnFullClose(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        // No more batches → the whole position is flat. Cancel every DCA we know about so the
        // grid can be re-seeded around a fresh anchor next bar. (Belt: also clear residual
        // orders on the symbol in case something we didn't track is still live.)
        try { await exchange.CancelAllOrdersAsync(config.Symbol); } catch { }

        state.DcaOrders.Clear();
        state.AnchorPrice = 0;
        // Cooldown anchor: open on the first closed candle past this instant. Using UtcNow
        // ensures we always wait for the next bar regardless of whether the in-progress bar
        // has already been processed in this tick.
        state.OpenAfterTime = DateTime.UtcNow;
        state.PlacementCooldownUntil = null;

        Log(strategy, "Info",
            $"🏁 Полное закрытие сетки: realized={Math.Round(state.RealizedPnlDollar, 2)}USD, " +
            $"кулдаун до {state.OpenAfterTime.Value:HH:mm:ss} → новый якорь на следующей закрытой свече");
        await Task.CompletedTask;
    }

    // ────────────────────────── Heal ──────────────────────────

    private async Task HealMissingTps(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange)
    {
        // Snapshot — PlaceBatchTpLimit may drop sub-min legacy batches from state.Batches,
        // which would otherwise corrupt the foreach iterator (Fix #5 cleanup path).
        var snapshot = state.Batches.ToList();
        var placed = 0;
        foreach (var batch in snapshot)
        {
            if (string.IsNullOrEmpty(batch.TpOrderId))
            {
                if (placed > 0) await Task.Delay(InterOrderDelayMs);
                await PlaceBatchTpLimit(strategy, config, state, exchange, batch);
                placed++;
            }
        }
    }

    private async Task HealMissingDcas(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange)
    {
        if (state.PlacementCooldownUntil.HasValue && state.PlacementCooldownUntil.Value > DateTime.UtcNow)
            return;

        var occupiedLevels = new HashSet<int>();
        foreach (var b in state.Batches) occupiedLevels.Add(b.LevelIdx);
        foreach (var d in state.DcaOrders) occupiedLevels.Add(d.LevelIdx);

        var levels = ComputeDcaLevels(config, state);
        var placed = 0;
        foreach (var (idx, price, tier) in levels)
        {
            if (occupiedLevels.Contains(idx)) continue;
            if (placed > 0) await Task.Delay(InterOrderDelayMs);
            await PlaceDcaLimit(strategy, config, state, exchange, idx, price, tier);
            placed++;
        }
    }

    // ────────────────────────── Startup sync ──────────────────────────

    /// <summary>
    /// On first ProcessAsync after worker boot, reconcile state with exchange.
    ///
    /// Best-effort, prefers safety over reconstructing lost data:
    ///   - Exchange flat & state thinks we have batches → drop the stale state (it'd try to
    ///     poll dead order ids forever).
    ///   - Exchange has a position & state matches roughly (sum of batch qtys close enough) →
    ///     wipe stored order_ids on the symbol via CancelAllOrders so heal re-places fresh
    ///     TP/DCA limits this same tick (order_ids from before the restart are stale).
    ///   - Exchange has a position & state.Batches is empty → state was lost; we can't
    ///     reconstruct per-batch fill prices, so we just log loudly and let the user close it
    ///     manually. Don't auto-close (user explicitly said no SL).
    /// </summary>
    private async Task SyncFromExchangeOnStartup(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange, bool isLongConfig, CancellationToken ct)
    {
        PositionDto? pos;
        try { pos = await exchange.GetPositionAsync(config.Symbol, config.Direction); }
        catch (NotSupportedException)
        {
            Log(strategy, "Warning", "Биржа не поддерживает GetPositionAsync — пропуск restart-sync");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat restart-sync: GetPositionAsync failed for {Symbol}", config.Symbol);
            return;
        }

        var exchangeHasPosition = pos != null && pos.Quantity > 0;
        var stateHasBatches = state.Batches.Count > 0;

        if (!exchangeHasPosition && !stateHasBatches)
        {
            // Truly flat — drop any stale order tracking, normal flow seeds a fresh grid.
            try { await exchange.CancelAllOrdersAsync(config.Symbol); }
            catch (NotSupportedException) { }
            state.DcaOrders.Clear();
            state.AnchorPrice = 0;
            Log(strategy, "Info", "🔄 RESTART SYNC: позиции нет, state пуст — старт с чистого листа");
            return;
        }

        if (!exchangeHasPosition && stateHasBatches)
        {
            Log(strategy, "Warning",
                $"🔄 RESTART SYNC: state помнит {state.Batches.Count} батчей, но на бирже нет позиции — сбрасываю state");
            try { await exchange.CancelAllOrdersAsync(config.Symbol); }
            catch (NotSupportedException) { }
            state.Batches.Clear();
            state.DcaOrders.Clear();
            state.AnchorPrice = 0;
            state.OpenAfterTime = DateTime.UtcNow;
            return;
        }

        if (exchangeHasPosition && !stateHasBatches)
        {
            // State was lost (manual reset / DB rollback). Without per-fill prices we can't
            // rebuild batches with their TPs — refuse to touch the position and ask the user.
            Log(strategy, "Error",
                $"🚨 RESTART SYNC: на бирже открыта позиция (qty={pos!.Quantity} @ {pos.EntryPrice}), " +
                "но state пуст. Не могу восстановить батчи (нет цен отдельных филлов). " +
                "Закройте позицию вручную, затем перезапустите бота.");
            return;
        }

        // Both side say we're in position. Verify roughly that quantities line up (within 1%);
        // if not, surface a warning but proceed because per-batch tracking is the source of truth.
        var sumQty = state.Batches.Sum(b => b.Qty);
        var qtyDelta = sumQty > 0 ? Math.Abs(pos!.Quantity - sumQty) / sumQty : 1m;
        if (qtyDelta > 0.01m)
        {
            Log(strategy, "Warning",
                $"🔄 RESTART SYNC: state qty={sumQty} vs exchange qty={pos!.Quantity} — расхождение, продолжаю по state");
        }

        // Wipe stored order_ids on the symbol — they're stale across restart. Heal steps will
        // re-place fresh TPs for each batch and re-arm DCAs at empty levels.
        try { await exchange.CancelAllOrdersAsync(config.Symbol); }
        catch (NotSupportedException) { }
        foreach (var b in state.Batches) b.TpOrderId = null;
        state.DcaOrders.Clear();

        Log(strategy, "Info",
            $"🔄 RESTART SYNC: позиция жива (qty={pos!.Quantity}, anchor={state.AnchorPrice}, batches={state.Batches.Count}) — переставлю TP/DCA");
    }

    // ────────────────────────── Utilities ──────────────────────────

    /// <summary>
    /// Returns the list of (levelIdx, price, tier) for live DCA slots — slot 0 is the anchor
    /// and is excluded. Each tier walks INDEPENDENTLY with its own effective DCA step:
    ///
    ///   tier 1: offsets step₁, 2·step₁, …, up to UpTo₁
    ///   tier 2: offsets UpTo₁ + step₂, UpTo₁ + 2·step₂, …, up to UpTo₂
    ///   tier 3: offsets UpTo₂ + step₃, …, up to UpTo₃
    ///
    /// (step_n = tier_n.DcaStepPercent ?? config.DcaStepPercent.) So a misalignment between
    /// a tier's step and the previous tier's UpTo boundary "restarts" the walk from the
    /// boundary — placing the first level of tier N at UpTo_{N-1} + step_N regardless of
    /// where tier N-1's last level happened to land.
    ///
    /// Range modes:
    ///   - Dynamic: walk strictly inside each tier; stop at the outermost tier's UpToPercent.
    ///   - Static (Long): same plus the additional price-floor check against StaticLowerBound.
    ///   - Static (Short): mirror image with StaticUpperBound as ceiling.
    ///
    /// Returns the tier reference (not its SizeUsdt) so callers can apply Fix #5's
    /// pre-placement guard and bump the tier in-place if the exchange minimum demands it.
    /// </summary>
    private static List<(int idx, decimal price, GridFloatTier tier)> ComputeDcaLevels(
        GridFloatConfig config, GridFloatState state)
    {
        var list = new List<(int idx, decimal price, GridFloatTier tier)>();
        if (state.AnchorPrice <= 0 || config.Tiers.Count == 0) return list;

        // Safety cap on total levels — keeps a misconfigured static bound or absurdly small
        // tier step from generating thousands of orders.
        const int safetyCeiling = 500;

        int k = 0;
        decimal prevTopPct = 0m;

        foreach (var tier in config.Tiers)
        {
            var stepPct = EffectiveDcaStep(config, tier);
            if (stepPct <= 0) continue;

            // Strict-greater tolerance: 1e-9 lets the loop emit a level exactly at the tier
            // boundary (e.g. step=1%, UpTo=5% → last level at 5%) without floating-point
            // jitter dropping it.
            const decimal eps = 1e-9m;

            var offsetPct = prevTopPct + stepPct;
            while (offsetPct <= tier.UpToPercent + eps && k < safetyCeiling)
            {
                decimal price = state.IsLong
                    ? state.AnchorPrice * (1m - offsetPct / 100m)
                    : state.AnchorPrice * (1m + offsetPct / 100m);

                if (price <= 0) return list;

                if (config.UseStaticRange && state.StaticBoundsInitialized)
                {
                    if (state.IsLong && price < state.StaticLowerBound) return list;
                    if (!state.IsLong && price > state.StaticUpperBound) return list;
                }

                k++;
                list.Add((k, price, tier));
                offsetPct += stepPct;
            }

            prevTopPct = tier.UpToPercent;
            if (k >= safetyCeiling) break;
        }

        return list;
    }

    /// <summary>
    /// Effective DCA step% for a tier: the tier-level override if set, otherwise the global
    /// DcaStepPercent from the config root.
    /// </summary>
    private static decimal EffectiveDcaStep(GridFloatConfig config, GridFloatTier tier)
        => tier.DcaStepPercent is > 0 ? tier.DcaStepPercent.Value : config.DcaStepPercent;

    /// <summary>
    /// Effective TP step% for a fill at <paramref name="fillPrice"/> against the current
    /// anchor. The tier is the first tier whose UpToPercent ≥ |fillPrice - anchor| / anchor %;
    /// the tier's TpStepPercent override is used when set, otherwise config.TpStepPercent.
    /// Defaults to config.TpStepPercent when anchor is 0 or tiers are empty.
    /// </summary>
    private static decimal EffectiveTpStep(GridFloatConfig config, decimal anchorPrice, decimal fillPrice)
    {
        if (anchorPrice <= 0 || config.Tiers.Count == 0) return config.TpStepPercent;
        var offsetPct = Math.Abs(fillPrice - anchorPrice) / anchorPrice * 100m;
        var tier = config.Tiers.FirstOrDefault(t => offsetPct <= t.UpToPercent) ?? config.Tiers[^1];
        return tier.TpStepPercent is > 0 ? tier.TpStepPercent.Value : config.TpStepPercent;
    }

    /// <summary>
    /// Migrates legacy single-tier configs (BaseSizeUsdt + RangePercent) into the new Tiers
    /// list when Tiers is empty. Then sorts the list ascending by UpToPercent so tier lookup
    /// in ComputeDcaLevels can short-circuit on the first match. Idempotent.
    /// </summary>
    private static void NormalizeTiers(GridFloatConfig config)
    {
        if ((config.Tiers == null || config.Tiers.Count == 0)
            && config.BaseSizeUsdt is > 0
            && config.RangePercent is > 0)
        {
            config.Tiers = new List<GridFloatTier>
            {
                new() { UpToPercent = config.RangePercent.Value, SizeUsdt = config.BaseSizeUsdt.Value }
            };
        }

        config.Tiers ??= new List<GridFloatTier>();
        config.Tiers = config.Tiers
            .Where(t => t.UpToPercent > 0 && t.SizeUsdt > 0)
            .OrderBy(t => t.UpToPercent)
            .ToList();
    }

    private static decimal ComputeTp(decimal fillPrice, decimal tpPercent, bool isLong)
        => isLong
            ? fillPrice * (1m + tpPercent / 100m)
            : fillPrice * (1m - tpPercent / 100m);

    private static CandleDto? GetLastClosedCandle(List<CandleDto> candles)
    {
        var now = DateTime.UtcNow;
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            if (candles[i].CloseTime <= now) return candles[i];
        }
        return null;
    }

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

    private static void SaveState(Strategy strategy, GridFloatState state)
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

    // ────────────────────────── Pre-placement guard (Fix #5) ──────────────────────────

    /// <summary>
    /// Pre-placement guard: ensures the tier's USDT size is at or above the exchange's
    /// minimum-notional requirement for this symbol at the given price.
    ///
    ///   - If tier fits → return true, place normally.
    ///   - If tier &lt; minNotional ≤ 2 × tier → auto-bump tier.SizeUsdt to minNotional rounded
    ///     up to whole USDT, persist to ConfigJson, log INFO. Return true so the placement
    ///     proceeds with the bumped size.
    ///   - If minNotional &gt; 2 × tier → stop the bot (Status=Stopped) and log ERROR; the
    ///     gap is too wide for an auto-fix without surprising the user. Return false.
    ///
    /// Without this guard, partial fills / lot-size rounding on low-cost symbols (e.g.
    /// JCTUSDT, lot=100) can produce sub-minimum batches whose TPs cannot be placed and
    /// cause an infinite "Qty 0 &lt; min N" error loop in HealMissingTps.
    /// </summary>
    private async Task<bool> EnsureTierFitsExchangeMinimum(
        Strategy strategy, GridFloatConfig config,
        IFuturesExchangeService exchange, GridFloatTier tier, decimal price, int levelIdx)
    {
        if (price <= 0 || tier.SizeUsdt <= 0) return true;

        (decimal qtyStep, decimal minQty) info;
        try
        {
            info = await exchange.GetSymbolInfoAsync(config.Symbol);
        }
        catch (NotSupportedException) { return true; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat tier-guard: GetSymbolInfoAsync failed for {Symbol}", config.Symbol);
            return true; // fail-open — let the exchange itself reject if size is truly wrong
        }

        if (info.minQty <= 0) return true; // exchange didn't expose a minimum

        var minNotional = info.minQty * price;
        if (tier.SizeUsdt >= minNotional) return true; // tier already fits

        if (minNotional <= 2m * tier.SizeUsdt)
        {
            // Auto-bump within the user's tolerance: round up to whole USDT so the new size
            // is recognisable in the UI (e.g. $10 → $11, not $10.4087).
            var oldSize = tier.SizeUsdt;
            tier.SizeUsdt = Math.Ceiling(minNotional);
            PersistConfigJson(strategy, config);
            Log(strategy, "Info",
                $"⚙️ Автокоррекция тира #{levelIdx}: ${oldSize} → ${tier.SizeUsdt} " +
                $"(минимум биржи: {info.minQty} @ {Math.Round(price, 8)} = ${Math.Round(minNotional, 4)} USDT). " +
                $"Размер тира был меньше биржевого минимума, поднял автоматически.");
            return true;
        }

        // Gap is > 2× — refuse to silently spend more than 2× the user's intent.
        strategy.Status = StrategyStatus.Stopped;
        Log(strategy, "Error",
            $"🛑 Бот остановлен: для тира #{levelIdx} биржевой минимум ${Math.Round(minNotional, 4)} " +
            $"({info.minQty} @ {Math.Round(price, 8)}) превышает текущий размер тира ${tier.SizeUsdt} более чем в 2 раза. " +
            $"Увеличьте sizeUsdt в настройках бота вручную или выберите другой инструмент.");
        return false;
    }

    private static void PersistConfigJson(Strategy strategy, GridFloatConfig config)
    {
        var configDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(strategy.ConfigJson, JsonOptions)
                         ?? new Dictionary<string, JsonElement>();
        configDict["tiers"] = JsonSerializer.SerializeToElement(config.Tiers, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        // Clear any legacy fields so they cannot shadow the new tier list on subsequent loads.
        configDict.Remove("baseSizeUsdt");
        configDict.Remove("rangePercent");
        strategy.ConfigJson = JsonSerializer.Serialize(configDict);
    }
}
