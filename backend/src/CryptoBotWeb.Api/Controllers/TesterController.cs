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

    public TesterController(AppDbContext db, IExchangeServiceFactory exchangeFactory, SimulationEngine engine)
    {
        _db = db;
        _exchangeFactory = exchangeFactory;
        _engine = engine;
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

        // FuturesArbitrage prices a spread between two venues, so it needs a second account whose
        // exchange differs from the primary one — same rules the live handler validates.
        ExchangeAccount? secondAccount = null;
        if (request.StrategyType == StrategyTypes.FuturesArbitrage)
        {
            if (!request.SecondAccountId.HasValue)
                return BadRequest(new { message = "Для FuturesArbitrage нужен второй аккаунт (secondAccountId) — арбитраж торгуется между двумя биржами." });

            if (request.SecondAccountId.Value == request.AccountId)
                return BadRequest(new { message = "Второй аккаунт совпадает с первым — межбиржевому арбитражу нужны две разные биржи." });

            secondAccount = await LoadOwnedAccountAsync(request.SecondAccountId.Value);
            if (secondAccount == null)
                return NotFound();

            if (account.ExchangeType == ExchangeType.Dzengi || secondAccount.ExchangeType == ExchangeType.Dzengi)
                return BadRequest(new { message = "Dzengi не поддерживается симулятором. Используйте аккаунты Bybit, Bitget или BingX." });

            if (account.ExchangeType == secondAccount.ExchangeType)
                return BadRequest(new { message = $"Оба аккаунта на {account.ExchangeType} — межбиржевому арбитражу нужны две разные биржи." });
        }

        IFuturesExchangeService? secondService = null;
        try
        {
            using var service = _exchangeFactory.CreateFutures(account);
            if (secondAccount != null)
                secondService = _exchangeFactory.CreateFutures(secondAccount);

            var result = await _engine.RunAsync(request, service, secondService, ct);
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
        finally
        {
            secondService?.Dispose();
        }
    }

    /// <summary>Account lookup with the proxy graph the exchange factory needs, scoped to the caller.</summary>
    private Task<ExchangeAccount?> LoadOwnedAccountAsync(Guid accountId) =>
        _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());
}
