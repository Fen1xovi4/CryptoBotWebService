using System.Security.Claims;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OtpNet;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEncryptionService _encryption;

    public SettingsController(AppDbContext db, IEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("2fa/status")]
    public async Task<ActionResult<TwoFactorStatusResponse>> GetTwoFactorStatus()
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user == null) return NotFound();

        return Ok(new TwoFactorStatusResponse { IsEnabled = user.TwoFactorEnabled });
    }

    [HttpPost("2fa/setup")]
    public async Task<ActionResult<TwoFactorSetupResponse>> SetupTwoFactor()
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user == null) return NotFound();

        if (user.TwoFactorEnabled)
            return BadRequest(new { message = "2FA is already enabled" });

        var key = KeyGeneration.GenerateRandomKey(20);
        var base32Secret = Base32Encoding.ToString(key);

        user.TwoFactorSecret = _encryption.Encrypt(base32Secret);
        await _db.SaveChangesAsync();

        var qrCodeUri = $"otpauth://totp/CryptoBotWeb:{user.Username}?secret={base32Secret}&issuer=CryptoBotWeb&digits=6&period=30";

        return Ok(new TwoFactorSetupResponse
        {
            SecretKey = base32Secret,
            QrCodeUri = qrCodeUri
        });
    }

    [HttpPost("2fa/verify")]
    public async Task<IActionResult> VerifyTwoFactor([FromBody] TwoFactorVerifyRequest request)
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user == null) return NotFound();

        if (user.TwoFactorEnabled)
            return BadRequest(new { message = "2FA is already enabled" });

        if (string.IsNullOrEmpty(user.TwoFactorSecret))
            return BadRequest(new { message = "2FA setup not initiated" });

        var base32Secret = _encryption.Decrypt(user.TwoFactorSecret);
        var key = Base32Encoding.ToBytes(base32Secret);
        var totp = new Totp(key);

        if (!totp.VerifyTotp(request.Code, out _, VerificationWindow.RfcSpecifiedNetworkDelay))
            return BadRequest(new { message = "Invalid verification code" });

        user.TwoFactorEnabled = true;
        await _db.SaveChangesAsync();

        return Ok(new { message = "2FA enabled successfully" });
    }

    [HttpPost("2fa/disable")]
    public async Task<IActionResult> DisableTwoFactor([FromBody] TwoFactorDisableRequest request)
    {
        var user = await _db.Users.FindAsync(GetUserId());
        if (user == null) return NotFound();

        if (!user.TwoFactorEnabled)
            return BadRequest(new { message = "2FA is not enabled" });

        var base32Secret = _encryption.Decrypt(user.TwoFactorSecret!);
        var key = Base32Encoding.ToBytes(base32Secret);
        var totp = new Totp(key);

        if (!totp.VerifyTotp(request.Code, out _, VerificationWindow.RfcSpecifiedNetworkDelay))
            return BadRequest(new { message = "Invalid verification code" });

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        await _db.SaveChangesAsync();

        return Ok(new { message = "2FA disabled successfully" });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
            return BadRequest(new { message = "New password must be at least 6 characters" });

        if (request.NewPassword != request.ConfirmPassword)
            return BadRequest(new { message = "Passwords do not match" });

        var user = await _db.Users.FindAsync(GetUserId());
        if (user == null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
            return BadRequest(new { message = "Current password is incorrect" });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Password changed successfully" });
    }
}
