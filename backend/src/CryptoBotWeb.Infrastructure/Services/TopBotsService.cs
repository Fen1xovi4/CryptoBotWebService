using System.Text.Json;
using System.Text.Json.Nodes;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CryptoBotWeb.Infrastructure.Services;

/// <summary>
/// Public leaderboard of bots: every bot with more than <see cref="MinClosedTrades"/> closed trades
/// whose owner did not hide it, ranked by return on peak invested capital over a period
/// (see <see cref="TopBotMetrics"/>). Anonymous by construction — see <see cref="TopBotDto"/>.
/// Served to guests from the landing page, so the result is cached per period.
/// </summary>
public class TopBotsService
{
    public const int MinClosedTrades = 3; // "more than 2 trades"
    public static readonly int[] AllowedPeriods = { 7, 30, 90 };

    // Strategies whose every close flattens the whole position (see TopBotMetrics).
    private static readonly HashSet<string> CloseFlattensTypes = new()
    {
        StrategyTypes.MaratG, StrategyTypes.SmaDca, StrategyTypes.HuntingFunding
    };

    // Start merges the MaratG workspace fields (orderSize, martingale, …) into every non-HuntingFunding
    // bot's config, and old configs carry leftovers of removed features. Only the properties of the
    // type's own config class are "the settings" — everything else is dropped from display and copies.
    private static readonly Dictionary<string, Type> ConfigTypes = new()
    {
        [StrategyTypes.MaratG] = typeof(EmaBounceConfig),
        [StrategyTypes.MaratGGrid] = typeof(EmaBounceConfig),
        [StrategyTypes.HuntingFunding] = typeof(HuntingFundingConfig),
        [StrategyTypes.SmaDca] = typeof(SmaDcaConfig),
        [StrategyTypes.GridFloat] = typeof(GridFloatConfig),
        [StrategyTypes.GridHedge] = typeof(GridHedgeConfig),
        [StrategyTypes.SmartGridHedge] = typeof(SmartGridHedgeConfig),
    };

    private static readonly string[] SupportedTypes = ConfigTypes.Keys.ToArray();

    private static readonly Dictionary<Type, HashSet<string>> KeysByConfigType = ConfigTypes.Values
        .Concat(new[] { typeof(WorkspaceHuntingFundingConfig) })
        .Distinct()
        .ToDictionary(t => t, t => t.GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;

    public TopBotsService(AppDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<TopBotsResponse> GetAsync(int periodDays, CancellationToken ct)
    {
        if (!AllowedPeriods.Contains(periodDays)) periodDays = 30;

        return (await _cache.GetOrCreateAsync($"top-bots:{periodDays}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await BuildAsync(periodDays, ct);
        }))!;
    }

    /// <summary>Drops the cached leaderboards — call when a bot's visibility changes.</summary>
    public void Invalidate()
    {
        foreach (var days in AllowedPeriods)
            _cache.Remove($"top-bots:{days}");
    }

    private async Task<TopBotsResponse> BuildAsync(int periodDays, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-periodDays);

        var candidates = await _db.Strategies
            .AsNoTracking()
            .Where(s => !s.HideFromTop && SupportedTypes.Contains(s.Type)
                && s.Trades.Count(t => t.PnlDollar != null) >= MinClosedTrades
                && s.Trades.Any(t => t.PnlDollar != null && t.ExecutedAt >= since))
            .Select(s => new
            {
                s.Id,
                s.Type,
                s.ConfigJson,
                s.Status,
                s.Account.ExchangeType
            })
            .ToListAsync(ct);

        var ids = candidates.Select(c => c.Id).ToList();
        var trades = (await _db.Trades
                .AsNoTracking()
                .Where(t => ids.Contains(t.StrategyId))
                .Select(t => new { t.StrategyId, t.ExecutedAt, t.Quantity, t.Price, t.PnlDollar })
                .ToListAsync(ct))
            .GroupBy(t => t.StrategyId)
            .ToDictionary(g => g.Key, g => g.Select(t => new TopBotTrade(t.ExecutedAt, t.Quantity, t.Price, t.PnlDollar)).ToList());

        var bots = new List<TopBotDto>();
        foreach (var c in candidates)
        {
            if (!trades.TryGetValue(c.Id, out var botTrades)) continue;

            var period = TopBotMetrics.Compute(botTrades, since, CloseFlattensTypes.Contains(c.Type));
            if (period.PeakCapitalUsd <= 0m) continue;

            var config = SanitizeConfig(c.ConfigJson, c.Type);
            var closes = botTrades.Where(t => t.PnlDollar != null).ToList();

            bots.Add(new TopBotDto
            {
                Id = c.Id,
                StrategyType = c.Type,
                Exchange = c.ExchangeType.ToString(),
                Symbol = ReadString(config, "symbol") ?? ReadString(config, "gridSymbol") ?? string.Empty,
                Timeframe = ReadString(config, "timeframe"),
                Direction = ReadString(config, "direction"),
                IsRunning = c.Status == StrategyStatus.Running,
                ReturnPercent = Math.Round(period.ReturnPercent, 2),
                PnlUsd = Math.Round(period.PnlUsd, 2),
                PeakCapitalUsd = Math.Round(period.PeakCapitalUsd, 2),
                PeriodClosedTrades = period.ClosedTrades,
                PeriodWinRate = period.ClosedTrades > 0
                    ? Math.Round((decimal)period.WinningTrades / period.ClosedTrades * 100m, 1)
                    : 0m,
                TotalClosedTrades = closes.Count,
                TotalPnlUsd = Math.Round(closes.Sum(t => t.PnlDollar!.Value), 2),
                ActiveDays = (int)(now - botTrades.Min(t => t.ExecutedAt)).TotalDays,
                Config = JsonSerializer.SerializeToElement(config)
            });
        }

        return new TopBotsResponse
        {
            PeriodDays = periodDays,
            GeneratedAtUtc = now,
            Bots = bots
                .OrderByDescending(b => b.ReturnPercent)
                .ThenByDescending(b => b.PnlUsd)
                .ToList()
        };
    }

    /// <summary>The bot's settings: only the fields of its strategy type's config class.</summary>
    public static JsonObject SanitizeConfig(string? configJson, string strategyType) =>
        ConfigTypes.TryGetValue(strategyType, out var configType)
            ? KeepOnly(Parse(configJson), KeysByConfigType[configType])
            : new JsonObject();

    /// <summary>
    /// Workspace-level settings a copied bot needs. Only HuntingFunding keeps real settings there
    /// (the funding-rate range); MaratG's are rebuilt from the bot itself by the caller, and every
    /// other type gets an empty workspace so Start has nothing to merge over the copied config.
    /// </summary>
    public static JsonObject SanitizeWorkspaceConfig(string? workspaceJson, string strategyType) =>
        strategyType == StrategyTypes.HuntingFunding
            ? KeepOnly(Parse(workspaceJson), KeysByConfigType[typeof(WorkspaceHuntingFundingConfig)])
            : new JsonObject();

    private static JsonObject Parse(string? json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static JsonObject KeepOnly(JsonObject obj, HashSet<string> keys)
    {
        foreach (var key in obj.Select(kv => kv.Key).ToList())
            if (!keys.Contains(key))
                obj.Remove(key);
        return obj;
    }

    private static string? ReadString(JsonObject obj, string key)
    {
        var node = obj.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
    }
}
