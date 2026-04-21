using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

public class FundingClaimHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.FundingClaim;

    private readonly AppDbContext _db;
    private readonly ILogger<FundingClaimHandler> _logger;
    private readonly ITelegramSignalService _telegramSignalService;
    private readonly ISymbolBlacklistService _blacklist;
    private readonly IFundingTickerRotationService _rotation;

    public FundingClaimHandler(AppDbContext db, ILogger<FundingClaimHandler> logger,
        ITelegramSignalService telegramSignalService,
        ISymbolBlacklistService blacklist,
        IFundingTickerRotationService rotation)
    {
        _db = db;
        _logger = logger;
        _telegramSignalService = telegramSignalService;
        _blacklist = blacklist;
        _rotation = rotation;
    }

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<FundingClaimConfig>(strategy.ConfigJson, JsonOptions);
        if (config == null || string.IsNullOrEmpty(config.Symbol))
        {
            _logger.LogError("Invalid config for strategy {Id}", strategy.Id);
            Log(strategy, "Error", "Invalid config — symbol is empty");
            return;
        }

        var state = JsonSerializer.Deserialize<FundingClaimState>(strategy.StateJson, JsonOptions)
                    ?? new FundingClaimState();

        switch (state.Phase)
        {
            case FundingClaimPhase.Idle:
                await ProcessIdle(strategy, config, state, exchange, ct);
                break;
            case FundingClaimPhase.InPosition:
                await ProcessInPosition(strategy, config, state, exchange, ct);
                break;
        }

        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
    }

    // ───────────────────── Phase: Idle ─────────────────────

    private async Task ProcessIdle(Strategy strategy, FundingClaimConfig config,
        FundingClaimState state, IFuturesExchangeService exchange, CancellationToken ct)
    {
        var wsCfg = await GetWorkspaceConfigAsync(strategy, ct);

        var funding = await exchange.GetFundingRateAsync(config.Symbol);
        if (funding == null)
        {
            _logger.LogWarning("Strategy {Id}: Failed to get funding rate for {Symbol}", strategy.Id, config.Symbol);
            return;
        }

        state.CurrentFundingRate = funding.Rate;
        state.NextFundingTime = funding.NextFundingTime;

        var ratePercent = Math.Abs(funding.Rate * 100m);

        var currentPrice = await exchange.GetTickerPriceAsync(config.Symbol);
        if (currentPrice != null)
            state.LastPrice = currentPrice;

        if (ratePercent < wsCfg.FcMinFundingRatePercent)
        {
            _logger.LogDebug("Strategy {Id}: Funding {Rate:P4} below threshold {Threshold}%, skipping",
                strategy.Id, funding.Rate, wsCfg.FcMinFundingRatePercent);
            return;
        }

        // Only enter in the hourly check window (CheckBeforeFundingMinutes before the hour)
        var now = DateTime.UtcNow;
        var minutesBeforeHour = 60 - now.Minute;
        if (minutesBeforeHour > config.CheckBeforeFundingMinutes)
        {
            _logger.LogDebug("Strategy {Id}: Funding OK but not in entry window (minute={Min}, need ≥{Need})",
                strategy.Id, now.Minute, 60 - config.CheckBeforeFundingMinutes);
            return;
        }

        // Determine direction: short when rate > 0 (shorts receive), long when rate < 0 (longs receive)
        string direction = funding.Rate > 0 ? "Short" : "Long";

        // Set leverage before opening position
        await exchange.SetLeverageAsync(config.Symbol, wsCfg.FcLeverage);

        // Open market order
        OrderResultDto result;
        if (direction == "Long")
            result = await exchange.OpenLongAsync(config.Symbol, wsCfg.FcSizeUsdt);
        else
            result = await exchange.OpenShortAsync(config.Symbol, wsCfg.FcSizeUsdt);

        if (!result.Success)
        {
            Log(strategy, "Error", $"Failed to open {direction} market order: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: Failed to open {Dir}: {Error}",
                strategy.Id, direction, result.ErrorMessage);

            if (IsUnsupportedSymbolError(result.ErrorMessage))
            {
                await BlacklistAndRotateAsync(strategy, config.Symbol, result.ErrorMessage!, ct);
            }
            return;
        }

        var entryPrice = result.FilledPrice ?? currentPrice ?? 0;
        var entryQty = result.FilledQuantity ?? (entryPrice > 0 ? wsCfg.FcSizeUsdt / entryPrice : 0);

        // Verify actual position on exchange
        var exchangePos = await exchange.GetPositionAsync(config.Symbol, direction);
        if (exchangePos != null && exchangePos.Quantity > 0)
        {
            entryPrice = exchangePos.EntryPrice;
            entryQty = exchangePos.Quantity;
        }

        state.Direction = direction;
        state.Symbol = config.Symbol;
        state.EntryPrice = entryPrice;
        state.EntryQuantity = entryQty;
        state.EntrySizeUsdt = entryQty * entryPrice;
        state.PositionOpenedAt = DateTime.UtcNow;
        state.LastHourlyCheckAt = DateTime.UtcNow;
        state.Phase = FundingClaimPhase.InPosition;

        RecordTrade(strategy, config.Symbol, direction == "Long" ? "Buy" : "Sell",
            entryQty, entryPrice, result.OrderId);

        Log(strategy, "Info",
            $"Opened {direction} market order: symbol={config.Symbol}, price={Math.Round(entryPrice, 6)}, " +
            $"qty={Math.Round(entryQty, 6)}, size=${Math.Round(state.EntrySizeUsdt ?? 0, 2)}, " +
            $"fundingRate={funding.Rate:P4}");
        _logger.LogInformation(
            "Strategy {Id}: Opened {Dir} {Symbol} at {Price}, qty={Qty}, funding={Rate}",
            strategy.Id, direction, config.Symbol, entryPrice, entryQty, funding.Rate);

        await _telegramSignalService.SendOpenPositionSignalAsync(strategy, config.Symbol,
            direction, state.EntrySizeUsdt ?? wsCfg.FcSizeUsdt, entryPrice, 0, 0, ct);
    }

    // ───────────────────── Phase: InPosition ─────────────────────

    private async Task ProcessInPosition(Strategy strategy, FundingClaimConfig config,
        FundingClaimState state, IFuturesExchangeService exchange, CancellationToken ct)
    {
        var symbol = state.Symbol ?? config.Symbol;

        // Update current price
        var currentPrice = await exchange.GetTickerPriceAsync(symbol);
        if (currentPrice != null)
            state.LastPrice = currentPrice;

        // Verify position still exists
        var posSide = state.Direction ?? "Long";
        var exchangePos = await exchange.GetPositionAsync(symbol, posSide);
        if (exchangePos == null || exchangePos.Quantity <= 0)
        {
            // Also try with "Both" side for one-way mode exchanges
            exchangePos = await exchange.GetPositionAsync(symbol, "Both");
            if (exchangePos == null || exchangePos.Quantity <= 0)
            {
                // Position closed externally (ADL, liquidation, manual close) — calculate PNL
                var closePrice = state.LastPrice ?? currentPrice ?? 0;
                var entryPrice = state.EntryPrice ?? 0;
                var quantity = state.EntryQuantity ?? 0;
                var totalUsdt = state.EntrySizeUsdt ?? 0;

                decimal pnlPercent = 0;
                if (entryPrice > 0)
                {
                    pnlPercent = posSide == "Long"
                        ? (closePrice - entryPrice) / entryPrice * 100m
                        : (entryPrice - closePrice) / entryPrice * 100m;
                }
                var pnlDollar = totalUsdt * pnlPercent / 100m;
                var commission = totalUsdt * 2m * 0.0005m;
                var netPnl = pnlDollar - commission;

                // Fetch definitive funding payments for this position
                var payments = await exchange.GetFundingPaymentsAsync(symbol, state.PositionOpenedAt);
                var positionFundingPnl = payments.Sum(p => p.Amount);

                var closeSide = posSide == "Long" ? "Sell" : "Buy";
                RecordTrade(strategy, symbol, closeSide, quantity, closePrice,
                    null, "ExternalClose", pnlDollar: netPnl, commission: commission,
                    fundingPnl: positionFundingPnl);

                state.CycleTotalPnl += netPnl;
                state.CycleTotalFundingPnl += positionFundingPnl;
                state.CurrentCycleFundingPnl = 0;
                state.CycleCount++;

                Log(strategy, "Warning",
                    $"Position closed externally — {posSide} {symbol}: closePrice={Math.Round(closePrice, 6)}, " +
                    $"entry={Math.Round(entryPrice, 6)}, tradingPnL=${Math.Round(netPnl, 2)}, " +
                    $"fundingPnL=${Math.Round(positionFundingPnl, 4)}, cycle #{state.CycleCount}");
                _logger.LogWarning("Strategy {Id}: Position gone externally for {Symbol} side={Side}, PnL=${Pnl}",
                    strategy.Id, symbol, posSide, Math.Round(netPnl, 2));
                await ResetToIdle(strategy, config, state, exchange, ct);
                return;
            }
        }

        // Update funding info for display
        var funding = await exchange.GetFundingRateAsync(symbol);
        if (funding != null)
        {
            state.CurrentFundingRate = funding.Rate;
            state.NextFundingTime = funding.NextFundingTime;
        }

        // ── Hourly check: N minutes before each hour ──
        // We check at minute (60 - CheckBeforeFundingMinutes) .. 59 of each hour.
        // Throttled by LastHourlyCheckAt to avoid re-checking every 5 seconds.
        var now = DateTime.UtcNow;
        var minutesBeforeHour = 60 - now.Minute;
        bool inCheckWindow = minutesBeforeHour <= config.CheckBeforeFundingMinutes;

        // Already checked this window?
        bool alreadyChecked = state.LastHourlyCheckAt.HasValue &&
                              (now - state.LastHourlyCheckAt.Value).TotalMinutes < 30;

        if (inCheckWindow && !alreadyChecked)
        {
            state.LastHourlyCheckAt = now;

            var wsCfg = await GetWorkspaceConfigAsync(strategy, ct);
            var ratePercent = Math.Abs(state.CurrentFundingRate ?? 0) * 100m;

            // Check sign consistency
            bool signOk = (state.Direction == "Long" && (state.CurrentFundingRate ?? 0) < 0) ||
                          (state.Direction == "Short" && (state.CurrentFundingRate ?? 0) > 0);

            bool rateOk = ratePercent >= wsCfg.FcMinFundingRatePercent;

            if (!rateOk || !signOk)
            {
                string reason = !signOk ? "FundingSignFlipped" : "FundingBelowThreshold";
                Log(strategy, "Info",
                    $"Hourly check: closing position — {reason} (rate={state.CurrentFundingRate:P4}, " +
                    $"threshold={wsCfg.FcMinFundingRatePercent}%)");
                await ClosePosition(strategy, config, state, exchange,
                    currentPrice ?? state.LastPrice ?? 0, reason, ct);
                return;
            }

            Log(strategy, "Info",
                $"Hourly check: funding OK (rate={state.CurrentFundingRate:P4}, " +
                $"threshold={wsCfg.FcMinFundingRatePercent}%) — keeping position");
        }

        // ── After funding payment: fetch payment data ──
        if (state.NextFundingTime.HasValue && now > state.NextFundingTime.Value.AddSeconds(30))
        {
            var payments = await exchange.GetFundingPaymentsAsync(symbol, state.PositionOpenedAt);
            if (payments.Count > 0)
            {
                // Use the full sum as the definitive total for this position
                decimal totalFunding = payments.Sum(p => p.Amount);
                decimal delta = totalFunding - state.CurrentCycleFundingPnl;
                if (delta != 0)
                {
                    state.CurrentCycleFundingPnl = totalFunding;
                    Log(strategy, "Info",
                        $"Funding payment: +${Math.Round(delta, 4)}, " +
                        $"position funding=${Math.Round(state.CurrentCycleFundingPnl, 4)}");
                }
            }

            // Refresh next funding time
            var nextFunding = await exchange.GetFundingRateAsync(symbol);
            if (nextFunding != null)
            {
                state.NextFundingTime = nextFunding.NextFundingTime;
                // DON'T update CurrentFundingRate here — it just reset after payment
            }
        }
    }

    // ───────────────────── Close Position ─────────────────────

    private async Task ClosePosition(Strategy strategy, FundingClaimConfig config,
        FundingClaimState state, IFuturesExchangeService exchange,
        decimal closePrice, string reason, CancellationToken ct)
    {
        var symbol = state.Symbol ?? config.Symbol;
        var side = state.Direction ?? "Long";

        var exchangePos = await exchange.GetPositionAsync(symbol, side);

        decimal quantity;
        decimal avgEntry;
        decimal totalUsdt;

        if (exchangePos != null && exchangePos.Quantity > 0)
        {
            quantity = exchangePos.Quantity;
            avgEntry = exchangePos.EntryPrice;
            totalUsdt = quantity * avgEntry;
        }
        else
        {
            quantity = state.EntryQuantity ?? 0;
            avgEntry = state.EntryPrice ?? 0;
            totalUsdt = state.EntrySizeUsdt ?? 0;
        }

        if (quantity <= 0)
        {
            Log(strategy, "Error", "Cannot close — position quantity is zero");
            await ResetToIdle(strategy, config, state, exchange, ct);
            return;
        }

        OrderResultDto result;
        string closeSide;
        if (state.Direction == "Long")
        {
            result = await exchange.CloseLongAsync(symbol, quantity);
            closeSide = "Sell";
        }
        else
        {
            result = await exchange.CloseShortAsync(symbol, quantity);
            closeSide = "Buy";
        }

        if (!result.Success)
        {
            Log(strategy, "Error", $"Failed to close {state.Direction} position: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: Failed to close {Dir}: {Error}",
                strategy.Id, state.Direction, result.ErrorMessage);

            if (result.ErrorMessage != null &&
                result.ErrorMessage.Contains("No position", StringComparison.OrdinalIgnoreCase))
            {
                Log(strategy, "Warning", "Position already closed externally — moving to Idle");
                await ResetToIdle(strategy, config, state, exchange, ct);
            }
            return;
        }

        // Calculate trading PnL
        decimal pnlPercent;
        if (state.Direction == "Long")
            pnlPercent = avgEntry > 0 ? (closePrice - avgEntry) / avgEntry * 100m : 0;
        else
            pnlPercent = avgEntry > 0 ? (avgEntry - closePrice) / avgEntry * 100m : 0;

        var pnlDollar = totalUsdt * pnlPercent / 100m;
        var commission = totalUsdt * 2m * 0.0005m;
        var netPnl = pnlDollar - commission;

        // Fetch definitive funding payments for this position
        var payments = await exchange.GetFundingPaymentsAsync(symbol, state.PositionOpenedAt);
        var positionFundingPnl = payments.Sum(p => p.Amount);

        RecordTrade(strategy, symbol, closeSide, quantity, closePrice,
            result.OrderId, reason, pnlDollar: netPnl, commission: commission,
            fundingPnl: positionFundingPnl);

        state.CycleTotalPnl += netPnl;
        state.CycleTotalFundingPnl += positionFundingPnl;
        state.CurrentCycleFundingPnl = 0;
        state.CycleCount++;

        Log(strategy, "Info",
            $"{state.Direction} closed ({reason}): price={closePrice}, entry={Math.Round(avgEntry, 6)}, " +
            $"qty={Math.Round(quantity, 6)}, tradingPnL=${Math.Round(netPnl, 2)}, " +
            $"fundingPnL=${Math.Round(positionFundingPnl, 4)}, commission=${Math.Round(commission, 2)}, " +
            $"cycle #{state.CycleCount}, totalPnl=${Math.Round(state.CycleTotalPnl, 2)}, " +
            $"totalFunding=${Math.Round(state.CycleTotalFundingPnl, 4)}");
        _logger.LogInformation(
            "Strategy {Id}: {Dir} closed ({Reason}) at {Price}, NetPnl={NetPnl}, FundingPnl={FundingPnl}, Cycle={Cycle}",
            strategy.Id, state.Direction, reason, closePrice, Math.Round(netPnl, 2),
            Math.Round(positionFundingPnl, 4), state.CycleCount);

        await ResetToIdle(strategy, config, state, exchange, ct);
    }

    // ───────────────────── Reset to Idle ─────────────────────

    private async Task ResetToIdle(Strategy strategy, FundingClaimConfig config,
        FundingClaimState state, IFuturesExchangeService exchange, CancellationToken ct)
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

        state.Direction = null;
        state.Symbol = null;
        state.EntryPrice = null;
        state.EntryQuantity = null;
        state.EntrySizeUsdt = null;
        state.PositionOpenedAt = null;
        state.LastHourlyCheckAt = null;
        state.CurrentCycleFundingPnl = 0;
        state.Phase = FundingClaimPhase.Idle;

        if (funding != null)
        {
            state.CurrentFundingRate = funding.Rate;
            state.NextFundingTime = funding.NextFundingTime;
        }

        Log(strategy, "Info",
            $"Phase → Idle: nextFunding={state.NextFundingTime:u}, rate={state.CurrentFundingRate:P4}");
    }

    // ───────────────────── Helpers ─────────────────────

    private async Task<WorkspaceFundingClaimConfig> GetWorkspaceConfigAsync(Strategy strategy, CancellationToken ct)
    {
        if (!strategy.WorkspaceId.HasValue)
            return new WorkspaceFundingClaimConfig();

        var workspace = await _db.Workspaces.FindAsync(new object[] { strategy.WorkspaceId.Value }, ct);
        if (workspace == null || string.IsNullOrEmpty(workspace.ConfigJson))
            return new WorkspaceFundingClaimConfig();

        try
        {
            return JsonSerializer.Deserialize<WorkspaceFundingClaimConfig>(workspace.ConfigJson, JsonOptions)
                   ?? new WorkspaceFundingClaimConfig();
        }
        catch
        {
            return new WorkspaceFundingClaimConfig();
        }
    }

    private static void SaveState(Strategy strategy, FundingClaimState state)
    {
        strategy.StateJson = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private void RecordTrade(Strategy strategy, string symbol, string side, decimal quantity, decimal price,
        string? orderId, string? status = null, decimal? pnlDollar = null, decimal? commission = null,
        decimal? fundingPnl = null)
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
            Commission = commission,
            FundingPnl = fundingPnl
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

    private static bool IsUnsupportedSymbolError(string? errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage)) return false;
        var msg = errorMessage.ToLowerInvariant();
        return msg.Contains("not supported")
            || msg.Contains("unsupported symbol")
            || msg.Contains("invalid symbol")
            || msg.Contains("symbol does not exist");
    }

    private async Task BlacklistAndRotateAsync(Strategy strategy, string symbol, string reason, CancellationToken ct)
    {
        try
        {
            await _blacklist.AddOrRefreshAsync(strategy.Account.ExchangeType, symbol, reason, ct);
            Log(strategy, "Warning",
                $"Symbol {symbol} blacklisted for 3 days (reason: {reason}). Triggering ticker rotation.");

            await _rotation.RotateTickersAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Strategy {Id}: Failed to blacklist/rotate after unsupported symbol {Symbol}",
                strategy.Id, symbol);
        }
    }
}
