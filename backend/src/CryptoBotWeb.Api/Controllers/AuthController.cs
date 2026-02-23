using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CryptoBotWeb.Core.DTOs;
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

        var expiresAt = DateTime.UtcNow.AddMinutes(60);
        var accessToken = GenerateToken(user.Id, user.Username, user.IsAdmin, expiresAt);
        var refreshToken = GenerateToken(user.Id, user.Username, user.IsAdmin, DateTime.UtcNow.AddDays(7));

        return Ok(new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt
        });
    }

    [HttpPost("refresh")]
    public ActionResult<AuthResponse> Refresh([FromBody] RefreshRequest request)
    {
        var principal = ValidateToken(request.RefreshToken);
        if (principal == null)
            return Unauthorized(new { message = "Invalid refresh token" });

        var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var username = principal.FindFirstValue(ClaimTypes.Name)!;
        var isAdmin = principal.FindFirstValue(ClaimTypes.Role) == "Admin";
        var expiresAt = DateTime.UtcNow.AddMinutes(60);

        return Ok(new AuthResponse
        {
            AccessToken = GenerateToken(userId, username, isAdmin, expiresAt),
            RefreshToken = GenerateToken(userId, username, isAdmin, DateTime.UtcNow.AddDays(7)),
            ExpiresAt = expiresAt
        });
    }

    private string GenerateToken(Guid userId, string username, bool isAdmin, DateTime expires)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Secret"]!));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username)
        };
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

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
