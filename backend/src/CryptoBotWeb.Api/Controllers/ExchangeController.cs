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
            .Include(a => a.Proxy)
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

    [HttpGet("{accountId:guid}/symbols")]
    public async Task<ActionResult<List<SymbolDto>>> GetSymbols(Guid accountId)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.Proxy)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            using var service = _exchangeFactory.CreateFutures(account);
            var symbols = await service.GetSymbolsAsync();
            return Ok(symbols);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Probes whether the account is configured in hedge mode for the given symbol.
    /// Used by the Grid Hedge bot card to warn the user if they picked PositionMode=Hedge
    /// but the exchange account is still in one-way mode (orders would otherwise fail).
    /// Bybit-only in V1 — other exchanges return supported=false.
    /// </summary>
    [HttpGet("{accountId:guid}/position-mode")]
    public async Task<IActionResult> GetPositionMode(Guid accountId, [FromQuery] string symbol)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.Proxy)
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            using var service = _exchangeFactory.CreateFutures(account);
            if (!service.IsHedgeModeSupported)
                return Ok(new { supported = false, hedgeMode = (bool?)null, message = "Hedge mode не поддерживается этой биржей." });

            var hedge = await service.IsHedgeModeEnabledAsync(symbol);
            return Ok(new
            {
                supported = true,
                hedgeMode = hedge,
                message = hedge switch
                {
                    true => $"Аккаунт в Hedge Mode для {symbol}.",
                    false => $"Аккаунт в One-Way режиме для {symbol}. Переключите на бирже в Hedge Mode.",
                    null => $"Не удалось определить режим для {symbol} (нет позиций или сбой запроса)."
                }
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
            .Include(a => a.Proxy)
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
