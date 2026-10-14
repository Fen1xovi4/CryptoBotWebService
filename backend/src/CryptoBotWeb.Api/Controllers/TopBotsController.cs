using System.Security.Claims;
using System.Text.Json.Nodes;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

/// <summary>
/// Public "top bots" leaderboard (landing page + the in-app "Top bots" tab) and "create the same
/// bot" for signed-in users.
/// </summary>
[ApiController]
[Route("api/top-bots")]
[Authorize]
public class TopBotsController : ControllerBase
{
    // MaratG keeps sizing/martingale/direction in the WORKSPACE config, and Start overwrites the
    // bot's copy with it (StrategiesController.MergeWorkspaceConfig). A copied bot therefore needs
    // a workspace carrying the source bot's effective values: workspace key ← strategy key.
    private static readonly (string WorkspaceKey, string StrategyKey)[] MaratGWorkspaceKeys =
    {
        ("betAmount", "orderSize"),
        ("useMartingale", "useMartingale"),
        ("martingaleCoeff", "martingaleCoeff"),
        ("useSteppedMartingale", "useSteppedMartingale"),
        ("martingaleStep", "martingaleStep"),
        ("onlyLong", "onlyLong"),
        ("onlyShort", "onlyShort"),
        ("useDrawdownScale", "useDrawdownScale"),
        ("drawdownBalance", "drawdownBalance"),
        ("drawdownPercent", "drawdownPercent"),
        ("drawdownTarget", "drawdownTarget"),
    };

    private readonly AppDbContext _db;
    private readonly TopBotsService _topBots;

    public TopBotsController(AppDbContext db, TopBotsService topBots)
    {
        _db = db;
        _topBots = topBots;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 30, [FromQuery] int? limit = null, CancellationToken ct = default)
    {
        var result = await _topBots.GetAsync(days, ct);
        if (limit is > 0)
            result = new()
            {
                PeriodDays = result.PeriodDays,
                GeneratedAtUtc = result.GeneratedAtUtc,
                Bots = result.Bots.Take(limit.Value).ToList()
            };
        return Ok(result);
    }

    /// <summary>
    /// Creates a stopped copy of a leaderboard bot on the caller's account, in a new workspace of
    /// the same strategy type. Nothing trades until the user reviews it and presses Start.
    /// </summary>
    [HttpPost("{id:guid}/copy")]
    public async Task<IActionResult> Copy(Guid id, [FromBody] CopyTopBotRequest request)
    {
        var userId = GetUserId();

        var source = await _db.Strategies
            .AsNoTracking()
            .Include(s => s.Account)
            .Include(s => s.Workspace)
            .FirstOrDefaultAsync(s => s.Id == id && !s.HideFromTop);
        if (source == null)
            return NotFound(new { message = "Бот не найден или скрыт владельцем." });

        var closedTrades = await _db.Trades.CountAsync(t => t.StrategyId == id && t.PnlDollar != null);
        if (closedTrades < TopBotsService.MinClosedTrades)
            return NotFound(new { message = "Бот не найден в топе." });

        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == userId);
        if (account == null)
            return NotFound(new { message = "Аккаунт не найден." });

        if (source.Type == StrategyTypes.SmartGridHedge && account.ExchangeType != ExchangeType.Bybit)
            return BadRequest(new { message = "SmartGridHedge работает только на Bybit." });
        if (account.ExchangeType == ExchangeType.Dzengi && source.Account.ExchangeType != ExchangeType.Dzengi)
            return BadRequest(new { message = "Этого бота нельзя запустить на Dzengi — выберите аккаунт Bybit, Bitget или BingX." });

        var strategyConfig = TopBotsService.SanitizeConfig(source.ConfigJson, source.Type);
        var workspaceConfig = TopBotsService.SanitizeWorkspaceConfig(source.Workspace?.ConfigJson, source.Type);
        if (source.Type == StrategyTypes.MaratG)
        {
            foreach (var (wsKey, stKey) in MaratGWorkspaceKeys)
                if (strategyConfig[stKey] is { } value)
                    workspaceConfig[wsKey] = value.DeepClone();
        }

        var symbol = (strategyConfig["symbol"] ?? strategyConfig["gridSymbol"]) is JsonValue sv
                     && sv.TryGetValue<string>(out var s) ? s : "";
        var maxOrder = await _db.Workspaces
            .Where(w => w.UserId == userId)
            .MaxAsync(w => (int?)w.SortOrder) ?? -1;

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"Из топа: {source.Type} {symbol}".Trim(),
            StrategyType = source.Type,
            ConfigJson = workspaceConfig.ToJsonString(),
            SortOrder = maxOrder + 1,
            CreatedAt = DateTime.UtcNow
        };
        var strategy = new Strategy
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            WorkspaceId = workspace.Id,
            Name = string.IsNullOrEmpty(symbol) ? source.Type : symbol,
            Type = source.Type,
            ConfigJson = strategyConfig.ToJsonString(),
            Status = StrategyStatus.Idle,
            CreatedAt = DateTime.UtcNow
        };

        _db.Workspaces.Add(workspace);
        _db.Strategies.Add(strategy);
        await _db.SaveChangesAsync();

        return Ok(new { workspaceId = workspace.Id, strategyId = strategy.Id });
    }
}

public class CopyTopBotRequest
{
    public Guid AccountId { get; set; }
}
