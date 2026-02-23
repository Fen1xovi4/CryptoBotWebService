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

    [HttpGet]
    public async Task<ActionResult<List<ExchangeAccountDto>>> GetAll()
    {
        var accounts = await _db.ExchangeAccounts
            .Where(a => a.UserId == GetUserId())
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new ExchangeAccountDto
            {
                Id = a.Id,
                Name = a.Name,
                ExchangeType = a.ExchangeType,
                IsActive = a.IsActive,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return Ok(accounts);
    }

    [HttpPost]
    public async Task<ActionResult<ExchangeAccountDto>> Create([FromBody] CreateExchangeAccountRequest request)
    {
        var account = new ExchangeAccount
        {
            Id = Guid.NewGuid(),
            UserId = GetUserId(),
            Name = request.Name,
            ExchangeType = request.ExchangeType,
            ApiKeyEncrypted = _encryption.Encrypt(request.ApiKey),
            ApiSecretEncrypted = _encryption.Encrypt(request.ApiSecret),
            PassphraseEncrypted = request.Passphrase != null ? _encryption.Encrypt(request.Passphrase) : null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.ExchangeAccounts.Add(account);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new ExchangeAccountDto
        {
            Id = account.Id,
            Name = account.Name,
            ExchangeType = account.ExchangeType,
            IsActive = account.IsActive,
            CreatedAt = account.CreatedAt
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateExchangeAccountRequest request)
    {
        var account = await _db.ExchangeAccounts
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        if (request.Name != null) account.Name = request.Name;
        if (request.ApiKey != null) account.ApiKeyEncrypted = _encryption.Encrypt(request.ApiKey);
        if (request.ApiSecret != null) account.ApiSecretEncrypted = _encryption.Encrypt(request.ApiSecret);
        if (request.Passphrase != null) account.PassphraseEncrypted = _encryption.Encrypt(request.Passphrase);
        if (request.IsActive.HasValue) account.IsActive = request.IsActive.Value;

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
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == GetUserId());

        if (account == null)
            return NotFound();

        try
        {
            using var service = (IDisposable)_exchangeFactory.Create(account);
            var exchangeService = (IExchangeService)service;
            var success = await exchangeService.TestConnectionAsync();
            return Ok(new { success, message = success ? "Connection successful" : "Connection failed" });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, message = ex.Message });
        }
    }
}
