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
    private readonly ISymbolBlacklistService _blacklist;
    private readonly ILogger<FundingTickerRotationService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FundingTickerRotationService(
        AppDbContext db,
        IExchangeServiceFactory factory,
        ISymbolBlacklistService blacklist,
        ILogger<FundingTickerRotationService> logger)
    {
        _db = db;
        _factory = factory;
        _blacklist = blacklist;
        _logger = logger;
    }

    public async Task RotateTickersAsync(CancellationToken ct)
    {
        // 1. Load all funding-based strategies with workspace + account
        var fundingTypes = new[] { StrategyTypes.HuntingFunding, StrategyTypes.FundingClaim };
        var allStrategies = await _db.Strategies
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
            .Include(s => s.Workspace)
            .Where(s => fundingTypes.Contains(s.Type) && s.WorkspaceId != null)
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

                // Filter out non-tradeable symbols by cross-referencing with active symbols list
                var activeSymbols = await exchange.GetSymbolsAsync();
                var activeSet = new HashSet<string>(
                    activeSymbols.Select(s => s.Symbol.ToUpperInvariant()),
                    StringComparer.OrdinalIgnoreCase);
                rates.RemoveAll(r => !activeSet.Contains(r.Symbol.ToUpperInvariant()));

                // Filter out symbols blacklisted after recent exchange errors (e.g. delisted / not supported for orders).
                var blacklist = await _blacklist.GetActiveSetAsync(account.ExchangeType, ct);
                if (blacklist.Count > 0)
                {
                    var before = rates.Count;
                    rates.RemoveAll(r => blacklist.Contains(r.Symbol.ToUpperInvariant()));
                    _logger.LogInformation(
                        "Blacklist dropped {Dropped} symbols for {Exchange} ({BlacklistCount} entries)",
                        before - rates.Count, account.ExchangeType, blacklist.Count);
                }

                // Sort by absolute rate descending
                rates.Sort((a, b) => Math.Abs(b.Rate).CompareTo(Math.Abs(a.Rate)));
                fundingByExchangeType[account.ExchangeType] = rates;
                _logger.LogInformation(
                    "Fetched {Count} funding rates for {Exchange} ({ActiveCount} active symbols), top rate: {TopSymbol} = {TopRate:P4}",
                    rates.Count, account.ExchangeType, activeSet.Count,
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
                    if (IsInIdlePhase(strategy))
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

                // Pre-arm HuntingFunding: commit the rotation decision into state so the
                // handler won't re-evaluate per-bot thresholds at funding time. The handler's
                // `armedAndActive` guard then preserves Direction through rate decay until
                // funding+60s. Runs every rotation tick (even when symbol didn't change) so
                // bots stay armed across hourly cycles.
                if (strategy.Type == StrategyTypes.HuntingFunding)
                    PreArmHuntingFunding(strategy, picked);

                occupiedTickers.Add(picked.Symbol);
            }
        }

        // Always flush — pre-arm may touch StateJson / add StrategyLogs even when no
        // ticker changed hands.
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Ticker rotation completed: {Count} strategies with ticker changes", updatedCount);
    }

    private void PreArmHuntingFunding(Strategy strategy, FundingRateDto picked)
    {
        HuntingFundingState state;
        try
        {
            state = JsonSerializer.Deserialize<HuntingFundingState>(strategy.StateJson, JsonOptions)
                    ?? new HuntingFundingState();
        }
        catch
        {
            return;
        }

        // Only arm bots sitting idle in WaitingForFunding — never disturb an
        // active cycle (OrdersPlaced / InPosition / Cooldown).
        if (state.Phase != HuntingFundingPhase.WaitingForFunding)
            return;

        var direction = picked.Rate < 0 ? "Long" : "Short";
        state.Direction = direction;
        state.CurrentFundingRate = picked.Rate;
        state.LastSkipLogAt = null;

        // BingX (and sometimes other exchanges) occasionally return a stale
        // `NextFundingTime` that is ~now or in the past — e.g. we observed
        // 15:50:08Z at a 15:50:08 rotation tick. Writing that blindly would
        // trip the threshold check immediately and cause a spurious early
        // placement → 60s timeout → cancel-all → Cooldown (wastes a cycle).
        // Only accept `picked.NextFundingTime` when it's genuinely in the
        // future (> now + 2 min). Otherwise leave whatever the handler has
        // already validated via its own drift/rollover guard.
        var nowUtc = DateTime.UtcNow;
        var nextFundingForLog = state.NextFundingTime;
        if (picked.NextFundingTime > nowUtc.AddMinutes(2))
        {
            state.NextFundingTime = picked.NextFundingTime;
            nextFundingForLog = picked.NextFundingTime;
        }

        strategy.StateJson = JsonSerializer.Serialize(state, JsonOptions);

        var nextFundingStr = nextFundingForLog.HasValue
            ? nextFundingForLog.Value.ToString("u")
            : "(unset)";
        _db.StrategyLogs.Add(new StrategyLog
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            Level = "Info",
            Message = $"Armed by rotation: direction={direction}, nextFunding={nextFundingStr}, rate={picked.Rate:P4}",
            CreatedAt = DateTime.UtcNow
        });
    }

    private static bool IsAutoRotateEnabled(Strategy strategy)
    {
        if (string.IsNullOrEmpty(strategy.ConfigJson))
            return true;

        try
        {
            if (strategy.Type == StrategyTypes.FundingClaim)
            {
                var cfg = JsonSerializer.Deserialize<FundingClaimConfig>(strategy.ConfigJson, JsonOptions);
                return cfg?.AutoRotateTicker ?? true;
            }
            else
            {
                var cfg = JsonSerializer.Deserialize<HuntingFundingConfig>(strategy.ConfigJson, JsonOptions);
                return cfg?.AutoRotateTicker ?? true;
            }
        }
        catch
        {
            return true;
        }
    }

    private static bool IsInIdlePhase(Strategy strategy)
    {
        if (string.IsNullOrEmpty(strategy.StateJson))
            return true;

        try
        {
            if (strategy.Type == StrategyTypes.FundingClaim)
            {
                var state = JsonSerializer.Deserialize<FundingClaimState>(strategy.StateJson, JsonOptions);
                return state?.Phase == FundingClaimPhase.Idle;
            }
            else
            {
                var state = JsonSerializer.Deserialize<HuntingFundingState>(strategy.StateJson, JsonOptions);
                return state?.Phase == HuntingFundingPhase.WaitingForFunding;
            }
        }
        catch
        {
            return true;
        }
    }

    private static (decimal minPct, decimal maxPct) GetFundingRange(Strategy strategy)
    {
        // FundingClaim uses workspace-level threshold, no max
        if (strategy.Type == StrategyTypes.FundingClaim)
        {
            var wsJson = strategy.Workspace?.ConfigJson;
            if (string.IsNullOrEmpty(wsJson))
                return (0.3m, decimal.MaxValue);

            try
            {
                var cfg = JsonSerializer.Deserialize<WorkspaceFundingClaimConfig>(wsJson, JsonOptions);
                var min = cfg?.FcMinFundingRatePercent ?? 0.3m;
                return (min, decimal.MaxValue);
            }
            catch
            {
                return (0.3m, decimal.MaxValue);
            }
        }

        // HuntingFunding uses workspace-level range
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
            // Both configs have Symbol as the first field — use a shared approach
            using var doc = JsonDocument.Parse(strategy.ConfigJson);
            if (doc.RootElement.TryGetProperty("symbol", out var symbolProp))
                return symbolProp.GetString() ?? string.Empty;
            return string.Empty;
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
            if (strategy.Type == StrategyTypes.FundingClaim)
            {
                var cfg = JsonSerializer.Deserialize<FundingClaimConfig>(strategy.ConfigJson, JsonOptions);
                if (cfg == null) return;
                cfg.Symbol = newSymbol;
                strategy.ConfigJson = JsonSerializer.Serialize(cfg, JsonOptions);
            }
            else
            {
                var cfg = JsonSerializer.Deserialize<HuntingFundingConfig>(strategy.ConfigJson, JsonOptions);
                if (cfg == null) return;
                cfg.Symbol = newSymbol;
                strategy.ConfigJson = JsonSerializer.Serialize(cfg, JsonOptions);
            }
        }
        catch
        {
            // If config can't be deserialized, leave it unchanged
        }
    }
}
