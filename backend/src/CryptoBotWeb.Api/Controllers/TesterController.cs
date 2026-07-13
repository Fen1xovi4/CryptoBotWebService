using System.Security.Claims;
using CryptoBotWeb.Core.DTOs;
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
        var account = await _db.ExchangeAccounts
            .Include(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
            .FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            using var service = _exchangeFactory.CreateFutures(account);
            var result = await _engine.RunAsync(request, service, ct);
            return Ok(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
