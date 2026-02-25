using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AuthController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { message = "Invalid username or password" });

        if (!user.IsEnabled)
            return StatusCode(403, new { message = "Account is disabled" });

        var expiresAt = DateTime.UtcNow.AddMinutes(60);
        var accessToken = GenerateToken(user.Id, user.Username, user.Role, expiresAt);
        var refreshToken = GenerateToken(user.Id, user.Username, user.Role, DateTime.UtcNow.AddDays(7));

        return Ok(new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt
        });
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshRequest request)
    {
        var principal = ValidateToken(request.RefreshToken);
        if (principal == null)
            return Unauthorized(new { message = "Invalid refresh token" });

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.Users.FindAsync(userId);
        if (user == null || !user.IsEnabled)
            return Unauthorized(new { message = "Account is disabled or not found" });

        var expiresAt = DateTime.UtcNow.AddMinutes(60);

        return Ok(new AuthResponse
        {
            AccessToken = GenerateToken(user.Id, user.Username, user.Role, expiresAt),
            RefreshToken = GenerateToken(user.Id, user.Username, user.Role, DateTime.UtcNow.AddDays(7)),
            ExpiresAt = expiresAt
        });
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length < 3)
            return BadRequest(new { message = "Username must be at least 3 characters" });
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
            return BadRequest(new { message = "Password must be at least 6 characters" });

        var invite = await _db.InviteCodes
            .FirstOrDefaultAsync(c => c.Code == request.InviteCode.Trim().ToUpper());

        if (invite == null)
            return BadRequest(new { message = "Invalid invite code" });
        if (!invite.IsActive)
            return BadRequest(new { message = "Invite code is no longer active" });
        if (invite.ExpiresAt.HasValue && invite.ExpiresAt.Value < DateTime.UtcNow)
            return BadRequest(new { message = "Invite code has expired" });
        if (invite.MaxUses > 0 && invite.UsedCount >= invite.MaxUses)
            return BadRequest(new { message = "Invite code has reached its usage limit" });

        if (await _db.Users.AnyAsync(u => u.Username == request.Username))
            return BadRequest(new { message = "Username is already taken" });

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = invite.AssignedRole,
            IsEnabled = true,
            InvitedByUserId = invite.CreatedByUserId,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);

        var usage = new InviteCodeUsage
        {
            Id = Guid.NewGuid(),
            InviteCodeId = invite.Id,
            UserId = user.Id,
            UsedAt = DateTime.UtcNow
        };
        _db.InviteCodeUsages.Add(usage);

        invite.UsedCount++;
        if (invite.MaxUses > 0 && invite.UsedCount >= invite.MaxUses)
            invite.IsActive = false;

        await _db.SaveChangesAsync();

        var expiresAt = DateTime.UtcNow.AddMinutes(60);
        return Ok(new AuthResponse
        {
            AccessToken = GenerateToken(user.Id, user.Username, user.Role, expiresAt),
            RefreshToken = GenerateToken(user.Id, user.Username, user.Role, DateTime.UtcNow.AddDays(7)),
            ExpiresAt = expiresAt
        });
    }

    private string GenerateToken(Guid userId, string username, UserRole role, DateTime expires)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, role.ToString())
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private ClaimsPrincipal? ValidateToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!)),
                ClockSkew = TimeSpan.Zero
            }, out _);
        }
        catch
        {
            return null;
        }
    }
}

public class RefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}
