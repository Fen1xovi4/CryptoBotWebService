using System.Security.Claims;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AccountsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly IExchangeServiceFactory _exchangeFactory;

    public AccountsController(AppDbContext db, IEncryptionService encryption, IExchangeServiceFactory exchangeFactory)
    {
        _db = db;
        _encryption = encryption;
        _exchangeFactory = exchangeFactory;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool IsAdmin() => User.IsInRole("Admin");

    [HttpGet]
    public async Task<ActionResult<List<ExchangeAccountDto>>> GetAll([FromQuery] Guid? userId)
    {
        var targetUserId = IsAdmin() && userId.HasValue ? userId.Value : GetUserId();

        var accounts = await _db.ExchangeAccounts
            .Where(a => a.UserId == targetUserId)
            .Include(a => a.Proxy)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new ExchangeAccountDto
            {
                Id = a.Id,
                Name = a.Name,
                ExchangeType = a.ExchangeType,
                ProxyId = a.ProxyId,
                ProxyName = a.Proxy != null ? a.Proxy.Name : null,
                IsActive = a.IsActive,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return Ok(accounts);
    }

    [HttpPost]
    public async Task<ActionResult<ExchangeAccountDto>> Create([FromBody] CreateExchangeAccountRequest request)
    {
        var userId = GetUserId();

        // Non-admin users must provide a proxy
        if (!IsAdmin() && request.ProxyId == null)
            return BadRequest(new { message = "Proxy is required. Please add a proxy first." });

        // Validate proxy belongs to user
        if (request.ProxyId.HasValue)
        {
            var proxyExists = await _db.ProxyServers
                .AnyAsync(p => p.Id == request.ProxyId.Value && p.UserId == userId);

            if (!proxyExists)
                return BadRequest(new { message = "Invalid proxy selected." });
        }

        var account = new ExchangeAccount
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name,
            ExchangeType = request.ExchangeType,
            ApiKeyEncrypted = _encryption.Encrypt(request.ApiKey),
            ApiSecretEncrypted = _encryption.Encrypt(request.ApiSecret),
            PassphraseEncrypted = request.Passphrase != null ? _encryption.Encrypt(request.Passphrase) : null,
            ProxyId = request.ProxyId,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.ExchangeAccounts.Add(account);
        await _db.SaveChangesAsync();

        // Load proxy name for response
        string? proxyName = null;
        if (request.ProxyId.HasValue)
        {
            proxyName = await _db.ProxyServers
                .Where(p => p.Id == request.ProxyId.Value)
                .Select(p => p.Name)
                .FirstOrDefaultAsync();
        }

        return CreatedAtAction(nameof(GetAll), new ExchangeAccountDto
        {
            Id = account.Id,
            Name = account.Name,
            ExchangeType = account.ExchangeType,
            ProxyId = account.ProxyId,
            ProxyName = proxyName,
            IsActive = account.IsActive,
            CreatedAt = account.CreatedAt
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateExchangeAccountRequest request)
    {
        var userId = GetUserId();
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);

        if (account == null)
            return NotFound();

        if (request.Name != null) account.Name = request.Name;
        if (request.ApiKey != null) account.ApiKeyEncrypted = _encryption.Encrypt(request.ApiKey);
        if (request.ApiSecret != null) account.ApiSecretEncrypted = _encryption.Encrypt(request.ApiSecret);
        if (request.Passphrase != null) account.PassphraseEncrypted = _encryption.Encrypt(request.Passphrase);
        if (request.IsActive.HasValue) account.IsActive = request.IsActive.Value;

        // Update proxy: Guid.Empty = clear proxy (admin only), valid Guid = set proxy
        if (request.ProxyId.HasValue)
        {
            if (request.ProxyId.Value == Guid.Empty)
            {
                if (IsAdmin())
                    account.ProxyId = null;
            }
            else
            {
                var proxyExists = await _db.ProxyServers
                    .AnyAsync(p => p.Id == request.ProxyId.Value && p.UserId == userId);

                if (!proxyExists)
                    return BadRequest(new { message = "Invalid proxy selected." });

                account.ProxyId = request.ProxyId.Value;
            }
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        _db.ExchangeAccounts.Remove(account);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> TestConnection(Guid id)
    {
        var account = await _db.ExchangeAccounts
            .Include(a => a.Proxy)
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            using var service = (IDisposable)_exchangeFactory.Create(account);
            var exchangeService = (IExchangeService)service;
            var (success, error) = await exchangeService.TestConnectionAsync();
            return Ok(new { success, message = success ? "Connection successful" : $"Connection failed: {error}" });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, message = ex.Message });
        }
    }
}
