using System.Security.Claims;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Strategies;
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

    public TesterController(AppDbContext db, IExchangeServiceFactory exchangeFactory)
    {
        _db = db;
        _exchangeFactory = exchangeFactory;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("klines")]
    public async Task<IActionResult> GetKlines(
        [FromQuery] Guid accountId,
        [FromQuery] string symbol,
        [FromQuery] string timeframe = "1h",
        [FromQuery] int limit = 200)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        if (limit < 1) limit = 1;
        if (limit > 1000) limit = 1000;

        try
        {
            var service = _exchangeFactory.CreateFutures(account);
            var candles = await service.GetKlinesAsync(symbol, timeframe, limit);
            if (service is IDisposable disposable) disposable.Dispose();

            return Ok(candles);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("simulate")]
    public async Task<IActionResult> Simulate([FromBody] SimulationRequest request)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == request.AccountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        if (request.CandleLimit < 1) request.CandleLimit = 1;
        if (request.CandleLimit > 1000) request.CandleLimit = 1000;

        try
        {
            var service = _exchangeFactory.CreateFutures(account);
            var candles = await service.GetKlinesAsync(request.Symbol, request.Timeframe, request.CandleLimit);
            if (service is IDisposable disposable) disposable.Dispose();

            var config = new EmaBounceConfig
            {
                IndicatorType = request.IndicatorType,
                IndicatorLength = request.IndicatorLength,
                CandleCount = request.CandleCount,
                OffsetPercent = request.OffsetPercent,
                TakeProfitPercent = request.TakeProfitPercent,
                StopLossPercent = request.StopLossPercent,
                Symbol = request.Symbol,
                Timeframe = request.Timeframe,
                OrderSize = request.OrderSize,
                UseMartingale = request.UseMartingale,
                MartingaleCoeff = request.MartingaleCoeff,
                UseSteppedMartingale = request.UseSteppedMartingale,
                MartingaleStep = request.MartingaleStep,
                UseDrawdownScale = request.UseDrawdownScale,
                DrawdownBalance = request.DrawdownBalance,
                DrawdownPercent = request.DrawdownPercent,
                DrawdownTarget = request.DrawdownTarget
            };

            var result = EmaBounceSimulator.Run(candles, config);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
