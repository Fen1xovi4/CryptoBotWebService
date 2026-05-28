using System.Security.Claims;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/balance-snapshots")]
[Authorize]
public class BalanceSnapshotsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IExchangeServiceFactory _exchangeFactory;
    private readonly ILogger<BalanceSnapshotsController> _logger;

    public BalanceSnapshotsController(AppDbContext db, IExchangeServiceFactory exchangeFactory, ILogger<BalanceSnapshotsController> logger)
    {
        _db = db;
        _exchangeFactory = exchangeFactory;
        _logger = logger;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<BalanceSnapshotDto>>> GetAll([FromQuery] int limit = 50)
    {
        if (limit <= 0 || limit > 500) limit = 50;
        var userId = GetUserId();

        var snapshots = await _db.BalanceSnapshots
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.TakenAt)
            .Take(limit)
            .Select(s => new BalanceSnapshotDto
            {
                Id = s.Id,
                TotalUsdt = s.TotalUsdt,
                TakenAt = s.TakenAt
            })
            .ToListAsync();

        return Ok(snapshots);
    }

    [HttpPost]
    public async Task<ActionResult<BalanceSnapshotDto>> Create()
    {
        var userId = GetUserId();

        var accounts = await _db.ExchangeAccounts
            .Include(a => a.Proxy)
            .Where(a => a.UserId == userId && a.IsActive)
            .ToListAsync();

        if (accounts.Count == 0)
            return BadRequest(new { message = "No active accounts to snapshot." });

        decimal total = 0m;
        int succeeded = 0;
        var errors = new List<string>();

        foreach (var account in accounts)
        {
            try
            {
                using var service = (IDisposable)_exchangeFactory.Create(account);
                var exchangeService = (IExchangeService)service;
                var balances = await exchangeService.GetBalancesAsync();
                var usdt = balances.FirstOrDefault(b => b.Asset == "USDT");
                if (usdt != null) total += usdt.Total;
                succeeded++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Balance snapshot: failed to query account {AccountId} ({Name})", account.Id, account.Name);
                errors.Add($"{account.Name}: {ex.Message}");
            }
        }

        if (succeeded == 0)
            return StatusCode(502, new { message = "All exchange queries failed.", errors });

        var snapshot = new BalanceSnapshot
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TotalUsdt = total,
            TakenAt = DateTime.UtcNow
        };
        _db.BalanceSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            id = snapshot.Id,
            totalUsdt = snapshot.TotalUsdt,
            takenAt = snapshot.TakenAt,
            succeededAccounts = succeeded,
            totalAccounts = accounts.Count,
            errors
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetUserId();
        var snapshot = await _db.BalanceSnapshots.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (snapshot == null) return NotFound();
        _db.BalanceSnapshots.Remove(snapshot);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
