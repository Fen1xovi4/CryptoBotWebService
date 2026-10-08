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
        var (account, secondAccount, error) = await ResolveAccountsAsync(request);
        if (error != null)
            return error;

        IFuturesExchangeService? secondService = null;
        try
        {
            using var service = _exchangeFactory.CreateFutures(account!);
            if (secondAccount != null)
                secondService = _exchangeFactory.CreateFutures(secondAccount);

            var result = await _engine.RunAsync(request, service, secondService, ct,
                account!.ExchangeType, secondAccount?.ExchangeType);
            return Ok(result);
        }
        catch (NotSupportedException)
        {
            return BadRequest(new { message = $"Биржа {account!.ExchangeType} не поддерживается симулятором. Используйте аккаунт Bybit, Bitget или BingX." });
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
        finally
        {
            secondService?.Dispose();
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
        var (account, secondAccount, error) = await ResolveAccountsAsync(request);
        if (error != null)
            return error;

        try
        {
            return Ok(_optimizer.Start(request, account!, secondAccount));
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

    [HttpGet("optimize/{jobId:guid}")]
    public IActionResult GetOptimization(Guid jobId)
    {
        var status = _optimizer.GetStatus(jobId);
        return status == null ? NotFound() : Ok(status);
    }

    [HttpPost("optimize/{jobId:guid}/cancel")]
    public IActionResult CancelOptimization(Guid jobId) =>
        _optimizer.Cancel(jobId) ? Ok() : NotFound();

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

    /// <summary>
    /// Resolves the caller's account(s) for a simulation-shaped request and enforces the
    /// FuturesArbitrage two-venue rules (same ones the live handler validates).
    /// </summary>
    private async Task<(ExchangeAccount? Account, ExchangeAccount? SecondAccount, IActionResult? Error)>
        ResolveAccountsAsync(SimulationRunRequest request)
    {
        var account = await LoadOwnedAccountAsync(request.AccountId);
        if (account == null)
            return (null, null, NotFound());

        ExchangeAccount? secondAccount = null;
        if (request.StrategyType == StrategyTypes.FuturesArbitrage)
        {
            if (!request.SecondAccountId.HasValue)
                return (null, null, BadRequest(new { message = "Для FuturesArbitrage нужен второй аккаунт (secondAccountId) — арбитраж торгуется между двумя биржами." }));

            if (request.SecondAccountId.Value == request.AccountId)
                return (null, null, BadRequest(new { message = "Второй аккаунт совпадает с первым — межбиржевому арбитражу нужны две разные биржи." }));

            secondAccount = await LoadOwnedAccountAsync(request.SecondAccountId.Value);
            if (secondAccount == null)
                return (null, null, NotFound());

            if (account.ExchangeType == ExchangeType.Dzengi || secondAccount.ExchangeType == ExchangeType.Dzengi)
                return (null, null, BadRequest(new { message = "Dzengi не поддерживается симулятором. Используйте аккаунты Bybit, Bitget или BingX." }));

            if (account.ExchangeType == secondAccount.ExchangeType)
                return (null, null, BadRequest(new { message = $"Оба аккаунта на {account.ExchangeType} — межбиржевому арбитражу нужны две разные биржи." }));
        }

        return (account, secondAccount, null);
    }

    /// <summary>Account lookup with the proxy graph the exchange factory needs, scoped to the caller.</summary>
    private Task<ExchangeAccount?> LoadOwnedAccountAsync(Guid accountId) =>
        _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());
}
