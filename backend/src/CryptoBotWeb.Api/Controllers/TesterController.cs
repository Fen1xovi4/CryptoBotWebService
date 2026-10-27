using System.Security.Claims;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Simulation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class TesterController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _exchangeFactory;
    private readonly SimulationEngine _engine;
    private readonly KlineHistoryCache _klineCache;
    private readonly OptimizationJobService _optimizer;

    public TesterController(AppDbContext db, IExchangeServiceFactory exchangeFactory, SimulationEngine engine,
        KlineHistoryCache klineCache, OptimizationJobService optimizer)
    {
        _db = db;
        _exchangeFactory = exchangeFactory;
        _engine = engine;
        _klineCache = klineCache;
        _optimizer = optimizer;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("strategies")]
    public IActionResult GetSupportedStrategies() => Ok(_engine.SupportedStrategyTypes);

    [HttpGet("klines")]
    public async Task<IActionResult> GetKlines(
        [FromQuery] Guid accountId,
        [FromQuery] string symbol,
        [FromQuery] string timeframe = "1h",
        [FromQuery] int limit = 200)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        if (limit < 1) limit = 1;
        if (limit > 1000) limit = 1000;

        try
        {
            using var service = _exchangeFactory.CreateFutures(account);
            var candles = await service.GetKlinesAsync(symbol, timeframe, limit);
            return Ok(candles);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("simulate")]
    public async Task<IActionResult> Simulate([FromBody] SimulationRunRequest request, CancellationToken ct)
    {
        var account = await LoadOwnedAccountAsync(request.AccountId);
        if (account == null)
            return NotFound();

        try
        {
            using var service = _exchangeFactory.CreateFutures(account);
            var result = await _engine.RunAsync(request, service, ct, account.ExchangeType);
            return Ok(result);
        }
        catch (NotSupportedException)
        {
            return BadRequest(new { message = $"Биржа {account.ExchangeType} не поддерживается симулятором. Используйте аккаунт Bybit, Bitget или BingX." });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // client went away — nothing to report
        }
        catch (Exception ex)
        {
            // Exchange-side failures during the history download (rate limit, bad symbol, range
            // rejected…) surface as plain Exception from the clients. Report them to the UI instead
            // of a bare 500 — the message already names the exchange and symbol.
            return BadRequest(new { message = ex.Message });
        }
    }

    // ───────────── parameter optimization (sweep over config fields) ─────────────

    /// <summary>
    /// Starts a parameter sweep: same body as /simulate plus a list of config fields to vary.
    /// The job downloads history once and runs every combination in the background;
    /// poll GET /optimize/{jobId} for progress and results.
    /// </summary>
    [HttpPost("optimize")]
    public async Task<IActionResult> StartOptimization([FromBody] OptimizationRunRequest request)
    {
        var account = await LoadOwnedAccountAsync(request.AccountId);
        if (account == null)
            return NotFound();

        try
        {
            return Ok(_optimizer.Start(request, account));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Progress and, when Done, the result page: sorted server-side by <paramref name="sortBy"/>
    /// (summary metric name, "pnlToDd", or "p:&lt;path&gt;") and cut to <paramref name="limit"/>
    /// rows — sweeps may hold 100k summaries, which are never shipped whole.
    /// </summary>
    [HttpGet("optimize/{jobId:guid}")]
    public IActionResult GetOptimization(
        Guid jobId, [FromQuery] string? sortBy, [FromQuery] bool desc = true,
        [FromQuery] int limit = OptimizationJobService.DefaultPageSize, [FromQuery] int minTrades = 0)
    {
        var status = _optimizer.GetStatus(jobId, sortBy, desc, limit, Math.Max(0, minTrades));
        return status == null ? NotFound() : Ok(status);
    }

    [HttpPost("optimize/{jobId:guid}/cancel")]
    public IActionResult CancelOptimization(Guid jobId) =>
        _optimizer.Cancel(jobId) ? Ok() : NotFound();

    // ───────────── saved parameter sets (per exchange + symbol + strategy) ─────────────

    /// <summary>Saved sets, newest first. All filters optional; symbol is matched case-insensitively.</summary>
    [HttpGet("param-sets")]
    public async Task<IActionResult> GetParamSets(
        [FromQuery] ExchangeType? exchangeType, [FromQuery] string? symbol, [FromQuery] string? strategyType,
        CancellationToken ct)
    {
        var q = _db.TesterParamSets.AsNoTracking();
        if (exchangeType.HasValue) q = q.Where(p => p.ExchangeType == exchangeType.Value);
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var sym = NormalizeSymbol(symbol);
            q = q.Where(p => p.Symbol == sym);
        }
        if (!string.IsNullOrWhiteSpace(strategyType)) q = q.Where(p => p.StrategyType == strategyType);

        var list = await q.OrderByDescending(p => p.CreatedAt).Take(500).ToListAsync(ct);
        return Ok(list.Select(ToDto));
    }

    [HttpPost("param-sets")]
    public async Task<IActionResult> SaveParamSet([FromBody] SaveTesterParamSetRequest request, CancellationToken ct)
    {
        var account = await _db.ExchangeAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == GetUserId(), ct);
        if (account == null)
            return NotFound(new { message = "Аккаунт не найден." });
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "Не указан символ." });
        if (string.IsNullOrWhiteSpace(request.StrategyType) || !_engine.SupportedStrategyTypes.Contains(request.StrategyType))
            return BadRequest(new { message = "Неизвестный тип стратегии." });
        if (request.Summary == null)
            return BadRequest(new { message = "Нет результата симуляции — нечего сохранять." });

        try
        {
            // Reject malformed JSON up front; the column is jsonb and would fail later with a 500.
            using var _ = System.Text.Json.JsonDocument.Parse(request.ConfigJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequest(new { message = "ConfigJson не является корректным JSON." });
        }

        var s = request.Summary;
        var entity = new TesterParamSet
        {
            ExchangeType = account.ExchangeType,
            Symbol = NormalizeSymbol(request.Symbol),
            StrategyType = request.StrategyType,
            Name = string.IsNullOrWhiteSpace(request.Name)
                ? $"{NormalizeSymbol(request.Symbol)} {request.StrategyType} {DateTime.UtcNow:yyyy-MM-dd}"
                : request.Name.Trim(),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            ConfigJson = request.ConfigJson,
            SweptParamsJson = System.Text.Json.JsonSerializer.Serialize(request.SweptParams ?? new()),
            PeriodFromUtc = s.StartTime == default ? null : s.StartTime,
            PeriodToUtc = s.EndTime == default ? null : s.EndTime,
            PathTimeframe = string.IsNullOrWhiteSpace(request.PathTimeframe) ? "1m" : request.PathTimeframe,
            NetPnlUsd = s.NetPnlUsd,
            MaxDrawdownUsd = s.MaxDrawdownUsd,
            MaxDrawdownPercent = s.MaxDrawdownPercent,
            WinRate = s.WinRate,
            TotalTrades = s.TotalTrades,
            CreatedByUserId = GetUserId(),
        };
        _db.TesterParamSets.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpPatch("param-sets/{id:guid}")]
    public async Task<IActionResult> UpdateParamSet(Guid id, [FromBody] UpdateTesterParamSetRequest request, CancellationToken ct)
    {
        var entity = await _db.TesterParamSets.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (entity == null) return NotFound();
        if (request.Name != null && !string.IsNullOrWhiteSpace(request.Name)) entity.Name = request.Name.Trim();
        if (request.Note != null) entity.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpDelete("param-sets/{id:guid}")]
    public async Task<IActionResult> DeleteParamSet(Guid id, CancellationToken ct)
    {
        var removed = await _db.TesterParamSets.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        return removed == 0 ? NotFound() : Ok();
    }

    private static string NormalizeSymbol(string symbol) =>
        new string(symbol.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static TesterParamSetDto ToDto(TesterParamSet p)
    {
        Dictionary<string, decimal> swept;
        try
        {
            swept = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, decimal>>(p.SweptParamsJson) ?? new();
        }
        catch (System.Text.Json.JsonException)
        {
            swept = new();
        }
        return new TesterParamSetDto
        {
            Id = p.Id,
            ExchangeType = (int)p.ExchangeType,
            Symbol = p.Symbol,
            StrategyType = p.StrategyType,
            Name = p.Name,
            Note = p.Note,
            ConfigJson = p.ConfigJson,
            SweptParams = swept,
            PeriodFromUtc = p.PeriodFromUtc,
            PeriodToUtc = p.PeriodToUtc,
            PathTimeframe = p.PathTimeframe,
            NetPnlUsd = p.NetPnlUsd,
            MaxDrawdownUsd = p.MaxDrawdownUsd,
            MaxDrawdownPercent = p.MaxDrawdownPercent,
            WinRate = p.WinRate,
            TotalTrades = p.TotalTrades,
            CreatedAt = p.CreatedAt,
        };
    }

    // ───────────── kline history cache (admin maintenance) ─────────────

    [HttpGet("cache")]
    public async Task<IActionResult> GetCache(CancellationToken ct) =>
        Ok(await _klineCache.ListAsync(ct));

    /// <summary>Drop cached history. No filters = everything; otherwise one exchange/symbol/timeframe key.</summary>
    [HttpDelete("cache")]
    public async Task<IActionResult> ClearCache(
        [FromQuery] ExchangeType? exchangeType, [FromQuery] string? symbol, [FromQuery] string? timeframe,
        CancellationToken ct)
    {
        var removed = await _klineCache.ClearAsync(exchangeType, symbol, timeframe, ct);
        return Ok(new { removedCandles = removed });
    }

    /// <summary>Account lookup with the proxy graph the exchange factory needs, scoped to the caller.</summary>
    private Task<ExchangeAccount?> LoadOwnedAccountAsync(Guid accountId) =>
        _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());
}
