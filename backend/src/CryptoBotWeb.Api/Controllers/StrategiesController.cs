using System.Security.Claims;
using System.Text.Json;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StrategiesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _exchangeFactory;

    public StrategiesController(AppDbContext db, IExchangeServiceFactory exchangeFactory)
    {
        _db = db;
        _exchangeFactory = exchangeFactory;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var strategies = await _db.Strategies
            .Where(s => s.Account.UserId == GetUserId())
            .Select(s => new
            {
                s.Id,
                s.AccountId,
                s.WorkspaceId,
                AccountName = s.Account.Name,
                Exchange = s.Account.ExchangeType.ToString(),
                s.Name,
                s.Type,
                s.ConfigJson,
                s.StateJson,
                Status = s.Status.ToString(),
                s.CreatedAt,
                s.StartedAt
            })
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        return Ok(strategies);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats([FromQuery] Guid? workspaceId)
    {
        var userId = GetUserId();

        var query = _db.Strategies
            .Include(s => s.Trades)
            .Where(s => s.Account.UserId == userId);
        if (workspaceId.HasValue)
            query = query.Where(s => s.WorkspaceId == workspaceId.Value);

        var strategies = await query.ToListAsync();

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var stats = strategies
            .GroupBy(s => s.WorkspaceId)
            .Select(g =>
            {
                // Realized PNL from closed trades
                var realizedPnl = g.Sum(s => s.Trades
                    .Where(t => t.PnlDollar != null)
                    .Sum(t => t.PnlDollar ?? 0m));

                // Unrealized PNL from open positions
                var unrealizedPnl = 0m;
                foreach (var s in g)
                {
                    if (string.IsNullOrEmpty(s.StateJson) || s.StateJson == "{}") continue;
                    try
                    {
                        var state = JsonSerializer.Deserialize<EmaBounceState>(s.StateJson, jsonOpts);
                        if (state == null) continue;

                        if (state.OpenLong != null && state.LastPrice.HasValue && state.OpenLong.EntryPrice > 0)
                        {
                            var pnlPct = (state.LastPrice.Value - state.OpenLong.EntryPrice) / state.OpenLong.EntryPrice * 100m;
                            unrealizedPnl += state.OpenLong.OrderSize * pnlPct / 100m;
                        }
                        if (state.OpenShort != null && state.LastPrice.HasValue && state.OpenShort.EntryPrice > 0)
                        {
                            var pnlPct = (state.OpenShort.EntryPrice - state.LastPrice.Value) / state.OpenShort.EntryPrice * 100m;
                            unrealizedPnl += state.OpenShort.OrderSize * pnlPct / 100m;
                        }
                    }
                    catch { /* skip invalid state */ }
                }

                return new
                {
                    WorkspaceId = g.Key,
                    TotalBots = g.Count(),
                    ActiveBots = g.Count(s => s.Status == StrategyStatus.Running),
                    TotalTrades = g.Sum(s => s.Trades.Count),
                    Pnl = realizedPnl,
                    UnrealizedPnl = unrealizedPnl
                };
            })
            .ToList();

        return Ok(stats);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var strategy = await _db.Strategies
            .Where(s => s.Id == id && s.Account.UserId == GetUserId())
            .Select(s => new
            {
                s.Id,
                s.AccountId,
                AccountName = s.Account.Name,
                Exchange = s.Account.ExchangeType.ToString(),
                s.Name,
                s.Type,
                s.ConfigJson,
                s.StateJson,
                Status = s.Status.ToString(),
                s.CreatedAt,
                s.StartedAt
            })
            .FirstOrDefaultAsync();

        if (strategy == null) return NotFound();
        return Ok(strategy);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateStrategyRequest request)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound(new { message = "Account not found" });

        if (request.WorkspaceId.HasValue)
        {
            var ws = await _db.Workspaces
                .FirstOrDefaultAsync(w => w.Id == request.WorkspaceId.Value && w.UserId == GetUserId());
            if (ws == null)
                return BadRequest(new { message = "Workspace not found" });
        }

        var strategy = new Strategy
        {
            Id = Guid.NewGuid(),
            AccountId = request.AccountId,
            WorkspaceId = request.WorkspaceId,
            Name = request.Name,
            Type = request.Type,
            ConfigJson = request.ConfigJson ?? "{}",
            Status = StrategyStatus.Idle,
            CreatedAt = DateTime.UtcNow
        };

        _db.Strategies.Add(strategy);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new { strategy.Id, strategy.Name, Status = strategy.Status.ToString() });
    }

    [HttpPost("{id:guid}/start")]
    public async Task<IActionResult> Start(Guid id)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account)
            .Include(s => s.Workspace)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        // Merge workspace config into strategy config at start time
        if (strategy.Workspace != null)
        {
            var merged = MergeWorkspaceConfig(strategy.ConfigJson, strategy.Workspace.ConfigJson);
            strategy.ConfigJson = merged;
        }

        // Preserve martingale state across restarts; clear counters so handler recalculates from history
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var prevState = string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}"
            ? new EmaBounceState()
            : JsonSerializer.Deserialize<EmaBounceState>(strategy.StateJson, jsonOpts) ?? new EmaBounceState();

        var freshState = new EmaBounceState
        {
            ConsecutiveLosses = prevState.ConsecutiveLosses,
            RunningPnlDollar = prevState.RunningPnlDollar
        };
        strategy.StateJson = JsonSerializer.Serialize(freshState, jsonOpts);

        strategy.Status = StrategyStatus.Running;
        strategy.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Strategy started", status = strategy.Status.ToString() });
    }

    private static string MergeWorkspaceConfig(string strategyJson, string workspaceJson)
    {
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var wsConfig = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workspaceJson, jsonOpts);
        if (wsConfig == null || wsConfig.Count == 0) return strategyJson;

        var stratConfig = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(strategyJson, jsonOpts)
            ?? new Dictionary<string, JsonElement>();

        // Map workspace fields to strategy config fields
        if (wsConfig.TryGetValue("betAmount", out var bet))
            stratConfig["orderSize"] = bet;
        if (wsConfig.TryGetValue("useMartingale", out var v))
            stratConfig["useMartingale"] = v;
        if (wsConfig.TryGetValue("martingaleCoeff", out v))
            stratConfig["martingaleCoeff"] = v;
        if (wsConfig.TryGetValue("useSteppedMartingale", out v))
            stratConfig["useSteppedMartingale"] = v;
        if (wsConfig.TryGetValue("martingaleStep", out v))
            stratConfig["martingaleStep"] = v;
        if (wsConfig.TryGetValue("onlyLong", out v))
            stratConfig["onlyLong"] = v;
        if (wsConfig.TryGetValue("onlyShort", out v))
            stratConfig["onlyShort"] = v;
        if (wsConfig.TryGetValue("useDrawdownScale", out v))
            stratConfig["useDrawdownScale"] = v;
        if (wsConfig.TryGetValue("drawdownBalance", out v))
            stratConfig["drawdownBalance"] = v;
        if (wsConfig.TryGetValue("drawdownPercent", out v))
            stratConfig["drawdownPercent"] = v;
        if (wsConfig.TryGetValue("drawdownTarget", out v))
            stratConfig["drawdownTarget"] = v;

        return JsonSerializer.Serialize(stratConfig);
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> Stop(Guid id)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        strategy.Status = StrategyStatus.Stopped;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Strategy stopped", status = strategy.Status.ToString() });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateStrategyRequest request)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        if (strategy.Status == StrategyStatus.Running)
            return BadRequest(new { message = "Нельзя редактировать запущенного бота. Сначала остановите его." });

        strategy.Name = request.Name;
        strategy.ConfigJson = request.ConfigJson ?? strategy.ConfigJson;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Strategy updated", strategy.Id, strategy.Name });
    }

    [HttpPost("{id:guid}/close-position")]
    public async Task<IActionResult> ClosePosition(Guid id)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        if (string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}")
            return BadRequest(new { message = "Нет открытой позиции" });

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var state = JsonSerializer.Deserialize<EmaBounceState>(strategy.StateJson, jsonOpts);

        if (state == null || (state.OpenLong == null && state.OpenShort == null))
            return BadRequest(new { message = "Нет открытой позиции" });

        var config = JsonSerializer.Deserialize<JsonElement>(strategy.ConfigJson);
        var symbol = config.GetProperty("symbol").GetString()!;

        using var exchange = _exchangeFactory.CreateFutures(strategy.Account);

        var results = new List<string>();

        if (state.OpenLong != null)
        {
            var result = await exchange.CloseLongAsync(symbol, state.OpenLong.Quantity);
            results.Add($"Long closed: qty={state.OpenLong.Quantity}, orderId={result.OrderId}");
            state.OpenLong = null;
            state.WaitNextCandleAfterLongClose = false;
        }

        if (state.OpenShort != null)
        {
            var result = await exchange.CloseShortAsync(symbol, state.OpenShort.Quantity);
            results.Add($"Short closed: qty={state.OpenShort.Quantity}, orderId={result.OrderId}");
            state.OpenShort = null;
            state.WaitNextCandleAfterShortClose = false;
        }

        strategy.StateJson = JsonSerializer.Serialize(state, jsonOpts);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Позиция закрыта по рынку", details = results });
    }

    [HttpPost("{id:guid}/reset-losses")]
    public async Task<IActionResult> ResetLosses(Guid id)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        if (string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}")
            return Ok(new { message = "Нечего сбрасывать", consecutiveLosses = 0 });

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var state = JsonSerializer.Deserialize<EmaBounceState>(strategy.StateJson, jsonOpts);

        if (state == null)
            return Ok(new { message = "Нечего сбрасывать", consecutiveLosses = 0 });

        state.ConsecutiveLosses = 0;
        strategy.StateJson = JsonSerializer.Serialize(state, jsonOpts);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Убытки сброшены", consecutiveLosses = 0 });
    }

    [HttpGet("{id:guid}/chart")]
    public async Task<IActionResult> GetChart(Guid id, [FromQuery] int limit = 300)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null) return NotFound();

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<EmaBounceConfig>(strategy.ConfigJson, jsonOpts);
        if (config == null || string.IsNullOrEmpty(config.Symbol))
            return BadRequest(new { message = "Invalid config" });

        if (limit < 1) limit = 1;
        if (limit > 1000) limit = 1000;

        try
        {
            using var exchange = _exchangeFactory.CreateFutures(strategy.Account);
            var candles = await exchange.GetKlinesAsync(config.Symbol, config.Timeframe, limit);

            var closePrices = candles.Select(c => c.Close).ToArray();
            var maValues = config.IndicatorType.Equals("SMA", StringComparison.OrdinalIgnoreCase)
                ? IndicatorCalculator.CalculateSma(closePrices, config.IndicatorLength)
                : IndicatorCalculator.CalculateEma(closePrices, config.IndicatorLength);

            var indicatorPoints = new List<object>();
            for (int i = config.IndicatorLength - 1; i < candles.Count; i++)
            {
                if (maValues[i] != 0)
                {
                    indicatorPoints.Add(new
                    {
                        time = new DateTimeOffset(candles[i].OpenTime).ToUnixTimeSeconds(),
                        value = maValues[i]
                    });
                }
            }

            return Ok(new { candles, indicatorValues = indicatorPoints });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{id:guid}/logs")]
    public async Task<IActionResult> GetLogs(Guid id, [FromQuery] int limit = 200)
    {
        var strategy = await _db.Strategies
            .Where(s => s.Id == id && s.Account.UserId == GetUserId())
            .Select(s => s.Id)
            .FirstOrDefaultAsync();

        if (strategy == Guid.Empty) return NotFound();

        if (limit < 1) limit = 1;
        if (limit > 1000) limit = 1000;

        var logs = await _db.StrategyLogs
            .Where(l => l.StrategyId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(l => new
            {
                l.Id,
                l.Level,
                l.Message,
                l.CreatedAt
            })
            .ToListAsync();

        return Ok(logs);
    }

    [HttpDelete("{id:guid}/logs")]
    public async Task<IActionResult> ClearLogs(Guid id)
    {
        var strategy = await _db.Strategies
            .Where(s => s.Id == id && s.Account.UserId == GetUserId())
            .Select(s => s.Id)
            .FirstOrDefaultAsync();

        if (strategy == Guid.Empty) return NotFound();

        var count = await _db.StrategyLogs
            .Where(l => l.StrategyId == id)
            .ExecuteDeleteAsync();

        return Ok(new { message = $"Удалено {count} записей" });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        _db.Strategies.Remove(strategy);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public class CreateStrategyRequest
{
    public Guid AccountId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? ConfigJson { get; set; }
}

public class UpdateStrategyRequest
{
    public string Name { get; set; } = string.Empty;
    public string? ConfigJson { get; set; }
}
