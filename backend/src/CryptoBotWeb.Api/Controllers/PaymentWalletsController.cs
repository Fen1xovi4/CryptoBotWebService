using System.Security.Claims;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class PaymentWalletsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PaymentWalletsController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var now = DateTime.UtcNow;

        var wallets = await _db.PaymentWallets
            .AsNoTracking()
            .Include(w => w.PaymentSessions)
            .ToListAsync();

        var result = wallets.Select(w => new PaymentWalletDto
        {
            Id = w.Id,
            AddressTrc20 = w.AddressTrc20,
            AddressBep20 = w.AddressBep20,
            IsActive = w.IsActive,
            CreatedAt = w.CreatedAt,
            IsLocked = w.PaymentSessions.Any(s =>
                s.Status == PaymentSessionStatus.Pending && s.ExpiresAt > now)
        });

        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePaymentWalletRequest request)
    {
        var wallet = new PaymentWallet
        {
            Id = Guid.NewGuid(),
            AddressTrc20 = request.AddressTrc20,
            AddressBep20 = request.AddressBep20,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.PaymentWallets.Add(wallet);
        await _db.SaveChangesAsync();

        return Ok(new PaymentWalletDto
        {
            Id = wallet.Id,
            AddressTrc20 = wallet.AddressTrc20,
            AddressBep20 = wallet.AddressBep20,
            IsActive = wallet.IsActive,
            CreatedAt = wallet.CreatedAt,
            IsLocked = false
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreatePaymentWalletRequest request)
    {
        var wallet = await _db.PaymentWallets.FindAsync(id);
        if (wallet == null)
            return NotFound(new { message = "Wallet not found" });

        wallet.AddressTrc20 = request.AddressTrc20;
        wallet.AddressBep20 = request.AddressBep20;

        await _db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var isLocked = await _db.PaymentSessions.AnyAsync(s =>
            s.WalletId == id &&
            s.Status == PaymentSessionStatus.Pending &&
            s.ExpiresAt > now);

        return Ok(new PaymentWalletDto
        {
            Id = wallet.Id,
            AddressTrc20 = wallet.AddressTrc20,
            AddressBep20 = wallet.AddressBep20,
            IsActive = wallet.IsActive,
            CreatedAt = wallet.CreatedAt,
            IsLocked = isLocked
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var wallet = await _db.PaymentWallets.FindAsync(id);
        if (wallet == null)
            return NotFound(new { message = "Wallet not found" });

        var now = DateTime.UtcNow;
        var hasPendingSession = await _db.PaymentSessions.AnyAsync(s =>
            s.WalletId == id &&
            s.Status == PaymentSessionStatus.Pending &&
            s.ExpiresAt > now);

        if (hasPendingSession)
            return Conflict(new { message = "Cannot deactivate wallet with active pending sessions" });

        wallet.IsActive = false;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Wallet deactivated" });
    }
}
