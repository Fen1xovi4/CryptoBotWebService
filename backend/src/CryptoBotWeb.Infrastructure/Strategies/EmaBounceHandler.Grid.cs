using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

/// <summary>
/// MaratGGrid: the MaratG base position plus a grid of extra market lots of GridLotSize each.
///
/// - Starter lot (level 0) opens with the base. It takes profit one grid step in our favour from
///   its own fill; it is reopened at market on the next candle while the base is still open.
/// - Level k (k ≥ 1) sits at baseEntry ∓ k·GridStepPercent, strictly inside the stop loss and
///   capped by GridMaxLots. Price reaching a free level buys one lot; each lot takes profit one
///   step from its own fill (reduce-only, exactly its own quantity) and the level can then refill.
/// - Base TP/SL closes base and every lot in one reduce-only market order.
/// - Martingale scales the lot by the same multiplier as the base (fixed for the cycle); grid PnL
///   feeds RunningPnlDollar, the base outcome alone decides win/loss.
///
/// One-way mode: base and lots are one net position on the exchange; the bot owns the split.
/// Prices come from the websocket quote stream (REST fallback), market fills are read back from
/// the private order stream (Bybit; REST elsewhere) so PnL is booked at real prices and fees.
/// </summary>
public partial class EmaBounceHandler
{
    private const double GridMaxQuoteAgeMs = 5000;
    private const int GridFillReadBackAttempts = 3;
    private const int GridFillReadBackDelayMs = 400;
    private static readonly TimeSpan GridFailBackoff = TimeSpan.FromSeconds(30);

    private static bool IsGrid(Strategy strategy) => strategy.Type == StrategyTypes.MaratGGrid;

    /// <summary>Lot size the next cycle would use: GridLotSize × the base's martingale multiplier.</summary>
    public static decimal GetCurrentGridLotSize(EmaBounceConfig config, EmaBounceState state)
    {
        if (config.GridLotSize <= 0) return 0m;
        if (!config.UseMartingale || config.OrderSize <= 0) return config.GridLotSize;
        var (orderSize, _) = GetCurrentOrderSize(config, state);
        return Math.Round(config.GridLotSize * orderSize / config.OrderSize, 2);
    }

    /// <summary>
    /// Deepest level the grid may buy: k·step strictly inside the stop distance, capped by
    /// GridMaxLots when that is set. SL 10% / step 0.5% → 19 levels.
    /// </summary>
    public static int GetGridMaxLevel(EmaBounceConfig config)
    {
        if (config.GridStepPercent <= 0 || config.StopLossPercent <= 0) return 0;
        var levels = Math.Max(0, (int)Math.Ceiling(config.StopLossPercent / config.GridStepPercent) - 1);
        return config.GridMaxLots > 0 ? Math.Min(levels, config.GridMaxLots) : levels;
    }

    private static decimal GridLevelPrice(decimal baseEntry, int level, decimal stepPercent, bool isLong)
        => isLong
            ? baseEntry * (1 - level * stepPercent / 100m)
            : baseEntry * (1 + level * stepPercent / 100m);

    /// <summary>Open time of the candle after the one forming now (exchange candles are epoch-aligned).</summary>
    private static DateTime NextCandleOpen(string timeframe, DateTime nowUtc)
    {
        var span = SymbolHelper.GetTimeframeSpan(timeframe);
        var floor = new DateTime(nowUtc.Ticks - nowUtc.Ticks % span.Ticks, DateTimeKind.Utc);
        return floor + span;
    }

    private async Task EnsureGridStreamsAsync(Strategy strategy, EmaBounceConfig config, CancellationToken ct)
    {
        // Never block, never throw — until the streams are up everything falls back to REST.
        if (strategy.Account == null) return;
        await _orderStream.EnsureSubscribedAsync(strategy.Account, ct);
        await _quotes.EnsureSubscribedAsync(strategy.Account, config.Symbol, ct);
    }

    /// <summary>
    /// MaratGGrid: mid of a fresh websocket quote when one is streaming, else the REST ticker.
    /// Plain MaratG keeps the REST ticker it always used.
    /// </summary>
    private async Task<decimal?> GetTickPriceAsync(Strategy strategy, string symbol, IFuturesExchangeService exchange)
    {
        if (IsGrid(strategy) && strategy.Account != null)
        {
            var quote = _quotes.TryGetQuote(strategy.Account.ExchangeType, symbol);
            if (quote is { IsValid: true } && quote.AgeMs(DateTime.UtcNow) <= GridMaxQuoteAgeMs)
                return (quote.Bid + quote.Ask) / 2m;
        }
        return await exchange.GetTickerPriceAsync(symbol);
    }

    // ────────────────────────── Cycle start ──────────────────────────

    /// <summary>Called right after the base opened: fixes the cycle's lot size and opens the starter lot.</summary>
    private async Task StartGridCycleAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal basePrice, CancellationToken ct)
    {
        state.GridLots.Clear();
        state.GridStarterReopenAt = null;
        state.GridRetryAfter = null;
        state.CycleGridPnl = 0m;
        state.CycleGridLotSize = GetCurrentGridLotSize(config, state);

        if (state.CycleGridLotSize <= 0 || config.GridStepPercent <= 0)
        {
            Log(strategy, "Warning",
                $"Сетка выключена в этом цикле: сумма лота={state.CycleGridLotSize}, шаг={config.GridStepPercent}%");
            return;
        }

        var isLong = state.OpenLong != null;
        var maxLevel = GetGridMaxLevel(config);
        var deepest = maxLevel > 0
            ? $", уровни до {Math.Round(GridLevelPrice(basePrice, maxLevel, config.GridStepPercent, isLong), 6)} ({maxLevel} шт.)"
            : ", докупок нет (шаг ≥ стопа)";
        Log(strategy, "Info",
            $"Сетка: лот={state.CycleGridLotSize} USDT, шаг={config.GridStepPercent}%{deepest}");

        if (!await OpenGridLotAsync(strategy, config, state, exchange, 0, isLong, basePrice, ct))
            state.GridStarterReopenAt = DateTime.UtcNow; // retried after the backoff
    }

    // ────────────────────────── Per-poll grid ──────────────────────────

    /// <summary>Lot take-profits, starter reopen, level buys. Returns true when state changed.</summary>
    private async Task<bool> ProcessGridAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal price, CancellationToken ct)
    {
        var position = state.OpenLong ?? state.OpenShort;
        if (position == null || state.CycleGridLotSize <= 0 || config.GridStepPercent <= 0)
            return false;

        var now = DateTime.UtcNow;
        if (state.GridRetryAfter.HasValue && now < state.GridRetryAfter.Value)
            return false;

        var isLong = state.OpenLong != null;
        var changed = false;

        // 1. Lot take-profits.
        var hit = state.GridLots
            .Where(l => isLong ? price >= l.TakeProfit : price <= l.TakeProfit)
            .OrderBy(l => l.Level)
            .ToList();
        foreach (var lot in hit)
        {
            if (!await CloseGridLotAsync(strategy, config, state, exchange, lot, isLong, price, ct))
                return true;
            changed = true;
            if (lot.Level == 0)
                state.GridStarterReopenAt = NextCandleOpen(config.Timeframe, now);
        }

        // 2. Starter reopen on the next candle (the base is still open, or we wouldn't be here).
        if (state.GridStarterReopenAt.HasValue && now >= state.GridStarterReopenAt.Value
            && state.GridLots.All(l => l.Level != 0))
        {
            changed = true;
            if (!await OpenGridLotAsync(strategy, config, state, exchange, 0, isLong, price, ct))
                return true;
            state.GridStarterReopenAt = null;
        }

        // 3. Level buys against the base entry.
        var maxLevel = GetGridMaxLevel(config);
        for (var k = 1; k <= maxLevel; k++)
        {
            if (state.GridLots.Any(l => l.Level == k)) continue;
            var levelPrice = GridLevelPrice(position.EntryPrice, k, config.GridStepPercent, isLong);
            var reached = isLong ? price <= levelPrice : price >= levelPrice;
            if (!reached) continue;

            changed = true;
            if (!await OpenGridLotAsync(strategy, config, state, exchange, k, isLong, price, ct))
                return true;
        }

        return changed;
    }

    private async Task<bool> OpenGridLotAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, int level, bool isLong, decimal refPrice, CancellationToken ct)
    {
        var size = state.CycleGridLotSize;
        var side = isLong ? "LONG" : "SHORT";
        var result = isLong
            ? await exchange.OpenLongAsync(config.Symbol, size)
            : await exchange.OpenShortAsync(config.Symbol, size);
        if (!result.Success)
        {
            state.GridRetryAfter = DateTime.UtcNow + GridFailBackoff;
            Log(strategy, "Error",
                $"Сетка: ошибка открытия лота L{level} ({side}, {size} USDT): {result.ErrorMessage}. Повтор через {GridFailBackoff.TotalSeconds:0} с");
            _logger.LogError("MaratGGrid {Id}: failed to open grid lot L{Level}: {Error}",
                strategy.Id, level, result.ErrorMessage);
            return false;
        }

        var fillPrice = result.FilledPrice ?? refPrice;
        var fillQty = result.FilledQuantity ?? 0m;
        decimal? fee = null;
        var fill = await ReadBackMarketFillAsync(strategy, config.Symbol, exchange, result.OrderId, $"Сетка L{level}", ct);
        if (fill != null) (fillPrice, fillQty, fee) = fill.Value;

        if (fillQty <= 0)
        {
            // Can't track a lot whose size we don't know — it would be closed with the base anyway
            // only if we knew its qty. Stop the grid for this cycle rather than guess.
            state.GridRetryAfter = DateTime.MaxValue;
            Log(strategy, "Error",
                $"Сетка: лот L{level} открыт (ордер {result.OrderId}), но количество неизвестно — сетка остановлена до конца цикла, проверьте позицию на бирже");
            return false;
        }

        var step = config.GridStepPercent / 100m;
        var lot = new GridLotInfo
        {
            Level = level,
            EntryPrice = fillPrice,
            Quantity = fillQty,
            OrderSize = size,
            TakeProfit = isLong ? fillPrice * (1 + step) : fillPrice * (1 - step),
            OpenedAt = DateTime.UtcNow,
            EntryFee = fee,
            ExchangeOrderId = result.OrderId
        };
        state.GridLots.Add(lot);
        state.GridRetryAfter = null;

        RecordTrade(strategy, config.Symbol, isLong ? "Buy" : "Sell", fillQty, fillPrice, result.OrderId, "GridOpen");
        Log(strategy, "Info",
            $"Сетка: открыт лот L{level} {side} qty={fillQty} @ {fillPrice} ({size} USDT), TP={Math.Round(lot.TakeProfit, 6)}, лотов={state.GridLots.Count}");
        return true;
    }

    private async Task<bool> CloseGridLotAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, GridLotInfo lot, bool isLong, decimal triggerPrice, CancellationToken ct)
    {
        var result = isLong
            ? await exchange.CloseLongAsync(config.Symbol, lot.Quantity)
            : await exchange.CloseShortAsync(config.Symbol, lot.Quantity);
        if (!result.Success)
        {
            state.GridRetryAfter = DateTime.UtcNow + GridFailBackoff;
            Log(strategy, "Error",
                $"Сетка: ошибка закрытия лота L{lot.Level} qty={lot.Quantity}: {result.ErrorMessage}. Повтор через {GridFailBackoff.TotalSeconds:0} с");
            _logger.LogError("MaratGGrid {Id}: failed to close grid lot L{Level}: {Error}",
                strategy.Id, lot.Level, result.ErrorMessage);
            return false;
        }

        var exitPrice = result.FilledPrice ?? triggerPrice;
        decimal? exitFee = null;
        var fill = await ReadBackMarketFillAsync(strategy, config.Symbol, exchange, result.OrderId, $"Сетка TP L{lot.Level}", ct);
        if (fill != null) { exitPrice = fill.Value.Price; exitFee = fill.Value.Fee; }

        BookGridLot(strategy, config, state, exchange, lot, isLong, exitPrice, exitFee, result.OrderId, "GridTP");
        state.GridLots.Remove(lot);
        state.GridLotsClosed++;
        return true;
    }

    /// <summary>Books one lot's close: trade row with net PnL, grid totals, martingale running PnL.</summary>
    private void BookGridLot(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, GridLotInfo lot, bool isLong, decimal exitPrice, decimal? exitFee,
        string? orderId, string reason)
    {
        var gross = isLong
            ? lot.Quantity * (exitPrice - lot.EntryPrice)
            : lot.Quantity * (lot.EntryPrice - exitPrice);
        var commission = (lot.EntryFee ?? lot.EntryPrice * lot.Quantity * exchange.TakerFeeRate)
                         + (exitFee ?? exitPrice * lot.Quantity * exchange.TakerFeeRate);
        var net = gross - commission;

        state.GridRealizedPnl += net;
        state.CycleGridPnl += net;
        // Grid PnL counts toward the drawdown-scaled martingale; the base outcome alone sets win/loss.
        if (config.UseMartingale)
            state.RunningPnlDollar += gross;

        RecordTrade(strategy, config.Symbol, isLong ? "Sell" : "Buy", lot.Quantity, exitPrice, orderId, reason,
            pnlDollar: net, commission: commission);
        Log(strategy, net >= 0 ? "Info" : "Warning",
            $"Сетка: закрыт лот L{lot.Level} ({reason}) qty={lot.Quantity}: {lot.EntryPrice} → {exitPrice}, PnL=${Math.Round(net, 4)} (комиссия ${Math.Round(commission, 4)})");
    }

    // ────────────────────────── Base TP/SL ──────────────────────────

    /// <summary>
    /// Base TP/SL for MaratGGrid: one reduce-only order for base + every lot, then books each part
    /// at the order's real fill price with the reported fee split by quantity.
    /// </summary>
    private async Task CloseBaseWithGridAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal triggerPrice, string reason, CancellationToken ct)
    {
        var isLong = state.OpenLong != null;
        var position = (state.OpenLong ?? state.OpenShort)!;
        var side = isLong ? "LONG" : "SHORT";
        var lotsQty = state.GridLots.Sum(l => l.Quantity);
        var totalQty = position.Quantity + lotsQty;

        var result = isLong
            ? await exchange.CloseLongAsync(config.Symbol, totalQty)
            : await exchange.CloseShortAsync(config.Symbol, totalQty);
        if (!result.Success)
        {
            Log(strategy, "Error", $"Ошибка закрытия {side} (база + сетка, qty={totalQty}): {result.ErrorMessage}");
            _logger.LogError("MaratGGrid {Id}: failed to close {Side}: {Error}", strategy.Id, side, result.ErrorMessage);
            if (ExchangeErrorHelper.IsPositionGoneError(result.ErrorMessage))
                await HandleGridPositionGoneAsync(strategy, config, state, exchange, triggerPrice, reason, ct);
            return;
        }

        var closePrice = result.FilledPrice ?? triggerPrice;
        decimal? totalFee = null;
        var fillQty = totalQty;
        var fill = await ReadBackMarketFillAsync(strategy, config.Symbol, exchange, result.OrderId, $"{side} {reason}", ct);
        if (fill != null) (closePrice, fillQty, totalFee) = fill.Value;

        decimal? FeeShare(decimal qty) => totalFee.HasValue && fillQty > 0 ? totalFee.Value * qty / fillQty : null;

        foreach (var lot in state.GridLots)
            BookGridLot(strategy, config, state, exchange, lot, isLong, closePrice, FeeShare(lot.Quantity),
                result.OrderId, "Grid" + reason);
        var lotsClosed = state.GridLots.Count;
        ClearGridCycle(state);

        BookBaseClose(strategy, config, state, exchange, position, isLong, closePrice, FeeShare(position.Quantity),
            result.OrderId, reason, lotsClosed);
    }

    private void BookBaseClose(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, OpenPositionInfo position, bool isLong, decimal closePrice,
        decimal? exitFee, string? orderId, string reason, int lotsClosed)
    {
        var pnlPercent = isLong
            ? (closePrice - position.EntryPrice) / position.EntryPrice * 100m
            : (position.EntryPrice - closePrice) / position.EntryPrice * 100m;
        UpdateMartingaleState(config, state, pnlPercent, position.OrderSize);

        var gross = position.Quantity * (isLong ? closePrice - position.EntryPrice : position.EntryPrice - closePrice);
        var commission = (position.EntryFee ?? position.OrderSize * exchange.TakerFeeRate)
                         + (exitFee ?? closePrice * position.Quantity * exchange.TakerFeeRate);
        var netPnl = gross - commission;

        RecordTrade(strategy, config.Symbol, isLong ? "Sell" : "Buy", position.Quantity, closePrice, orderId, reason,
            pnlDollar: netPnl, commission: commission);
        ResetBaseAfterClose(state, isLong);

        var (nextSize, nextReason) = GetCurrentOrderSize(config, state);
        Log(strategy, reason.Contains("TakeProfit") ? "Info" : "Warning",
            $"{(isLong ? "LONG" : "SHORT")} закрыт ({reason}): цена={closePrice}, вход={position.EntryPrice}, PnL базы={Math.Round(pnlPercent, 4)}% (${Math.Round(netPnl, 2)}, комиссия=${Math.Round(commission, 2)}); " +
            $"сетка за цикл=${Math.Round(state.CycleGridPnl, 2)} (закрыто с базой лотов: {lotsClosed}); итог цикла=${Math.Round(netPnl + state.CycleGridPnl, 2)}");
        Log(strategy, "Info",
            $"Мартингейл: losses={state.ConsecutiveLosses}, runningPnl=${Math.Round(state.RunningPnlDollar, 2)}, следующая ставка={nextSize} ({nextReason}), лот сетки={GetCurrentGridLotSize(config, state)}");
        _logger.LogInformation("MaratGGrid {Id}: {Side} closed ({Reason}) at {Price}, base net={Net}, cycle grid={Grid}",
            strategy.Id, isLong ? "LONG" : "SHORT", reason, closePrice, Math.Round(netPnl, 2), Math.Round(state.CycleGridPnl, 2));
    }

    /// <summary>
    /// The close found no position on the exchange. If the API already closed it (manual close),
    /// adopt the DB state; otherwise book base and lots at the trigger price as an estimate.
    /// </summary>
    private async Task HandleGridPositionGoneAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal triggerPrice, string reason, CancellationToken ct)
    {
        var isLong = state.OpenLong != null;
        var position = (state.OpenLong ?? state.OpenShort)!;

        var dbValues = await _db.Entry(strategy).GetDatabaseValuesAsync(ct);
        var dbStateJson = dbValues?.GetValue<string>(nameof(Strategy.StateJson));
        EmaBounceState? dbState = null;
        if (!string.IsNullOrEmpty(dbStateJson))
            dbState = JsonSerializer.Deserialize<EmaBounceState>(dbStateJson, JsonOptions);

        if (dbState != null && dbState.OpenLong == null && dbState.OpenShort == null)
        {
            ResetBaseAfterClose(state, isLong);
            ClearGridCycle(state);
            state.ConsecutiveLosses = dbState.ConsecutiveLosses;
            state.RunningPnlDollar = dbState.RunningPnlDollar;
            state.GridRealizedPnl = dbState.GridRealizedPnl;
            state.GridLotsClosed = dbState.GridLotsClosed;
            Log(strategy, "Info", "Позиция закрыта извне — state синхронизирован из БД");
            return;
        }

        foreach (var lot in state.GridLots)
            BookGridLot(strategy, config, state, exchange, lot, isLong, triggerPrice, null, null, "Ext:Grid" + reason);
        var lotsClosed = state.GridLots.Count;
        ClearGridCycle(state);
        BookBaseClose(strategy, config, state, exchange, position, isLong, triggerPrice, null, null, "Ext:" + reason, lotsClosed);
        Log(strategy, "Warning", "Позиция уже закрыта на бирже — state очищен, PnL посчитан примерно по цене срабатывания");
    }

    private static void ResetBaseAfterClose(EmaBounceState state, bool isLong)
    {
        if (isLong)
        {
            state.OpenLong = null;
            state.LongCounter = 0;
            state.WaitNextCandleAfterLongClose = true;
        }
        else
        {
            state.OpenShort = null;
            state.ShortCounter = 0;
            state.WaitNextCandleAfterShortClose = true;
        }
        state.LastPrice = null;
    }

    private static void ClearGridCycle(EmaBounceState state)
    {
        state.GridLots.Clear();
        state.GridStarterReopenAt = null;
        state.GridRetryAfter = null;
    }

    // ────────────────────────── Fill read-back ──────────────────────────

    /// <summary>Real fill of the base's opening market order; falls back to what the service reported.</summary>
    private async Task<(decimal Price, decimal Qty, decimal? Fee)> ReadBackBaseFillAsync(Strategy strategy,
        EmaBounceConfig config, IFuturesExchangeService exchange, string? orderId, decimal price, decimal qty,
        string side, CancellationToken ct)
    {
        var fill = await ReadBackMarketFillAsync(strategy, config.Symbol, exchange, orderId, $"Открытие {side}", ct);
        return fill ?? (price, qty, null);
    }

    /// <summary>
    /// Reads a just-sent market order back: average fill price, filled quantity and the fee the
    /// exchange charged. The private websocket answers when it can be trusted, REST otherwise.
    /// Null when the order can't be read — the caller books the estimate and the log says so.
    /// </summary>
    private async Task<(decimal Price, decimal Qty, decimal? Fee)?> ReadBackMarketFillAsync(Strategy strategy,
        string symbol, IFuturesExchangeService exchange, string? orderId, string what, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(orderId)) return null;

        string? failure = null;
        for (var attempt = 1; attempt <= GridFillReadBackAttempts; attempt++)
        {
            try
            {
                var order = await GetGridOrderStatusAsync(strategy, symbol, exchange, orderId);
                // A market order that stopped part-way ends Cancelled with what it filled.
                var settled = order?.Status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled
                    or OrderLifecycleStatus.Rejected;
                if (order != null && order.FilledQuantity > 0 && order.AverageFilledPrice > 0 &&
                    (settled || attempt == GridFillReadBackAttempts))
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

            if (attempt < GridFillReadBackAttempts) await Task.Delay(GridFillReadBackDelayMs, ct);
        }

        Log(strategy, "Warning",
            $"{what}: не удалось прочитать ордер {orderId} с биржи ({failure}) — цена и комиссия взяты по оценке.");
        _logger.LogWarning("MaratGGrid {Id}: fill read-back failed for {Symbol} order {OrderId}: {Reason}",
            strategy.Id, symbol, orderId, failure);
        return null;
    }

    private async Task<OrderStatusDto?> GetGridOrderStatusAsync(Strategy strategy, string symbol,
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
}
