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
public class ProxiesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEncryptionService _encryption;

    public ProxiesController(AppDbContext db, IEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<ProxyServerDto>>> GetAll()
    {
        var proxies = await _db.ProxyServers
            .Where(p => p.UserId == GetUserId())
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new ProxyServerDto
            {
                Id = p.Id,
                Name = p.Name,
                Host = p.Host,
                Port = p.Port,
                HasAuth = p.Username != null,
                IsActive = p.IsActive,
                CreatedAt = p.CreatedAt,
                UsedByAccounts = p.ExchangeAccounts.Count
            })
            .ToListAsync();

        return Ok(proxies);
    }

    [HttpPost]
    public async Task<ActionResult<ProxyServerDto>> Create([FromBody] CreateProxyRequest request)
    {
        var proxy = new ProxyServer
        {
            Id = Guid.NewGuid(),
            UserId = GetUserId(),
            Name = request.Name,
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            PasswordEncrypted = request.Password != null ? _encryption.Encrypt(request.Password) : null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.ProxyServers.Add(proxy);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new ProxyServerDto
        {
            Id = proxy.Id,
            Name = proxy.Name,
            Host = proxy.Host,
            Port = proxy.Port,
            HasAuth = proxy.Username != null,
            IsActive = proxy.IsActive,
            CreatedAt = proxy.CreatedAt,
            UsedByAccounts = 0
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateProxyRequest request)
    {
        var proxy = await _db.ProxyServers
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == GetUserId());

        if (proxy == null)
            return NotFound();

        if (request.Name != null) proxy.Name = request.Name;
        if (request.Host != null) proxy.Host = request.Host;
        if (request.Port.HasValue) proxy.Port = request.Port.Value;
        if (request.Username != null) proxy.Username = request.Username;
        if (request.Password != null) proxy.PasswordEncrypted = _encryption.Encrypt(request.Password);
        if (request.IsActive.HasValue) proxy.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var proxy = await _db.ProxyServers
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == GetUserId());

        if (proxy == null)
            return NotFound();

        _db.ProxyServers.Remove(proxy);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
