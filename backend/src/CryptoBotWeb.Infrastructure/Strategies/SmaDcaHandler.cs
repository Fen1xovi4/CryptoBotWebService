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
/// SMA DCA (averaging-down on an SMA).
///
/// Bar-close signal: Long enters when close > SMA, Short enters when close < SMA.
/// While in position, each time price moves DcaStepPercent against the *current average*,
/// a DCA fill is added with qty = currentTotalQty * DcaMultiplier (geometric sizing).
/// The trigger step and TP are both recomputed off the new average after every fill.
///
/// OrderType controls DCA placement (first entry is ALWAYS market):
///   - Market — DCAs go as market orders (taker fees, guaranteed fill, slippage).
///   - Limit  — DCAs go as maker limits offset by EntryLimitOffsetPercent from candle close
///              (below for Long Buy, above for Short Sell). DCA limit waits indefinitely — it
///              either fills or the TP closes the position first and the DCA limit is cancelled.
///
/// TP exit: always a reduce-only LIMIT order placed right after a fill, and replaced after
/// every DCA fill (cancel old, place new for the new avg/total qty). Fill is detected when
/// GetPositionAsync reports quantity=0 while state.InPosition=true.
///
/// No stop-loss. One direction per bot (Long or Short, set in config).
/// </summary>
public class SmaDcaHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // Market open + limit maker close fee estimate: ~0.05% taker + ~0.02% maker ≈ 0.07%.
    private const decimal LimitExitFeeRate = 0.0005m + 0.0002m;

    private const int DcaCooldownMinutes = 5;

    public string StrategyType => StrategyTypes.SmaDca;

    private readonly AppDbContext _db;
    private readonly ILogger<SmaDcaHandler> _logger;
    private readonly ITelegramSignalService _telegramSignalService;

    public SmaDcaHandler(AppDbContext db, ILogger<SmaDcaHandler> logger,
        ITelegramSignalService telegramSignalService)
    {
        _db = db;
        _logger = logger;
        _telegramSignalService = telegramSignalService;
    }

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<SmaDcaConfig>(strategy.ConfigJson, JsonOptions);
        if (config == null || string.IsNullOrEmpty(config.Symbol))
        {
            _logger.LogError("Invalid config for strategy {Id}", strategy.Id);
            Log(strategy, "Error", "Invalid config — symbol is empty");
            return;
        }

        if (config.SmaPeriod < 2 || config.MaxDcaLevels < 0 || config.PositionSizeUsd <= 0
            || config.DcaMultiplier <= 0 || config.DcaStepPercent <= 0 || config.TakeProfitPercent <= 0)
        {
            Log(strategy, "Error",
                $"Invalid parameters: SmaPeriod={config.SmaPeriod}, MaxDcaLevels={config.MaxDcaLevels}, " +
                $"PositionSizeUsd={config.PositionSizeUsd}, DcaMultiplier={config.DcaMultiplier}, " +
                $"DcaStep={config.DcaStepPercent}%, TP={config.TakeProfitPercent}%");
            return;
        }

        var isLongConfig = config.Direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var useLimitOrders = config.OrderType.Equals("Limit", StringComparison.OrdinalIgnoreCase);
        var state = JsonSerializer.Deserialize<SmaDcaState>(strategy.StateJson, JsonOptions)
                    ?? new SmaDcaState();

        // 1. Restart re-sync (once per worker boot, driven by state.StateInitialized)
        if (!state.StateInitialized)
        {
            await SyncFromExchangeOnStartup(strategy, config, state, exchange, isLongConfig, ct);
            state.StateInitialized = true;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 2. Poll pending ENTRY limit (if any). On fill → adopt as entry, place TP limit.
        if (!string.IsNullOrEmpty(state.EntryOrderId))
        {
            await ProcessPendingEntry(strategy, config, state, exchange, isLongConfig, ct);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 3. Poll pending DCA limit (if any). On fill → update avg/qty, replace TP limit.
        if (!string.IsNullOrEmpty(state.DcaOrderId))
        {
            await ProcessPendingDca(strategy, config, state, exchange, ct);
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }

        // 4. Detect if the reduce-only limit TP order was filled on the exchange.
        if (state.InPosition)
        {
            if (await CheckLimitTpFilled(strategy, config, state, exchange, ct))
            {
                SaveState(strategy, state);
                await _db.SaveChangesAsync(ct);
                return;
            }

            // Verify the stored TP is still alive on the exchange. Manual cancel or an exchange-side
            // drop (risk engine, margin change) won't close the position, so CheckLimitTpFilled
            // misses it. Clear the id here; the self-heal below then re-places the TP.
            if (!string.IsNullOrEmpty(state.TakeProfitOrderId))
                await VerifyTakeProfitAlive(strategy, config, state, exchange);

            // Heal: if we're in position but have no active TP limit (startup / previous placement failed
            // / TP vanished from the exchange) → place one.
            if (string.IsNullOrEmpty(state.TakeProfitOrderId))
            {
                await PlaceTakeProfitLimit(strategy, config, state, exchange);
                SaveState(strategy, state);
                await _db.SaveChangesAsync(ct);
            }
        }

        // 3. Fetch candles for SMA. Extra headroom so the seed SMA is well-populated.
        var needsCandles = Math.Max(config.SmaPeriod + 20, 300);
        var candles = await exchange.GetKlinesAsync(config.Symbol, config.Timeframe, needsCandles);
        if (candles.Count < config.SmaPeriod + 1)
        {
            _logger.LogWarning("Not enough candles ({Count}/{Need}) for SmaDca strategy {Id}",
                candles.Count, config.SmaPeriod + 1, strategy.Id);
            return;
        }

        var lastClosed = GetLastClosedCandle(candles);
        if (lastClosed == null) return;

        // 4. No new closed candle? Just persist and return.
        if (state.LastProcessedCandleTime.HasValue
            && lastClosed.CloseTime <= state.LastProcessedCandleTime.Value)
        {
            if (await SyncIfClosedExternally(strategy, state, ct))
                return;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        state.LastProcessedCandleTime = lastClosed.CloseTime;

        // Entry-limit timeout: cancel unconditionally after N bars; next candle will re-evaluate the signal.
        if (!string.IsNullOrEmpty(state.EntryOrderId) && state.EntryOrderPlacedAtCandleTime.HasValue)
        {
            var barMinutes = ParseTimeframeMinutes(config.Timeframe);
            if (barMinutes > 0)
            {
                var elapsedBars = (int)((lastClosed.CloseTime - state.EntryOrderPlacedAtCandleTime.Value).TotalMinutes / barMinutes);
                if (elapsedBars >= Math.Max(1, config.EntryLimitTimeoutBars))
                {
                    Log(strategy, "Info",
                        $"⏱ Entry лимит #{state.EntryOrderId} висел {elapsedBars} свечей без исполнения — отменяем");
                    await CancelEntryLimit(strategy, config, state, exchange, isLongConfig);
                }
            }
        }

        // 5. Skip first candle after Exit to prevent instant re-entry on the same candle (spec §11).
        if (state.SkipNextCandle && !state.InPosition)
        {
            state.SkipNextCandle = false;
            Log(strategy, "Info", "Пропуск свечи после выхода — новый вход на этой свече запрещён");
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // 6. Compute SMA on closed prices only.
        var closedCloses = candles
            .Where(c => c.CloseTime <= DateTime.UtcNow)
            .Select(c => c.Close)
            .ToArray();
        if (closedCloses.Length < config.SmaPeriod)
        {
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var smaValues = IndicatorCalculator.CalculateSma(closedCloses, config.SmaPeriod);
        var sma = smaValues[^1];
        if (sma == 0)
        {
            if (await SyncIfClosedExternally(strategy, state, ct)) return;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        state.LastPrice = lastClosed.Close;

        Log(strategy, "Info",
            $"Новая свеча: O={lastClosed.Open} H={lastClosed.High} L={lastClosed.Low} C={lastClosed.Close} | " +
            $"SMA{config.SmaPeriod}={Math.Round(sma, 6)} | " +
            $"Dir={config.Direction} InPos={state.InPosition} DcaLvl={state.DcaLevel} " +
            (state.InPosition ? $"Avg={Math.Round(state.AverageEntryPrice, 6)} TP={Math.Round(state.CurrentTakeProfit, 6)}" : ""));

        // 7. Entry / DCA on this closed bar.
        var positionOpenedThisBar = false;

        if (!state.InPosition)
        {
            // Don't double up if an entry limit is already pending fill.
            if (string.IsNullOrEmpty(state.EntryOrderId))
            {
                var shouldEnter = isLongConfig
                    ? lastClosed.Close > sma
                    : lastClosed.Close < sma;

                if (shouldEnter)
                {
                    if (await IsWorkspaceTimerExpiredAsync(strategy, ct))
                    {
                        Log(strategy, "Info",
                            "⏱ Таймер рабочего пространства истёк — новая позиция не открывается " +
                            "(докупки и take-profit по существующим позициям продолжают работать)");
                    }
                    else
                    {
                        // First entry is ALWAYS market regardless of OrderType — we need the position
                        // open immediately so TP/DCA can start working. OrderType only affects DCAs.
                        await OpenEntryMarket(strategy, config, state, exchange, lastClosed, isLongConfig, ct);
                        positionOpenedThisBar = state.InPosition;
                    }
                }
            }
        }
        else
        {
            // TP is handled exclusively by the reduce-only limit order placed after Entry/DCA.
            // CheckLimitTpFilled (step 4 each poll) detects the fill. We never market-close on TP.
            // DCA check — skipped if a DCA limit is already pending fill.
            if (string.IsNullOrEmpty(state.DcaOrderId))
            {
                var dcaOnCooldown = state.DcaCooldownUntil.HasValue && state.DcaCooldownUntil.Value > DateTime.UtcNow;
                if (!dcaOnCooldown && state.DcaLevel < config.MaxDcaLevels)
                {
                    var useLastFill = config.DcaTriggerBase
                        .Equals("LastFill", StringComparison.OrdinalIgnoreCase);
                    var triggerBase = useLastFill && state.LastDcaPrice > 0
                        ? state.LastDcaPrice
                        : state.AverageEntryPrice;

                    var dcaTrigger = state.IsLong
                        ? lastClosed.Close <= triggerBase * (1m - config.DcaStepPercent / 100m)
                        : lastClosed.Close >= triggerBase * (1m + config.DcaStepPercent / 100m);

                    if (dcaTrigger)
                    {
                        if (useLimitOrders)
                            await PlaceDcaLimit(strategy, config, state, exchange, lastClosed);
                        else
                            await OpenDcaMarket(strategy, config, state, exchange, lastClosed, ct);
                    }
                }
            }
        }

        // 8. External-close sync (skip if we just opened — DB doesn't have the new trade yet)
        if (!positionOpenedThisBar && await SyncIfClosedExternally(strategy, state, ct))
            return;

        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
    }

    // ───────── Entry / DCA / Exit ─────────

    private async Task OpenEntryMarket(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, CandleDto candle, bool isLong, CancellationToken ct)
    {
        var quoteAmount = config.PositionSizeUsd;
        Log(strategy, "Info",
            $"📈 ENTRY {config.Direction}: {config.Symbol}, USD={quoteAmount}, close={candle.Close}");

        var result = isLong
            ? await exchange.OpenLongAsync(config.Symbol, quoteAmount)
            : await exchange.OpenShortAsync(config.Symbol, quoteAmount);

        if (!result.Success || result.FilledQuantity is not > 0)
        {
            Log(strategy, "Error", $"Ошибка ENTRY: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: ENTRY failed: {Error}", strategy.Id, result.ErrorMessage);
            return;
        }

        var filledQty = result.FilledQuantity.Value;
        var fillPrice = result.FilledPrice ?? candle.Close;

        state.InPosition = true;
        state.IsLong = isLong;
        state.TotalQuantity = filledQty;
        state.TotalCost = fillPrice * filledQty;
        state.AverageEntryPrice = fillPrice;
        state.CurrentTakeProfit = ComputeTakeProfit(fillPrice, config.TakeProfitPercent, isLong);
        state.DcaLevel = 0;
        // Seed LastDcaPrice with the entry price so the "LastFill" trigger base has a reference
        // for the very first DCA (which hasn't filled yet).
        state.LastDcaPrice = fillPrice;
        state.PositionOpenedAt = DateTime.UtcNow;
        state.SkipNextCandle = false;
        state.DcaCooldownUntil = null;

        RecordTrade(strategy, config.Symbol, isLong ? "Buy" : "Sell", filledQty, fillPrice, result.OrderId, "Entry");

        Log(strategy, "Info",
            $"✅ ENTRY {config.Direction} open: qty={filledQty} @ {fillPrice}, TP={Math.Round(state.CurrentTakeProfit, 6)}");
        _logger.LogInformation("Strategy {Id}: SmaDca ENTRY {Dir} qty={Qty} @ {Price}, TP={TP}",
            strategy.Id, config.Direction, filledQty, fillPrice, state.CurrentTakeProfit);

        await PlaceTakeProfitLimit(strategy, config, state, exchange);

        await _telegramSignalService.SendOpenPositionSignalAsync(strategy, config.Symbol, config.Direction,
            quoteAmount, fillPrice, state.CurrentTakeProfit, 0m, ct);
    }

    private async Task OpenDcaMarket(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, CandleDto candle, CancellationToken ct)
    {
        var targetQty = state.TotalQuantity * config.DcaMultiplier;
        if (targetQty <= 0)
        {
            Log(strategy, "Warning", $"DCA пропущен: targetQty={targetQty} (TotalQty={state.TotalQuantity})");
            return;
        }

        var dcaPrice = candle.Close;
        var quoteAmount = Math.Round(targetQty * dcaPrice, 2);

        Log(strategy, "Info",
            $"➕ DCA #{state.DcaLevel + 1}/{config.MaxDcaLevels} {config.Direction}: close={dcaPrice}, " +
            $"avg={Math.Round(state.AverageEntryPrice, 6)}, targetQty={targetQty}, USD≈{quoteAmount}");

        var result = state.IsLong
            ? await exchange.OpenLongAsync(config.Symbol, quoteAmount)
            : await exchange.OpenShortAsync(config.Symbol, quoteAmount);

        if (!result.Success || result.FilledQuantity is not > 0)
        {
            state.DcaCooldownUntil = DateTime.UtcNow.AddMinutes(DcaCooldownMinutes);
            Log(strategy, "Warning",
                $"Ошибка DCA (cooldown {DcaCooldownMinutes}min): {result.ErrorMessage}");
            _logger.LogWarning("Strategy {Id}: DCA failed ({Dir}): {Error}",
                strategy.Id, config.Direction, result.ErrorMessage);
            return;
        }

        var filledQty = result.FilledQuantity.Value;
        var fillPrice = result.FilledPrice ?? dcaPrice;

        state.TotalCost += fillPrice * filledQty;
        state.TotalQuantity += filledQty;
        state.AverageEntryPrice = state.TotalCost / state.TotalQuantity;
        state.CurrentTakeProfit = ComputeTakeProfit(state.AverageEntryPrice, config.TakeProfitPercent, state.IsLong);
        state.DcaLevel++;
        state.LastDcaPrice = fillPrice;
        state.DcaCooldownUntil = null;

        RecordTrade(strategy, config.Symbol, state.IsLong ? "Buy" : "Sell", filledQty, fillPrice,
            result.OrderId, $"DCA#{state.DcaLevel}");

        Log(strategy, "Info",
            $"✅ DCA #{state.DcaLevel} filled: qty={filledQty} @ {fillPrice} → " +
            $"totalQty={state.TotalQuantity}, avg={Math.Round(state.AverageEntryPrice, 6)}, " +
            $"newTP={Math.Round(state.CurrentTakeProfit, 6)}");
        _logger.LogInformation(
            "Strategy {Id}: SmaDca DCA#{Lvl} {Dir} qty={Qty} @ {Price} → totalQty={Total} avg={Avg} TP={TP}",
            strategy.Id, state.DcaLevel, config.Direction, filledQty, fillPrice,
            state.TotalQuantity, state.AverageEntryPrice, state.CurrentTakeProfit);

        AssertInvariant(strategy, state);

        await ReplaceTakeProfitLimit(strategy, config, state, exchange);
    }

    /// <summary>
    /// Detects limit-TP fill: if state says InPosition but the exchange reports no position,
    /// the reduce-only limit TP must have executed at its limit price.
    /// Returns true if the fill was processed (caller should persist state and return).
    /// </summary>
    private async Task<bool> CheckLimitTpFilled(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.TakeProfitOrderId)) return false;

        PositionDto? pos;
        try
        {
            pos = await exchange.GetPositionAsync(config.Symbol, config.Direction);
        }
        catch (NotSupportedException) { return false; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmaDca: TP-fill detection failed for {Symbol}", config.Symbol);
            return false;
        }

        var exchangeHasPosition = pos != null && pos.Quantity > 0;
        if (exchangeHasPosition) return false;

        // Position closed on exchange while our TP limit was active → treat as TP fill.
        var tpPrice = state.CurrentTakeProfit;
        var qtyClosed = state.TotalQuantity;
        var pnlPct = ComputePnlPercent(state.IsLong, state.AverageEntryPrice, tpPrice);
        var grossPnl = state.TotalCost * pnlPct / 100m;
        var commission = state.TotalCost * LimitExitFeeRate;
        var netPnl = grossPnl - commission;

        RecordTrade(strategy, config.Symbol, state.IsLong ? "Sell" : "Buy", qtyClosed, tpPrice,
            state.TakeProfitOrderId, "TakeProfit", pnlDollar: netPnl, commission: commission);

        state.RealizedPnlDollar += netPnl;

        Log(strategy, "Info",
            $"💰 EXIT {(state.IsLong ? "Long" : "Short")} (TP лимит): qty={qtyClosed} @ {tpPrice}, " +
            $"avg={Math.Round(state.AverageEntryPrice, 6)}, PnL={Math.Round(pnlPct, 4)}% " +
            $"(${Math.Round(netPnl, 2)}, комиссия≈${Math.Round(commission, 4)})");
        _logger.LogInformation(
            "Strategy {Id}: SmaDca LIMIT TP filled qty={Qty} @ {Price}, avg={Avg}, netPnL={Pnl}",
            strategy.Id, qtyClosed, tpPrice, state.AverageEntryPrice, netPnl);

        // Position is gone — any pending DCA limit would otherwise reopen a mistaken position.
        if (!string.IsNullOrEmpty(state.DcaOrderId))
        {
            var dcaId = state.DcaOrderId;
            try { await exchange.CancelOrderAsync(config.Symbol, dcaId); } catch { }
            Log(strategy, "Info", $"Отменён висящий DCA лимит #{dcaId} (TP закрыл позицию)");
        }

        ResetPositionState(state);
        state.SkipNextCandle = true;
        return true;
    }

    // ───────── Sync helpers ─────────

    /// <summary>
    /// On first ProcessAsync after worker boot, reconcile in-memory state with real exchange position.
    /// - Live position exists but state says closed → rehydrate.
    /// - State says InPosition but exchange has none → record synthetic close, clear state.
    /// </summary>
    private async Task SyncFromExchangeOnStartup(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, bool isLongConfig, CancellationToken ct)
    {
        PositionDto? pos;
        try
        {
            pos = await exchange.GetPositionAsync(config.Symbol, config.Direction);
        }
        catch (NotSupportedException)
        {
            Log(strategy, "Warning", "Биржа не поддерживает GetPositionAsync — пропуск restart-sync");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmaDca restart-sync: GetPositionAsync failed for {Symbol}", config.Symbol);
            return;
        }

        var exchangeHasPosition = pos != null && pos.Quantity > 0;

        if (exchangeHasPosition)
        {
            // Rehydrate from exchange data.
            state.InPosition = true;
            state.IsLong = isLongConfig;
            state.TotalQuantity = pos!.Quantity;
            state.AverageEntryPrice = pos.EntryPrice > 0 ? pos.EntryPrice : state.AverageEntryPrice;
            state.TotalCost = state.AverageEntryPrice * state.TotalQuantity;
            state.CurrentTakeProfit = ComputeTakeProfit(state.AverageEntryPrice, config.TakeProfitPercent, isLongConfig);
            state.DcaLevel = await EstimateDcaLevelFromHistory(strategy.Id, isLongConfig, ct);
            // Individual fill prices aren't available from the exchange — seed with avg so the
            // "LastFill" trigger base has a reference after restart (conservative fallback).
            if (state.LastDcaPrice <= 0)
                state.LastDcaPrice = state.AverageEntryPrice;
            state.SkipNextCandle = true;
            state.DcaCooldownUntil = null;

            // Wipe any stale orders on this symbol (old limit TPs from before restart) before the
            // self-heal step places a fresh one next poll.
            try { await exchange.CancelAllOrdersAsync(config.Symbol); }
            catch (NotSupportedException) { }
            state.TakeProfitOrderId = null;

            Log(strategy, "Info",
                $"🔄 RESTART SYNC: найдена позиция на бирже — qty={state.TotalQuantity}, " +
                $"avg={Math.Round(state.AverageEntryPrice, 6)}, dcaLvl={state.DcaLevel}, " +
                $"TP={Math.Round(state.CurrentTakeProfit, 6)}");
            return;
        }

        if (state.InPosition)
        {
            // State thought we had a position but exchange says no → externally closed.
            Log(strategy, "Warning",
                $"🔄 RESTART SYNC: в state была позиция (qty={state.TotalQuantity}, avg={state.AverageEntryPrice}), " +
                "но на бирже её нет — очищаем state");

            // Clean any orphan limit TP left hanging after an external close.
            try { await exchange.CancelAllOrdersAsync(config.Symbol); }
            catch (NotSupportedException) { }

            ResetPositionState(state);
            state.SkipNextCandle = true;
        }
    }

    private async Task<int> EstimateDcaLevelFromHistory(Guid strategyId, bool isLong, CancellationToken ct)
    {
        var openSide = isLong ? "Buy" : "Sell";

        // Find the most recent Exit trade; count Entry/DCA trades of the current cycle after it.
        var latestExit = await _db.Trades
            .Where(t => t.StrategyId == strategyId
                        && (t.Status.StartsWith("TakeProfit") || t.Status.StartsWith("Exit")))
            .OrderByDescending(t => t.ExecutedAt)
            .FirstOrDefaultAsync(ct);

        var cycleEntryCount = await _db.Trades
            .Where(t => t.StrategyId == strategyId
                        && t.Side == openSide
                        && (t.Status == "Entry" || t.Status.StartsWith("DCA"))
                        && (latestExit == null || t.ExecutedAt > latestExit.ExecutedAt))
            .CountAsync(ct);

        return cycleEntryCount > 0 ? cycleEntryCount - 1 : 0;
    }

    /// <summary>
    /// Detects position closed externally (e.g. manual close via API/UI) by comparing
    /// in-memory state against DB state. Matches EmaBounce semantics.
    /// </summary>
    private async Task<bool> SyncIfClosedExternally(Strategy strategy, SmaDcaState state, CancellationToken ct)
    {
        if (!state.InPosition) return false;

        var dbValues = await _db.Entry(strategy).GetDatabaseValuesAsync(ct);
        if (dbValues == null) return false;

        var dbStateJson = dbValues.GetValue<string>(nameof(Strategy.StateJson));
        if (string.IsNullOrEmpty(dbStateJson)) return false;

        var dbState = JsonSerializer.Deserialize<SmaDcaState>(dbStateJson, JsonOptions);
        if (dbState == null) return false;

        if (state.InPosition && !dbState.InPosition)
        {
            _logger.LogInformation("Strategy {Id}: position closed externally — syncing state from DB", strategy.Id);
            Log(strategy, "Info", "Позиция закрыта извне (API/UI/ручной) — state синхронизирован из БД");
            await _db.Entry(strategy).ReloadAsync(ct);
            await _db.SaveChangesAsync(ct); // saves log
            return true;
        }

        return false;
    }

    private async Task<bool> IsWorkspaceTimerExpiredAsync(Strategy strategy, CancellationToken ct)
    {
        if (!strategy.WorkspaceId.HasValue) return false;

        var workspace = await _db.Workspaces.FindAsync(new object[] { strategy.WorkspaceId.Value }, ct);
        if (workspace == null || string.IsNullOrEmpty(workspace.ConfigJson)) return false;

        try
        {
            var cfg = JsonSerializer.Deserialize<WorkspaceSmaDcaConfig>(workspace.ConfigJson, JsonOptions);
            if (cfg == null || !cfg.TimerEnabled || !cfg.TimerExpiresAt.HasValue) return false;
            return cfg.TimerExpiresAt.Value <= DateTime.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    // ───────── Limit TP lifecycle ─────────

    /// <summary>
    /// Places a reduce-only LIMIT order at state.CurrentTakeProfit for state.TotalQuantity.
    /// On success, stores the order id in state.TakeProfitOrderId.
    /// </summary>
    private async Task PlaceTakeProfitLimit(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange)
    {
        if (!state.InPosition || state.TotalQuantity <= 0 || state.CurrentTakeProfit <= 0)
            return;

        var closeSide = state.IsLong ? "Sell" : "Buy";
        try
        {
            var result = await exchange.PlaceLimitOrderAsync(
                config.Symbol, closeSide, state.CurrentTakeProfit, state.TotalQuantity, reduceOnly: true);

            if (result.Success && !string.IsNullOrEmpty(result.OrderId))
            {
                state.TakeProfitOrderId = result.OrderId;
                Log(strategy, "Info",
                    $"🎯 TP лимит выставлен: {closeSide} {state.TotalQuantity} @ {Math.Round(state.CurrentTakeProfit, 6)} (id={result.OrderId})");
            }
            else
            {
                state.TakeProfitOrderId = null;
                Log(strategy, "Error", $"Не удалось выставить TP лимит: {result.ErrorMessage}");
                _logger.LogError("Strategy {Id}: TP placement failed: {Error}", strategy.Id, result.ErrorMessage);
            }
        }
        catch (NotSupportedException)
        {
            state.TakeProfitOrderId = null;
            Log(strategy, "Warning",
                "Биржа не поддерживает лимитные ордера — TP будет закрываться рынком на закрытии свечи");
        }
    }

    /// <summary>
    /// Cancels the active limit TP (if any). Always clears state.TakeProfitOrderId so the next
    /// poll/place is unambiguous.
    /// </summary>
    private async Task CancelTakeProfitLimit(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange)
    {
        if (string.IsNullOrEmpty(state.TakeProfitOrderId)) return;

        var orderId = state.TakeProfitOrderId;
        try
        {
            var ok = await exchange.CancelOrderAsync(config.Symbol, orderId);
            Log(strategy, ok ? "Info" : "Warning",
                ok
                    ? $"Отменён TP лимит #{orderId}"
                    : $"Не удалось отменить TP #{orderId} (возможно уже исполнен/отменён)");
        }
        catch (NotSupportedException)
        {
            Log(strategy, "Warning", "Биржа не поддерживает CancelOrderAsync");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmaDca: CancelOrderAsync failed for {OrderId}", orderId);
        }

        state.TakeProfitOrderId = null;
    }

    /// <summary>Cancel + place — used after a DCA fill recomputes avg/total qty.</summary>
    private async Task ReplaceTakeProfitLimit(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange)
    {
        await CancelTakeProfitLimit(strategy, config, state, exchange);
        await PlaceTakeProfitLimit(strategy, config, state, exchange);
    }

    /// <summary>
    /// Checks that the stored TP order is still live on the exchange. If it's Cancelled/Rejected
    /// or the exchange has no record of it, clears state.TakeProfitOrderId so the self-heal
    /// re-places it. Filled TPs are handled by CheckLimitTpFilled via the position-gone heuristic.
    /// Network/unsupported errors leave state untouched.
    /// </summary>
    private async Task VerifyTakeProfitAlive(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange)
    {
        if (string.IsNullOrEmpty(state.TakeProfitOrderId)) return;
        var orderId = state.TakeProfitOrderId;

        OrderStatusDto? status;
        try { status = await exchange.GetOrderAsync(config.Symbol, orderId); }
        catch (NotSupportedException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmaDca: TP verify GetOrderAsync failed for {Id}", orderId);
            return;
        }

        if (status == null)
        {
            Log(strategy, "Warning",
                $"TP лимит #{orderId} не найден на бирже — сбрасываю id, будет поставлен новый");
            state.TakeProfitOrderId = null;
            return;
        }

        if (status.Status == OrderLifecycleStatus.Cancelled || status.Status == OrderLifecycleStatus.Rejected)
        {
            Log(strategy, "Warning",
                $"TP лимит #{orderId} {status.Status} на бирже — сбрасываю id, будет поставлен новый");
            state.TakeProfitOrderId = null;
        }
    }

    // ───────── Entry limit lifecycle ─────────

    /// <summary>
    /// Places a maker-side LIMIT order for the first entry. Price is offset from candle close
    /// by EntryLimitOffsetPercent (below for Long Buy, above for Short Sell) so it rests on the book.
    /// </summary>
    private async Task PlaceEntryLimit(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, CandleDto candle, bool isLong)
    {
        var side = isLong ? "Buy" : "Sell";
        var offsetMul = isLong
            ? (1m - config.EntryLimitOffsetPercent / 100m)
            : (1m + config.EntryLimitOffsetPercent / 100m);
        var limitPrice = candle.Close * offsetMul;
        var quantity = config.PositionSizeUsd / limitPrice;

        Log(strategy, "Info",
            $"📈 ENTRY LIMIT {config.Direction}: {config.Symbol}, close={candle.Close}, " +
            $"offset={config.EntryLimitOffsetPercent}%, limit={Math.Round(limitPrice, 8)}, qty≈{quantity}");

        try
        {
            var result = await exchange.PlaceLimitOrderAsync(config.Symbol, side, limitPrice, quantity, reduceOnly: false);
            if (result.Success && !string.IsNullOrEmpty(result.OrderId))
            {
                state.EntryOrderId = result.OrderId;
                state.EntryOrderLimitPrice = result.FilledPrice ?? limitPrice;
                state.EntryOrderQuantity = result.FilledQuantity ?? quantity;
                state.EntryOrderPlacedAtCandleTime = candle.CloseTime;
                Log(strategy, "Info",
                    $"🎯 Entry лимит выставлен: {side} {state.EntryOrderQuantity} @ {Math.Round(state.EntryOrderLimitPrice, 6)} (id={result.OrderId})");
            }
            else
            {
                Log(strategy, "Error", $"Не удалось выставить Entry лимит: {result.ErrorMessage}");
                _logger.LogError("Strategy {Id}: Entry limit placement failed: {Error}", strategy.Id, result.ErrorMessage);
            }
        }
        catch (NotSupportedException)
        {
            Log(strategy, "Warning",
                "Биржа не поддерживает PlaceLimitOrderAsync — нельзя использовать OrderType=Limit. Переключи на Market.");
        }
    }

    /// <summary>
    /// Polls the pending entry limit. On Filled / Cancelled (with partial fill) → adopt as entry and
    /// place the reduce-only limit TP. On no-fill Cancelled/Rejected → clear pending fields.
    /// </summary>
    private async Task ProcessPendingEntry(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, bool isLong, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.EntryOrderId)) return;
        var orderId = state.EntryOrderId;

        OrderStatusDto? status;
        try { status = await exchange.GetOrderAsync(config.Symbol, orderId); }
        catch (NotSupportedException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmaDca: entry GetOrderAsync failed for {Id}", orderId);
            return;
        }
        if (status == null) return;

        switch (status.Status)
        {
            case OrderLifecycleStatus.Filled:
            {
                var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : state.EntryOrderLimitPrice;
                var filledQty = status.FilledQuantity > 0 ? status.FilledQuantity : state.EntryOrderQuantity;
                AdoptEntryFill(strategy, config, state, isLong, filledQty, fillPrice, orderId);
                ClearEntryPendingFields(state);
                await PlaceTakeProfitLimit(strategy, config, state, exchange);
                await _telegramSignalService.SendOpenPositionSignalAsync(
                    strategy, config.Symbol, config.Direction,
                    fillPrice * filledQty, fillPrice, state.CurrentTakeProfit, 0m, ct);
                break;
            }
            case OrderLifecycleStatus.Cancelled:
            case OrderLifecycleStatus.Rejected:
            {
                if (status.FilledQuantity > 0)
                {
                    var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : state.EntryOrderLimitPrice;
                    AdoptEntryFill(strategy, config, state, isLong, status.FilledQuantity, fillPrice, orderId);
                    ClearEntryPendingFields(state);
                    await PlaceTakeProfitLimit(strategy, config, state, exchange);
                }
                else
                {
                    Log(strategy, "Warning", $"Entry лимит #{orderId} отменён/отклонён — позиции нет");
                    ClearEntryPendingFields(state);
                }
                break;
            }
            default:
                // Open / PartiallyFilled / Unknown — keep waiting. Timeout handled per new bar.
                break;
        }
    }

    /// <summary>
    /// Cancels a pending entry limit (timeout path). If there was a partial fill on the exchange,
    /// adopts it as entry before clearing pending state.
    /// </summary>
    private async Task CancelEntryLimit(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, bool isLong)
    {
        if (string.IsNullOrEmpty(state.EntryOrderId)) return;
        var orderId = state.EntryOrderId;

        OrderStatusDto? status = null;
        try { status = await exchange.GetOrderAsync(config.Symbol, orderId); } catch { }

        try { await exchange.CancelOrderAsync(config.Symbol, orderId); } catch { }

        if (status != null && status.FilledQuantity > 0)
        {
            var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : state.EntryOrderLimitPrice;
            Log(strategy, "Info",
                $"Entry лимит #{orderId} отменён с частичным филлом {status.FilledQuantity} @ {fillPrice} — принимаем как вход");
            AdoptEntryFill(strategy, config, state, isLong, status.FilledQuantity, fillPrice, orderId);
            ClearEntryPendingFields(state);
            await PlaceTakeProfitLimit(strategy, config, state, exchange);
        }
        else
        {
            Log(strategy, "Info", $"Entry лимит #{orderId} отменён");
            ClearEntryPendingFields(state);
        }
    }

    /// <summary>Writes in-memory state for a first entry and records the Trade row.</summary>
    private void AdoptEntryFill(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        bool isLong, decimal qty, decimal price, string orderId)
    {
        state.InPosition = true;
        state.IsLong = isLong;
        state.TotalQuantity = qty;
        state.TotalCost = price * qty;
        state.AverageEntryPrice = price;
        state.CurrentTakeProfit = ComputeTakeProfit(price, config.TakeProfitPercent, isLong);
        state.DcaLevel = 0;
        state.LastDcaPrice = price;
        state.PositionOpenedAt = DateTime.UtcNow;
        state.SkipNextCandle = false;
        state.DcaCooldownUntil = null;

        RecordTrade(strategy, config.Symbol, isLong ? "Buy" : "Sell", qty, price, orderId, "Entry");
        Log(strategy, "Info",
            $"✅ ENTRY {config.Direction} (лимит filled): qty={qty} @ {price}, TP={Math.Round(state.CurrentTakeProfit, 6)}");
        _logger.LogInformation("Strategy {Id}: SmaDca LIMIT ENTRY {Dir} qty={Qty} @ {Price}",
            strategy.Id, config.Direction, qty, price);
    }

    // ───────── DCA limit lifecycle ─────────

    /// <summary>
    /// Places a maker-side LIMIT order for DCA. TP stays intact while DCA waits.
    /// Price offset from candle close by EntryLimitOffsetPercent so the order rests on the book.
    /// </summary>
    private async Task PlaceDcaLimit(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, CandleDto candle)
    {
        var targetQty = state.TotalQuantity * config.DcaMultiplier;
        if (targetQty <= 0)
        {
            Log(strategy, "Warning", $"DCA LIMIT пропущен: targetQty={targetQty} (TotalQty={state.TotalQuantity})");
            return;
        }

        var side = state.IsLong ? "Buy" : "Sell";
        var offsetMul = state.IsLong
            ? (1m - config.EntryLimitOffsetPercent / 100m)
            : (1m + config.EntryLimitOffsetPercent / 100m);
        var limitPrice = candle.Close * offsetMul;

        Log(strategy, "Info",
            $"➕ DCA LIMIT #{state.DcaLevel + 1}/{config.MaxDcaLevels} {config.Direction}: close={candle.Close}, " +
            $"offset={config.EntryLimitOffsetPercent}%, limit={Math.Round(limitPrice, 8)}, qty={targetQty}");

        try
        {
            var result = await exchange.PlaceLimitOrderAsync(config.Symbol, side, limitPrice, targetQty, reduceOnly: false);
            if (result.Success && !string.IsNullOrEmpty(result.OrderId))
            {
                state.DcaOrderId = result.OrderId;
                state.DcaOrderLimitPrice = result.FilledPrice ?? limitPrice;
                state.DcaOrderQuantity = result.FilledQuantity ?? targetQty;
                Log(strategy, "Info",
                    $"🎯 DCA лимит выставлен: {side} {state.DcaOrderQuantity} @ {Math.Round(state.DcaOrderLimitPrice, 6)} (id={result.OrderId})");
            }
            else
            {
                state.DcaCooldownUntil = DateTime.UtcNow.AddMinutes(DcaCooldownMinutes);
                Log(strategy, "Warning",
                    $"Ошибка постановки DCA лимита (cooldown {DcaCooldownMinutes}min): {result.ErrorMessage}");
                _logger.LogWarning("Strategy {Id}: DCA limit placement failed: {Error}",
                    strategy.Id, result.ErrorMessage);
            }
        }
        catch (NotSupportedException)
        {
            Log(strategy, "Warning", "Биржа не поддерживает PlaceLimitOrderAsync — DCA не будет поставлен");
        }
    }

    /// <summary>
    /// Polls the pending DCA limit. On Filled → update avg/qty, cancel old TP, place new TP.
    /// On Cancelled (partial) → adopt partial fill. On Cancelled (no fill) → clear pending.
    /// </summary>
    private async Task ProcessPendingDca(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.DcaOrderId)) return;
        var orderId = state.DcaOrderId;

        OrderStatusDto? status;
        try { status = await exchange.GetOrderAsync(config.Symbol, orderId); }
        catch (NotSupportedException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SmaDca: DCA GetOrderAsync failed for {Id}", orderId);
            return;
        }
        if (status == null) return;

        switch (status.Status)
        {
            case OrderLifecycleStatus.Filled:
            {
                var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : state.DcaOrderLimitPrice;
                var filledQty = status.FilledQuantity > 0 ? status.FilledQuantity : state.DcaOrderQuantity;
                AdoptDcaFill(strategy, config, state, filledQty, fillPrice, orderId);
                ClearDcaPendingFields(state);
                await ReplaceTakeProfitLimit(strategy, config, state, exchange);
                break;
            }
            case OrderLifecycleStatus.Cancelled:
            case OrderLifecycleStatus.Rejected:
            {
                if (status.FilledQuantity > 0)
                {
                    var fillPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : state.DcaOrderLimitPrice;
                    AdoptDcaFill(strategy, config, state, status.FilledQuantity, fillPrice, orderId);
                    ClearDcaPendingFields(state);
                    await ReplaceTakeProfitLimit(strategy, config, state, exchange);
                }
                else
                {
                    Log(strategy, "Warning", $"DCA лимит #{orderId} отменён/отклонён без филла");
                    ClearDcaPendingFields(state);
                }
                break;
            }
            default:
                // Open / PartiallyFilled / Unknown — keep waiting until TP closes or fill completes.
                break;
        }
    }

    /// <summary>Applies a DCA fill: updates avg/qty, bumps level, records the Trade row.</summary>
    private void AdoptDcaFill(Strategy strategy, SmaDcaConfig config, SmaDcaState state,
        decimal filledQty, decimal fillPrice, string orderId)
    {
        state.TotalCost += fillPrice * filledQty;
        state.TotalQuantity += filledQty;
        state.AverageEntryPrice = state.TotalCost / state.TotalQuantity;
        state.CurrentTakeProfit = ComputeTakeProfit(state.AverageEntryPrice, config.TakeProfitPercent, state.IsLong);
        state.DcaLevel++;
        state.LastDcaPrice = fillPrice;
        state.DcaCooldownUntil = null;

        RecordTrade(strategy, config.Symbol, state.IsLong ? "Buy" : "Sell", filledQty, fillPrice,
            orderId, $"DCA#{state.DcaLevel}");

        Log(strategy, "Info",
            $"✅ DCA #{state.DcaLevel} (лимит filled): qty={filledQty} @ {fillPrice} → " +
            $"totalQty={state.TotalQuantity}, avg={Math.Round(state.AverageEntryPrice, 6)}, " +
            $"newTP={Math.Round(state.CurrentTakeProfit, 6)}");
        _logger.LogInformation(
            "Strategy {Id}: SmaDca LIMIT DCA#{Lvl} {Dir} qty={Qty} @ {Price}",
            strategy.Id, state.DcaLevel, config.Direction, filledQty, fillPrice);

        AssertInvariant(strategy, state);
    }

    // ───────── Utilities ─────────

    private static decimal ComputeTakeProfit(decimal averagePrice, decimal tpPercent, bool isLong)
        => isLong
            ? averagePrice * (1m + tpPercent / 100m)
            : averagePrice * (1m - tpPercent / 100m);

    private static decimal ComputePnlPercent(bool isLong, decimal entry, decimal exit)
        => isLong
            ? (exit - entry) / entry * 100m
            : (entry - exit) / entry * 100m;

    private static void ResetPositionState(SmaDcaState state)
    {
        state.InPosition = false;
        state.TotalQuantity = 0;
        state.TotalCost = 0;
        state.AverageEntryPrice = 0;
        state.CurrentTakeProfit = 0;
        state.DcaLevel = 0;
        state.LastDcaPrice = 0;
        state.PositionOpenedAt = null;
        state.DcaCooldownUntil = null;
        state.TakeProfitOrderId = null;
        ClearEntryPendingFields(state);
        ClearDcaPendingFields(state);
    }

    private static void ClearEntryPendingFields(SmaDcaState state)
    {
        state.EntryOrderId = null;
        state.EntryOrderLimitPrice = 0;
        state.EntryOrderQuantity = 0;
        state.EntryOrderPlacedAtCandleTime = null;
    }

    private static void ClearDcaPendingFields(SmaDcaState state)
    {
        state.DcaOrderId = null;
        state.DcaOrderLimitPrice = 0;
        state.DcaOrderQuantity = 0;
    }

    private static int ParseTimeframeMinutes(string timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe)) return 0;
        var tf = timeframe.Trim().ToLowerInvariant();
        if (tf.Length < 2) return 0;
        var suffix = tf[^1];
        if (!int.TryParse(tf[..^1], out var n) || n <= 0) return 0;
        return suffix switch
        {
            'm' => n,
            'h' => n * 60,
            'd' => n * 60 * 24,
            _ => 0
        };
    }

    private void AssertInvariant(Strategy strategy, SmaDcaState state)
    {
        if (state.TotalQuantity <= 0) return;
        var implied = state.TotalCost / state.TotalQuantity;
        var diff = Math.Abs(implied - state.AverageEntryPrice);
        var tolerance = state.AverageEntryPrice * 0.0001m; // 0.01%
        if (diff > tolerance)
        {
            Log(strategy, "Warning",
                $"Invariant drift: TotalCost/TotalQty={implied} vs Avg={state.AverageEntryPrice} (diff={diff})");
            _logger.LogWarning("Strategy {Id}: SmaDca invariant drift: implied={Imp} avg={Avg}",
                strategy.Id, implied, state.AverageEntryPrice);
        }
    }

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

    private static void SaveState(Strategy strategy, SmaDcaState state)
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
