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
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;

    public UsersController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> GetAll()
    {
        var users = await _db.Users
            .AsNoTracking()
            .Include(u => u.InvitedByUser)
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Role = u.Role.ToString(),
                IsEnabled = u.IsEnabled,
                InvitedBy = u.InvitedByUser != null ? u.InvitedByUser.Username : null,
                CreatedAt = u.CreatedAt,
                AccountsCount = u.ExchangeAccounts.Count,
                StrategiesCount = u.ExchangeAccounts.SelectMany(a => a.Strategies).Count()
            })
            .ToListAsync();

        return Ok(users);
    }

    [HttpPut("{id:guid}/role")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateUserRoleRequest request)
    {
        if (id == GetUserId())
            return BadRequest(new { message = "Cannot change your own role" });

        var user = await _db.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "User not found" });

        if (!Enum.TryParse<UserRole>(request.Role, true, out var role))
            return BadRequest(new { message = "Invalid role. Valid values: User, Manager, Admin" });

        user.Role = role;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Role updated" });
    }

    [HttpPut("{id:guid}/enabled")]
    public async Task<IActionResult> UpdateEnabled(Guid id, [FromBody] UpdateUserEnabledRequest request)
    {
        if (id == GetUserId())
            return BadRequest(new { message = "Cannot disable your own account" });

        var user = await _db.Users.FindAsync(id);
        if (user == null)
            return NotFound(new { message = "User not found" });

        user.IsEnabled = request.IsEnabled;
        await _db.SaveChangesAsync();

        return Ok(new { message = request.IsEnabled ? "User enabled" : "User disabled" });
    }
}
