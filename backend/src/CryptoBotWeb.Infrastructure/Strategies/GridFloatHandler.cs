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

    // Fix #9: how long state > exchange must persist (without any batch TP getting crossed by
    // price) before we treat the smallest-matching batch as a phantom and drop it from state.
    // 30 min covers transient API glitches, partial fills mid-reconcile, and slow manual UI
    // close sequences while still bounding the warning storm.
    private const int PhantomBatchStaleMinutes = 30;

    // Small delay between sequential REST placements to stay inside per-key rate limits
    // (Bybit ~10/sec on linear post-order, Bitget ~10/sec, BingX ~5/sec). 75ms between calls
    // caps the burst at ~13 req/sec which fits every exchange we support. Applied in
    // PlaceInitialDcaLadder, HealMissingDcas and HealMissingTps — the three sites that can
    // legitimately fire dozens of orders back-to-back (initial ladder on a fresh anchor,
    // re-arm after restart, or TP heal after restart).
    private const int InterOrderDelayMs = 75;

    // While the account's private order stream is healthy, fills are detected from the stream and
    // the position-based reconcile drops from every tick to this cadence. It stays as the backstop
    // for anything the stream missed; with the stream down it runs every tick as before.
    private static readonly TimeSpan ReconcileIntervalWithStream = TimeSpan.FromSeconds(60);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _lastReconcileUtc = new();

    // A websocket quote older than this is not used as the live price — REST ticker instead.
    private const double MaxQuoteAgeMs = 5000;

    // Market orders (anchor, forced close) are read back from the exchange for the real average
    // price and fee; a just-sent order may not be settled on the first read.
    private const int FillReadBackAttempts = 3;
    private const int FillReadBackDelayMs = 400;

    public string StrategyType => StrategyTypes.GridFloat;

    private readonly AppDbContext _db;
    private readonly ILogger<GridFloatHandler> _logger;
    private readonly ITelegramSignalService _telegramSignalService;
    private readonly IOrderStreamService _orderStream;
    private readonly IQuoteStreamService _quotes;

    // Set at the top of every ProcessAsync (the handler is scoped per strategy tick): the real
    // config with the full stake-ladder tier list, and the stage the grid view was built for.
    private GridFloatConfig? _fullConfig;
    private int _stakeStage;

    public GridFloatHandler(AppDbContext db, ILogger<GridFloatHandler> logger,
        ITelegramSignalService telegramSignalService, IOrderStreamService orderStream,
        IQuoteStreamService quotes)
    {
        _db = db;
        _logger = logger;
        _telegramSignalService = telegramSignalService;
        _orderStream = orderStream;
        _quotes = quotes;
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

        // Stake-ladder mode: everything below builds the grid from the current stage's VIEW of
        // the config (one tier = T1 geometry + this stage's stake). `fullConfig` keeps the real
        // tier list — the stake steps — for stage transitions and for anything persisted.
        var fullConfig = config;
        _fullConfig = fullConfig;
        _stakeStage = state.StakeStage;
        config = GridFloatStakeLadder.ForStage(fullConfig, state.StakeStage);

        // Websocket streams: never block, never throw — until they are up everything below
        // falls back to REST exactly as before.
        if (strategy.Account != null)
        {
            await _orderStream.EnsureSubscribedAsync(strategy.Account, ct);
            if (config.TakeProfitEnabled || fullConfig.RestartOnRangeExit)
                await _quotes.EnsureSubscribedAsync(strategy.Account, config.Symbol, ct);
        }

        // 1. Restart re-sync (once per worker boot).
        if (!state.StateInitialized)
        {
            await SyncFromExchangeOnStartup(strategy, config, state, exchange, isLongConfig, ct);
            state.StateInitialized = true;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 1.5. Orphan-cancel retry. While the flag is live and state is flat, keep sweeping any
        // limit orders the exchange still has for this symbol. Single-shot CancelAllOrdersAsync
        // in OnFullClose is best-effort (catches all exceptions silently); without this retry, a
        // single failed cancel (timestamp drift, network blip, rate limit) leaves DCA buy limits
        // resting → one fires below market → dust position appears that state can't track.
        if (state.OrphanCancelPendingUntil.HasValue
            && state.Batches.Count == 0 && state.DcaOrders.Count == 0)
        {
            await SweepOrphanOrders(strategy, config, state, exchange);
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
        if ((state.Batches.Count > 0 || state.DcaOrders.Count > 0) && ReconcileDue(strategy))
        {
            await ReconcileBatchesFromPosition(strategy, config, state, exchange, ct);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 3c'. Series take-profit that keeps trading (stake ladder, or TakeProfitContinue) and the
        // stake ladder's losing-side range exit. Both close everything at market; the tick ends
        // there and the next anchor waits for a closed bar.
        if (fullConfig.RestartOnRangeExit || fullConfig.TakeProfitContinue)
        {
            if (state.AnchorPrice > 0 && state.Batches.Count > 0
                && await HandleStakeLadderAsync(strategy, fullConfig, config, state, exchange, isLongConfig, ct))
            {
                SaveState(strategy, state);
                await _db.SaveChangesAsync(ct);
                return;
            }
        }
        // 3c. Take-profit check — cumulative realized + mark-to-market unrealized.
        // Triggers force-close and stops the bot. Cheap when disabled (single bool check);
        // when enabled and TP is satisfied the ticker fetch is the only extra exchange call
        // per tick beyond what was already in flight.
        else if (config.TakeProfitEnabled && config.TakeProfitTargetUsd > 0m)
        {
            decimal? markN = null;
            try { markN = await GetLivePriceAsync(strategy, config.Symbol, exchange); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridFloat TP check: GetTickerPriceAsync failed for {Symbol}", config.Symbol);
            }
            if (markN is > 0m)
            {
                var mark = markN.Value;
                var unrealized = 0m;
                foreach (var batch in state.Batches)
                {
                    unrealized += isLongConfig
                        ? batch.Qty * (mark - batch.FillPrice)
                        : batch.Qty * (batch.FillPrice - mark);
                }
                var totalPnl = state.RealizedPnlDollar + unrealized;
                if (totalPnl >= config.TakeProfitTargetUsd)
                {
                    await ForceCloseOnTakeProfitAsync(strategy, config, state, exchange,
                        isLongConfig, mark, totalPnl, unrealized);
                    SaveState(strategy, state);
                    await _db.SaveChangesAsync(ct);
                    return;
                }
            }
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

        // 9. Open new anchor if we're flat. Also gated on OrphanCancelPendingUntil — if a
        // previous close left limit orders still resting on the exchange we MUST clear them
        // before placing a new anchor, otherwise a stale DCA could fire moments later and add
        // to the fresh anchor's qty as untracked dust.
        if (state.Batches.Count == 0 && state.DcaOrders.Count == 0
            && !state.OpenAfterTime.HasValue
            && !state.OrphanCancelPendingUntil.HasValue
            && !AnchorBlockedByPriceLimit(strategy, fullConfig, state, lastClosed.Close, isLongConfig))
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

        // The service's FilledPrice is only the ticker it sized against — read the order back
        // for the real average price and the fee actually charged (taker).
        var fill = await ReadBackMarketFillAsync(strategy, config.Symbol, exchange, result.OrderId, "ANCHOR", ct);
        var fillQty = fill?.Qty ?? result.FilledQuantity.Value;
        var fillPrice = fill?.Price ?? result.FilledPrice ?? candle.Close;
        var entryFee = fill?.Fee ?? fillPrice * fillQty * exchange.TakerFeeRate;

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
            EntryFee = entryFee,
        };
        state.Batches.Add(batch);

        RecordTrade(strategy, config.Symbol, isLong ? "Buy" : "Sell", fillQty, fillPrice, result.OrderId, "Entry");

        Log(strategy, "Info",
            $"✅ ANCHOR open: qty={fillQty} @ {fillPrice}, комиссия {Math.Round(entryFee, 6)}, batch TP={Math.Round(tpPrice, 8)}");

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
            var placedAt = DateTime.UtcNow;
            var result = await exchange.PlaceLimitOrderAsync(config.Symbol, side, price, qty, reduceOnly: false);
            if (result.Success && !string.IsNullOrEmpty(result.OrderId))
            {
                SeedPlaced(strategy, result.OrderId, placedAt);
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
        //
        // Fix #6 (prevention): also floor batch.Qty to the symbol's lot step before placement,
        // so the reduce-only limit qty matches what the exchange can actually fill exactly.
        // Without this, a batch.Qty=3042.685 placed as a TP on Bybit JCTUSDT can fill 3000
        // (rounded) and leave 42.685 dust untracked on the exchange — which then triggers a
        // chronic ReconcileMissedDcaFills warning storm because state.DcaOrders is empty and
        // the orphan can never be adopted. By flooring at placement time we keep state and
        // exchange aligned per-batch.
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

            if (qtyStep > 0)
            {
                var flooredQty = Math.Floor(batch.Qty / qtyStep) * qtyStep;
                if (flooredQty < batch.Qty && flooredQty >= minQty)
                {
                    var dust = batch.Qty - flooredQty;
                    Log(strategy, "Info",
                        $"⚙️ Fix #6: округляю TP qty батча #{batch.LevelIdx} вниз до lot-step: " +
                        $"{batch.Qty} → {flooredQty} (lot-step={qtyStep}, dust={dust} списан). " +
                        $"Предотвращает orphan на бирже.");
                    batch.Qty = flooredQty;
                }
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
            var placedAt = DateTime.UtcNow;
            var result = await exchange.PlaceLimitOrderAsync(config.Symbol, closeSide, batch.TpPrice, batch.Qty, reduceOnly: true);
            if (result.Success && !string.IsNullOrEmpty(result.OrderId))
            {
                SeedPlaced(strategy, result.OrderId, placedAt);
                batch.TpOrderId = result.OrderId;
                // Fix #10: recovered batch — clear backoff so it's no longer skipped by HealMissingTps.
                batch.TpFailCount = 0;
                batch.TpRetryAfter = null;
                Log(strategy, "Info",
                    $"🎯 TP батча #{batch.LevelIdx}: {closeSide} {batch.Qty} @ {Math.Round(batch.TpPrice, 8)} (id={result.OrderId})");
                return;
            }

            batch.TpOrderId = null;
            ApplyTpBackoff(batch);
            Log(strategy, "Error",
                $"TP батча #{batch.LevelIdx} не выставлен: {result.ErrorMessage} " +
                $"(Fix #10 backoff: попытка #{batch.TpFailCount}, повтор после {batch.TpRetryAfter:HH:mm:ss})");
            _logger.LogError("Strategy {Id}: TP batch #{Lvl} placement failed: {Error}",
                strategy.Id, batch.LevelIdx, result.ErrorMessage);
        }
        catch (NotSupportedException)
        {
            batch.TpOrderId = null;
            ApplyTpBackoff(batch);
            Log(strategy, "Warning",
                "Биржа не поддерживает PlaceLimitOrderAsync — TP не выставится");
        }
        catch (Exception ex)
        {
            batch.TpOrderId = null;
            ApplyTpBackoff(batch);
            _logger.LogError(ex, "Strategy {Id}: TP batch #{Lvl} placement threw", strategy.Id, batch.LevelIdx);
            Log(strategy, "Error",
                $"TP батча #{batch.LevelIdx} исключение: {ex.Message} " +
                $"(Fix #10 backoff: попытка #{batch.TpFailCount}, повтор после {batch.TpRetryAfter:HH:mm:ss})");
        }
    }

    // Fix #10: exponential per-batch TP backoff, capped at 30 minutes. After the Nth consecutive
    // failure the next retry is delayed by min(2^(N-1), 30) minutes. HealMissingTps honours
    // batch.TpRetryAfter so a phantom batch can no longer hammer the exchange every 5 seconds with
    // "orderQty will be truncated to zero". Reset on the first successful placement.
    private static void ApplyTpBackoff(GridFloatBatch batch)
    {
        batch.TpFailCount++;
        var minutes = Math.Min(Math.Pow(2, batch.TpFailCount - 1), 30);
        batch.TpRetryAfter = DateTime.UtcNow.AddMinutes(minutes);
    }

    // ────────────────────────── Fill detection ──────────────────────────

    /// <summary>
    /// Order status from the account's websocket cache when it can be trusted, else REST (whose
    /// answer is fed back into the cache so the next tick is served without a request). Same
    /// contract as <see cref="IFuturesExchangeService.GetOrderAsync"/>: null = not found, throws
    /// on a failed lookup.
    /// </summary>
    private async Task<OrderStatusDto?> GetOrderStatusAsync(Strategy strategy, string symbol,
        IFuturesExchangeService exchange, string orderId)
    {
        if (_orderStream.TryGetTrusted(strategy.AccountId, orderId, out var cached))
            return cached;

        // Stamp BEFORE the request: a change racing it must not be covered by this answer.
        var askedAt = DateTime.UtcNow;
        var status = await exchange.GetOrderAsync(symbol, orderId);
        if (status != null)
        {
            if (string.IsNullOrEmpty(status.OrderId)) status.OrderId = orderId;
            _orderStream.Seed(strategy.AccountId, status, askedAt);
        }
        return status;
    }

    /// <summary>
    /// Reads a just-sent market order back: average fill price, filled quantity and the fee the
    /// exchange charged (null when it doesn't report one). The service's own FilledPrice is only
    /// the ticker it sized against, and booking it with a fee-rate estimate made the recorded
    /// PnL differ from the exchange's. Null when the order can't be read back — the caller then
    /// falls back to the estimate and the strategy log says so.
    /// </summary>
    private async Task<(decimal Price, decimal Qty, decimal? Fee)?> ReadBackMarketFillAsync(Strategy strategy,
        string symbol, IFuturesExchangeService exchange, string? orderId, string what, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(orderId)) return null;

        string? failure = null;
        for (var attempt = 1; attempt <= FillReadBackAttempts; attempt++)
        {
            try
            {
                var order = await GetOrderStatusAsync(strategy, symbol, exchange, orderId);
                // A market order that stopped part-way ends Cancelled with what it filled.
                var settled = order?.Status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled
                    or OrderLifecycleStatus.Rejected;
                if (order != null && order.FilledQuantity > 0 && order.AverageFilledPrice > 0 &&
                    (settled || attempt == FillReadBackAttempts))
                    return (order.AverageFilledPrice, order.FilledQuantity, order.Fee);

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

            if (attempt < FillReadBackAttempts) await Task.Delay(FillReadBackDelayMs, ct);
        }

        Log(strategy, "Warning",
            $"{what}: не удалось прочитать ордер {orderId} с биржи ({failure}) — цена и комиссия взяты по оценке.");
        _logger.LogWarning("GridFloat {Id}: fill read-back failed for {Symbol} order {OrderId}: {Reason}",
            strategy.Id, symbol, orderId, failure);
        return null;
    }

    /// <summary>
    /// The batch's unbooked entry fee: what the exchange reported, else an estimate — taker for
    /// the market anchor (level 0), maker for DCA limits and other adopted fills.
    /// </summary>
    private static decimal BatchEntryFee(GridFloatBatch batch, IFuturesExchangeService exchange)
        => batch.EntryFee
           ?? batch.FillPrice * batch.Qty * (batch.LevelIdx == 0 ? exchange.TakerFeeRate : exchange.MakerFeeRate);

    /// <summary>A just-placed order is Open as of the moment we sent it; the stream takes it from there.</summary>
    private void SeedPlaced(Strategy strategy, string orderId, DateTime placedAtUtc)
        => _orderStream.Seed(strategy.AccountId,
            new OrderStatusDto { OrderId = orderId, Status = OrderLifecycleStatus.Open }, placedAtUtc);

    /// <summary>
    /// Every tick while the order stream is down (the old behaviour); once per
    /// <see cref="ReconcileIntervalWithStream"/> while it is healthy.
    /// </summary>
    private bool ReconcileDue(Strategy strategy)
    {
        var now = DateTime.UtcNow;
        if (_orderStream.IsHealthy(strategy.AccountId)
            && _lastReconcileUtc.TryGetValue(strategy.Id, out var last)
            && now - last < ReconcileIntervalWithStream)
            return false;

        _lastReconcileUtc[strategy.Id] = now;
        return true;
    }

    /// <summary>Mid of a fresh websocket quote when one is streaming, else the REST ticker.</summary>
    private async Task<decimal?> GetLivePriceAsync(Strategy strategy, string symbol, IFuturesExchangeService exchange)
    {
        if (strategy.Account != null)
        {
            var quote = _quotes.TryGetQuote(strategy.Account.ExchangeType, symbol);
            if (quote is { IsValid: true } && quote.AgeMs(DateTime.UtcNow) <= MaxQuoteAgeMs)
                return (quote.Bid + quote.Ask) / 2m;
        }
        return await exchange.GetTickerPriceAsync(symbol);
    }

    private async Task PollDcaFills(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        // Snapshot — we mutate state.DcaOrders inside the loop.
        var pending = state.DcaOrders.ToList();
        foreach (var dca in pending)
        {
            OrderStatusDto? status;
            try { status = await GetOrderStatusAsync(strategy, config.Symbol, exchange, dca.OrderId); }
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
                    await AdoptDcaFill(strategy, config, state, exchange, dca, fillQty, fillPrice, status.Fee, ct);
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
                        await AdoptDcaFill(strategy, config, state, exchange, dca, status.FilledQuantity, fillPrice, status.Fee, ct);
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
        decimal? entryFee, CancellationToken ct)
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
            // Reported by the order (REST/stream); null → maker estimate when the batch closes.
            EntryFee = entryFee,
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
            try { status = await GetOrderStatusAsync(strategy, config.Symbol, exchange, batch.TpOrderId!); }
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
                        status.FilledQuantity, status.Fee, ct);
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
                            status.FilledQuantity, status.Fee, ct);
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
        decimal? exitFee, CancellationToken ct)
    {
        var notional = batch.FillPrice * closeQty;
        var pnlPct = state.IsLong
            ? (closePrice - batch.FillPrice) / batch.FillPrice * 100m
            : (batch.FillPrice - closePrice) / batch.FillPrice * 100m;
        var grossPnl = notional * pnlPct / 100m;
        // Both legs at what the exchange charged: the batch's own entry fee (pro rata to the
        // closed qty) plus the TP order's fee; maker estimate on exit when the order didn't report.
        var batchEntryFee = BatchEntryFee(batch, exchange);
        var entryFeeShare = batch.Qty > 0m ? batchEntryFee * Math.Min(1m, closeQty / batch.Qty) : batchEntryFee;
        var commission = entryFeeShare + (exitFee ?? closePrice * closeQty * exchange.MakerFeeRate);
        var netPnl = grossPnl - commission;

        RecordTrade(strategy, config.Symbol, state.IsLong ? "Sell" : "Buy", closeQty, closePrice,
            batch.TpOrderId, $"TakeProfit#{batch.LevelIdx}", pnlDollar: netPnl, commission: commission);

        state.RealizedPnlDollar += netPnl;
        state.SeriesRealizedPnl += netPnl;

        Log(strategy, "Info",
            $"💰 TP батча #{batch.LevelIdx} филлд: qty={closeQty} @ {closePrice}, " +
            $"вход={batch.FillPrice}, PnL={Math.Round(pnlPct, 4)}% (${Math.Round(netPnl, 2)})");
        _logger.LogInformation(
            "Strategy {Id}: GridFloat TP batch#{Lvl} {Dir} qty={Qty} @ {Price} netPnL={Pnl}",
            strategy.Id, batch.LevelIdx, config.Direction, closeQty, closePrice, netPnl);

        // Fix #6 (defense in depth): handle partial-TP-fill where exchange returned
        // Status=Filled but FilledQuantity < batch.Qty. Without this branch the batch is
        // dropped from state while a residual qty sits on the exchange — producing the
        // chronic qtyExcess loop documented in iter-50/53. Two sub-cases:
        //
        //   residual ≥ minQty → keep the batch open at the reduced qty, clear TpOrderId so
        //     HealMissingTps re-places a fresh TP on the next tick. The batch will close
        //     normally when the new TP fills.
        //   residual < minQty → can't be re-sold, so just write it off (log + remove batch).
        //     Exchange dust remains but Fix #6's reconcile-DCA dedupe (see ReconcileMissedDcaFills)
        //     will not flood the log.
        var residual = batch.Qty - closeQty;
        if (residual > 0)
        {
            decimal partialMinQty = 0m;
            try
            {
                (_, partialMinQty) = await exchange.GetSymbolInfoAsync(config.Symbol);
            }
            catch (NotSupportedException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridFloat Fix #6 partial-TP: GetSymbolInfoAsync failed for {Symbol}", config.Symbol);
            }

            if (partialMinQty > 0 && residual >= partialMinQty)
            {
                batch.EntryFee = batchEntryFee - entryFeeShare;
                batch.Qty = residual;
                batch.TpOrderId = null;
                Log(strategy, "Warning",
                    $"⚠️ Fix #6 partial TP батча #{batch.LevelIdx}: закрылось {closeQty} из {closeQty + residual}, " +
                    $"residual {residual} ≥ min {partialMinQty} — оставляю батч, TP переустановится.");
                await _telegramSignalService.SendPositionClosedSignalAsync(
                    strategy, config.Symbol, config.Direction, netPnl, pnlPct, ct);
                return;
            }

            Log(strategy, "Info",
                $"⚙️ Fix #6 partial TP батча #{batch.LevelIdx}: dust residual={residual} < min={partialMinQty} — " +
                $"списываю и удаляю батч (untracked dust может остаться на бирже).");
        }

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
            var ticker = await GetLivePriceAsync(strategy, config.Symbol, exchange);
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

        // Fix #8: false-flat sanity check. ReconcileBatchesFromPosition's two-probe gate filters
        // most transient empties, but it can't distinguish "real flat from a high-bar TP cross"
        // from "exchange returned 0 for both probes due to a real API glitch (rate-limit empty,
        // V5 history snapshot mid-write, etc.)". The decisive signal is whether any batch TP
        // has been crossed by the current price: if exchange says flat but NO batch TP is on
        // the right side of price, the "flat" reading is almost certainly bogus — closing all
        // batches at their TpPrice would force-realize fictional PnL and leak the real position
        // as an untracked orphan (the exact FFUSDT 2026-05-21 cascade). Refuse to act; the next
        // tick's reconcile probe will re-evaluate. Also refuse if price ≤ 0 — without a real
        // price we can't tell legit flat from glitch.
        if (exchangeIsFlat)
        {
            if (price <= 0)
            {
                Log(strategy, "Warning",
                    $"⚠️ Fix #8: биржа сообщает flat (qty={Math.Round(exchangeQty, 8)} vs state={Math.Round(stateQty, 8)}), " +
                    $"но цена недоступна (ticker=0) — отказываюсь закрывать батчи без подтверждения. " +
                    "Повторная проверка на следующем тике.");
                return;
            }

            var anyCrossed = state.Batches.Any(b => state.IsLong ? b.TpPrice <= price : b.TpPrice >= price);
            if (!anyCrossed)
            {
                var minTp = state.Batches.Count > 0 ? state.Batches.Min(b => b.TpPrice) : 0m;
                var maxTp = state.Batches.Count > 0 ? state.Batches.Max(b => b.TpPrice) : 0m;
                Log(strategy, "Warning",
                    $"⚠️ Fix #8: подозрительный flat биржи (qty={Math.Round(exchangeQty, 8)} vs state={Math.Round(stateQty, 8)}) " +
                    $"при цене {Math.Round(price, 8)} — ни один TP не пересечён " +
                    $"(диапазон TP: {Math.Round(minTp, 8)}…{Math.Round(maxTp, 8)}, направление={(state.IsLong ? "Long" : "Short")}). " +
                    "Скорее всего транзитный сбой API; не закрываю батчи. Повторная проверка на следующем тике.");
                return;
            }
        }

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

            await RecordTpFill(strategy, config, state, exchange, batch, batch.TpPrice, batch.Qty, exitFee: null, ct);
            qtyDelta -= batch.Qty;
        }

        // Fix #9: if the foreach successfully accounted for the discrepancy (any batch got
        // closed and qtyDelta is now inside the noise floor), the previously-tracked "phantom"
        // window is moot — clear it so a future independent anomaly starts a fresh 30-min
        // ripening clock.
        if (qtyDelta <= stateQty * 0.01m && state.PhantomNegativeDeltaSince.HasValue)
        {
            state.PhantomNegativeDeltaSince = null;
        }

        if (qtyDelta > stateQty * 0.01m)
        {
            // NOTE (Fix #10): the position-headroom guard in HealMissingTps is now the PRIMARY
            // mechanism for dropping phantom batches (deterministic, driven by the live
            // GetPositionAsync reading, ≥2 confirmations). This time-based Fix #9 path is kept as
            // a harmless fallback for the reconcile flow; it may fire redundantly.
            //
            // Fix #9: time-based phantom-batch drop. The "Возможно ручное частичное закрытие
            // извне" path fires whenever state > exchange and no batch TP was on the right side
            // of price (so the foreach above couldn't close anything). On the FIRST observation
            // we just start a clock. If the same negative-delta condition persists for ≥
            // PhantomBatchStaleMinutes (without ANY batch TP getting crossed and absorbing the
            // delta in the meantime), we conclude that one of our batches doesn't actually
            // exist on the exchange — most likely residue from a Fix #8-era false-flat reconcile
            // that closed everything, a manual UI partial close, or an exchange-side liquidation
            // we missed. We pick the batch whose Qty is closest to |qtyDelta| and drop it,
            // cancelling its TP order best-effort. Without this, phantom batches linger forever
            // and chronically fail TP re-placement with "orderQty will be truncated to zero" on
            // Bybit (the auto-reduce-only cancellation cascades from sum(reduce-only Sells) >
            // actual position).
            if (state.PhantomNegativeDeltaSince == null)
            {
                state.PhantomNegativeDeltaSince = DateTime.UtcNow;
                Log(strategy, "Warning",
                    $"После reconcile TP остаток qtyDelta={Math.Round(qtyDelta, 8)} — не удалось найти подходящий пересечённый батч. " +
                    $"Возможно ручное частичное закрытие извне. Запускаю окно созревания Fix #9 ({PhantomBatchStaleMinutes} мин); " +
                    "если расхождение сохранится без пересечений TP — удалю фантомный батч.");
            }
            else
            {
                var age = DateTime.UtcNow - state.PhantomNegativeDeltaSince.Value;
                if (age >= TimeSpan.FromMinutes(PhantomBatchStaleMinutes) && state.Batches.Count > 0)
                {
                    // Pick the batch whose qty most closely matches the missing amount —
                    // typically the same batch that's been failing TP re-placement.
                    var phantom = state.Batches.OrderBy(b => Math.Abs(b.Qty - qtyDelta)).First();
                    var minTp = state.Batches.Min(b => b.TpPrice);
                    var maxTp = state.Batches.Max(b => b.TpPrice);

                    Log(strategy, "Warning",
                        $"🧹 Fix #9: удаляю фантомный батч #{phantom.LevelIdx} (qty={phantom.Qty} ≈ |delta|={Math.Round(qtyDelta, 8)}, " +
                        $"TP={Math.Round(phantom.TpPrice, 8)}) после {(int)age.TotalMinutes} мин стабильного расхождения " +
                        $"state > exchange без пересечения TP (цена={Math.Round(price, 8)}, диапазон TP батчей: " +
                        $"{Math.Round(minTp, 8)}…{Math.Round(maxTp, 8)}, направление={(state.IsLong ? "Long" : "Short")}). " +
                        "Этой qty нет на бирже — PnL не реализуется. Скорее всего легаси из false-flat reconcile " +
                        "или ручного закрытия через UI.");

                    // Best-effort cancel of the orphaned reduce-only TP. If the exchange already
                    // auto-cancelled it (e.g. Bybit's "qty truncated to zero" auto-reject) the
                    // call will return an error — swallow it; the state cleanup below is what
                    // matters.
                    if (!string.IsNullOrEmpty(phantom.TpOrderId))
                    {
                        try { await exchange.CancelOrderAsync(config.Symbol, phantom.TpOrderId!); }
                        catch (NotSupportedException) { }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "GridFloat Fix #9: CancelOrderAsync for phantom TP {OrderId} failed (likely already cancelled)",
                                phantom.TpOrderId);
                        }
                    }

                    state.Batches.Remove(phantom);
                    state.PhantomNegativeDeltaSince = null;
                }
                else
                {
                    Log(strategy, "Warning",
                        $"После reconcile TP остаток qtyDelta={Math.Round(qtyDelta, 8)} — расхождение сохраняется " +
                        $"{(int)age.TotalMinutes}/{PhantomBatchStaleMinutes} мин (Fix #9 созревает). " +
                        "Возможно ручное частичное закрытие извне.");
                }
            }
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

        // Fix #9 (cleanup symmetric path): exchange > state means the previous negative-delta
        // scenario tracked by PhantomNegativeDeltaSince is no longer active — clear the clock so
        // any future state > exchange anomaly starts a fresh ripening window.
        state.PhantomNegativeDeltaSince = null;

        // Fix #6 (dedupe): when state.DcaOrders is empty (nothing to adopt the orphan into)
        // AND the qtyExcess value hasn't moved more than 0.1% since the last log AND less than
        // 30 minutes have passed, suppress both this entry log and the trailing "остаток"
        // warning. Without this, a stuck orphan (e.g. residual from a Bybit partial TP fill
        // that left dust on the exchange) generates ~600 warnings/hour for hours on end.
        //
        // The state field gets cleared the moment qtyExcess changes value (someone placed/
        // filled/cancelled an order, or the orphan resolved itself), so a fresh anomaly
        // always gets logged once. Re-logs every 30 min as a heartbeat.
        var canAdopt = state.DcaOrders.Count > 0;
        if (!canAdopt && state.LastReconcileOrphanQty.HasValue && state.LastReconcileOrphanLoggedAt.HasValue)
        {
            var lastQty = state.LastReconcileOrphanQty.Value;
            var deltaPct = lastQty > 0 ? Math.Abs(qtyExcess - lastQty) / lastQty : 1m;
            var ageMin = (DateTime.UtcNow - state.LastReconcileOrphanLoggedAt.Value).TotalMinutes;
            if (deltaPct < 0.001m && ageMin < 30) return;
        }

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
            await AdoptDcaFill(strategy, config, state, exchange, dca, adoptQty, dca.Price, entryFee: null, ct);
            state.DcaOrders.Remove(dca);
            qtyExcess -= adoptQty;
        }

        if (qtyExcess > stateQty * 0.01m)
        {
            // Fix #7: orphan can't be absorbed into any existing DCA slot (grid fully filled
            // or DCAs already adopted in the loop above) but the residue is big enough to
            // trade. Without this branch the bot loops forever in the heartbeat warning storm
            // documented in Fix #6's dedupe — the orphan never resolves on its own because
            // there's no slot to attach it to. Synthesize an "orphan-absorption" batch at the
            // current ticker price + standard TP step so the dust gets tracked and exits for
            // +tpStep% PnL on the next upcross. Sub-lot-step residue stays as untracked
            // exchange dust (≤ 1 lot, negligible). Uses LevelIdx=-1 as a sentinel so it never
            // collides with grid levels in HealMissingDcas' occupiedLevels set.
            //
            // Using price (current ticker) as the synthetic fillPrice is intentionally
            // conservative — the true fill price is unknowable (that's why it's an orphan).
            // The synthetic batch will realize only +tpStep% PnL when its TP fills, which is
            // a small but positive amount; refusing to absorb leaves the qty stuck on the
            // exchange forever.
            var absorbed = false;
            if (state.AnchorPrice > 0 && price > 0)
            {
                decimal qtyStep = 0m, minQty = 0m;
                try { (qtyStep, minQty) = await exchange.GetSymbolInfoAsync(config.Symbol); }
                catch (NotSupportedException) { /* exchange doesn't expose; fall through to warn */ }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GridFloat Fix #7: GetSymbolInfoAsync failed for {Symbol}", config.Symbol);
                }

                var absorbQty = qtyStep > 0 ? Math.Floor(qtyExcess / qtyStep) * qtyStep : qtyExcess;
                if (minQty > 0 && absorbQty >= minQty)
                {
                    var tpStep = EffectiveTpStep(config, state.AnchorPrice, price);
                    var tpPrice = ComputeTp(price, tpStep, state.IsLong);
                    var synth = new GridFloatBatch
                    {
                        LevelIdx = -1,
                        FillPrice = price,
                        Qty = absorbQty,
                        TpPrice = tpPrice,
                        FilledAt = DateTime.UtcNow,
                    };
                    state.Batches.Add(synth);

                    var dust = qtyExcess - absorbQty;
                    Log(strategy, "Warning",
                        $"🩹 Fix #7 orphan-absorption: создан synth-батч qty={absorbQty} @ {Math.Round(price, 8)} → " +
                        $"TP={Math.Round(tpPrice, 8)} ({tpStep}% step). " +
                        (dust > 0 ? $"Sub-lot dust={Math.Round(dust, 8)} списан." : "Полное поглощение."));

                    RecordTrade(strategy, config.Symbol, state.IsLong ? "Buy" : "Sell", absorbQty, price,
                        null, "OrphanAbsorb");

                    await PlaceBatchTpLimit(strategy, config, state, exchange, synth);
                    absorbed = true;
                }
            }

            if (absorbed)
            {
                state.LastReconcileOrphanQty = null;
                state.LastReconcileOrphanLoggedAt = null;
            }
            else
            {
                Log(strategy, "Warning",
                    $"После reconcile DCA остаток qtyExcess={Math.Round(qtyExcess, 8)} — нет больше DCA-уровней для адаптации. " +
                    "Возможно ручное открытие извне или повреждение state.");

                // Fix #6 (dedupe): remember this orphan qty + timestamp so subsequent identical
                // observations get suppressed by the gate at the top of this method.
                state.LastReconcileOrphanQty = qtyExcess;
                state.LastReconcileOrphanLoggedAt = DateTime.UtcNow;
            }
        }
        else
        {
            // Orphan resolved (either fully adopted or dropped below 1% noise floor) —
            // clear the dedupe state so any new anomaly logs immediately.
            state.LastReconcileOrphanQty = null;
            state.LastReconcileOrphanLoggedAt = null;
        }
    }

    /// <summary>
    /// Verify the exchange's open-orders list for the symbol is empty, re-cancelling if
    /// anything still rests on the book. Driven by <see cref="GridFloatState.OrphanCancelPendingUntil"/>;
    /// runs each tick (while state is flat) until either the book is verified empty or the
    /// deadline expires.
    ///
    /// Why this exists: OnFullClose's single-shot CancelAllOrdersAsync is wrapped in a silent
    /// try/catch (it can't block closure on a transient API error), so a failed cancel used to
    /// leak DCA buy limits onto the book. After the user manually closed the bulk of the
    /// position, one of those leaked DCAs would fire below market and produce a dust orphan
    /// position (e.g. 0.1 XRP @ 1.4222) that state knew nothing about and couldn't adopt.
    /// </summary>
    private async Task SweepOrphanOrders(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange)
    {
        if (!state.OrphanCancelPendingUntil.HasValue) return;

        if (DateTime.UtcNow > state.OrphanCancelPendingUntil.Value)
        {
            Log(strategy, "Warning",
                "🧹 Orphan-cancel: дедлайн истёк, не удалось подтвердить пустой ордербук. " +
                "Снимаю флаг — проверьте Bybit Orders вручную, могут остаться брошенные лимиты.");
            state.OrphanCancelPendingUntil = null;
            return;
        }

        List<LimitOrderDto> open;
        try { open = await exchange.GetOpenOrdersAsync(config.Symbol); }
        catch (NotSupportedException)
        {
            // Exchange doesn't expose the listing endpoint — best-effort cancel once and let go.
            try { await exchange.CancelAllOrdersAsync(config.Symbol); } catch { }
            state.OrphanCancelPendingUntil = null;
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat orphan-cancel: GetOpenOrdersAsync failed for {Symbol}", config.Symbol);
            return; // try again next tick — flag stays set
        }

        if (open.Count == 0)
        {
            Log(strategy, "Info",
                "🧹 Orphan-cancel: на бирже нет открытых ордеров — ордербук чист, снимаю флаг.");
            state.OrphanCancelPendingUntil = null;
            return;
        }

        Log(strategy, "Warning",
            $"🧹 Orphan-cancel: обнаружено {open.Count} незакрытых ордеров после полного закрытия " +
            $"(пример: id={open[0].OrderId} side={open[0].Side} price={open[0].Price} qty={open[0].Quantity}) — отменяю.");
        try { await exchange.CancelAllOrdersAsync(config.Symbol); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat orphan-cancel: CancelAllOrdersAsync failed for {Symbol}", config.Symbol);
            // Flag stays set — we'll re-try next tick.
        }
    }

    // Take-profit force-close: cancel everything, market-close whatever's open, mark Stopped.
    // Mirrors the controller's manual-close logic (StrategiesController GridFloat block) so
    // residual orphan-cancel retry + state reset behave consistently. Differs only in that
    // we set Status=Stopped to prevent the next-bar anchor re-open.
    private async Task ForceCloseOnTakeProfitAsync(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange, bool isLong, decimal markAtTrigger,
        decimal totalPnlAtTrigger, decimal unrealizedAtTrigger)
    {
        Log(strategy, "Warning",
            $"🎯 TAKE-PROFIT: total PnL = ${Math.Round(totalPnlAtTrigger, 2)} " +
            $"(realized ${Math.Round(state.RealizedPnlDollar, 2)} + unreal ${Math.Round(unrealizedAtTrigger, 2)}) " +
            $"≥ цели ${Math.Round(config.TakeProfitTargetUsd, 2)} @ mark {Math.Round(markAtTrigger, 8)}. " +
            "Снимаю все лимиты и закрываю позиции по рынку.");

        await CloseAllAtMarketAsync(strategy, config, state, exchange, isLong, markAtTrigger, "TakeProfitClose");

        // Halt the bot so the next-bar anchor logic doesn't re-open a position. User flips it
        // back via Start when they want to resume.
        strategy.Status = StrategyStatus.Stopped;
        Log(strategy, "Info",
            $"🎯 TAKE-PROFIT выполнен. Total realized = ${Math.Round(state.RealizedPnlDollar, 2)}. Бот остановлен.");
    }

    /// <summary>
    /// Cancel everything, market-close the live position, book the close PnL (into both the
    /// lifetime and the stake-ladder series counters) and reset positional state. Leaves
    /// OpenAfterTime and Status to the caller. Returns false when a position was there and
    /// the market close did not go through — callers that would otherwise keep trading must
    /// stop the bot instead, since the position may still be open on the exchange.
    /// </summary>
    private async Task<bool> CloseAllAtMarketAsync(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange, bool isLong, decimal markAtTrigger,
        string tradeReason)
    {
        try { await exchange.CancelAllOrdersAsync(config.Symbol); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat TP force-close: pre-cancel CancelAllOrdersAsync failed for {Symbol}", config.Symbol);
        }

        // Prefer live exchange qty over state-tracked sum — state may have drifted (orphan DCA
        // fills, residual dust). Falls back to state if the live lookup throws.
        PositionDto? livePos = null;
        try { livePos = await exchange.GetPositionAsync(config.Symbol, config.Direction); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat TP force-close: GetPositionAsync failed for {Symbol}", config.Symbol);
        }
        var stateQty = state.Batches.Sum(b => b.Qty);
        var stateCost = state.Batches.Sum(b => b.FillPrice * b.Qty);
        var totalQty = livePos != null && livePos.Quantity > 0 ? livePos.Quantity : stateQty;
        var avgEntry = stateQty > 0 ? stateCost / stateQty
                     : livePos != null ? livePos.EntryPrice
                     : 0m;

        OrderResultDto? closeResult = null;
        if (totalQty > 0m)
        {
            try
            {
                closeResult = isLong
                    ? await exchange.CloseLongAsync(config.Symbol, totalQty)
                    : await exchange.CloseShortAsync(config.Symbol, totalQty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GridFloat TP force-close: market close failed for {Symbol}", config.Symbol);
                Log(strategy, "Warning",
                    $"TP закрытие рынком не выполнено: {ex.Message}. Бот будет остановлен; " +
                    "позицию проверьте на бирже и закройте вручную.");
            }
        }

        // Second cancel-all AFTER the market close — catches the race where a DCA limit
        // we just-cancelled actually filled in the few hundred ms between cancel and close.
        try { await exchange.CancelAllOrdersAsync(config.Symbol); }
        catch { /* best-effort; orphan-cancel flag below will keep sweeping */ }

        var closedOk = totalQty <= 0m || closeResult is { Success: true };

        // PnL bookkeeping for the trade row + realized counter, at the close order's real average
        // price, quantity and fee read back from the exchange; markAtTrigger / taker-rate estimate
        // only when the read-back fails.
        var closeFill = totalQty > 0m && closedOk
            ? await ReadBackMarketFillAsync(strategy, config.Symbol, exchange, closeResult?.OrderId, tradeReason, CancellationToken.None)
            : null;
        var closePrice = closeFill?.Price ?? closeResult?.FilledPrice ?? markAtTrigger;
        var closedQty = closeFill?.Qty ?? totalQty;
        var closePnlPct = avgEntry > 0
            ? (isLong
                ? (closePrice - avgEntry) / avgEntry * 100m
                : (avgEntry - closePrice) / avgEntry * 100m)
            : 0m;
        var grossClosePnl = avgEntry * closedQty * closePnlPct / 100m;
        // The batches' entry fees are still unbooked — they close here too. Qty the state never
        // tracked (live > state) gets a maker estimate; less live than tracked scales them down.
        var openFees = stateQty > 0m
            ? state.Batches.Sum(b => BatchEntryFee(b, exchange)) * Math.Min(1m, totalQty / stateQty)
            : 0m;
        openFees += Math.Max(0m, totalQty - stateQty) * avgEntry * exchange.MakerFeeRate;
        var entryFeeShare = totalQty > 0m ? openFees * Math.Min(1m, closedQty / totalQty) : 0m;
        var exitFee = closeFill?.Fee ?? closePrice * closedQty * exchange.TakerFeeRate;
        var closeCommission = entryFeeShare + exitFee;
        var netClosePnl = grossClosePnl - closeCommission;

        if (totalQty > 0m && closedOk)
        {
            RecordTrade(strategy, config.Symbol, isLong ? "Sell" : "Buy",
                closedQty, closePrice, closeResult?.OrderId, tradeReason,
                pnlDollar: netClosePnl, commission: closeCommission);
            state.RealizedPnlDollar += netClosePnl;
            state.SeriesRealizedPnl += netClosePnl;
        }
        else if (!closedOk)
        {
            Log(strategy, "Error",
                $"Закрытие рынком не прошло ({closeResult?.ErrorMessage ?? "исключение"}): позиция qty={Math.Round(totalQty, 8)} " +
                "может остаться на бирже — проверьте и закройте вручную.");
        }

        // Reset position state. OpenAfterTime is the caller's call; OrphanCancelPendingUntil
        // makes the next ticks confirm the book is clean before any new anchor.
        state.Batches.Clear();
        state.DcaOrders.Clear();
        state.AnchorPrice = 0;
        state.OpenAfterTime = null;
        state.PlacementCooldownUntil = null;
        state.OrphanCancelPendingUntil = DateTime.UtcNow.AddMinutes(30);

        if (closedOk)
            Log(strategy, "Info",
                $"Закрыто по рынку ({tradeReason}): qty={Math.Round(closedQty, 8)} @ {Math.Round(closePrice, 8)}, " +
                $"PnL закрытия = ${Math.Round(netClosePnl, 2)} (комиссии: вход ${Math.Round(entryFeeShare, 4)} + " +
                $"выход ${Math.Round(exitFee, 4)}).");
        return closedOk;
    }

    // ────────────────────────── Stake ladder (RestartOnRangeExit) ──────────────────────────

    /// <summary>
    /// Series take-profit and losing-side range exit for the stake-ladder mode. Returns true when
    /// it closed the grid this tick (the caller then ends the tick).
    /// </summary>
    private async Task<bool> HandleStakeLadderAsync(Strategy strategy, GridFloatConfig fullConfig,
        GridFloatConfig config, GridFloatState state, IFuturesExchangeService exchange, bool isLong,
        CancellationToken ct)
    {
        decimal? priceN = null;
        try { priceN = await GetLivePriceAsync(strategy, config.Symbol, exchange); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat stake ladder: price fetch failed for {Symbol}", config.Symbol);
        }
        if (priceN is not > 0m) return false;
        var price = priceN.Value;

        var unrealized = state.Batches.Sum(b => isLong ? b.Qty * (price - b.FillPrice) : b.Qty * (b.FillPrice - price));
        var series = state.SeriesRealizedPnl + unrealized;
        var stake = GridFloatStakeLadder.StakeAt(fullConfig, state.StakeStage);

        // ── Series take-profit → close everything, back to the base stake, keep trading ──
        if (fullConfig.TakeProfitEnabled && fullConfig.TakeProfitTargetUsd > 0m
            && series >= fullConfig.TakeProfitTargetUsd)
        {
            Log(strategy, "Warning",
                $"🎯 Тейк серии: ${Math.Round(series, 2)} (realized ${Math.Round(state.SeriesRealizedPnl, 2)} + " +
                $"unreal ${Math.Round(unrealized, 2)}) ≥ цели ${fullConfig.TakeProfitTargetUsd} @ {Math.Round(price, 8)}, " +
                $"ступень {state.StakeStage + 1} (${stake}). Закрываю всё по рынку.");

            if (!await CloseAllAtMarketAsync(strategy, config, state, exchange, isLong, price, "SeriesTakeProfit"))
            {
                await StopWithAlertAsync(strategy, "GridFloat: тейк серии не закрылся",
                    "Закрытие по рынку не прошло — бот остановлен, проверьте позицию на бирже.", ct);
                return true;
            }
            ResetSeries(strategy, state, "тейк серии");
            state.OpenAfterTime = DateTime.UtcNow;
            return true;
        }

        // ── Losing-side range exit → close everything, next stake (or stop on the last) ──
        if (!GridFloatStakeLadder.IsOutsideRange(fullConfig, state.AnchorPrice, price, isLong))
            return false;

        // Irreversible action on one quote: confirm with the REST ticker first, so a stale or
        // glitched websocket book can't stop out the whole grid.
        decimal? confirm = null;
        try { confirm = await exchange.GetTickerPriceAsync(config.Symbol); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat range exit: confirm ticker failed for {Symbol}", config.Symbol);
        }
        if (confirm is not > 0m || !GridFloatStakeLadder.IsOutsideRange(fullConfig, state.AnchorPrice, confirm.Value, isLong))
            return false;
        price = confirm.Value;

        var bound = GridFloatStakeLadder.RangeBound(fullConfig, state.AnchorPrice, isLong);
        Log(strategy, "Warning",
            $"📉 Выход из диапазона: цена {Math.Round(price, 8)} за границей {Math.Round(bound, 8)} " +
            $"(якорь {Math.Round(state.AnchorPrice, 8)}), ступень {state.StakeStage + 1} (${stake}). Закрываю всё по рынку.");

        var closed = await CloseAllAtMarketAsync(strategy, config, state, exchange, isLong, price, "RangeExit");
        state.RangeExitCount++;
        if (!closed)
        {
            await StopWithAlertAsync(strategy, "GridFloat: выход из диапазона не закрылся",
                "Закрытие по рынку не прошло — бот остановлен, проверьте позицию на бирже.", ct);
            return true;
        }

        if (GridFloatStakeLadder.IsLastStage(fullConfig, state.StakeStage))
        {
            await StopWithAlertAsync(strategy, "GridFloat: ступени ставки исчерпаны",
                $"{config.Symbol}: выход из диапазона на последней ступени (${stake}). Позиции закрыты, бот остановлен. " +
                $"Результат серии: ${Math.Round(state.SeriesRealizedPnl, 2)}.", ct);
            return true;
        }

        state.StakeStage++;
        state.OpenAfterTime = DateTime.UtcNow;
        Log(strategy, "Info",
            $"➡️ Ступень {state.StakeStage + 1}: ставка ${GridFloatStakeLadder.StakeAt(fullConfig, state.StakeStage)}, " +
            $"серия ${Math.Round(state.SeriesRealizedPnl, 2)}. Новый диапазон — на следующей закрытой свече.");
        return true;
    }

    /// <summary>After a grid closed by its own TPs: reset to the base stake once the series hits its threshold.</summary>
    private void OnStakeLadderFullClose(Strategy strategy, GridFloatState state)
    {
        // TakeProfitContinue without the ladder: a grid that closed by its own TPs may already
        // have carried the series past the target — start a new series now, otherwise the next
        // anchor would be closed by the take-profit on its very first tick.
        if (_fullConfig is { RestartOnRangeExit: false, TakeProfitContinue: true, TakeProfitEnabled: true } cont)
        {
            if (cont.TakeProfitTargetUsd > 0m && state.SeriesRealizedPnl >= cont.TakeProfitTargetUsd)
                ResetSeries(strategy, state, "серия достигла цели тейка");
            return;
        }

        if (_fullConfig is not { RestartOnRangeExit: true } full) return;

        var threshold = full.TakeProfitEnabled && full.TakeProfitTargetUsd > 0m ? full.TakeProfitTargetUsd : 0m;
        if (state.SeriesRealizedPnl >= threshold)
        {
            ResetSeries(strategy, state, full.TakeProfitEnabled ? "серия достигла цели" : "серия в плюсе");
        }
        else if (state.StakeStage > 0)
        {
            Log(strategy, "Info",
                $"Сетка закрылась по TP, серия ${Math.Round(state.SeriesRealizedPnl, 2)} < ${threshold} — " +
                $"остаюсь на ступени {state.StakeStage + 1} (${GridFloatStakeLadder.StakeAt(full, state.StakeStage)}).");
        }
    }

    private void ResetSeries(Strategy strategy, GridFloatState state, string why)
    {
        if (_fullConfig is { RestartOnRangeExit: false })
            Log(strategy, "Info",
                $"🔄 Новая серия ({why}): прошлая закрыта с ${Math.Round(state.SeriesRealizedPnl, 2)}, бот продолжает торговлю.");
        else if (state.StakeStage > 0 || state.SeriesRealizedPnl != 0m)
            Log(strategy, "Info",
                $"🔄 Сброс на базовую ставку ({why}): серия закрыта с ${Math.Round(state.SeriesRealizedPnl, 2)}, " +
                $"была ступень {state.StakeStage + 1}.");
        state.StakeStage = 0;
        state.SeriesRealizedPnl = 0m;
    }

    private async Task StopWithAlertAsync(Strategy strategy, string title, string details, CancellationToken ct)
    {
        strategy.Status = StrategyStatus.Stopped;
        Log(strategy, "Error", $"🛑 {title}. {details}");
        try { await _telegramSignalService.SendStrategyAlertAsync(strategy, title, details, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "GridFloat: Telegram alert failed for strategy {Id}", strategy.Id); }
    }

    /// <summary>Applies <see cref="GridFloatConfig.AnchorPriceLimit"/>; logs once per blocked stretch.</summary>
    private bool AnchorBlockedByPriceLimit(Strategy strategy, GridFloatConfig fullConfig, GridFloatState state,
        decimal price, bool isLong)
    {
        if (!GridFloatStakeLadder.IsAnchorBlocked(fullConfig, price, isLong))
        {
            state.AnchorBlockedSince = null;
            return false;
        }
        if (state.AnchorBlockedSince == null)
        {
            state.AnchorBlockedSince = DateTime.UtcNow;
            Log(strategy, "Info",
                $"⏸ Новый якорь не открываю: цена {Math.Round(price, 8)} {(isLong ? "выше" : "ниже")} лимита " +
                $"{fullConfig.AnchorPriceLimit}. Жду возврата цены.");
        }
        return true;
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
        // Arm the orphan-cancel retry. Even though we just called CancelAllOrdersAsync above,
        // it's wrapped in a silent catch — any failure (timestamp, 5xx, rate limit) would
        // silently leave limit orders alive. The retry step in ProcessAsync confirms the book
        // is empty via GetOpenOrdersAsync and re-cancels if needed; OpenAnchor is gated on this
        // flag being clear, so we never overlap a fresh anchor with a stale DCA.
        state.OrphanCancelPendingUntil = DateTime.UtcNow.AddMinutes(30);

        Log(strategy, "Info",
            $"🏁 Полное закрытие сетки: realized={Math.Round(state.RealizedPnlDollar, 2)}USD, " +
            $"кулдаун до {state.OpenAfterTime.Value:HH:mm:ss} → новый якорь на следующей закрытой свече");
        OnStakeLadderFullClose(strategy, state);
        await Task.CompletedTask;
    }

    // ────────────────────────── Heal ──────────────────────────

    private async Task HealMissingTps(Strategy strategy, GridFloatConfig config, GridFloatState state,
        IFuturesExchangeService exchange)
    {
        // Snapshot — PlaceBatchTpLimit / the Fix #10 phantom write-off may drop batches from
        // state.Batches, which would otherwise corrupt the foreach iterator (Fix #5 cleanup path).
        var snapshot = state.Batches.ToList();

        // Only batches that need a (re)placement AND whose Fix #10 backoff window has elapsed.
        var now = DateTime.UtcNow;
        var needTp = snapshot
            .Where(b => string.IsNullOrEmpty(b.TpOrderId)
                        && (b.TpRetryAfter == null || b.TpRetryAfter.Value <= now))
            .ToList();
        if (needTp.Count == 0) return;

        // ── Fix #10: position-headroom guard ─────────────────────────────────────────────
        // The LIVE exchange position is authoritative for how much reduce-only TP can rest on
        // the book. Sum of all batch reduce-only orders must not exceed the live position, or
        // Bybit auto-reduces / rejects the surplus with "orderQty will be truncated to zero"
        // every tick forever (phantom batches: state qty > exchange qty after missed/partial
        // fills, restart adoption, false-flat reconciles). We fetch the position ONCE per pass.
        //
        // Fix #10 is now the PRIMARY mechanism for dropping phantom batches; Fix #9 (time-based,
        // in ReconcileMissedTpFills) remains as a harmless fallback.
        decimal livePos;
        decimal qtyStep, minQty;
        try
        {
            var pos = await exchange.GetPositionAsync(config.Symbol, config.Direction);
            livePos = pos?.Quantity ?? 0m;
        }
        catch (NotSupportedException)
        {
            // Exchange can't report positions — fall back to the legacy per-batch behaviour
            // (each batch validated in isolation by PlaceBatchTpLimit). Don't break it.
            await HealMissingTpsFallback(strategy, config, state, exchange, needTp);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat HealMissingTps: GetPositionAsync failed for {Symbol} — fallback", config.Symbol);
            await HealMissingTpsFallback(strategy, config, state, exchange, needTp);
            return;
        }

        try { (qtyStep, minQty) = await exchange.GetSymbolInfoAsync(config.Symbol); }
        catch (NotSupportedException) { (qtyStep, minQty) = (0m, 0m); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GridFloat HealMissingTps: GetSymbolInfoAsync failed for {Symbol}", config.Symbol);
            (qtyStep, minQty) = (0m, 0m);
        }

        // ── Fix #10 (orphan-desync recovery): base headroom on the ACTUAL exchange book ──────
        // STATE MEMORY under-counts committed reduce-only when an order keeps resting on the
        // book but its TpOrderId went null in state ("lost-ID orphan"). The old
        //   committed = Σ batch.Qty where TpOrderId != null
        // then over-estimated headroom and re-placed TPs the exchange rejects forever with
        // "orderQty will be truncated to zero" (position already fully subscribed by orphans).
        //
        // Production evidence (Bybit DRIFTUSDT Long): 13 batches summing 56163, only 6 tracked
        // (30993), livePos≈56163. State-memory headroom looked like 25170 so the 7 null batches
        // re-placed every tick and every placement was rejected — but those 7 are REAL position
        // protected by orphaned (lost-ID) resting TPs, not phantoms. We must NOT write them off.
        //
        // So we query the real book once and treat resting close-side qty as authoritative.
        // closeSide is the reduce-only TP side: Sell for Long, Buy for Short (DCA limits rest on
        // the OPEN side — Buy for Long / Sell for Short — so this cleanly isolates resting TPs).
        var closeSide = state.IsLong ? "Sell" : "Buy";

        // Qty the *tracked* (non-null TpOrderId) batches account for. Used only to detect
        // orphans (book has more resting close-side than state can see).
        var trackedResting = state.Batches.Where(b => !string.IsNullOrEmpty(b.TpOrderId)).Sum(b => b.Qty);

        decimal committed;
        try
        {
            var open = await exchange.GetOpenOrdersAsync(config.Symbol);
            // Remaining (unfilled) reduce-only qty actually resting on the book.
            var restingClose = open
                .Where(o => string.Equals(o.Side, closeSide, StringComparison.OrdinalIgnoreCase))
                .Sum(o => Math.Max(0m, o.Quantity - o.FilledQuantity));

            // Orphan-desync trigger: the book holds significantly more resting reduce-only than
            // state's tracked TpOrderIds can explain → lost-ID orphans exist. minQty margin keeps
            // it off rounding noise. Only act if there's actually a null-TP batch to (re)place.
            if (restingClose > trackedResting + minQty)
            {
                var canResync = state.LastTpResyncAt == null
                                || (now - state.LastTpResyncAt.Value) >= TimeSpan.FromMinutes(2);
                if (canResync)
                {
                    // One-shot TP resync. The book is already fully subscribed by a mix of tracked
                    // + orphaned reduce-only orders, so we can't add anything. Cancel EVERYTHING
                    // and re-place every batch's TP from a clean book on the next heal tick.
                    //
                    // CAPITAL SAFETY (SOUL.md): GridFloat has NO stop-loss — these reduce-only
                    // limits are profit-taking TPs, not risk protection. Cancelling them for ~one
                    // tick cannot increase downside risk; it only briefly removes profit-taking.
                    // We realize NO PnL here — the resync re-places real TPs, records no trade.
                    Log(strategy, "Warning",
                        $"🔁 Fix #10 resync: обнаружены осиротевшие reduce-only ордера на бирже " +
                        $"(restingClose={Math.Round(restingClose, 8)} > tracked={Math.Round(trackedResting, 8)}), " +
                        $"позиция (livePos={Math.Round(livePos, 8)}) полностью покрыта потерянными TP — " +
                        "отменяю ВСЕ ордера и переставляю TP с чистого листа.");

                    try { await exchange.CancelAllOrdersAsync(config.Symbol); }
                    catch (Exception cex)
                    {
                        _logger.LogWarning(cex,
                            "GridFloat HealMissingTps Fix #10 resync: CancelAllOrdersAsync failed for {Symbol}", config.Symbol);
                    }

                    // Clear ALL per-batch TP tracking so the next tick re-places from scratch and
                    // re-adopts the full live position into freshly-tracked TPs.
                    foreach (var b in state.Batches)
                    {
                        b.TpOrderId = null;
                        b.TpFailCount = 0;
                        b.TpRetryAfter = null;
                        b.NoHeadroomConfirmations = 0;
                    }

                    // Verify the book actually emptied (SweepOrphanOrders re-cancels if not).
                    state.OrphanCancelPendingUntil = DateTime.UtcNow.AddMinutes(30);
                    state.LastTpResyncAt = DateTime.UtcNow;

                    // DCA limits cancelled here are re-placed by the existing HealMissingDcas.
                    return;
                }
                // Within the 2-min thrash guard → we just fired a resync moments ago and the
                // cancel-all hasn't propagated yet (book still shows orphans). DO NOT fall through
                // to greedy placement here: with committed=restingClose≈livePos the headroom is
                // ~0, every null-TP batch would route to HandleNoHeadroomBatch and — across the
                // 2-min window — get written off as a phantom. That is exactly the real,
                // orphan-backed position we must NOT lose. Instead wait quietly for the book to
                // empty so the next post-guard tick can re-place cleanly. SweepOrphanOrders
                // (armed via OrphanCancelPendingUntil) keeps re-cancelling if the book is stuck.
                _logger.LogDebug(
                    "GridFloat HealMissingTps Fix #10: orphan-desync still visible within 2-min resync guard for {Symbol} " +
                    "(restingClose={Resting} > tracked={Tracked}) — waiting for cancel-all to propagate, skipping placement",
                    config.Symbol, restingClose, trackedResting);
                return;
            }

            committed = restingClose;
        }
        catch (NotSupportedException)
        {
            // Exchange can't list open orders — fall back to the legacy state-memory committed.
            committed = trackedResting;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GridFloat HealMissingTps: GetOpenOrdersAsync failed for {Symbol} — fallback to state-memory committed", config.Symbol);
            committed = trackedResting;
        }

        var headroom = livePos - committed;
        if (headroom < 0m) headroom = 0m; // clamp — book may momentarily exceed live during fills

        // Allocate the remaining headroom greedily to the batches most likely to close soonest:
        // Long → lowest TpPrice first; Short → highest TpPrice first.
        var ordered = state.IsLong
            ? needTp.OrderBy(b => b.TpPrice).ToList()
            : needTp.OrderByDescending(b => b.TpPrice).ToList();

        var placed = 0;
        foreach (var batch in ordered)
        {
            if (headroom >= batch.Qty)
            {
                // Full live position backs this batch — place its TP normally.
                batch.NoHeadroomConfirmations = 0;
                if (placed > 0) await Task.Delay(InterOrderDelayMs);
                await PlaceBatchTpLimit(strategy, config, state, exchange, batch);
                placed++;
                headroom -= batch.Qty;
            }
            else if (minQty > 0 && headroom >= minQty)
            {
                // Partial headroom: trim the batch to what the position can actually back, place
                // a real (smaller) TP, and write off the trimmed remainder loudly. This consumes
                // the rest of the headroom.
                var trimmed = qtyStep > 0 ? Math.Floor(headroom / qtyStep) * qtyStep : headroom;
                if (trimmed < minQty)
                {
                    // Flooring pushed it below min — treat as no headroom for this batch.
                    HandleNoHeadroomBatch(strategy, state, batch, livePos, committed, minQty);
                    continue;
                }
                var writtenOff = batch.Qty - trimmed;
                Log(strategy, "Warning",
                    $"⚠️ Fix #10 partial: батч #{batch.LevelIdx} обрезан под живую позицию: " +
                    $"{batch.Qty} → {trimmed} (списано {writtenOff}, livePos={Math.Round(livePos, 8)}, " +
                    $"committed={Math.Round(committed, 8)}, headroom={Math.Round(headroom, 8)}). " +
                    "Остаток не обеспечен позицией — PnL по нему не реализуется.");
                batch.Qty = trimmed;
                batch.NoHeadroomConfirmations = 0;
                if (placed > 0) await Task.Delay(InterOrderDelayMs);
                await PlaceBatchTpLimit(strategy, config, state, exchange, batch);
                placed++;
                headroom = 0m;
            }
            else
            {
                // No live position backs this batch → phantom candidate. Require ≥2 consecutive
                // confirmations before deleting so a single transient GetPositionAsync glitch
                // can't drop a real batch (CAPITAL SAFETY — see SOUL.md). Never realizes PnL.
                HandleNoHeadroomBatch(strategy, state, batch, livePos, committed, minQty);
            }
        }
    }

    // Fix #10: phantom write-off with 2-confirmation gate. A batch reaches here when the live
    // exchange position has no room (< minQty) left to back its reduce-only TP. We confirm twice
    // (across two heal passes) before removing it, never realize fictional PnL, and best-effort
    // cancel any stale TP id. When in doubt we KEEP the batch (just back it off).
    private void HandleNoHeadroomBatch(Strategy strategy, GridFloatState state, GridFloatBatch batch,
        decimal livePos, decimal committed, decimal minQty)
    {
        batch.NoHeadroomConfirmations++;
        if (batch.NoHeadroomConfirmations < 2)
        {
            // First sighting — don't delete yet; apply backoff so we don't re-probe instantly.
            ApplyTpBackoff(batch);
            Log(strategy, "Warning",
                $"Fix #10: батч #{batch.LevelIdx} (qty={batch.Qty}) не обеспечен живой позицией " +
                $"(livePos={Math.Round(livePos, 8)}, committed={Math.Round(committed, 8)}, minQty={minQty}) — " +
                $"подтверждение {batch.NoHeadroomConfirmations}/2. Жду повторное подтверждение перед списанием.");
            return;
        }

        Log(strategy, "Warning",
            $"🧹 Fix #10 phantom write-off: удаляю батч #{batch.LevelIdx} (qty={batch.Qty}, TP={Math.Round(batch.TpPrice, 8)}) " +
            $"после 2 подтверждений отсутствия headroom (livePos={Math.Round(livePos, 8)}, committed={Math.Round(committed, 8)}). " +
            "Этой qty нет на бирже — PnL НЕ реализуется. Скорее всего легаси из false-flat reconcile или ручного закрытия.");
        state.Batches.Remove(batch);
    }

    // Fix #10 fallback: exchanges without GetPositionAsync support keep the original per-batch
    // heal (each TP validated in isolation by PlaceBatchTpLimit). Backoff still applies via the
    // failure branches there.
    private async Task HealMissingTpsFallback(Strategy strategy, GridFloatConfig config,
        GridFloatState state, IFuturesExchangeService exchange, List<GridFloatBatch> needTp)
    {
        var placed = 0;
        foreach (var batch in needTp)
        {
            if (placed > 0) await Task.Delay(InterOrderDelayMs);
            await PlaceBatchTpLimit(strategy, config, state, exchange, batch);
            placed++;
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
            // Arm the orphan-cancel retry: the user closed the position out-of-band (UI or
            // account-level button), so any DCA/TP limits the bot left on the book before
            // shutdown are still alive. ProcessAsync will keep re-cancelling each tick until
            // GetOpenOrdersAsync reports a clean book.
            state.OrphanCancelPendingUntil = DateTime.UtcNow.AddMinutes(30);
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
            if (_fullConfig is { RestartOnRangeExit: true } full && full.Tiers.Count > 0)
            {
                // `config` is the stage view (a copy). Stakes derive from the T1 base, so raise
                // the base just enough that this stage's stake reaches the bumped size, and
                // persist the real config — never the one-tier view.
                var factor = GridFloatStakeLadder.StakeAt(full, _stakeStage) / Math.Max(full.Tiers[0].SizeUsdt, 0.01m);
                var neededBase = Math.Ceiling(tier.SizeUsdt / Math.Max(factor, 1m) * 100m) / 100m;
                if (neededBase > full.Tiers[0].SizeUsdt) full.Tiers[0].SizeUsdt = neededBase;
                PersistConfigJson(strategy, full);
            }
            else
            {
                PersistConfigJson(strategy, config);
            }
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
