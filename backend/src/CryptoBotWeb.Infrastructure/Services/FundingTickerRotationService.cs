using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Services;

public class FundingTickerRotationService : IFundingTickerRotationService
{
    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _factory;
    private readonly ILogger<FundingTickerRotationService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FundingTickerRotationService(
        AppDbContext db,
        IExchangeServiceFactory factory,
        ILogger<FundingTickerRotationService> logger)
    {
        _db = db;
        _factory = factory;
        _logger = logger;
    }

    public async Task RotateTickersAsync(CancellationToken ct)
    {
        // 1. Load all HuntingFunding strategies with workspace + account
        var allStrategies = await _db.Strategies
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
            .Include(s => s.Workspace)
            .Where(s => s.Type == StrategyTypes.HuntingFunding && s.WorkspaceId != null)
            .ToListAsync(ct);

        if (allStrategies.Count == 0)
            return;

        // 2. Group by AccountId
        var accountGroups = allStrategies.GroupBy(s => s.AccountId).ToList();

        // 3. Fetch funding rates once per ExchangeType
        var fundingByExchangeType = new Dictionary<ExchangeType, List<FundingRateDto>>();

        foreach (var group in accountGroups)
        {
            var account = group.First().Account;
            if (fundingByExchangeType.ContainsKey(account.ExchangeType))
                continue;

            try
            {
                using var exchange = _factory.CreateFutures(account);
                var rates = await exchange.GetAllFundingRatesAsync();
                // Sort by absolute rate descending
                rates.Sort((a, b) => Math.Abs(b.Rate).CompareTo(Math.Abs(a.Rate)));
                fundingByExchangeType[account.ExchangeType] = rates;
                _logger.LogInformation(
                    "Fetched {Count} funding rates for {Exchange}, top rate: {TopSymbol} = {TopRate:P4}",
                    rates.Count, account.ExchangeType,
                    rates.FirstOrDefault()?.Symbol ?? "N/A",
                    rates.FirstOrDefault()?.Rate ?? 0m);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch funding rates for {Exchange}", account.ExchangeType);
                fundingByExchangeType[account.ExchangeType] = new List<FundingRateDto>();
            }
        }

        // 4. Build workspace-level symbol ownership map across ALL bots
        //    (workspaceId, UPPER symbol) -> set of strategy IDs currently holding it.
        //    Used to enforce workspace-wide ticker uniqueness regardless of account.
        var wsSymbolHolders = new Dictionary<(Guid wsId, string symbol), HashSet<Guid>>();
        foreach (var strategy in allStrategies)
        {
            if (strategy.WorkspaceId is not Guid wsId) continue;
            var sym = GetSymbol(strategy);
            if (string.IsNullOrEmpty(sym)) continue;
            var key = (wsId, sym.ToUpperInvariant());
            if (!wsSymbolHolders.TryGetValue(key, out var holders))
            {
                holders = new HashSet<Guid>();
                wsSymbolHolders[key] = holders;
            }
            holders.Add(strategy.Id);
        }

        bool IsTakenByOtherInWorkspace(Guid? workspaceId, string symbol, Guid currentStrategyId)
        {
            if (workspaceId is not Guid wsId) return false;
            if (string.IsNullOrEmpty(symbol)) return false;
            if (!wsSymbolHolders.TryGetValue((wsId, symbol.ToUpperInvariant()), out var holders))
                return false;
            return holders.Any(id => id != currentStrategyId);
        }

        void TransferOwnership(Guid? workspaceId, string? oldSymbol, string newSymbol, Guid strategyId)
        {
            if (workspaceId is not Guid wsId) return;
            if (!string.IsNullOrEmpty(oldSymbol)
                && wsSymbolHolders.TryGetValue((wsId, oldSymbol.ToUpperInvariant()), out var oldHolders))
            {
                oldHolders.Remove(strategyId);
            }
            var newKey = (wsId, newSymbol.ToUpperInvariant());
            if (!wsSymbolHolders.TryGetValue(newKey, out var newHolders))
            {
                newHolders = new HashSet<Guid>();
                wsSymbolHolders[newKey] = newHolders;
            }
            newHolders.Add(strategyId);
        }

        // 5. For each account group, assign tickers
        var updatedCount = 0;

        foreach (var group in accountGroups)
        {
            var account = group.First().Account;
            if (!fundingByExchangeType.TryGetValue(account.ExchangeType, out var rates) || rates.Count == 0)
                continue;

            // Separate strategies into locked (phases 1-3) and eligible (phase 0 / idle / stopped)
            var locked = new List<Strategy>();
            var eligible = new List<Strategy>();

            foreach (var strategy in group)
            {
                // Skip bots with auto-rotate disabled
                if (!IsAutoRotateEnabled(strategy))
                {
                    locked.Add(strategy);
                    continue;
                }

                if (strategy.Status == StrategyStatus.Running)
                {
                    var phase = GetPhase(strategy);
                    if (phase == HuntingFundingPhase.WaitingForFunding)
                        eligible.Add(strategy);
                    else
                        locked.Add(strategy);
                }
                else if (strategy.Status == StrategyStatus.Idle || strategy.Status == StrategyStatus.Stopped)
                {
                    eligible.Add(strategy);
                }
                // Skip Error status
            }

            if (eligible.Count == 0)
                continue;

            // Account-group-local occupancy (locked bots in this account)
            var occupiedTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in locked)
            {
                var symbol = GetSymbol(s);
                if (!string.IsNullOrEmpty(symbol))
                    occupiedTickers.Add(symbol);
            }

            // Assign per-bot: each eligible bot picks the best ticker that is
            // (a) not locked in this account group, (b) not held by any other bot
            // in the same workspace, and (c) inside the bot's funding range.
            foreach (var strategy in eligible)
            {
                var (minPct, maxPct) = GetFundingRange(strategy);

                FundingRateDto? picked = null;
                foreach (var rate in rates)
                {
                    if (occupiedTickers.Contains(rate.Symbol))
                        continue;

                    if (IsTakenByOtherInWorkspace(strategy.WorkspaceId, rate.Symbol, strategy.Id))
                        continue;

                    var absPct = Math.Abs(rate.Rate * 100m);
                    if (absPct < minPct || absPct > maxPct)
                        continue;

                    picked = rate;
                    break;
                }

                if (picked == null)
                    continue;

                var oldSymbol = GetSymbol(strategy);
                if (!string.Equals(oldSymbol, picked.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    SetSymbol(strategy, picked.Symbol);
                    _db.StrategyLogs.Add(new StrategyLog
                    {
                        Id = Guid.NewGuid(),
                        StrategyId = strategy.Id,
                        Level = "Info",
                        Message = $"Ticker rotated: {oldSymbol} → {picked.Symbol} (funding {picked.Rate:P4}, range {minPct}–{maxPct}%)",
                        CreatedAt = DateTime.UtcNow
                    });
                    updatedCount++;
                    TransferOwnership(strategy.WorkspaceId, oldSymbol, picked.Symbol, strategy.Id);
                }

                occupiedTickers.Add(picked.Symbol);
            }
        }

        if (updatedCount > 0)
        {
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Ticker rotation completed: {Count} strategies updated", updatedCount);
        }
        else
        {
            _logger.LogInformation("Ticker rotation: no changes needed");
        }
    }

    private static bool IsAutoRotateEnabled(Strategy strategy)
    {
        if (string.IsNullOrEmpty(strategy.ConfigJson))
            return true; // default is true

        try
        {
            var config = JsonSerializer.Deserialize<HuntingFundingConfig>(strategy.ConfigJson, JsonOptions);
            return config?.AutoRotateTicker ?? true;
        }
        catch
        {
            return true;
        }
    }

    private static HuntingFundingPhase GetPhase(Strategy strategy)
    {
        if (string.IsNullOrEmpty(strategy.StateJson))
            return HuntingFundingPhase.WaitingForFunding;

        try
        {
            var state = JsonSerializer.Deserialize<HuntingFundingState>(strategy.StateJson, JsonOptions);
            return state?.Phase ?? HuntingFundingPhase.WaitingForFunding;
        }
        catch
        {
            return HuntingFundingPhase.WaitingForFunding;
        }
    }

    private static (decimal minPct, decimal maxPct) GetFundingRange(Strategy strategy)
    {
        var workspaceJson = strategy.Workspace?.ConfigJson;
        if (string.IsNullOrEmpty(workspaceJson))
            return (1.0m, 2.0m);

        try
        {
            var cfg = JsonSerializer.Deserialize<WorkspaceHuntingFundingConfig>(workspaceJson, JsonOptions);
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

    private static string GetSymbol(Strategy strategy)
    {
        if (string.IsNullOrEmpty(strategy.ConfigJson))
            return string.Empty;

        try
        {
            var config = JsonSerializer.Deserialize<HuntingFundingConfig>(strategy.ConfigJson, JsonOptions);
            return config?.Symbol ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void SetSymbol(Strategy strategy, string newSymbol)
    {
        if (string.IsNullOrEmpty(strategy.ConfigJson))
            return;

        try
        {
            var config = JsonSerializer.Deserialize<HuntingFundingConfig>(strategy.ConfigJson, JsonOptions);
            if (config == null) return;

            config.Symbol = newSymbol;
            strategy.ConfigJson = JsonSerializer.Serialize(config, JsonOptions);
        }
        catch
        {
            // If config can't be deserialized, leave it unchanged
        }
    }
}
