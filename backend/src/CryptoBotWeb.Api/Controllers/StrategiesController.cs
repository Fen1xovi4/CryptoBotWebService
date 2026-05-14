using System.Security.Claims;
using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Strategies;
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

    private bool IsAdmin() => User.IsInRole("Admin");

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] Guid? userId)
    {
        var targetUserId = IsAdmin() && userId.HasValue ? userId.Value : GetUserId();

        var strategies = await _db.Strategies
            .Where(s => s.Account.UserId == targetUserId)
            .Select(s => new
            {
                s.Id,
                s.AccountId,
                s.WorkspaceId,
                s.TelegramBotId,
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
    public async Task<IActionResult> GetStats([FromQuery] Guid? workspaceId, [FromQuery] Guid? userId)
    {
        var targetUserId = IsAdmin() && userId.HasValue ? userId.Value : GetUserId();

        var query = _db.Strategies
            .Include(s => s.Trades)
            .Where(s => s.Account.UserId == targetUserId);
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
                        if (s.Type == StrategyTypes.HuntingFunding)
                        {
                            var hfState = JsonSerializer.Deserialize<HuntingFundingState>(s.StateJson, jsonOpts);
                            if (hfState == null) continue;

                            if (hfState.Phase == HuntingFundingPhase.InPosition
                                && hfState.AvgEntryPrice.HasValue && hfState.AvgEntryPrice.Value > 0
                                && hfState.TotalFilledUsdt.HasValue
                                && hfState.LastPrice.HasValue)
                            {
                                var pnlPct = hfState.Direction == "Long"
                                    ? (hfState.LastPrice.Value - hfState.AvgEntryPrice.Value) / hfState.AvgEntryPrice.Value * 100m
                                    : (hfState.AvgEntryPrice.Value - hfState.LastPrice.Value) / hfState.AvgEntryPrice.Value * 100m;
                                unrealizedPnl += hfState.TotalFilledUsdt.Value * pnlPct / 100m;
                            }
                        }
                        else if (s.Type == StrategyTypes.FundingClaim)
                        {
                            var fcState = JsonSerializer.Deserialize<FundingClaimState>(s.StateJson, jsonOpts);
                            if (fcState == null) continue;

                            if (fcState.Phase == FundingClaimPhase.InPosition
                                && fcState.EntryPrice.HasValue && fcState.EntryPrice.Value > 0
                                && fcState.EntrySizeUsdt.HasValue
                                && fcState.LastPrice.HasValue)
                            {
                                var pnlPct = fcState.Direction == "Long"
                                    ? (fcState.LastPrice.Value - fcState.EntryPrice.Value) / fcState.EntryPrice.Value * 100m
                                    : (fcState.EntryPrice.Value - fcState.LastPrice.Value) / fcState.EntryPrice.Value * 100m;
                                unrealizedPnl += fcState.EntrySizeUsdt.Value * pnlPct / 100m;
                            }
                        }
                        else
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

    [AllowAnonymous]
    [HttpGet("top")]
    public async Task<IActionResult> GetTopBots()
    {
        var since = DateTime.UtcNow.AddDays(-90);
        var minAge = DateTime.UtcNow.AddDays(-15);
        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var topBots = await _db.Strategies
            .Include(s => s.Trades)
            .Include(s => s.Account)
            .Where(s => s.CreatedAt <= minAge)
            .Where(s => s.Trades.Any(t => t.PnlDollar != null && t.ExecutedAt >= since))
            .Select(s => new
            {
                Strategy = s,
                Trades90d = s.Trades.Where(t => t.PnlDollar != null && t.ExecutedAt >= since).ToList(),
                Exchange = s.Account.ExchangeType.ToString()
            })
            .ToListAsync();

        var result = topBots
            .Select(x =>
            {
                var pnl = x.Trades90d.Sum(t => t.PnlDollar ?? 0m);
                var winning = x.Trades90d.Count(t => t.PnlDollar > 0);
                var total = x.Trades90d.Count;

                string symbol;
                string timeframe;
                TopBotConfigDto? configDto;

                if (x.Strategy.Type == StrategyTypes.HuntingFunding)
                {
                    HuntingFundingConfig? hfConfig = null;
                    try { hfConfig = JsonSerializer.Deserialize<HuntingFundingConfig>(x.Strategy.ConfigJson, jsonOpts); }
                    catch { }

                    symbol = hfConfig?.Symbol ?? "";
                    timeframe = "";
                    configDto = hfConfig != null ? new TopBotConfigDto
                    {
                        TakeProfitPercent = hfConfig.TakeProfitPercent,
                        StopLossPercent = hfConfig.StopLossPercent
                    } : null;
                }
                else
                {
                    EmaBounceConfig? emaConfig = null;
                    try { emaConfig = JsonSerializer.Deserialize<EmaBounceConfig>(x.Strategy.ConfigJson, jsonOpts); }
                    catch { }

                    symbol = emaConfig?.Symbol ?? "";
                    timeframe = emaConfig?.Timeframe ?? "";
                    configDto = emaConfig != null ? new TopBotConfigDto
                    {
                        IndicatorType = emaConfig.IndicatorType,
                        IndicatorLength = emaConfig.IndicatorLength,
                        TakeProfitPercent = emaConfig.TakeProfitPercent,
                        StopLossPercent = emaConfig.StopLossPercent,
                        OrderSize = emaConfig.OrderSize,
                        UseMartingale = emaConfig.UseMartingale,
                        MartingaleCoeff = emaConfig.MartingaleCoeff,
                        OnlyLong = emaConfig.OnlyLong,
                        OnlyShort = emaConfig.OnlyShort
                    } : null;
                }

                var totalVolume = x.Trades90d.Sum(t => t.Quantity * t.Price);
                var pnlPercent = totalVolume > 0 ? pnl / totalVolume * 100m : 0m;

                var runningDays = (DateTime.UtcNow - x.Strategy.CreatedAt).Days;

                return new TopBotDto
                {
                    Id = x.Strategy.Id,
                    Name = x.Strategy.Name,
                    Symbol = symbol,
                    Exchange = x.Exchange,
                    StrategyType = x.Strategy.Type,
                    Timeframe = timeframe,
                    RealizedPnlPercent = Math.Round(pnlPercent, 2),
                    RunningDays = runningDays,
                    TotalTrades = total,
                    WinningTrades = winning,
                    WinRate = total > 0 ? Math.Round((decimal)winning / total * 100, 1) : 0,
                    Config = configDto
                };
            })
            .Where(b => b.RealizedPnlPercent > 0)
            .OrderByDescending(b => b.RealizedPnlPercent)
            .Take(6)
            .ToList();

        return Ok(result);
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
        var userId = GetUserId();

        var strategy = await _db.Strategies
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
            .Include(s => s.Workspace)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == userId);

        if (strategy == null)
            return NotFound();

        // Subscription limit check for active bots
        if (!IsAdmin())
        {
            var sub = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId);
            var limits = PlanLimits.GetLimits(sub?.Plan ?? SubscriptionPlan.Basic);
            var runningCount = await _db.Strategies
                .CountAsync(s => s.Account.UserId == userId && s.Status == StrategyStatus.Running);
            if (runningCount >= limits.MaxActiveBots)
                return StatusCode(403, new { message = $"Active bots limit reached ({runningCount}/{limits.MaxActiveBots}). Upgrade your plan to run more bots." });
        }

        // Merge workspace config into strategy config at start time
        // HuntingFunding has no workspace-level config — all config is per-bot, skip merge
        if (strategy.Workspace != null && strategy.Type != StrategyTypes.HuntingFunding)
        {
            var merged = MergeWorkspaceConfig(strategy.ConfigJson, strategy.Workspace.ConfigJson);
            strategy.ConfigJson = merged;
        }

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (strategy.Type == StrategyTypes.HuntingFunding)
        {
            // HuntingFunding: preserve cycle stats across restarts, reset phase to waiting
            var prevHfState = string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}"
                ? new HuntingFundingState()
                : JsonSerializer.Deserialize<HuntingFundingState>(strategy.StateJson, jsonOpts) ?? new HuntingFundingState();

            var freshHfState = new HuntingFundingState
            {
                Phase = HuntingFundingPhase.WaitingForFunding,
                CycleCount = prevHfState.CycleCount,
                CycleTotalPnl = prevHfState.CycleTotalPnl
            };
            strategy.StateJson = JsonSerializer.Serialize(freshHfState, jsonOpts);
        }
        else if (strategy.Type == StrategyTypes.GridFloat)
        {
            // GridFloat: every Start clears static bounds, batches and DCAs — the user's spec
            // says the frozen range bound is reset on Stop+Start. Realized PnL is preserved
            // across restarts for reporting.
            var prevGfState = string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}"
                ? new GridFloatState()
                : JsonSerializer.Deserialize<GridFloatState>(strategy.StateJson, jsonOpts) ?? new GridFloatState();

            var freshGfState = new GridFloatState
            {
                RealizedPnlDollar = prevGfState.RealizedPnlDollar
            };
            strategy.StateJson = JsonSerializer.Serialize(freshGfState, jsonOpts);
        }
        else if (strategy.Type == StrategyTypes.FundingClaim)
        {
            // FundingClaim: preserve cycle stats across restarts, reset phase to Idle
            var prevFcState = string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}"
                ? new FundingClaimState()
                : JsonSerializer.Deserialize<FundingClaimState>(strategy.StateJson, jsonOpts) ?? new FundingClaimState();

            var freshFcState = new FundingClaimState
            {
                Phase = FundingClaimPhase.Idle,
                CycleCount = prevFcState.CycleCount,
                CycleTotalPnl = prevFcState.CycleTotalPnl,
                CycleTotalFundingPnl = prevFcState.CycleTotalFundingPnl
            };
            strategy.StateJson = JsonSerializer.Serialize(freshFcState, jsonOpts);
        }
        else
        {
            // Preserve martingale state across restarts; clear counters so handler recalculates from history
            var prevState = string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}"
                ? new EmaBounceState()
                : JsonSerializer.Deserialize<EmaBounceState>(strategy.StateJson, jsonOpts) ?? new EmaBounceState();

            var freshState = new EmaBounceState
            {
                ConsecutiveLosses = prevState.ConsecutiveLosses,
                RunningPnlDollar = prevState.RunningPnlDollar
            };
            strategy.StateJson = JsonSerializer.Serialize(freshState, jsonOpts);
        }

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
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        strategy.Status = StrategyStatus.Stopped;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Strategy stopped", status = strategy.Status.ToString() });
    }

    // Pause: Running → Paused. Worker dispatch filters Status == Running so the handler tick
    // stops naturally; nothing is cancelled on the exchange (TP and DCA limits keep living).
    // State (Batches, DcaOrders, AnchorPrice…) is left intact — critical difference vs Stop+Start
    // which would have Start clear Batches/DcaOrders and then trip SyncFromExchangeOnStartup.
    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id)
    {
        var strategy = await _db.Strategies
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null) return NotFound();

        if (strategy.Status == StrategyStatus.Paused)
            return Ok(new { message = "Strategy already paused", status = strategy.Status.ToString() });

        if (strategy.Status != StrategyStatus.Running)
            return BadRequest(new { message = "Можно ставить на паузу только запущенного бота." });

        strategy.Status = StrategyStatus.Paused;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Strategy paused", status = strategy.Status.ToString() });
    }

    // Resume: Paused → Running. Unlike Start, does NOT reset StateJson — the same grid
    // (batches + anchor + per-batch TPs) continues from where Pause stopped. On the next
    // handler tick, HealMissingDcas re-reads ConfigJson and places DCAs for any free slot
    // inside the (possibly widened) range; HealMissingTps does the same for TPs.
    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id)
    {
        var strategy = await _db.Strategies
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null) return NotFound();

        if (strategy.Status == StrategyStatus.Running)
            return Ok(new { message = "Strategy already running", status = strategy.Status.ToString() });

        if (strategy.Status != StrategyStatus.Paused)
            return BadRequest(new { message = "Возобновить можно только бота на паузе." });

        strategy.Status = StrategyStatus.Running;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Strategy resumed", status = strategy.Status.ToString() });
    }

    // Live-tune GridFloat RangePercent while paused. Only RangePercent is mutable here —
    // changing Symbol/Direction/DcaStepPercent/Timeframe mid-grid would break the
    // LevelIdx↔fillPrice correspondence of existing batches and is therefore disallowed
    // (use Stop → Edit → Start for a fresh start with new params).
    //
    // Widening Range adds slots beyond the existing ones — HealMissingDcas places them on
    // the next tick after Resume. Narrowing Range is allowed too: existing batches outside
    // the new range stay alive with their TPs, but no fresh DCAs get placed past the new bound.
    [HttpPatch("{id:guid}/grid-float/range")]
    public async Task<IActionResult> UpdateGridFloatRange(Guid id, [FromBody] UpdateGridFloatRangeRequest request)
    {
        var strategy = await _db.Strategies
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null) return NotFound();

        if (strategy.Type != StrategyTypes.GridFloat)
            return BadRequest(new { message = "Эта операция доступна только для стратегий GridFloat." });

        if (strategy.Status != StrategyStatus.Paused)
            return BadRequest(new { message = "Бот должен быть на паузе для изменения параметров." });

        if (request.RangePercent <= 0)
            return BadRequest(new { message = "RangePercent должен быть > 0." });

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<GridFloatConfig>(strategy.ConfigJson, jsonOpts);
        if (config == null)
            return BadRequest(new { message = "Не удалось прочитать ConfigJson стратегии." });

        if (request.RangePercent < config.DcaStepPercent)
            return BadRequest(new { message = $"RangePercent ({request.RangePercent}%) должен быть ≥ DcaStepPercent ({config.DcaStepPercent}%)." });

        // Mutate only RangePercent — everything else stays exactly as user configured it.
        var configDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(strategy.ConfigJson, jsonOpts)
                         ?? new Dictionary<string, JsonElement>();
        configDict["rangePercent"] = JsonSerializer.SerializeToElement(request.RangePercent);
        strategy.ConfigJson = JsonSerializer.Serialize(configDict);

        await _db.SaveChangesAsync();

        var newLevels = (int)Math.Floor(request.RangePercent / config.DcaStepPercent);
        return Ok(new
        {
            message = "Range updated",
            rangePercent = request.RangePercent,
            dcaSlots = newLevels
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateStrategyRequest request)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        if (strategy.Status == StrategyStatus.Running || strategy.Status == StrategyStatus.Paused)
            return BadRequest(new { message = "Нельзя редактировать запущенного бота или бота на паузе. Сначала остановите его." });

        strategy.Name = request.Name;
        strategy.ConfigJson = request.ConfigJson ?? strategy.ConfigJson;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Strategy updated", strategy.Id, strategy.Name });
    }

    [HttpPatch("{id:guid}/telegram-bot")]
    public async Task<IActionResult> SetTelegramBot(Guid id, [FromBody] SetTelegramBotRequest request)
    {
        var strategy = await _db.Strategies
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null) return NotFound();

        if (request.TelegramBotId.HasValue)
        {
            var bot = await _db.TelegramBots.FirstOrDefaultAsync(b => b.Id == request.TelegramBotId.Value && b.UserId == GetUserId());
            if (bot == null) return BadRequest(new { message = "Telegram bot not found" });
        }

        strategy.TelegramBotId = request.TelegramBotId;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Telegram bot updated", telegramBotId = strategy.TelegramBotId });
    }

    [HttpPost("{id:guid}/close-position")]
    public async Task<IActionResult> ClosePosition(Guid id)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
            .FirstOrDefaultAsync(s => s.Id == id && s.Account.UserId == GetUserId());

        if (strategy == null)
            return NotFound();

        if (string.IsNullOrEmpty(strategy.StateJson) || strategy.StateJson == "{}")
            return BadRequest(new { message = "Нет открытой позиции" });

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var saveOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        if (strategy.Type == StrategyTypes.HuntingFunding)
        {
            var hfState = JsonSerializer.Deserialize<HuntingFundingState>(strategy.StateJson, jsonOpts);

            if (hfState == null ||
                (hfState.Phase != HuntingFundingPhase.InPosition && hfState.Phase != HuntingFundingPhase.OrdersPlaced))
                return BadRequest(new { message = "Нет открытой позиции" });

            var hfConfig = JsonSerializer.Deserialize<HuntingFundingConfig>(strategy.ConfigJson, jsonOpts);
            var symbol = hfConfig?.Symbol ?? JsonSerializer.Deserialize<JsonElement>(strategy.ConfigJson).GetProperty("symbol").GetString()!;

            using var exchange = _exchangeFactory.CreateFutures(strategy.Account);

            // Cancel all open orders first
            await exchange.CancelAllOrdersAsync(symbol);

            // Get real position from exchange
            var side = hfState.Direction ?? "Long";
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
            else if (hfState.TotalFilledQuantity > 0)
            {
                // Fallback to tracked data
                quantity = hfState.TotalFilledQuantity!.Value;
                avgEntry = hfState.AvgEntryPrice ?? 0m;
                totalUsdt = hfState.TotalFilledUsdt ?? 0m;
            }
            else
            {
                // No position found anywhere — just reset state
                hfState.Phase = HuntingFundingPhase.Cooldown;
                hfState.PlacedOrders = new List<PlacedOrderInfo>();
                hfState.RemainingOrdersCancelled = true;
                strategy.StateJson = JsonSerializer.Serialize(hfState, saveOpts);
                await _db.SaveChangesAsync();
                return Ok(new { message = "Позиция не найдена на бирже, ордера отменены, состояние сброшено" });
            }

            var currentPrice = await exchange.GetTickerPriceAsync(symbol);

            OrderResultDto closeResult;
            string closeSide;
            if (hfState.Direction == "Long")
            {
                closeResult = await exchange.CloseLongAsync(symbol, quantity);
                closeSide = "Sell";
            }
            else
            {
                closeResult = await exchange.CloseShortAsync(symbol, quantity);
                closeSide = "Buy";
            }

            var closePrice = currentPrice ?? avgEntry;
            decimal pnlPercent = 0m;
            if (avgEntry > 0)
            {
                pnlPercent = hfState.Direction == "Long"
                    ? (closePrice - avgEntry) / avgEntry * 100m
                    : (avgEntry - closePrice) / avgEntry * 100m;
            }
            var pnlDollar = totalUsdt * pnlPercent / 100m;
            var commission = totalUsdt * 2m * 0.0005m;
            var netPnl = pnlDollar - commission;

            _db.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                AccountId = strategy.AccountId,
                ExchangeOrderId = closeResult.OrderId ?? "",
                Symbol = symbol,
                Side = closeSide,
                Quantity = quantity,
                Price = closePrice,
                Status = "ManualClose",
                ExecutedAt = DateTime.UtcNow,
                PnlDollar = netPnl,
                Commission = commission
            });

            _db.StrategyLogs.Add(new StrategyLog
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                Level = "Info",
                Message = $"{hfState.Direction?.ToUpper()} закрыт вручную (HuntingFunding): цена={Math.Round(closePrice, 6)}, вход={Math.Round(avgEntry, 6)}, qty={Math.Round(quantity, 6)}, PnL={Math.Round(pnlPercent, 4)}% (${Math.Round(netPnl, 2)})",
                CreatedAt = DateTime.UtcNow
            });

            // Reset position data, enter cooldown
            var closedDirection = hfState.Direction;
            hfState.Phase = HuntingFundingPhase.Cooldown;
            hfState.AvgEntryPrice = null;
            hfState.TotalFilledQuantity = null;
            hfState.TotalFilledUsdt = null;
            hfState.TakeProfit = null;
            hfState.StopLoss = null;
            hfState.PositionOpenedAt = null;
            hfState.PlacedOrders = new List<PlacedOrderInfo>();
            hfState.RemainingOrdersCancelled = true;
            hfState.LastPrice = null;
            hfState.CycleTotalPnl += netPnl;

            strategy.StateJson = JsonSerializer.Serialize(hfState, saveOpts);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Позиция закрыта по рынку", details = new[] { $"{closedDirection} closed: qty={Math.Round(quantity, 6)}, price={Math.Round(closePrice, 6)}, PnL=${Math.Round(netPnl, 2)}" } });
        }

        // --- SmaDca path ---
        if (strategy.Type == StrategyTypes.SmaDca)
        {
            var smaState = JsonSerializer.Deserialize<SmaDcaState>(strategy.StateJson, jsonOpts);
            if (smaState == null || !smaState.InPosition || smaState.TotalQuantity <= 0)
                return BadRequest(new { message = "Нет открытой позиции" });

            var smaConfig = JsonSerializer.Deserialize<SmaDcaConfig>(strategy.ConfigJson, jsonOpts);
            if (smaConfig == null || string.IsNullOrEmpty(smaConfig.Symbol))
                return BadRequest(new { message = "Некорректная конфигурация (symbol пуст)" });

            using var smaExchange = _exchangeFactory.CreateFutures(strategy.Account);

            // Cancel any TP/Entry/DCA limits before market-closing.
            try { await smaExchange.CancelAllOrdersAsync(smaConfig.Symbol); } catch { }

            var smaCurrentPrice = await smaExchange.GetTickerPriceAsync(smaConfig.Symbol);

            OrderResultDto smaResult;
            string smaCloseSide;
            if (smaState.IsLong)
            {
                smaResult = await smaExchange.CloseLongAsync(smaConfig.Symbol, smaState.TotalQuantity);
                smaCloseSide = "Sell";
            }
            else
            {
                smaResult = await smaExchange.CloseShortAsync(smaConfig.Symbol, smaState.TotalQuantity);
                smaCloseSide = "Buy";
            }

            var smaClosePrice = smaCurrentPrice ?? smaState.AverageEntryPrice;
            var smaPnlPct = smaState.AverageEntryPrice > 0
                ? (smaState.IsLong
                    ? (smaClosePrice - smaState.AverageEntryPrice) / smaState.AverageEntryPrice * 100m
                    : (smaState.AverageEntryPrice - smaClosePrice) / smaState.AverageEntryPrice * 100m)
                : 0m;
            var smaGrossPnl = smaState.TotalCost * smaPnlPct / 100m;
            var smaCommission = smaState.TotalCost * 2m * 0.0005m;
            var smaNetPnl = smaGrossPnl - smaCommission;
            var smaDirection = smaState.IsLong ? "Long" : "Short";

            _db.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                AccountId = strategy.AccountId,
                ExchangeOrderId = smaResult.OrderId ?? "",
                Symbol = smaConfig.Symbol,
                Side = smaCloseSide,
                Quantity = smaState.TotalQuantity,
                Price = smaClosePrice,
                Status = "ManualClose",
                ExecutedAt = DateTime.UtcNow,
                PnlDollar = smaNetPnl,
                Commission = smaCommission
            });

            _db.StrategyLogs.Add(new StrategyLog
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                Level = "Info",
                Message = $"{smaDirection.ToUpper()} закрыт вручную (SmaDca): цена={Math.Round(smaClosePrice, 6)}, " +
                          $"вход={Math.Round(smaState.AverageEntryPrice, 6)}, qty={Math.Round(smaState.TotalQuantity, 6)}, " +
                          $"PnL={Math.Round(smaPnlPct, 4)}% (${Math.Round(smaNetPnl, 2)}, комиссия≈${Math.Round(smaCommission, 4)})",
                CreatedAt = DateTime.UtcNow
            });

            // Reset position state — mirrors SmaDcaHandler.ResetPositionState. SkipNextCandle prevents
            // an instant re-entry on the same bar; StateInitialized is preserved so restart-sync
            // doesn't re-fire.
            var smaClosedQty = smaState.TotalQuantity;
            smaState.RealizedPnlDollar += smaNetPnl;
            smaState.InPosition = false;
            smaState.TotalQuantity = 0;
            smaState.TotalCost = 0;
            smaState.AverageEntryPrice = 0;
            smaState.CurrentTakeProfit = 0;
            smaState.DcaLevel = 0;
            smaState.LastDcaPrice = 0;
            smaState.PositionOpenedAt = null;
            smaState.DcaCooldownUntil = null;
            smaState.TakeProfitOrderId = null;
            smaState.EntryOrderId = null;
            smaState.EntryOrderLimitPrice = 0;
            smaState.EntryOrderQuantity = 0;
            smaState.EntryOrderPlacedAtCandleTime = null;
            smaState.DcaOrderId = null;
            smaState.DcaOrderLimitPrice = 0;
            smaState.DcaOrderQuantity = 0;
            smaState.TpCrossedAt = null;
            smaState.SkipNextCandle = true;

            strategy.StateJson = JsonSerializer.Serialize(smaState, saveOpts);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = "Позиция закрыта по рынку",
                details = new[]
                {
                    $"{smaDirection} closed: qty={Math.Round(smaClosedQty, 6)}, price={Math.Round(smaClosePrice, 6)}, PnL=${Math.Round(smaNetPnl, 2)}"
                }
            });
        }

        // --- GridFloat path ---
        if (strategy.Type == StrategyTypes.GridFloat)
        {
            var gfState = JsonSerializer.Deserialize<GridFloatState>(strategy.StateJson, jsonOpts);
            if (gfState == null || gfState.Batches == null || gfState.Batches.Count == 0)
                return BadRequest(new { message = "Нет открытой позиции" });

            var gfConfig = JsonSerializer.Deserialize<GridFloatConfig>(strategy.ConfigJson, jsonOpts);
            if (gfConfig == null || string.IsNullOrEmpty(gfConfig.Symbol))
                return BadRequest(new { message = "Некорректная конфигурация (symbol пуст)" });

            using var gfExchange = _exchangeFactory.CreateFutures(strategy.Account);

            // Cancel every limit (TPs + DCAs) before market-closing the aggregate position.
            try { await gfExchange.CancelAllOrdersAsync(gfConfig.Symbol); } catch { }

            var totalQty = gfState.Batches.Sum(b => b.Qty);
            var totalCost = gfState.Batches.Sum(b => b.FillPrice * b.Qty);
            var avgEntry = totalQty > 0 ? totalCost / totalQty : 0m;
            var gfCurrentPrice = await gfExchange.GetTickerPriceAsync(gfConfig.Symbol);

            OrderResultDto gfResult;
            string gfCloseSide;
            if (gfState.IsLong)
            {
                gfResult = await gfExchange.CloseLongAsync(gfConfig.Symbol, totalQty);
                gfCloseSide = "Sell";
            }
            else
            {
                gfResult = await gfExchange.CloseShortAsync(gfConfig.Symbol, totalQty);
                gfCloseSide = "Buy";
            }

            var gfClosePrice = gfCurrentPrice ?? avgEntry;
            var gfPnlPct = avgEntry > 0
                ? (gfState.IsLong
                    ? (gfClosePrice - avgEntry) / avgEntry * 100m
                    : (avgEntry - gfClosePrice) / avgEntry * 100m)
                : 0m;
            var gfGrossPnl = totalCost * gfPnlPct / 100m;
            var gfCommission = totalCost * 2m * 0.0005m;
            var gfNetPnl = gfGrossPnl - gfCommission;
            var gfDirection = gfState.IsLong ? "Long" : "Short";

            _db.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                AccountId = strategy.AccountId,
                ExchangeOrderId = gfResult.OrderId ?? "",
                Symbol = gfConfig.Symbol,
                Side = gfCloseSide,
                Quantity = totalQty,
                Price = gfClosePrice,
                Status = "ManualClose",
                ExecutedAt = DateTime.UtcNow,
                PnlDollar = gfNetPnl,
                Commission = gfCommission
            });

            _db.StrategyLogs.Add(new StrategyLog
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                Level = "Info",
                Message = $"{gfDirection.ToUpper()} закрыт вручную (GridFloat): " +
                          $"qty={Math.Round(totalQty, 6)} @ {Math.Round(gfClosePrice, 6)}, " +
                          $"avg={Math.Round(avgEntry, 6)}, батчей={gfState.Batches.Count}, " +
                          $"PnL={Math.Round(gfPnlPct, 4)}% (${Math.Round(gfNetPnl, 2)}, комиссия≈${Math.Round(gfCommission, 4)})",
                CreatedAt = DateTime.UtcNow
            });

            // Reset position state — mirrors GridFloatHandler.OnFullClose. SkipNextCandle
            // prevents an instant re-entry on the same bar; StateInitialized is preserved so
            // restart-sync doesn't fire again.
            gfState.RealizedPnlDollar += gfNetPnl;
            gfState.Batches.Clear();
            gfState.DcaOrders.Clear();
            gfState.AnchorPrice = 0;
            gfState.OpenAfterTime = DateTime.UtcNow;
            gfState.PlacementCooldownUntil = null;

            strategy.StateJson = JsonSerializer.Serialize(gfState, saveOpts);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = "Позиция закрыта по рынку",
                details = new[]
                {
                    $"{gfDirection} closed: qty={Math.Round(totalQty, 6)}, price={Math.Round(gfClosePrice, 6)}, PnL=${Math.Round(gfNetPnl, 2)}"
                }
            });
        }

        // --- EmaBounce / MaratG path ---
        var state = JsonSerializer.Deserialize<EmaBounceState>(strategy.StateJson, jsonOpts);

        if (state == null || (state.OpenLong == null && state.OpenShort == null))
            return BadRequest(new { message = "Нет открытой позиции" });

        var emaConfig = JsonSerializer.Deserialize<EmaBounceConfig>(strategy.ConfigJson, jsonOpts);
        var configEl = JsonSerializer.Deserialize<JsonElement>(strategy.ConfigJson);
        var symbol2 = configEl.GetProperty("symbol").GetString()!;

        using var exchange2 = _exchangeFactory.CreateFutures(strategy.Account);

        // Get current price for PnL calculation
        var currentPrice2 = await exchange2.GetTickerPriceAsync(symbol2);

        var results = new List<string>();

        if (state.OpenLong != null)
        {
            var position = state.OpenLong;
            var result = await exchange2.CloseLongAsync(symbol2, position.Quantity);
            var closePrice = currentPrice2 ?? position.EntryPrice;
            var pnlPercent = (closePrice - position.EntryPrice) / position.EntryPrice * 100m;
            var pnlDollar = position.OrderSize * pnlPercent / 100m;
            var commission = position.OrderSize * 2m * 0.0005m;
            var netPnl = pnlDollar - commission;

            // Update martingale state
            if (emaConfig?.UseMartingale == true)
            {
                state.RunningPnlDollar += pnlDollar;
                if (pnlPercent > 0)
                    state.ConsecutiveLosses = 0;
                else
                    state.ConsecutiveLosses++;
            }

            // Record trade
            _db.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                AccountId = strategy.AccountId,
                ExchangeOrderId = result.OrderId ?? "",
                Symbol = symbol2,
                Side = "Sell",
                Quantity = position.Quantity,
                Price = closePrice,
                Status = "ManualClose",
                ExecutedAt = DateTime.UtcNow,
                PnlDollar = netPnl,
                Commission = commission
            });

            // Write log
            _db.StrategyLogs.Add(new StrategyLog
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                Level = "Info",
                Message = $"LONG закрыт вручную: цена={closePrice}, вход={position.EntryPrice}, PnL={Math.Round(pnlPercent, 4)}% (${Math.Round(netPnl, 2)})",
                CreatedAt = DateTime.UtcNow
            });

            results.Add($"Long closed: qty={position.Quantity}, price={closePrice}, PnL=${Math.Round(netPnl, 2)}");
            state.OpenLong = null;
            state.LastPrice = null;
            state.LongCounter = 0;
            state.WaitNextCandleAfterLongClose = true;
        }

        if (state.OpenShort != null)
        {
            var position = state.OpenShort;
            var result = await exchange2.CloseShortAsync(symbol2, position.Quantity);
            var closePrice = currentPrice2 ?? position.EntryPrice;
            var pnlPercent = (position.EntryPrice - closePrice) / position.EntryPrice * 100m;
            var pnlDollar = position.OrderSize * pnlPercent / 100m;
            var commission = position.OrderSize * 2m * 0.0005m;
            var netPnl = pnlDollar - commission;

            // Update martingale state
            if (emaConfig?.UseMartingale == true)
            {
                state.RunningPnlDollar += pnlDollar;
                if (pnlPercent > 0)
                    state.ConsecutiveLosses = 0;
                else
                    state.ConsecutiveLosses++;
            }

            // Record trade
            _db.Trades.Add(new Trade
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                AccountId = strategy.AccountId,
                ExchangeOrderId = result.OrderId ?? "",
                Symbol = symbol2,
                Side = "Buy",
                Quantity = position.Quantity,
                Price = closePrice,
                Status = "ManualClose",
                ExecutedAt = DateTime.UtcNow,
                PnlDollar = netPnl,
                Commission = commission
            });

            // Write log
            _db.StrategyLogs.Add(new StrategyLog
            {
                Id = Guid.NewGuid(),
                StrategyId = strategy.Id,
                Level = "Info",
                Message = $"SHORT закрыт вручную: цена={closePrice}, вход={position.EntryPrice}, PnL={Math.Round(pnlPercent, 4)}% (${Math.Round(netPnl, 2)})",
                CreatedAt = DateTime.UtcNow
            });

            results.Add($"Short closed: qty={position.Quantity}, price={closePrice}, PnL=${Math.Round(netPnl, 2)}");
            state.OpenShort = null;
            state.LastPrice = null;
            state.ShortCounter = 0;
            state.WaitNextCandleAfterShortClose = true;
        }

        // Recompute next order size with proper martingale calculation
        if (emaConfig != null)
            state.NextOrderSize = EmaBounceHandler.GetCurrentOrderSize(emaConfig, state).orderSize;

        strategy.StateJson = JsonSerializer.Serialize(state, saveOpts);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Позиция закрыта по рынку", details = results });
    }

    [HttpPost("{id:guid}/reset-losses")]
    public async Task<IActionResult> ResetLosses(Guid id)
    {
        var strategy = await _db.Strategies
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
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
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
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
            .Include(s => s.Account).ThenInclude(a => a.Proxy)
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

public class SetTelegramBotRequest
{
    public Guid? TelegramBotId { get; set; }
}

public class UpdateGridFloatRangeRequest
{
    public decimal RangePercent { get; set; }
}
