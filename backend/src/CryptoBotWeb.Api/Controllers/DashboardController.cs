using System.Security.Claims;
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

    public DashboardController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummary>> GetSummary()
    {
        var userId = GetUserId();

        var accounts = await _db.ExchangeAccounts
            .Where(a => a.UserId == userId)
            .ToListAsync();

        var runningStrategies = await _db.Strategies
            .CountAsync(s => s.Account.UserId == userId && s.Status == StrategyStatus.Running);

        var totalTrades = await _db.Trades
            .CountAsync(t => t.Account.UserId == userId);

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
}
