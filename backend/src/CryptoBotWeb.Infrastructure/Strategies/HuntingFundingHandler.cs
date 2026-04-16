using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

public class HuntingFundingHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.HuntingFunding;

    private readonly AppDbContext _db;
    private readonly ILogger<HuntingFundingHandler> _logger;
    private readonly ITelegramSignalService _telegramSignalService;

    public HuntingFundingHandler(AppDbContext db, ILogger<HuntingFundingHandler> logger,
        ITelegramSignalService telegramSignalService)
    {
        _db = db;
        _logger = logger;
        _telegramSignalService = telegramSignalService;
    }

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<HuntingFundingConfig>(strategy.ConfigJson, JsonOptions);
        if (config == null || string.IsNullOrEmpty(config.Symbol))
        {
            _logger.LogError("Invalid config for strategy {Id}", strategy.Id);
            Log(strategy, "Error", "Invalid config — symbol is empty");
            return;
        }

        var state = JsonSerializer.Deserialize<HuntingFundingState>(strategy.StateJson, JsonOptions)
                    ?? new HuntingFundingState();

        switch (state.Phase)
        {
            case HuntingFundingPhase.WaitingForFunding:
                await ProcessWaitingForFunding(strategy, config, state, exchange, ct);
                break;
            case HuntingFundingPhase.OrdersPlaced:
                await ProcessOrdersPlaced(strategy, config, state, exchange, ct);
                break;
            case HuntingFundingPhase.InPosition:
                await ProcessInPosition(strategy, config, state, exchange, ct);
                break;
            case HuntingFundingPhase.Cooldown:
                await ProcessCooldown(strategy, config, state, exchange, ct);
                break;
        }

        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
    }

    // ───────────────────── Phase: WaitingForFunding ─────────────────────

    private async Task ProcessWaitingForFunding(Strategy strategy, HuntingFundingConfig config,
        HuntingFundingState state, IFuturesExchangeService exchange, CancellationToken ct)
    {
        var funding = await exchange.GetFundingRateAsync(config.Symbol);
        if (funding == null)
        {
            _logger.LogWarning("Strategy {Id}: Failed to get funding rate for {Symbol}", strategy.Id, config.Symbol);
            return;
        }

        state.CurrentFundingRate = funding.Rate;
        state.NextFundingTime = funding.NextFundingTime;

        // Determine direction based on funding rate and enabled directions
        var ratePercent = Math.Abs(funding.Rate * 100m); // e.g. -0.012 → 1.2%
        var (rangeMin, rangeMax) = await GetWorkspaceFundingRangeAsync(strategy, ct);
        var inRange = ratePercent >= rangeMin && ratePercent <= rangeMax;

        string? direction = null;
        if (inRange)
        {
            if (funding.Rate < 0 && config.EnableLong && ratePercent >= config.MinFundingLong)
                direction = "Long";
            else if (funding.Rate > 0 && config.EnableShort && ratePercent >= config.MinFundingShort)
                direction = "Short";
        }

        state.Direction = direction;

        var currentPrice = await exchange.GetTickerPriceAsync(config.Symbol);
        if (currentPrice != null)
            state.LastPrice = currentPrice;

        if (direction == null)
        {
            if (!inRange)
            {
                _logger.LogDebug("Strategy {Id}: Funding {Rate:P4} outside range [{Min}%, {Max}%], skipping",
                    strategy.Id, funding.Rate, rangeMin, rangeMax);
            }
            else
            {
                _logger.LogDebug("Strategy {Id}: Funding rate {Rate:P4} below threshold or direction disabled, skipping",
                    strategy.Id, funding.Rate);
            }
            return;
        }

        var threshold = state.NextFundingTime.Value.AddSeconds(-config.SecondsBeforeFunding);
        if (DateTime.UtcNow < threshold)
        {
            _logger.LogDebug("Strategy {Id}: Waiting for funding. Rate={Rate}, Next={Next}, Direction={Dir}",
                strategy.Id, funding.Rate, funding.NextFundingTime, state.Direction);
            return;
        }

        // Time to place orders
        var price = currentPrice ?? state.LastPrice;
        if (price == null || price == 0)
        {
            Log(strategy, "Error", "Cannot place orders — no price available");
            return;
        }

        Log(strategy, "Info",
            $"Funding rate={funding.Rate}, direction={state.Direction}, placing {config.Levels.Count} limit orders at price={price}");

        int placedCount = 0;
        bool settlementInProgress = false;
        for (int i = 0; i < config.Levels.Count; i++)
        {
            var level = config.Levels[i];
            decimal limitPrice;
            string side;

            if (state.Direction == "Long")
            {
                limitPrice = price.Value * (1 - level.OffsetPercent / 100m);
                side = "Buy";
            }
            else
            {
                limitPrice = price.Value * (1 + level.OffsetPercent / 100m);
                side = "Sell";
            }

            if (limitPrice <= 0)
            {
                Log(strategy, "Warning", $"Level {i}: calculated price={limitPrice} is invalid, skipping");
                continue;
            }

            var quantity = level.SizeUsdt / limitPrice;
            if (quantity <= 0)
            {
                Log(strategy, "Warning", $"Level {i}: calculated quantity={quantity} is invalid, skipping");
                continue;
            }

            var result = await exchange.PlaceLimitOrderAsync(config.Symbol, side, limitPrice, quantity);
            if (result.Success)
            {
                state.PlacedOrders.Add(new PlacedOrderInfo
                {
                    OrderId = result.OrderId ?? "",
                    LevelIndex = i,
                    Side = side,
                    Price = limitPrice,
                    Quantity = quantity,
                    IsFilled = false
                });
                placedCount++;
                Log(strategy, "Info", $"Level {i}: {side} limit order placed at {Math.Round(limitPrice, 6)}, qty={Math.Round(quantity, 6)}, orderId={result.OrderId}");
            }
            else
            {
                Log(strategy, "Error", $"Level {i}: Failed to place {side} order at {Math.Round(limitPrice, 6)}: {result.ErrorMessage}");

                // BingX blocks trading ~60s before funding (settlement window).
                // No point retrying remaining levels — they will all fail too.
                if (result.ErrorMessage != null &&
                    result.ErrorMessage.Contains("settlement", StringComparison.OrdinalIgnoreCase))
                {
                    settlementInProgress = true;
                    Log(strategy, "Warning",
                        $"Funding settlement in progress — skipping remaining {config.Levels.Count - i - 1} levels. " +
                        $"Consider increasing SecondsBeforeFunding (current={config.SecondsBeforeFunding}) to ≥65 for BingX");
                    break;
                }
            }
        }

        if (placedCount > 0)
        {
            state.Phase = HuntingFundingPhase.OrdersPlaced;
            state.OrdersPlacedAt = DateTime.UtcNow;
            Log(strategy, "Info", $"Phase → OrdersPlaced: {placedCount}/{config.Levels.Count} orders placed");
            _logger.LogInformation("Strategy {Id}: Placed {Count} limit orders for {Symbol}, direction={Dir}",
                strategy.Id, placedCount, config.Symbol, state.Direction);
        }
        else if (settlementInProgress)
        {
            // Missed this funding window — skip to cooldown to wait for the next one
            Log(strategy, "Warning", "Missed funding window (settlement) — skipping to Cooldown");
            state.Phase = HuntingFundingPhase.Cooldown;
        }
        else
        {
            Log(strategy, "Error", "Zero orders placed — staying in WaitingForFunding");
        }
    }

    // ───────────────────── Phase: OrdersPlaced ─────────────────────

    private async Task ProcessOrdersPlaced(Strategy strategy, HuntingFundingConfig config,
        HuntingFundingState state, IFuturesExchangeService exchange, CancellationToken ct)
    {
        var openOrders = await exchange.GetOpenOrdersAsync(config.Symbol);
        var openOrderIds = new HashSet<string>(openOrders.Select(o => o.OrderId));

        // Track newly filled orders
        var previouslyFilledIds = state.PlacedOrders.Where(o => o.IsFilled).Select(o => o.OrderId).ToHashSet();
        foreach (var placed in state.PlacedOrders)
        {
            if (!placed.IsFilled && !openOrderIds.Contains(placed.OrderId))
                placed.IsFilled = true;
        }
        var newFills = state.PlacedOrders.Where(o => o.IsFilled && !previouslyFilledIds.Contains(o.OrderId)).ToList();

        var currentPrice = await exchange.GetTickerPriceAsync(config.Symbol);
        if (currentPrice != null)
            state.LastPrice = currentPrice;

        // Record entry trades for newly detected fills
        foreach (var fill in newFills)
        {
            RecordTrade(strategy, config.Symbol, fill.Side, fill.Quantity, fill.Price, fill.OrderId);
            Log(strategy, "Info", $"Level {fill.LevelIndex} filled: {fill.Side} qty={Math.Round(fill.Quantity, 6)} at {Math.Round(fill.Price, 6)}");
        }

        // Update tracked totals from all fills so far
        var filledOrders = state.PlacedOrders.Where(o => o.IsFilled).ToList();
        if (filledOrders.Count > 0)
        {
            state.TotalFilledQuantity = filledOrders.Sum(o => o.Quantity);
            state.TotalFilledUsdt = filledOrders.Sum(o => o.Quantity * o.Price);
            state.AvgEntryPrice = state.TotalFilledUsdt / state.TotalFilledQuantity;
        }

        // Wait until 60s after funding time, then cancel remaining orders and transition
        bool timeoutReached = state.NextFundingTime.HasValue &&
                              DateTime.UtcNow > state.NextFundingTime.Value.AddSeconds(60);

        if (!timeoutReached)
            return;

        // Cancel all remaining open orders
        if (openOrders.Count > 0)
        {
            await exchange.CancelAllOrdersAsync(config.Symbol);
            Log(strategy, "Info", $"Cancelled {openOrders.Count} remaining orders after funding+60s");
        }

        if (filledOrders.Count == 0)
        {
            // No fills at all — go to cooldown
            Log(strategy, "Warning", "No fills after funding+60s timeout");
            state.Phase = HuntingFundingPhase.Cooldown;
            Log(strategy, "Info", "Phase → Cooldown (no fills timeout)");
            return;
        }

        // Verify actual position on exchange
        var side = state.Direction ?? "Long";
        var exchangePos = await exchange.GetPositionAsync(config.Symbol, side);

        if (exchangePos != null && exchangePos.Quantity > 0)
        {
            // Use real exchange data as source of truth
            state.TotalFilledQuantity = exchangePos.Quantity;
            state.AvgEntryPrice = exchangePos.EntryPrice;
            state.TotalFilledUsdt = exchangePos.Quantity * exchangePos.EntryPrice;

            Log(strategy, "Info",
                $"Exchange position verified: qty={Math.Round(exchangePos.Quantity, 6)}, avgEntry={Math.Round(exchangePos.EntryPrice, 6)} " +
                $"(tracked: {filledOrders.Count} fills, qty={Math.Round(filledOrders.Sum(o => o.Quantity), 6)})");
        }

        var avgEntry = state.AvgEntryPrice!.Value;

        if (state.Direction == "Long")
        {
            state.TakeProfit = avgEntry * (1 + config.TakeProfitPercent / 100m);
            state.StopLoss = avgEntry * (1 - config.StopLossPercent / 100m);
        }
        else
        {
            state.TakeProfit = avgEntry * (1 - config.TakeProfitPercent / 100m);
            state.StopLoss = avgEntry * (1 + config.StopLossPercent / 100m);
        }

        state.PositionOpenedAt = DateTime.UtcNow;
        state.RemainingOrdersCancelled = true;
        state.Phase = HuntingFundingPhase.InPosition;

        Log(strategy, "Info",
            $"Phase → InPosition: {filledOrders.Count} fills, avgEntry={Math.Round(avgEntry, 6)}, qty={Math.Round(state.TotalFilledQuantity!.Value, 6)}, " +
            $"TP={Math.Round(state.TakeProfit.Value, 6)}, SL={Math.Round(state.StopLoss.Value, 6)}");
        _logger.LogInformation("Strategy {Id}: Position opened — {Dir} avgEntry={Entry}, qty={Qty}, TP={TP}, SL={SL}",
            strategy.Id, state.Direction, avgEntry, state.TotalFilledQuantity, state.TakeProfit, state.StopLoss);

        await _telegramSignalService.SendOpenPositionSignalAsync(strategy, config.Symbol,
            state.Direction!, state.TotalFilledUsdt!.Value, state.AvgEntryPrice!.Value,
            state.TakeProfit.Value, state.StopLoss.Value, ct);
    }

    // ───────────────────── Phase: InPosition ─────────────────────

    private async Task ProcessInPosition(Strategy strategy, HuntingFundingConfig config,
        HuntingFundingState state, IFuturesExchangeService exchange, CancellationToken ct)
    {
        var currentPrice = await exchange.GetTickerPriceAsync(config.Symbol);
        if (currentPrice == null)
        {
            _logger.LogWarning("Strategy {Id}: Cannot get price in InPosition phase", strategy.Id);
            return;
        }
        state.LastPrice = currentPrice;

        string? closeReason = null;

        // Check TP
        if (state.Direction == "Long" && currentPrice >= state.TakeProfit)
            closeReason = "TakeProfit";
        else if (state.Direction == "Short" && currentPrice <= state.TakeProfit)
            closeReason = "TakeProfit";

        // Check SL
        if (closeReason == null)
        {
            if (state.Direction == "Long" && currentPrice <= state.StopLoss)
                closeReason = "StopLoss";
            else if (state.Direction == "Short" && currentPrice >= state.StopLoss)
                closeReason = "StopLoss";
        }

        // Check time limit
        if (closeReason == null && state.PositionOpenedAt.HasValue)
        {
            if (DateTime.UtcNow - state.PositionOpenedAt.Value > TimeSpan.FromMinutes(config.CloseAfterMinutes))
                closeReason = "TimeLimit";
        }

        if (closeReason == null)
            return;

        // Close position
        await ClosePosition(strategy, config, state, exchange, currentPrice.Value, closeReason, ct);
    }

    private async Task ClosePosition(Strategy strategy, HuntingFundingConfig config,
        HuntingFundingState state, IFuturesExchangeService exchange,
        decimal closePrice, string reason, CancellationToken ct)
    {
        // Get real position from exchange instead of relying on tracked quantity
        var side = state.Direction ?? "Long";
        var exchangePos = await exchange.GetPositionAsync(config.Symbol, side);

        decimal quantity;
        decimal avgEntry;
        decimal totalUsdt;

        if (exchangePos != null && exchangePos.Quantity > 0)
        {
            quantity = exchangePos.Quantity;
            avgEntry = exchangePos.EntryPrice;
            totalUsdt = quantity * avgEntry;

            if (state.TotalFilledQuantity.HasValue &&
                Math.Abs(quantity - state.TotalFilledQuantity.Value) / quantity > 0.01m)
            {
                Log(strategy, "Warning",
                    $"Position size mismatch: exchange={Math.Round(quantity, 6)}, tracked={Math.Round(state.TotalFilledQuantity.Value, 6)}");
            }
        }
        else
        {
            // Fallback to tracked data if exchange query fails
            quantity = state.TotalFilledQuantity ?? 0;
            avgEntry = state.AvgEntryPrice ?? 0;
            totalUsdt = state.TotalFilledUsdt ?? 0;
        }

        if (quantity <= 0)
        {
            Log(strategy, "Error", "Cannot close — position quantity is zero");
            state.Phase = HuntingFundingPhase.Cooldown;
            return;
        }

        OrderResultDto result;
        string closeSide;
        if (state.Direction == "Long")
        {
            result = await exchange.CloseLongAsync(config.Symbol, quantity);
            closeSide = "Sell";
        }
        else
        {
            result = await exchange.CloseShortAsync(config.Symbol, quantity);
            closeSide = "Buy";
        }

        if (!result.Success)
        {
            Log(strategy, "Error", $"Failed to close {state.Direction} position: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: Failed to close {Dir}: {Error}",
                strategy.Id, state.Direction, result.ErrorMessage);

            // If position doesn't exist on exchange, force transition to Cooldown
            if (result.ErrorMessage != null &&
                result.ErrorMessage.Contains("No position", StringComparison.OrdinalIgnoreCase))
            {
                Log(strategy, "Warning", "Position already closed externally — moving to Cooldown");
                state.Phase = HuntingFundingPhase.Cooldown;
            }
            return;
        }

        decimal pnlPercent;
        if (state.Direction == "Long")
            pnlPercent = avgEntry > 0 ? (closePrice - avgEntry) / avgEntry * 100m : 0;
        else
            pnlPercent = avgEntry > 0 ? (avgEntry - closePrice) / avgEntry * 100m : 0;

        var pnlDollar = totalUsdt * pnlPercent / 100m;
        var commission = totalUsdt * 2m * 0.0005m;
        var netPnl = pnlDollar - commission;

        RecordTrade(strategy, config.Symbol, closeSide, quantity, closePrice,
            result.OrderId, reason, pnlDollar: netPnl, commission: commission);

        state.CycleTotalPnl += netPnl;
        state.CycleCount++;
        state.Phase = HuntingFundingPhase.Cooldown;

        Log(strategy, reason == "TakeProfit" ? "Info" : "Warning",
            $"{state.Direction} closed ({reason}): price={closePrice}, entry={Math.Round(avgEntry, 6)}, qty={Math.Round(quantity, 6)}, " +
            $"PnL={Math.Round(pnlPercent, 4)}% (${Math.Round(netPnl, 2)}, commission=${Math.Round(commission, 2)}), " +
            $"cycle #{state.CycleCount}, totalPnl=${Math.Round(state.CycleTotalPnl, 2)}");
        _logger.LogInformation(
            "Strategy {Id}: {Dir} closed ({Reason}) at {Price}, entry={Entry}, qty={Qty}, PnL%={PnlPct}, NetPnl={NetPnl}, Cycle={Cycle}",
            strategy.Id, state.Direction, reason, closePrice, Math.Round(avgEntry, 6),
            Math.Round(quantity, 6), Math.Round(pnlPercent, 4), Math.Round(netPnl, 2), state.CycleCount);
    }

    // ───────────────────── Phase: Cooldown ─────────────────────

    private async Task ProcessCooldown(Strategy strategy, HuntingFundingConfig config,
        HuntingFundingState state, IFuturesExchangeService exchange, CancellationToken ct)
    {
        // Check max cycles
        if (config.MaxCycles > 0 && state.CycleCount >= config.MaxCycles)
        {
            strategy.Status = StrategyStatus.Stopped;
            Log(strategy, "Info", $"Max cycles reached ({state.CycleCount}/{config.MaxCycles}) — strategy stopped");
            _logger.LogInformation("Strategy {Id}: Max cycles {Count} reached — stopped",
                strategy.Id, state.CycleCount);
            return;
        }

        // Get next funding time
        var funding = await exchange.GetFundingRateAsync(config.Symbol);
        if (funding == null)
        {
            _logger.LogWarning("Strategy {Id}: Cannot get funding rate in Cooldown — will retry", strategy.Id);
            return;
        }

        // Reset state for next cycle
        state.PlacedOrders.Clear();
        state.AvgEntryPrice = null;
        state.TotalFilledQuantity = null;
        state.TotalFilledUsdt = null;
        state.TakeProfit = null;
        state.StopLoss = null;
        state.PositionOpenedAt = null;
        state.OrdersPlacedAt = null;
        state.RemainingOrdersCancelled = false;

        state.CurrentFundingRate = funding.Rate;
        state.NextFundingTime = funding.NextFundingTime;

        // Direction will be re-evaluated in WaitingForFunding based on thresholds
        state.Direction = null;
        state.Phase = HuntingFundingPhase.WaitingForFunding;

        Log(strategy, "Info",
            $"Phase → WaitingForFunding: nextFunding={funding.NextFundingTime:u}, rate={funding.Rate}");
    }

    // ───────────────────── Helpers ─────────────────────

    private async Task<(decimal min, decimal max)> GetWorkspaceFundingRangeAsync(Strategy strategy, CancellationToken ct)
    {
        if (!strategy.WorkspaceId.HasValue)
            return (1.0m, 2.0m);

        var workspace = await _db.Workspaces.FindAsync(new object[] { strategy.WorkspaceId.Value }, ct);
        if (workspace == null || string.IsNullOrEmpty(workspace.ConfigJson))
            return (1.0m, 2.0m);

        try
        {
            var cfg = JsonSerializer.Deserialize<WorkspaceHuntingFundingConfig>(workspace.ConfigJson, JsonOptions);
            if (cfg == null)
                return (1.0m, 2.0m);

            var min = cfg.FundingRateMin;
            var max = cfg.FundingRateMax;
            if (max < min) (min, max) = (max, min);
            return (min, max);
        }
        catch
        {
            return (1.0m, 2.0m);
        }
    }

    private static void SaveState(Strategy strategy, HuntingFundingState state)
    {
        strategy.StateJson = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private void RecordTrade(Strategy strategy, string symbol, string side, decimal quantity, decimal price,
        string? orderId, string? status = null, decimal? pnlDollar = null, decimal? commission = null)
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
            Status = status ?? "Filled",
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
