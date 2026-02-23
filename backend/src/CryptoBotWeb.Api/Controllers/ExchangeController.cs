using System.Security.Claims;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ExchangeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _exchangeFactory;

    public ExchangeController(AppDbContext db, IExchangeServiceFactory exchangeFactory)
    {
        _db = db;
        _exchangeFactory = exchangeFactory;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("{accountId:guid}/balances")]
    public async Task<ActionResult<AccountBalanceResponse>> GetBalances(Guid accountId)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            var service = _exchangeFactory.Create(account);
            var balances = await service.GetBalancesAsync();
            if (service is IDisposable disposable) disposable.Dispose();

            return Ok(new AccountBalanceResponse
            {
                AccountId = account.Id,
                AccountName = account.Name,
                Exchange = account.ExchangeType.ToString(),
                Balances = balances
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{accountId:guid}/ticker")]
    public async Task<IActionResult> GetTicker(Guid accountId, [FromQuery] string symbol)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            var service = _exchangeFactory.Create(account);
            var price = await service.GetTickerPriceAsync(symbol);
            if (service is IDisposable disposable) disposable.Dispose();

            return Ok(new { symbol, price });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
