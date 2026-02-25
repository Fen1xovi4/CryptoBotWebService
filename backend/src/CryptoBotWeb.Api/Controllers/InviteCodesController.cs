using System.Security.Claims;
using System.Security.Cryptography;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/invite-codes")]
[Authorize(Roles = "Admin,Manager")]
public class InviteCodesController : ControllerBase
{
    private readonly AppDbContext _db;

    public InviteCodesController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin() => User.IsInRole("Admin");

    [HttpGet]
    public async Task<ActionResult<List<InviteCodeDto>>> GetAll()
    {
        var userId = GetUserId();
        var isAdmin = IsAdmin();

        var codes = await _db.InviteCodes
            .AsNoTracking()
            .Where(c => isAdmin || c.CreatedByUserId == userId)
            .Include(c => c.CreatedByUser)
            .Include(c => c.Usages).ThenInclude(u => u.User)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new InviteCodeDto
            {
                Id = c.Id,
                Code = c.Code,
                AssignedRole = c.AssignedRole.ToString(),
                MaxUses = c.MaxUses,
                UsedCount = c.UsedCount,
                IsActive = c.IsActive,
                CreatedBy = c.CreatedByUser.Username,
                CreatedAt = c.CreatedAt,
                ExpiresAt = c.ExpiresAt,
                Usages = c.Usages.Select(u => new InviteCodeUsageDto
                {
                    Username = u.User.Username,
                    UsedAt = u.UsedAt
                }).ToList()
            })
            .ToListAsync();

        return Ok(codes);
    }

    [HttpPost]
    public async Task<ActionResult<InviteCodeDto>> Create([FromBody] CreateInviteCodeRequest request)
    {
        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
            return BadRequest(new { message = "Invalid role. Valid values: User, Manager, Admin" });

        if (!IsAdmin() && role != UserRole.User)
            return StatusCode(403, new { message = "Managers can only create invite codes for User role" });

        if (request.MaxUses < 0)
            return BadRequest(new { message = "MaxUses must be 0 (unlimited) or positive" });

        var code = GenerateCode();
        while (await _db.InviteCodes.AnyAsync(c => c.Code == code))
            code = GenerateCode();

        var invite = new InviteCode
        {
            Id = Guid.NewGuid(),
            CreatedByUserId = GetUserId(),
            Code = code,
            AssignedRole = role,
            MaxUses = request.MaxUses,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = request.ExpiresAt.HasValue
                ? DateTime.SpecifyKind(request.ExpiresAt.Value, DateTimeKind.Utc)
                : null
        };

        _db.InviteCodes.Add(invite);
        await _db.SaveChangesAsync();

        var username = User.FindFirstValue(ClaimTypes.Name)!;

        return Ok(new InviteCodeDto
        {
            Id = invite.Id,
            Code = invite.Code,
            AssignedRole = invite.AssignedRole.ToString(),
            MaxUses = invite.MaxUses,
            UsedCount = 0,
            IsActive = true,
            CreatedBy = username,
            CreatedAt = invite.CreatedAt,
            ExpiresAt = invite.ExpiresAt,
            Usages = new()
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id)
    {
        var invite = await _db.InviteCodes.FindAsync(id);
        if (invite == null)
            return NotFound(new { message = "Invite code not found" });

        if (!IsAdmin() && invite.CreatedByUserId != GetUserId())
            return StatusCode(403, new { message = "You can only deactivate your own invite codes" });

        invite.IsActive = false;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Invite code deactivated" });
    }

    private static string GenerateCode(int length = 8)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }
}
