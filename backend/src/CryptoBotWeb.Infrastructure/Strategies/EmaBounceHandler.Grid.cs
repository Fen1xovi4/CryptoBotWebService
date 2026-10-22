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
/// MaratGGrid: the MaratG base position plus a grid of extra lots of GridLotSize each.
///
/// - Starter lot (level 0) opens at market with the base. It takes profit one grid step in our
///   favour from its own fill; after that a limit at the current price reopens it on the next
///   candle (re-placed at each later candle open while unfilled), as long as the base lives.
/// - Level k (k ≥ 1) sits at baseEntry ∓ k·GridStepPercent, strictly inside the stop loss and
///   capped by GridMaxLots. Only ONE level limit rests at a time — on the nearest free level; the
///   next one is placed on the poll after a fill, so a crash can't sweep every level in one go.
///   If price is already past that level, the limit fills at once at the better price.
/// - Each lot takes profit with a resting reduce-only limit one step from its own fill, for
///   exactly its quantity; the level can then refill.
/// - Base TP/SL cancels every grid order, books what filled meanwhile, then closes base and every
///   lot in one reduce-only market order.
/// - Martingale scales the lot by the same multiplier as the base (fixed for the cycle); grid PnL
///   feeds RunningPnlDollar, the base outcome alone decides win/loss.
///
/// Exchanges without real reduce-only limits (UsesSoftTakeProfit: Dzengi) keep the market grid:
/// lots bought at market when price reaches a level, closed at market when it reaches their TP.
///
/// One-way mode: base and lots are one net position on the exchange; the bot owns the split.
/// Prices come from the websocket quote stream (REST fallback), order states from the private
/// order stream (Bybit; REST elsewhere) so PnL is booked at real prices and fees.
/// </summary>
public partial class EmaBounceHandler
{
    private const double GridMaxQuoteAgeMs = 5000;
    private const int GridFillReadBackAttempts = 3;
    private const int GridFillReadBackDelayMs = 400;
    private static readonly TimeSpan GridFailBackoff = TimeSpan.FromSeconds(30);
    // An order we placed that the exchange can't find is only written off after this long —
    // right after placement the REST endpoints may not list it yet.
    private static readonly TimeSpan GridOrderGoneAfter = TimeSpan.FromMinutes(1);

    private static bool IsGrid(Strategy strategy) => strategy.Type == StrategyTypes.MaratGGrid;

    private static bool UsesGridLimits(IFuturesExchangeService exchange) => !exchange.UsesSoftTakeProfit;

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

    /// <summary>Level the single resting level limit belongs on: the shallowest one without a lot.</summary>
    public static int? GetGridTargetLevel(int maxLevel, IEnumerable<int> occupiedLevels)
    {
        var occupied = occupiedLevels.ToHashSet();
        for (var k = 1; k <= maxLevel; k++)
            if (!occupied.Contains(k)) return k;
        return null;
    }

    public static decimal GridLevelPrice(decimal baseEntry, int level, decimal stepPercent, bool isLong)
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

    private static bool IsTerminal(OrderLifecycleStatus status)
        => status is OrderLifecycleStatus.Filled or OrderLifecycleStatus.Cancelled or OrderLifecycleStatus.Rejected;

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

    /// <summary>Passive price for the starter reopen limit: best bid for a long, best ask for a short.</summary>
    private decimal StarterLimitPrice(Strategy strategy, string symbol, bool isLong, decimal price)
    {
        if (strategy.Account != null)
        {
            var quote = _quotes.TryGetQuote(strategy.Account.ExchangeType, symbol);
            if (quote is { IsValid: true } && quote.AgeMs(DateTime.UtcNow) <= GridMaxQuoteAgeMs)
                return isLong ? quote.Bid : quote.Ask;
        }
        return price;
    }

    // ────────────────────────── Cycle start ──────────────────────────

    /// <summary>Called right after the base opened: fixes the cycle's lot size and opens the starter lot.</summary>
    private async Task StartGridCycleAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal basePrice, CancellationToken ct)
    {
        state.GridLots.Clear();
        state.GridPendingOrders.Clear();
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
        var mode = UsesGridLimits(exchange) ? "лимитки, по одному уровню" : "маркет";
        Log(strategy, "Info",
            $"Сетка: лот={state.CycleGridLotSize} USDT, шаг={config.GridStepPercent}%{deepest}; докупки и TP: {mode}");

        if (!await OpenGridLotAsync(strategy, config, state, exchange, 0, isLong, basePrice, ct))
            state.GridStarterReopenAt = DateTime.UtcNow; // retried after the backoff
    }

    // ────────────────────────── Per-poll grid ──────────────────────────

    /// <summary>Grid fills, take-profits, level and starter orders. Returns true when state changed.</summary>
    private async Task<bool> ProcessGridAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal price, CancellationToken ct)
    {
        var position = state.OpenLong ?? state.OpenShort;
        if (position == null || state.CycleGridLotSize <= 0 || config.GridStepPercent <= 0)
            return false;

        return UsesGridLimits(exchange)
            ? await ProcessGridLimitsAsync(strategy, config, state, exchange, position, price, ct)
            : await ProcessGridMarketAsync(strategy, config, state, exchange, position, price, ct);
    }

    /// <summary>
    /// Limit grid, in the order of one poll:
    /// 1) resting buys that filled become lots; 2) lot TP limits that filled are booked;
    /// 3) lots without a TP limit get one; 4) the single level limit is kept on the nearest free
    /// level (not re-placed on the poll a level filled); 5) starter reopen limit.
    /// Reads never wait for the backoff; only new orders do.
    /// </summary>
    private async Task<bool> ProcessGridLimitsAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, OpenPositionInfo position, decimal price, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var isLong = state.OpenLong != null;
        var canPlace = !(state.GridRetryAfter.HasValue && now < state.GridRetryAfter.Value);
        var changed = false;

        // 1. Resting buys.
        var levelFilled = false;
        foreach (var order in state.GridPendingOrders.ToList())
        {
            var status = await TryReadGridOrderAsync(strategy, config.Symbol, exchange, order.OrderId, order.PlacedAt);
            if (status == null || !IsTerminal(status.Status)) continue;

            state.GridPendingOrders.Remove(order);
            changed = true;
            if (status.FilledQuantity > 0)
            {
                AdoptGridBuyFill(strategy, config, state, exchange, order, status, isLong);
                if (order.Level > 0) levelFilled = true;
            }
            else
            {
                Log(strategy, "Warning",
                    $"Сетка: лимитка L{order.Level} @ {order.Price} снята без исполнения ({status.Status}) — будет выставлена заново");
                if (order.Level == 0) state.GridStarterReopenAt = now;
            }
        }

        // 2. Lot take-profits that filled.
        foreach (var lot in state.GridLots.Where(l => l.TpOrderId != null).ToList())
        {
            var status = await TryReadGridOrderAsync(strategy, config.Symbol, exchange, lot.TpOrderId!,
                lot.TpPlacedAt ?? lot.OpenedAt);
            if (status == null || !IsTerminal(status.Status)) continue;

            changed = true;
            lot.TpOrderId = null;
            lot.TpPlacedAt = null;
            if (status.FilledQuantity > 0)
            {
                var whole = BookGridTpFill(strategy, config, state, exchange, lot, isLong, status, "GridTP");
                if (whole && lot.Level == 0)
                    state.GridStarterReopenAt = NextCandleOpen(config.Timeframe, now);
            }
            else
            {
                Log(strategy, "Warning",
                    $"Сетка: TP лота L{lot.Level} снят без исполнения ({status.Status}) — будет выставлен заново");
            }
        }

        // 3. TP limits for lots that have none (new fills, or a failed/cancelled placement).
        if (canPlace)
        {
            foreach (var lot in state.GridLots.Where(l => l.TpOrderId == null).ToList())
            {
                changed = true;
                if (!await PlaceGridTpLimitAsync(strategy, config, state, exchange, lot, isLong))
                    return true;
            }
        }

        // 4. The single level limit, on the nearest free level.
        var maxLevel = GetGridMaxLevel(config);
        var target = GetGridTargetLevel(maxLevel, state.GridLots.Select(l => l.Level));
        var levelOrder = state.GridPendingOrders.FirstOrDefault(o => o.Level > 0);
        if (levelOrder != null && levelOrder.Level != target)
        {
            changed = true;
            var (settled, filled) = await CancelGridBuyAsync(strategy, config, state, exchange, levelOrder, isLong,
                target.HasValue ? $"ближайший свободный уровень теперь L{target}" : "все уровни заняты");
            if (!settled) return true;
            if (filled) levelFilled = true;
            levelOrder = null;
        }
        if (levelOrder == null && target.HasValue && !levelFilled && canPlace)
        {
            changed = true;
            var levelPrice = GridLevelPrice(position.EntryPrice, target.Value, config.GridStepPercent, isLong);
            if (!await PlaceGridBuyLimitAsync(strategy, config, state, exchange, target.Value, levelPrice, isLong, null, price))
                return true;
        }

        // 5. Starter reopen: a limit at the current price from the next candle on, re-placed every
        //    candle open while it hasn't filled.
        var starterOrder = state.GridPendingOrders.FirstOrDefault(o => o.Level == 0);
        if (starterOrder?.RepriceAt is { } repriceAt && now >= repriceAt)
        {
            changed = true;
            var (settled, filled) = await CancelGridBuyAsync(strategy, config, state, exchange, starterOrder, isLong,
                "не исполнилась за свечу, переставляю по текущей цене");
            if (!settled) return true;
            if (!filled) state.GridStarterReopenAt = now;
            starterOrder = null;
        }
        if (starterOrder == null && canPlace && state.GridStarterReopenAt.HasValue && now >= state.GridStarterReopenAt.Value
            && state.GridLots.All(l => l.Level != 0))
        {
            changed = true;
            var limitPrice = StarterLimitPrice(strategy, config.Symbol, isLong, price);
            if (!await PlaceGridBuyLimitAsync(strategy, config, state, exchange, 0, limitPrice, isLong,
                    NextCandleOpen(config.Timeframe, now), price))
                return true;
            state.GridStarterReopenAt = null;
        }

        return changed;
    }

    /// <summary>Market grid for exchanges without real reduce-only limits (UsesSoftTakeProfit).</summary>
    private async Task<bool> ProcessGridMarketAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, OpenPositionInfo position, decimal price, CancellationToken ct)
    {
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

    // ────────────────────────── Limit orders ──────────────────────────

    private async Task<bool> PlaceGridBuyLimitAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, int level, decimal limitPrice, bool isLong, DateTime? repriceAt,
        decimal marketPrice)
    {
        var size = state.CycleGridLotSize;
        var side = isLong ? "Buy" : "Sell";
        var qty = size / limitPrice;
        var placedAt = DateTime.UtcNow;

        OrderResultDto result;
        try
        {
            result = await exchange.PlaceLimitOrderAsync(config.Symbol, side, limitPrice, qty, reduceOnly: false);
        }
        catch (Exception ex)
        {
            result = new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }

        if (!result.Success || string.IsNullOrEmpty(result.OrderId))
        {
            state.GridRetryAfter = DateTime.UtcNow + GridFailBackoff;
            Log(strategy, "Error",
                $"Сетка: ошибка выставления лимитки L{level} ({side} {size} USDT @ {Math.Round(limitPrice, 6)}): {result.ErrorMessage}. Повтор через {GridFailBackoff.TotalSeconds:0} с");
            _logger.LogError("MaratGGrid {Id}: failed to place grid limit L{Level}: {Error}",
                strategy.Id, level, result.ErrorMessage);
            return false;
        }

        SeedPlacedGridOrder(strategy, result.OrderId, placedAt);
        var order = new GridPendingOrder
        {
            Level = level,
            OrderId = result.OrderId,
            Price = result.FilledPrice ?? limitPrice,
            Quantity = result.FilledQuantity ?? qty,
            OrderSize = size,
            PlacedAt = placedAt,
            RepriceAt = repriceAt
        };
        state.GridPendingOrders.Add(order);
        state.GridRetryAfter = null;

        var crossed = isLong ? marketPrice <= order.Price : marketPrice >= order.Price;
        Log(strategy, "Info",
            $"Сетка: лимитка L{level} {side} qty={order.Quantity} @ {order.Price} ({size} USDT)" +
            (crossed ? $" — цена {marketPrice} уже за уровнем, исполнится сразу" : ""));
        return true;
    }

    private async Task<bool> PlaceGridTpLimitAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, GridLotInfo lot, bool isLong)
    {
        var side = isLong ? "Sell" : "Buy";
        var placedAt = DateTime.UtcNow;

        OrderResultDto result;
        try
        {
            result = await exchange.PlaceLimitOrderAsync(config.Symbol, side, lot.TakeProfit, lot.Quantity, reduceOnly: true);
        }
        catch (Exception ex)
        {
            result = new OrderResultDto { Success = false, ErrorMessage = ex.Message };
        }

        if (!result.Success || string.IsNullOrEmpty(result.OrderId))
        {
            state.GridRetryAfter = DateTime.UtcNow + GridFailBackoff;
            Log(strategy, "Error",
                $"Сетка: ошибка выставления TP лота L{lot.Level} ({side} qty={lot.Quantity} @ {Math.Round(lot.TakeProfit, 6)}): {result.ErrorMessage}. Повтор через {GridFailBackoff.TotalSeconds:0} с");
            _logger.LogError("MaratGGrid {Id}: failed to place grid TP L{Level}: {Error}",
                strategy.Id, lot.Level, result.ErrorMessage);
            return false;
        }

        SeedPlacedGridOrder(strategy, result.OrderId, placedAt);
        lot.TpOrderId = result.OrderId;
        lot.TpPlacedAt = placedAt;
        state.GridRetryAfter = null;
        Log(strategy, "Info",
            $"Сетка: TP лота L{lot.Level} {side} qty={lot.Quantity} @ {result.FilledPrice ?? Math.Round(lot.TakeProfit, 6)}");
        return true;
    }

    /// <summary>
    /// Cancels one resting buy and reads where it ended. Settled = the order is final (a fill
    /// that raced the cancel is adopted as a lot); not settled = state still unknown, keep it.
    /// </summary>
    private async Task<(bool Settled, bool Filled)> CancelGridBuyAsync(Strategy strategy, EmaBounceConfig config,
        EmaBounceState state, IFuturesExchangeService exchange, GridPendingOrder order, bool isLong, string why)
    {
        var status = await CancelAndReadGridOrderAsync(strategy, config.Symbol, exchange, order.OrderId, order.PlacedAt);
        if (status == null)
        {
            state.GridRetryAfter = DateTime.UtcNow + GridFailBackoff;
            Log(strategy, "Error",
                $"Сетка: не удалось снять лимитку L{order.Level} (ордер {order.OrderId}) — повтор через {GridFailBackoff.TotalSeconds:0} с");
            return (false, false);
        }

        state.GridPendingOrders.Remove(order);
        if (status.FilledQuantity > 0)
        {
            AdoptGridBuyFill(strategy, config, state, exchange, order, status, isLong);
            return (true, true);
        }

        Log(strategy, "Info", $"Сетка: лимитка L{order.Level} @ {order.Price} снята — {why}");
        return (true, false);
    }

    /// <summary>A filled (or partly filled, then cancelled) resting buy becomes a lot.</summary>
    private void AdoptGridBuyFill(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, GridPendingOrder order, OrderStatusDto status, bool isLong)
    {
        var lot = BuildLotFromFill(order, status, isLong, config.GridStepPercent, exchange.MakerFeeRate);
        state.GridLots.Add(lot);

        RecordTrade(strategy, config.Symbol, isLong ? "Buy" : "Sell", lot.Quantity, lot.EntryPrice, order.OrderId, "GridOpen");
        var partial = status.FilledQuantity < order.Quantity ? $" (частично: {status.FilledQuantity} из {order.Quantity})" : "";
        Log(strategy, "Info",
            $"Сетка: исполнена лимитка L{lot.Level} {(isLong ? "LONG" : "SHORT")} qty={lot.Quantity} @ {lot.EntryPrice}{partial}, TP={Math.Round(lot.TakeProfit, 6)}, лотов={state.GridLots.Count}");
    }

    public static GridLotInfo BuildLotFromFill(GridPendingOrder order, OrderStatusDto status, bool isLong,
        decimal stepPercent, decimal makerFeeRate)
    {
        var price = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : order.Price;
        var qty = status.FilledQuantity;
        var step = stepPercent / 100m;
        return new GridLotInfo
        {
            Level = order.Level,
            EntryPrice = price,
            Quantity = qty,
            OrderSize = order.Quantity > 0 ? Math.Round(order.OrderSize * qty / order.Quantity, 2) : order.OrderSize,
            TakeProfit = isLong ? price * (1 + step) : price * (1 - step),
            OpenedAt = DateTime.UtcNow,
            EntryFee = status.Fee ?? price * qty * makerFeeRate,
            ExchangeOrderId = order.OrderId
        };
    }

    /// <summary>
    /// The part of a lot a TP fill closed: the lot itself (removed from state) when the whole
    /// quantity filled, else a slice — the lot keeps the rest, entry fee and size split pro rata.
    /// </summary>
    public static GridLotInfo TakeGridLotPart(EmaBounceState state, GridLotInfo lot, decimal filledQty)
    {
        if (filledQty >= lot.Quantity)
        {
            state.GridLots.Remove(lot);
            return lot;
        }

        var share = filledQty / lot.Quantity;
        var part = new GridLotInfo
        {
            Level = lot.Level,
            EntryPrice = lot.EntryPrice,
            Quantity = filledQty,
            OrderSize = Math.Round(lot.OrderSize * share, 2),
            TakeProfit = lot.TakeProfit,
            OpenedAt = lot.OpenedAt,
            EntryFee = lot.EntryFee * share,
            ExchangeOrderId = lot.ExchangeOrderId
        };
        lot.Quantity -= filledQty;
        lot.OrderSize -= part.OrderSize;
        lot.EntryFee -= part.EntryFee;
        return part;
    }

    /// <summary>Books a lot's TP limit fill. True when the whole lot closed (its level is free again).</summary>
    private bool BookGridTpFill(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, GridLotInfo lot, bool isLong, OrderStatusDto status, string reason)
    {
        var whole = status.FilledQuantity >= lot.Quantity;
        var part = TakeGridLotPart(state, lot, status.FilledQuantity);
        var price = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : lot.TakeProfit;
        var fee = status.Fee ?? price * part.Quantity * exchange.MakerFeeRate;

        BookGridLot(strategy, config, state, exchange, part, isLong, price, fee, status.OrderId, reason);
        if (whole) state.GridLotsClosed++;
        else
            Log(strategy, "Warning",
                $"Сетка: TP лота L{lot.Level} исполнен частично ({part.Quantity}), остаток qty={lot.Quantity} закроется новым TP или с базой");
        return whole;
    }

    /// <summary>
    /// Cancels every grid order before the base closes and books what filled meanwhile: buy fills
    /// become lots (closed with the base), TP fills are booked as GridTP. An order that won't
    /// settle gets a cancel-all on the symbol as a last resort; the close goes ahead regardless —
    /// the stop must not wait on a stuck cancel.
    /// </summary>
    private async Task SettleGridOrdersAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, bool isLong, CancellationToken ct)
    {
        if (state.GridPendingOrders.Count == 0 && state.GridLots.All(l => l.TpOrderId == null))
            return;

        var unsettled = new List<string>();

        foreach (var order in state.GridPendingOrders.ToList())
        {
            var status = await CancelAndReadGridOrderAsync(strategy, config.Symbol, exchange, order.OrderId, order.PlacedAt);
            if (status == null) { unsettled.Add($"лимитка L{order.Level} {order.OrderId}"); continue; }
            state.GridPendingOrders.Remove(order);
            if (status.FilledQuantity > 0)
                AdoptGridBuyFill(strategy, config, state, exchange, order, status, isLong);
        }

        foreach (var lot in state.GridLots.Where(l => l.TpOrderId != null).ToList())
        {
            var status = await CancelAndReadGridOrderAsync(strategy, config.Symbol, exchange, lot.TpOrderId!,
                lot.TpPlacedAt ?? lot.OpenedAt);
            if (status == null) { unsettled.Add($"TP L{lot.Level} {lot.TpOrderId}"); continue; }
            lot.TpOrderId = null;
            lot.TpPlacedAt = null;
            if (status.FilledQuantity > 0)
                BookGridTpFill(strategy, config, state, exchange, lot, isLong, status, "GridTP");
        }

        state.GridPendingOrders.Clear();
        if (unsettled.Count == 0) return;

        var cancelledAll = false;
        try { cancelledAll = await exchange.CancelAllOrdersAsync(config.Symbol); }
        catch (Exception ex) { _logger.LogWarning(ex, "MaratGGrid {Id}: cancel-all failed", strategy.Id); }
        Log(strategy, "Error",
            $"Сетка: не удалось подтвердить снятие ордеров ({string.Join(", ", unsettled)}); отмена всех ордеров по {config.Symbol}: {(cancelledAll ? "ок" : "ошибка")}. Закрываю позицию — проверьте ордера и позицию на бирже");
    }

    /// <summary>
    /// Cancel, then read the order until it is final. Null when it couldn't be confirmed final.
    /// A cancel that "fails" because the order already filled is fine — the read tells.
    /// </summary>
    private async Task<OrderStatusDto?> CancelAndReadGridOrderAsync(Strategy strategy, string symbol,
        IFuturesExchangeService exchange, string orderId, DateTime placedAt)
    {
        try
        {
            await exchange.CancelOrderAsync(symbol, orderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MaratGGrid {Id}: cancel of {OrderId} threw", strategy.Id, orderId);
        }

        for (var attempt = 1; attempt <= GridFillReadBackAttempts; attempt++)
        {
            var status = await TryReadGridOrderAsync(strategy, symbol, exchange, orderId, placedAt,
                goneAfter: TimeSpan.Zero);
            if (status != null && IsTerminal(status.Status)) return status;
            if (attempt < GridFillReadBackAttempts) await Task.Delay(GridFillReadBackDelayMs);
        }
        return null;
    }

    /// <summary>
    /// Order state, or null when it can't be read now. An order the exchange can't find is
    /// reported as Cancelled with nothing filled — but only once it is older than
    /// <paramref name="goneAfter"/> (default <see cref="GridOrderGoneAfter"/>).
    /// </summary>
    private async Task<OrderStatusDto?> TryReadGridOrderAsync(Strategy strategy, string symbol,
        IFuturesExchangeService exchange, string orderId, DateTime placedAt, TimeSpan? goneAfter = null)
    {
        try
        {
            var status = await GetGridOrderStatusAsync(strategy, symbol, exchange, orderId);
            if (status != null) return status;
            if (DateTime.UtcNow - placedAt < (goneAfter ?? GridOrderGoneAfter)) return null;
            return new OrderStatusDto { OrderId = orderId, Status = OrderLifecycleStatus.Cancelled };
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MaratGGrid {Id}: order {OrderId} read failed: {Error}", strategy.Id, orderId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// API side (manual close, stop): cancels the grid's resting orders over REST and folds what
    /// filled into state — buy fills become lots, TP fills are cut out of their lots and returned
    /// for the caller to book. Orders whose final state can't be read are counted as unsettled.
    /// </summary>
    public static async Task<GridOrdersCancelResult> CancelGridOrdersOverRestAsync(IFuturesExchangeService exchange,
        string symbol, EmaBounceState state, decimal stepPercent)
    {
        var isLong = state.OpenShort == null;
        var result = new GridOrdersCancelResult();

        async Task<OrderStatusDto?> CancelAndRead(string orderId)
        {
            try { await exchange.CancelOrderAsync(symbol, orderId); } catch { /* the read decides */ }
            for (var attempt = 1; attempt <= GridFillReadBackAttempts; attempt++)
            {
                try
                {
                    var status = await exchange.GetOrderAsync(symbol, orderId);
                    if (status == null) return new OrderStatusDto { OrderId = orderId, Status = OrderLifecycleStatus.Cancelled };
                    if (IsTerminal(status.Status)) return status;
                }
                catch { /* retry */ }
                if (attempt < GridFillReadBackAttempts) await Task.Delay(GridFillReadBackDelayMs);
            }
            return null;
        }

        foreach (var order in state.GridPendingOrders.ToList())
        {
            var status = await CancelAndRead(order.OrderId);
            if (status == null) { result.Unsettled++; continue; }
            if (status.FilledQuantity > 0)
                state.GridLots.Add(BuildLotFromFill(order, status, isLong, stepPercent, exchange.MakerFeeRate));
        }
        state.GridPendingOrders.Clear();

        foreach (var lot in state.GridLots.Where(l => l.TpOrderId != null).ToList())
        {
            var status = await CancelAndRead(lot.TpOrderId!);
            lot.TpOrderId = null;
            lot.TpPlacedAt = null;
            if (status == null) { result.Unsettled++; continue; }
            if (status.FilledQuantity <= 0) continue;

            var price = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : lot.TakeProfit;
            var part = TakeGridLotPart(state, lot, status.FilledQuantity);
            result.TpFills.Add((part, price, status.Fee ?? price * part.Quantity * exchange.MakerFeeRate, status.OrderId));
        }

        if (result.Unsettled > 0)
        {
            try { await exchange.CancelAllOrdersAsync(symbol); } catch { /* reported via Unsettled */ }
        }
        return result;
    }

    private void SeedPlacedGridOrder(Strategy strategy, string orderId, DateTime placedAtUtc)
        => _orderStream.Seed(strategy.AccountId,
            new OrderStatusDto { OrderId = orderId, Status = OrderLifecycleStatus.Open }, placedAtUtc);

    // ────────────────────────── Market lots ──────────────────────────

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
    /// Base TP/SL for MaratGGrid: cancels the grid's resting orders (booking what filled), then one
    /// reduce-only market order for base + every lot, each part booked at the order's real fill
    /// price with the reported fee split by quantity.
    /// </summary>
    private async Task CloseBaseWithGridAsync(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal triggerPrice, string reason, CancellationToken ct)
    {
        var isLong = state.OpenLong != null;
        var position = (state.OpenLong ?? state.OpenShort)!;
        var side = isLong ? "LONG" : "SHORT";

        await SettleGridOrdersAsync(strategy, config, state, exchange, isLong, ct);

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
        state.GridPendingOrders.Clear();
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
