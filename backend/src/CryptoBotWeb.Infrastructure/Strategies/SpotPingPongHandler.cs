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
/// SpotPingPong — spot-only, long-only ping-pong on hand-picked levels (see SpotPingPongConfig).
///
/// Every configured level is an independent cell that loops forever:
///   WaitingBuy  : a limit BUY of AmountUsdt / BuyPrice coins rests at BuyPrice. On fill → size the
///                 sell from what actually landed (fill − buy fee when it was taken in the base coin,
///                 clamped by the free wallet balance) and place a limit SELL at SellPrice.
///   WaitingSell : the SELL rests. On fill → book the round trip (Trade + PnL), place the BUY again.
///
/// No stop-loss, no chasing: a level whose price ran away just keeps its order resting.
///
/// Order state is polled over REST every tick (5s). The exchange is the source of truth for fill
/// qty, average price and fee. An order the exchange no longer knows (cancelled by hand and purged)
/// is re-placed after <see cref="MaxPollMisses"/> consecutive misses; transport errors are not
/// misses. Placement refusals back off <see cref="PlacementBackoffMinutes"/> and surface on the card.
///
/// Stop (controller → <see cref="CancelAllOrdersAsync"/>) cancels resting orders and keeps the
/// coins; the next Start re-places the sells. Close (<see cref="ForceCloseAsync"/>) cancels
/// everything and market-sells every level's holdings.
/// </summary>
public class SpotPingPongHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const int MaxPollMisses = 12;              // ≈ 1 minute of "order not found"
    private const int PlacementBackoffMinutes = 2;
    private const decimal FeeSafetyBuffer = 0.0001m;    // dust margin when the fee asset is unknown

    public string StrategyType => StrategyTypes.SpotPingPong;

    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _factory;
    private readonly ILogger<SpotPingPongHandler> _logger;

    public SpotPingPongHandler(AppDbContext db, IExchangeServiceFactory factory, ILogger<SpotPingPongHandler> logger)
    {
        _db = db;
        _factory = factory;
        _logger = logger;
    }

    // ────────────────────────── Config validation ──────────────────────────

    /// <summary>
    /// Shared by the handler, the API (create/update) and the simulator. Returns null when the
    /// config is usable, otherwise a human-readable reason. <paramref name="feeRate"/> is the
    /// per-leg rate used to require SellPrice to clear two fees.
    /// </summary>
    public static string? ValidateConfig(SpotPingPongConfig? config, decimal feeRate = 0.001m)
    {
        if (config == null) return "ConfigJson не распарсился";
        if (string.IsNullOrWhiteSpace(config.Symbol)) return "Не задан символ";
        if (config.Levels == null || config.Levels.Count == 0) return "Нужен хотя бы один уровень";

        for (var i = 0; i < config.Levels.Count; i++)
        {
            var l = config.Levels[i];
            var n = i + 1;
            if (l.BuyPrice <= 0) return $"Уровень {n}: цена покупки должна быть > 0";
            if (l.SellPrice <= 0) return $"Уровень {n}: цена продажи должна быть > 0";
            if (l.AmountUsdt <= 0) return $"Уровень {n}: сумма в USDT должна быть > 0";
            var minSell = l.BuyPrice * (1m + 2m * feeRate + FeeSafetyBuffer);
            if (l.SellPrice <= minSell)
                return $"Уровень {n}: цена продажи {l.SellPrice} не покрывает две комиссии — нужно больше {Math.Round(minSell, 8)}";
        }
        return null;
    }

    // ────────────────────────── Tick ──────────────────────────

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService futures, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = Deserialize<SpotPingPongConfig>(strategy.ConfigJson);
        var state = Deserialize<SpotPingPongState>(strategy.StateJson) ?? new SpotPingPongState();

        ISpotExchangeService spot;
        try { spot = _factory.CreateSpot(strategy.Account); }
        catch (NotSupportedException ex)
        {
            Log(strategy, "Error", $"Спот недоступен на этой бирже: {ex.Message}. Бот остановлен.");
            strategy.Status = StrategyStatus.Stopped;
            await _db.SaveChangesAsync(ct);
            return;
        }

        using (spot)
        {
            var error = ValidateConfig(config, spot.MakerFeeRate);
            if (error != null)
            {
                Log(strategy, "Error", $"Некорректная конфигурация: {error}. Бот остановлен.");
                strategy.Status = StrategyStatus.Stopped;
                await _db.SaveChangesAsync(ct);
                return;
            }

            var cfg = config!;
            var symbol = SymbolHelper.NormalizeSymbol(cfg.Symbol);
            var (baseAsset, quoteAsset) = SymbolHelper.SplitSymbol(symbol);

            SyncLevelStates(cfg, state);

            try { state.LastPrice = await spot.GetTickerPriceAsync(symbol); }
            catch (Exception ex) { _logger.LogDebug(ex, "SpotPingPong {Id}: ticker fetch failed", strategy.Id); }
            state.LastTickAt = DateTime.UtcNow;

            (decimal qtyStep, decimal minQty, decimal priceStep)? filters = null;
            async Task<(decimal, decimal, decimal)> FiltersAsync()
                => filters ??= await spot.GetSymbolFiltersAsync(symbol);

            for (var i = 0; i < state.LevelStates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ls = state.LevelStates[i];
                var lvl = i < cfg.Levels.Count ? cfg.Levels[i] : null;

                if (ls.RetryAfter.HasValue && ls.RetryAfter > DateTime.UtcNow) continue;

                try
                {
                    if (ls.Phase == SpotPingPongLevelPhase.WaitingBuy)
                        await TickWaitingBuyAsync(strategy, spot, symbol, baseAsset, quoteAsset, cfg, state, ls, lvl, i, FiltersAsync, ct);
                    else
                        await TickWaitingSellAsync(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, i, FiltersAsync, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Transport / SDK failure on one level must not stall the others or wipe state.
                    _logger.LogWarning(ex, "SpotPingPong {Id}: level {Level} tick failed", strategy.Id, i + 1);
                }
            }

            // A level removed from the config that holds nothing and has no order can go.
            for (var i = state.LevelStates.Count - 1; i >= cfg.Levels.Count; i--)
            {
                var s = state.LevelStates[i];
                if (s.Phase == SpotPingPongLevelPhase.WaitingBuy && s.BuyOrderId == null)
                    state.LevelStates.RemoveAt(i);
            }

            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }
    }

    // Keep LevelStates aligned with config.Levels by index. New levels start WaitingBuy; a removed
    // level that still has an order or coins is kept past the end so its cycle can finish.
    private static void SyncLevelStates(SpotPingPongConfig cfg, SpotPingPongState state)
    {
        while (state.LevelStates.Count < cfg.Levels.Count)
            state.LevelStates.Add(new SpotPingPongLevelState());
    }

    // ────────────────────────── WaitingBuy ──────────────────────────

    private async Task TickWaitingBuyAsync(
        Strategy strategy, ISpotExchangeService spot, string symbol, string baseAsset, string quoteAsset,
        SpotPingPongConfig cfg, SpotPingPongState state, SpotPingPongLevelState ls, SpotPingPongLevel? lvl, int index,
        Func<Task<(decimal qtyStep, decimal minQty, decimal priceStep)>> filtersAsync, CancellationToken ct)
    {
        var n = index + 1;

        if (ls.BuyOrderId == null)
        {
            if (lvl == null) return; // orphan level without an order — dropped at the end of the tick
            await PlaceBuyAsync(strategy, spot, symbol, baseAsset, state, ls, lvl, n, filtersAsync, ct);
            return;
        }

        var status = await spot.GetOrderAsync(symbol, ls.BuyOrderId);
        if (status == null)
        {
            if (++ls.PollMisses < MaxPollMisses) return;
            Log(strategy, "Warning", $"Уровень {n}: биржа не находит ордер на покупку {ls.BuyOrderId} — выставлю заново");
            await spot.CancelOrderAsync(symbol, ls.BuyOrderId); // belt and braces: never leave two buys resting
            ls.BuyOrderId = null;
            ls.BuyQty = 0;
            ls.PollMisses = 0;
            return;
        }
        ls.PollMisses = 0;

        switch (status.Status)
        {
            case OrderLifecycleStatus.Filled:
                await OnBuyFilledAsync(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, n, status, filtersAsync, ct);
                break;

            case OrderLifecycleStatus.Cancelled:
            case OrderLifecycleStatus.Rejected:
                if (status.FilledQuantity > 0)
                {
                    // Partially filled, then cancelled by hand: sell what we got.
                    Log(strategy, "Warning", $"Уровень {n}: покупка {ls.BuyOrderId} отменена после частичного исполнения {status.FilledQuantity} {baseAsset} — продаю эту часть");
                    await OnBuyFilledAsync(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, n, status, filtersAsync, ct);
                }
                else
                {
                    Log(strategy, "Warning", $"Уровень {n}: покупка {ls.BuyOrderId} {(status.Status == OrderLifecycleStatus.Rejected ? "отклонена" : "отменена")} без исполнения — выставлю заново");
                    ls.BuyOrderId = null;
                    ls.BuyQty = 0;
                    if (status.Status == OrderLifecycleStatus.Rejected)
                        ls.RetryAfter = DateTime.UtcNow.AddMinutes(PlacementBackoffMinutes);
                }
                break;

            // Open / PartiallyFilled / Unknown: keep waiting. A partial fill stays on the book
            // until it completes; the sell is sized from the final fill.
        }
    }

    private async Task PlaceBuyAsync(
        Strategy strategy, ISpotExchangeService spot, string symbol, string baseAsset,
        SpotPingPongState state, SpotPingPongLevelState ls, SpotPingPongLevel lvl, int n,
        Func<Task<(decimal qtyStep, decimal minQty, decimal priceStep)>> filtersAsync, CancellationToken ct)
    {
        var (qtyStep, minQty, _) = await filtersAsync();
        var qty = FloorToStep(lvl.AmountUsdt / lvl.BuyPrice, qtyStep);
        if (qty <= 0 || qty < minQty)
        {
            Fail(strategy, ls, n, $"сумма {lvl.AmountUsdt} USDT даёт {qty} {baseAsset} — меньше минимума {minQty}");
            return;
        }

        var res = await spot.PlaceLimitBuyAsync(symbol, lvl.BuyPrice, qty);
        if (!res.Success || string.IsNullOrEmpty(res.OrderId))
        {
            Fail(strategy, ls, n, $"покупка отклонена: {res.ErrorMessage ?? "нет ответа"}");
            return;
        }

        ls.BuyOrderId = res.OrderId;
        ls.BuyQty = res.FilledQuantity ?? qty;
        ls.LastError = null;
        ls.RetryAfter = null;
        ls.PollMisses = 0;
        Log(strategy, "Info", $"Уровень {n}: лимит на покупку {ls.BuyQty} {baseAsset} @ {lvl.BuyPrice} (ордер {res.OrderId})");

        // Persist the order id right away so a crash between here and the end of the tick
        // can't leave an untracked order on the exchange.
        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
    }

    private async Task OnBuyFilledAsync(
        Strategy strategy, ISpotExchangeService spot, string symbol, string baseAsset, string quoteAsset,
        SpotPingPongState state, SpotPingPongLevelState ls, SpotPingPongLevel? lvl, int n, OrderStatusDto status,
        Func<Task<(decimal qtyStep, decimal minQty, decimal priceStep)>> filtersAsync, CancellationToken ct)
    {
        ApplyBuyFill(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, n, status);

        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);

        await PlaceSellAsync(strategy, spot, symbol, baseAsset, state, ls, lvl, n, filtersAsync, ct);
    }

    // Moves a level to WaitingSell from the exchange's view of the filled buy. Sizes the sell from
    // what actually landed in the wallet: fee in the base coin → subtract it exactly; fee in the
    // quote → the full qty landed; unknown asset → assume base and discount by the taker rate.
    private void ApplyBuyFill(
        Strategy strategy, ISpotExchangeService spot, string symbol, string baseAsset, string quoteAsset,
        SpotPingPongState state, SpotPingPongLevelState ls, SpotPingPongLevel? lvl, int n, OrderStatusDto status)
    {
        var gross = status.FilledQuantity;
        var price = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : (lvl?.BuyPrice ?? 0m);

        decimal net, buyFeeUsd;
        var feeInQuote = false;
        if (status.Fee.HasValue && Eq(status.FeeAsset, baseAsset))
        {
            net = gross - status.Fee.Value;
            buyFeeUsd = status.Fee.Value * price;
        }
        else if (status.Fee.HasValue && Eq(status.FeeAsset, quoteAsset))
        {
            net = gross;
            buyFeeUsd = status.Fee.Value;
            feeInQuote = true;
        }
        else
        {
            net = gross * (1m - spot.TakerFeeRate - FeeSafetyBuffer);
            buyFeeUsd = gross * price * spot.TakerFeeRate;
        }

        ls.Phase = SpotPingPongLevelPhase.WaitingSell;
        ls.FilledQty = gross;
        ls.FilledPrice = price;
        ls.BuyFeeUsd = buyFeeUsd;
        ls.BuyFeeInQuote = feeInQuote;
        ls.BoughtAt = DateTime.UtcNow;
        ls.SellQty = net;
        ls.SellOrderId = null;
        ls.PollMisses = 0;
        state.FeesUsd += buyFeeUsd;

        RecordTrade(strategy, symbol, "Buy", gross, price, ls.BuyOrderId, "Filled", commission: buyFeeUsd);
        Log(strategy, "Info", $"Уровень {n}: куплено {gross} {baseAsset} @ {price} (комиссия ≈ {Math.Round(buyFeeUsd, 4)} USDT)");

        ls.BuyOrderId = null;
        ls.BuyQty = 0;
    }

    // ────────────────────────── WaitingSell ──────────────────────────

    private async Task TickWaitingSellAsync(
        Strategy strategy, ISpotExchangeService spot, string symbol, string baseAsset, string quoteAsset,
        SpotPingPongState state, SpotPingPongLevelState ls, SpotPingPongLevel? lvl, int index,
        Func<Task<(decimal qtyStep, decimal minQty, decimal priceStep)>> filtersAsync, CancellationToken ct)
    {
        var n = index + 1;

        if (ls.SellOrderId == null)
        {
            await PlaceSellAsync(strategy, spot, symbol, baseAsset, state, ls, lvl, n, filtersAsync, ct);
            return;
        }

        var status = await spot.GetOrderAsync(symbol, ls.SellOrderId);
        if (status == null)
        {
            if (++ls.PollMisses < MaxPollMisses) return;
            Log(strategy, "Warning", $"Уровень {n}: биржа не находит ордер на продажу {ls.SellOrderId} — выставлю заново");
            await spot.CancelOrderAsync(symbol, ls.SellOrderId); // belt and braces: never leave two sells resting
            ls.SellOrderId = null;
            ls.PollMisses = 0;
            return;
        }
        ls.PollMisses = 0;

        switch (status.Status)
        {
            case OrderLifecycleStatus.Filled:
                await OnSellFilledAsync(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, n, status, status.FilledQuantity, filtersAsync, ct);
                break;

            case OrderLifecycleStatus.Cancelled:
            case OrderLifecycleStatus.Rejected:
                if (status.FilledQuantity > 0)
                {
                    // Partial sell then cancel: book the sold part, keep selling the remainder.
                    Log(strategy, "Warning", $"Уровень {n}: продажа {ls.SellOrderId} отменена после частичного исполнения {status.FilledQuantity} {baseAsset} — остаток выставлю заново");
                    await OnSellFilledAsync(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, n, status, status.FilledQuantity, filtersAsync, ct);
                }
                else
                {
                    Log(strategy, "Warning", $"Уровень {n}: продажа {ls.SellOrderId} {(status.Status == OrderLifecycleStatus.Rejected ? "отклонена" : "отменена")} без исполнения — выставлю заново");
                    ls.SellOrderId = null;
                    if (status.Status == OrderLifecycleStatus.Rejected)
                        ls.RetryAfter = DateTime.UtcNow.AddMinutes(PlacementBackoffMinutes);
                }
                break;
        }
    }

    private async Task PlaceSellAsync(
        Strategy strategy, ISpotExchangeService spot, string symbol, string baseAsset,
        SpotPingPongState state, SpotPingPongLevelState ls, SpotPingPongLevel? lvl, int n,
        Func<Task<(decimal qtyStep, decimal minQty, decimal priceStep)>> filtersAsync, CancellationToken ct)
    {
        if (lvl == null)
        {
            // Config level removed while coins are held: nothing sensible to sell at — leave the
            // coins and surface it. Close will still market-sell them.
            Fail(strategy, ls, n, "уровень удалён из настроек, монеты остались в кошельке — продайте вручную или нажмите «Закрыть»");
            return;
        }

        var (qtyStep, minQty, _) = await filtersAsync();

        // Clamp by what the wallet really has free (other levels' sells already lock theirs).
        var qty = ls.SellQty;
        try
        {
            var free = await spot.GetFreeBalanceAsync(baseAsset);
            if (free < qty) qty = free;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "SpotPingPong {Id}: balance fetch failed", strategy.Id); }

        qty = FloorToStep(qty, qtyStep);
        if (qty <= 0 || qty < minQty)
        {
            // Balance may lag a moment after the fill — retry next tick, don't back off long.
            Fail(strategy, ls, n, $"нечего продавать: доступно {qty} {baseAsset}, минимум {minQty}", backoffMinutes: 0);
            return;
        }

        var res = await spot.PlaceLimitSellAsync(symbol, lvl.SellPrice, qty);
        if (!res.Success || string.IsNullOrEmpty(res.OrderId))
        {
            Fail(strategy, ls, n, $"продажа отклонена: {res.ErrorMessage ?? "нет ответа"}");
            return;
        }

        ls.SellOrderId = res.OrderId;
        ls.SellQty = res.FilledQuantity ?? qty;
        ls.LastError = null;
        ls.RetryAfter = null;
        ls.PollMisses = 0;
        Log(strategy, "Info", $"Уровень {n}: лимит на продажу {ls.SellQty} {baseAsset} @ {lvl.SellPrice} (ордер {res.OrderId})");

        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
    }

    private async Task OnSellFilledAsync(
        Strategy strategy, ISpotExchangeService spot, string symbol, string baseAsset, string quoteAsset,
        SpotPingPongState state, SpotPingPongLevelState ls, SpotPingPongLevel? lvl, int n, OrderStatusDto status,
        decimal soldQty, Func<Task<(decimal qtyStep, decimal minQty, decimal priceStep)>> filtersAsync, CancellationToken ct)
    {
        var sellPrice = status.AverageFilledPrice > 0 ? status.AverageFilledPrice : (lvl?.SellPrice ?? 0m);
        var sellFeeUsd = status.Fee.HasValue
            ? (Eq(status.FeeAsset, baseAsset) ? status.Fee.Value * sellPrice : status.Fee.Value)
            : soldQty * sellPrice * spot.MakerFeeRate;

        var fullSell = ls.SellQty <= 0 || soldQty >= ls.SellQty;
        // Cost basis: we paid for the gross buy qty. A partial sell carries its share. A buy fee
        // taken out of the coins is already inside "paid for FilledQty, sold SellQty"; one taken
        // in USDT must be subtracted explicitly.
        var share = fullSell || ls.SellQty <= 0 ? 1m : soldQty / ls.SellQty;
        var costUsd = ls.FilledQty * ls.FilledPrice * share;
        var buyFeeUsd = ls.BuyFeeInQuote ? ls.BuyFeeUsd * share : 0m;
        var pnl = soldQty * sellPrice - sellFeeUsd - costUsd - buyFeeUsd;

        state.FeesUsd += sellFeeUsd;
        state.RealizedPnlUsd += pnl;
        ls.RealizedPnlUsd += pnl;

        RecordTrade(strategy, symbol, "Sell", soldQty, sellPrice, ls.SellOrderId, "Filled", pnlDollar: pnl, commission: sellFeeUsd);

        if (fullSell)
        {
            ls.CompletedCycles++;
            state.CompletedCycles++;
            Log(strategy, "Info", $"Уровень {n}: продано {soldQty} {baseAsset} @ {sellPrice}, цикл #{ls.CompletedCycles} закрыт, PnL {Math.Round(pnl, 4)} USDT (всего {Math.Round(state.RealizedPnlUsd, 4)})");

            ResetToWaitingBuy(ls);

            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);

            // Re-arm the buy at once instead of burning a tick.
            if (lvl != null)
                await PlaceBuyAsync(strategy, spot, symbol, baseAsset, state, ls, lvl, n, filtersAsync, ct);
        }
        else
        {
            Log(strategy, "Info", $"Уровень {n}: частично продано {soldQty} {baseAsset} @ {sellPrice}, PnL {Math.Round(pnl, 4)} USDT, остаток {ls.SellQty - soldQty}");
            ls.SellQty -= soldQty;
            ls.FilledQty *= 1m - share;
            ls.BuyFeeUsd *= 1m - share;
            ls.SellOrderId = null;
            ls.PollMisses = 0;

            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
        }
    }

    private static void ResetToWaitingBuy(SpotPingPongLevelState ls)
    {
        ls.Phase = SpotPingPongLevelPhase.WaitingBuy;
        ls.BuyOrderId = null;
        ls.BuyQty = 0;
        ls.SellOrderId = null;
        ls.SellQty = 0;
        ls.FilledQty = 0;
        ls.FilledPrice = 0;
        ls.BuyFeeUsd = 0;
        ls.BuyFeeInQuote = false;
        ls.BoughtAt = null;
        ls.PollMisses = 0;
    }

    // ────────────────────────── Stop / Close (called from the API) ──────────────────────────

    /// <summary>Stop: cancel every resting order, keep the coins; the state remembers the holdings.</summary>
    public async Task<int> CancelAllOrdersAsync(Strategy strategy, CancellationToken ct)
    {
        var config = Deserialize<SpotPingPongConfig>(strategy.ConfigJson);
        var state = Deserialize<SpotPingPongState>(strategy.StateJson) ?? new SpotPingPongState();
        if (config == null || string.IsNullOrWhiteSpace(config.Symbol)) return 0;

        var symbol = SymbolHelper.NormalizeSymbol(config.Symbol);
        var (baseAsset, quoteAsset) = SymbolHelper.SplitSymbol(symbol);
        ISpotExchangeService spot;
        try { spot = _factory.CreateSpot(strategy.Account); }
        catch (NotSupportedException) { return 0; }

        var cancelled = 0;
        using (spot)
        {
            for (var i = 0; i < state.LevelStates.Count; i++)
            {
                var ls = state.LevelStates[i];
                var lvl = i < config.Levels.Count ? config.Levels[i] : null;

                if (ls.BuyOrderId != null)
                {
                    // A buy that filled between the last tick and now must not be forgotten: read it once.
                    var st = await SafeGetOrderAsync(spot, symbol, ls.BuyOrderId);
                    if (st?.Status == OrderLifecycleStatus.Filled)
                    {
                        ApplyBuyFill(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, i + 1, st);
                    }
                    else
                    {
                        if (await spot.CancelOrderAsync(symbol, ls.BuyOrderId)) cancelled++;
                        if (st != null && st.FilledQuantity > 0)
                            ApplyBuyFill(strategy, spot, symbol, baseAsset, quoteAsset, state, ls, lvl, i + 1, st);
                        ls.BuyOrderId = null;
                        ls.BuyQty = 0;
                    }
                }
                if (ls.SellOrderId != null)
                {
                    var st = await SafeGetOrderAsync(spot, symbol, ls.SellOrderId);
                    if (st?.Status == OrderLifecycleStatus.Filled)
                    {
                        // Keep the id: the first tick after Start books the round trip properly.
                        continue;
                    }
                    if (await spot.CancelOrderAsync(symbol, ls.SellOrderId)) cancelled++;
                    if (st != null && st.FilledQuantity > 0 && st.FilledQuantity < ls.SellQty)
                    {
                        // Partial sell cancelled on Stop: book the sold share, keep the rest.
                        var share = st.FilledQuantity / ls.SellQty;
                        var pnl = st.FilledQuantity * st.AverageFilledPrice
                                  - ls.FilledQty * ls.FilledPrice * share
                                  - (ls.BuyFeeInQuote ? ls.BuyFeeUsd * share : 0m);
                        state.RealizedPnlUsd += pnl;
                        ls.RealizedPnlUsd += pnl;
                        RecordTrade(strategy, symbol, "Sell", st.FilledQuantity, st.AverageFilledPrice, ls.SellOrderId, "Filled", pnlDollar: pnl);
                        ls.FilledQty *= 1m - share;
                        ls.BuyFeeUsd *= 1m - share;
                        ls.SellQty -= st.FilledQuantity;
                    }
                    ls.SellOrderId = null;
                }
                ls.RetryAfter = null;
                ls.LastError = null;
                ls.PollMisses = 0;
            }
        }

        SaveState(strategy, state);
        Log(strategy, "Info", $"Стоп: отменено ордеров — {cancelled}; монеты остаются в кошельке");
        return cancelled;
    }

    public sealed record ForceCloseResult(bool Ok, string Message, int CancelledOrders, decimal SoldQty, decimal PnlUsd);

    /// <summary>Close: cancel everything and market-sell every level's holdings, then stop the bot.</summary>
    public async Task<ForceCloseResult> ForceCloseAsync(Strategy strategy, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = Deserialize<SpotPingPongConfig>(strategy.ConfigJson);
        if (config == null || string.IsNullOrWhiteSpace(config.Symbol))
            return new ForceCloseResult(false, "Некорректная конфигурация SpotPingPong", 0, 0, 0);

        var cancelled = await CancelAllOrdersAsync(strategy, ct);
        var state = Deserialize<SpotPingPongState>(strategy.StateJson) ?? new SpotPingPongState();

        var symbol = SymbolHelper.NormalizeSymbol(config.Symbol);
        var (baseAsset, _) = SymbolHelper.SplitSymbol(symbol);
        var holding = state.LevelStates.Where(l => l.Phase == SpotPingPongLevelPhase.WaitingSell && l.SellQty > 0).ToList();

        if (holding.Count == 0)
        {
            strategy.Status = StrategyStatus.Stopped;
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return new ForceCloseResult(true, cancelled > 0 ? $"Ордера отменены ({cancelled}), монет на продажу нет" : "Нет открытых ордеров и монет", cancelled, 0, 0);
        }

        decimal soldTotal = 0, pnlTotal = 0;
        using (var spot = _factory.CreateSpot(strategy.Account))
        {
            var (qtyStep, minQty, _) = await spot.GetSymbolFiltersAsync(symbol);
            var totalQty = holding.Sum(l => l.SellQty);
            try
            {
                var free = await spot.GetFreeBalanceAsync(baseAsset);
                if (free < totalQty) totalQty = free;
            }
            catch { /* best effort */ }
            totalQty = FloorToStep(totalQty, qtyStep);

            if (totalQty <= 0 || totalQty < minQty)
            {
                SaveState(strategy, state);
                await _db.SaveChangesAsync(ct);
                return new ForceCloseResult(false, $"Нечего продавать: доступно {totalQty} {baseAsset}, минимум {minQty}", cancelled, 0, 0);
            }

            var res = await spot.PlaceMarketSellAsync(symbol, totalQty);
            if (!res.Success)
            {
                SaveState(strategy, state);
                await _db.SaveChangesAsync(ct);
                return new ForceCloseResult(false, $"Рыночная продажа отклонена: {res.ErrorMessage}", cancelled, 0, 0);
            }

            var soldQty = res.FilledQuantity ?? totalQty;
            var avgPrice = res.FilledPrice ?? state.LastPrice ?? 0m;
            var feeUsd = soldQty * avgPrice * spot.TakerFeeRate;
            soldTotal = soldQty;

            // Attribute the sale across levels pro rata to their holdings.
            var heldTotal = holding.Sum(l => l.SellQty);
            foreach (var ls in holding)
            {
                var share = heldTotal > 0 ? ls.SellQty / heldTotal : 0m;
                var lvlSold = soldQty * share;
                var lvlFee = feeUsd * share;
                var pnl = lvlSold * avgPrice - lvlFee - ls.FilledQty * ls.FilledPrice - (ls.BuyFeeInQuote ? ls.BuyFeeUsd : 0m);
                pnlTotal += pnl;
                ls.RealizedPnlUsd += pnl;
                ls.CompletedCycles++;
                ResetToWaitingBuy(ls);
            }
            state.RealizedPnlUsd += pnlTotal;
            state.FeesUsd += feeUsd;
            state.CompletedCycles += holding.Count;
            RecordTrade(strategy, symbol, "Sell", soldQty, avgPrice, res.OrderId, "Filled", pnlDollar: pnlTotal, commission: feeUsd);
            Log(strategy, "Info", $"Закрытие: продано по рынку {soldQty} {baseAsset} @ {avgPrice}, PnL {Math.Round(pnlTotal, 4)} USDT");
        }

        strategy.Status = StrategyStatus.Stopped;
        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
        return new ForceCloseResult(true, "Ордера отменены, монеты проданы по рынку", cancelled, soldTotal, pnlTotal);
    }

    // ────────────────────────── Helpers ──────────────────────────

    private void Fail(Strategy strategy, SpotPingPongLevelState ls, int n, string message, int backoffMinutes = PlacementBackoffMinutes)
    {
        if (ls.LastError != message)
            Log(strategy, "Error", $"Уровень {n}: {message}");
        ls.LastError = message;
        ls.RetryAfter = backoffMinutes > 0 ? DateTime.UtcNow.AddMinutes(backoffMinutes) : null;
    }

    private static async Task<OrderStatusDto?> SafeGetOrderAsync(ISpotExchangeService spot, string symbol, string orderId)
    {
        try { return await spot.GetOrderAsync(symbol, orderId); }
        catch { return null; }
    }

    private static bool Eq(string? a, string b) => a != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0) return value;
        return Math.Floor(value / step) * step;
    }

    private static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch { return null; }
    }

    public static void SaveState(Strategy strategy, SpotPingPongState state)
        => strategy.StateJson = JsonSerializer.Serialize(state, JsonOptions);

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
