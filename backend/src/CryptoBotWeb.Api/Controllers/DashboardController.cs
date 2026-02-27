using System.Security.Claims;
using System.Text.Json;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public DashboardController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin() => User.IsInRole("Admin");

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummary>> GetSummary([FromQuery] Guid? userId)
    {
        var targetUserId = IsAdmin() && userId.HasValue ? userId.Value : GetUserId();

        var accounts = await _db.ExchangeAccounts
            .Where(a => a.UserId == targetUserId)
            .ToListAsync();

        var runningStrategies = await _db.Strategies
            .CountAsync(s => s.Account.UserId == targetUserId && s.Status == StrategyStatus.Running);

        var totalTrades = await _db.Trades
            .CountAsync(t => t.Account.UserId == targetUserId);

        return Ok(new DashboardSummary
        {
            TotalAccounts = accounts.Count,
            ActiveAccounts = accounts.Count(a => a.IsActive),
            RunningStrategies = runningStrategies,
            TotalTrades = totalTrades,
            Accounts = accounts.Select(a => new AccountBalanceSummary
            {
                AccountId = a.Id,
                Name = a.Name,
                Exchange = a.ExchangeType.ToString(),
                IsActive = a.IsActive
            }).ToList()
        });
    }

    [HttpGet("workspaces")]
    public async Task<ActionResult<List<WorkspaceDashboardDto>>> GetWorkspaces([FromQuery] Guid? userId)
    {
        var targetUserId = IsAdmin() && userId.HasValue ? userId.Value : GetUserId();

        var workspaces = await _db.Workspaces
            .Where(w => w.UserId == targetUserId)
            .OrderBy(w => w.SortOrder)
            .ToListAsync();

        var strategies = await _db.Strategies
            .Include(s => s.Trades)
            .Where(s => s.Account.UserId == targetUserId && s.WorkspaceId != null)
            .ToListAsync();

        var result = new List<WorkspaceDashboardDto>();

        foreach (var ws in workspaces)
        {
            var wsStrategies = strategies.Where(s => s.WorkspaceId == ws.Id).ToList();
            result.Add(BuildWorkspaceDto(ws.Id, ws.Name, wsStrategies));
        }

        return Ok(result);
    }

    [HttpGet("workspaces/{id:guid}")]
    public async Task<ActionResult<WorkspaceDetailDto>> GetWorkspaceDetail(Guid id, [FromQuery] Guid? userId)
    {
        var targetUserId = IsAdmin() && userId.HasValue ? userId.Value : GetUserId();

        var workspace = await _db.Workspaces
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == targetUserId);
        if (workspace == null) return NotFound();

        var wsStrategies = await _db.Strategies
            .Include(s => s.Trades)
            .Where(s => s.WorkspaceId == id && s.Account.UserId == targetUserId)
            .ToListAsync();

        var baseDto = BuildWorkspaceDto(workspace.Id, workspace.Name, wsStrategies);

        // Close trades for avg & drawdown
        var closeTrades = wsStrategies
            .SelectMany(s => s.Trades)
            .Where(t => t.PnlDollar != null)
            .OrderBy(t => t.ExecutedAt)
            .ToList();

        var avgPnl = closeTrades.Count > 0 ? closeTrades.Average(t => t.PnlDollar!.Value) : 0m;

        // Max drawdown from cumulative PnL curve
        var maxDrawdown = 0m;
        var peak = 0m;
        var cum = 0m;
        foreach (var t in closeTrades)
        {
            cum += t.PnlDollar!.Value;
            if (cum > peak) peak = cum;
            var dd = peak - cum;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }

        // Recent trades (last 100, all sides)
        var recentTrades = wsStrategies
            .SelectMany(s => s.Trades)
            .OrderByDescending(t => t.ExecutedAt)
            .Take(100)
            .Select(t => new TradeDto
            {
                Id = t.Id,
                Symbol = t.Symbol,
                Side = t.Side,
                Price = t.Price,
                Quantity = t.Quantity,
                PnlDollar = t.PnlDollar,
                Status = t.Status,
                ExecutedAt = t.ExecutedAt
            }).ToList();

        // Per-bot breakdown
        var bots = wsStrategies.Select(s =>
        {
            var config = TryParseConfig(s.ConfigJson);
            var state = TryParseState(s.StateJson);
            var hasPos = state?.OpenLong != null || state?.OpenShort != null;
            var posDir = state?.OpenLong != null ? "Long" : state?.OpenShort != null ? "Short" : null;

            return new BotSummaryDto
            {
                StrategyId = s.Id,
                Name = s.Name,
                Symbol = config?.Symbol ?? "",
                Status = s.Status.ToString(),
                HasPosition = hasPos,
                PositionDirection = posDir,
                RealizedPnl = s.Trades.Where(t => t.PnlDollar != null).Sum(t => t.PnlDollar ?? 0m),
                TotalTrades = s.Trades.Count
            };
        }).ToList();

        return Ok(new WorkspaceDetailDto
        {
            WorkspaceId = baseDto.WorkspaceId,
            WorkspaceName = baseDto.WorkspaceName,
            TotalBots = baseDto.TotalBots,
            RunningBots = baseDto.RunningBots,
            BotsInPosition = baseDto.BotsInPosition,
            RealizedPnl = baseDto.RealizedPnl,
            UnrealizedPnl = baseDto.UnrealizedPnl,
            TotalTrades = baseDto.TotalTrades,
            WinningTrades = baseDto.WinningTrades,
            LosingTrades = baseDto.LosingTrades,
            WinRate = baseDto.WinRate,
            PnlCurve = baseDto.PnlCurve,
            AvgTradePnl = Math.Round(avgPnl, 2),
            MaxDrawdown = Math.Round(maxDrawdown, 2),
            RecentTrades = recentTrades,
            Bots = bots
        });
    }

    private WorkspaceDashboardDto BuildWorkspaceDto(Guid wsId, string wsName, List<Core.Entities.Strategy> wsStrategies)
    {
        var botsInPosition = 0;
        var unrealizedPnl = 0m;

        foreach (var s in wsStrategies)
        {
            var state = TryParseState(s.StateJson);
            if (state == null) continue;

            var hasPos = state.OpenLong != null || state.OpenShort != null;
            if (hasPos) botsInPosition++;

            if (state.OpenLong != null && state.LastPrice.HasValue && state.OpenLong.EntryPrice > 0)
            {
                var pct = (state.LastPrice.Value - state.OpenLong.EntryPrice) / state.OpenLong.EntryPrice * 100m;
                unrealizedPnl += state.OpenLong.OrderSize * pct / 100m;
            }
            if (state.OpenShort != null && state.LastPrice.HasValue && state.OpenShort.EntryPrice > 0)
            {
                var pct = (state.OpenShort.EntryPrice - state.LastPrice.Value) / state.OpenShort.EntryPrice * 100m;
                unrealizedPnl += state.OpenShort.OrderSize * pct / 100m;
            }
        }

        var closeTrades = wsStrategies
            .SelectMany(s => s.Trades)
            .Where(t => t.PnlDollar != null)
            .OrderBy(t => t.ExecutedAt)
            .ToList();

        var realizedPnl = closeTrades.Sum(t => t.PnlDollar ?? 0m);
        var winning = closeTrades.Count(t => t.PnlDollar > 0);
        var losing = closeTrades.Count(t => t.PnlDollar <= 0);
        var winRate = closeTrades.Count > 0 ? Math.Round((decimal)winning / closeTrades.Count * 100m, 1) : 0m;

        // PnL curve: cumulative PnL per trade
        var pnlCurve = new List<PnlPoint>();
        var cum = 0m;
        foreach (var t in closeTrades)
        {
            cum += t.PnlDollar!.Value;
            pnlCurve.Add(new PnlPoint { Date = t.ExecutedAt, CumPnl = Math.Round(cum, 2) });
        }

        return new WorkspaceDashboardDto
        {
            WorkspaceId = wsId,
            WorkspaceName = wsName,
            TotalBots = wsStrategies.Count,
            RunningBots = wsStrategies.Count(s => s.Status == StrategyStatus.Running),
            BotsInPosition = botsInPosition,
            RealizedPnl = Math.Round(realizedPnl, 2),
            UnrealizedPnl = Math.Round(unrealizedPnl, 2),
            TotalTrades = wsStrategies.Sum(s => s.Trades.Count),
            WinningTrades = winning,
            LosingTrades = losing,
            WinRate = winRate,
            PnlCurve = pnlCurve
        };
    }

    private static EmaBounceState? TryParseState(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}") return null;
        try { return JsonSerializer.Deserialize<EmaBounceState>(json, JsonOpts); }
        catch { return null; }
    }

    private static EmaBounceConfig? TryParseConfig(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}") return null;
        try { return JsonSerializer.Deserialize<EmaBounceConfig>(json, JsonOpts); }
        catch { return null; }
    }
}
