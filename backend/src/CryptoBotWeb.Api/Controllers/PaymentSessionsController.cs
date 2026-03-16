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
public class PaymentSessionsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PaymentSessionsController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static PaymentSessionDto ToDto(PaymentSession s, PaymentWallet w, string? inviteCode = null) => new()
    {
        Id = s.Id,
        Plan = s.Plan.ToString(),
        Network = s.Network.ToString(),
        Token = s.Token.ToString(),
        ExpectedAmount = s.ExpectedAmount,
        WalletAddress = s.Network == PaymentNetwork.TRC20 ? w.AddressTrc20 : w.AddressBep20,
        Status = s.Status.ToString(),
        TxHash = s.TxHash,
        ReceivedAmount = s.ReceivedAmount,
        CreatedAt = s.CreatedAt,
        ExpiresAt = s.ExpiresAt,
        ConfirmedAt = s.ConfirmedAt,
        RemainingSeconds = s.Status == PaymentSessionStatus.Pending
            ? Math.Max(0, (int)(s.ExpiresAt - DateTime.UtcNow).TotalSeconds)
            : 0,
        InviteCode = inviteCode
    };

    /// <summary>
    /// Finds an available invite code not yet assigned to any confirmed guest payment session.
    /// Returns null if none found.
    /// </summary>
    private async Task<InviteCode?> FindAvailableInviteCodeAsync()
    {
        var now = DateTime.UtcNow;

        // IDs already assigned to confirmed guest sessions
        var assignedIds = await _db.PaymentSessions
            .Where(s => s.AssignedInviteCodeId != null)
            .Select(s => s.AssignedInviteCodeId!.Value)
            .ToListAsync();

        return await _db.InviteCodes
            .Where(c =>
                c.IsActive &&
                (c.ExpiresAt == null || c.ExpiresAt > now) &&
                (c.MaxUses == 0 || c.UsedCount < c.MaxUses) &&
                !assignedIds.Contains(c.Id))
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Assigns an available invite code to a guest session. Call inside a transaction scope
    /// before SaveChangesAsync. Does NOT increment UsedCount (that happens at registration).
    /// Returns the assigned InviteCode, or null if none available.
    /// </summary>
    private async Task<InviteCode?> AssignInviteCodeToGuestSessionAsync(PaymentSession session)
    {
        var inviteCode = await FindAvailableInviteCodeAsync();
        if (inviteCode != null)
            session.AssignedInviteCodeId = inviteCode.Id;

        return inviteCode;
    }

    // ─── Authenticated endpoints ─────────────────────────────────────────────

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreatePaymentSessionRequest request)
    {
        if (!Enum.TryParse<SubscriptionPlan>(request.Plan, true, out var plan))
            return BadRequest(new { message = "Invalid plan. Valid values: Basic, Advanced, Pro" });

        if (!Enum.TryParse<PaymentNetwork>(request.Network, true, out var network))
            return BadRequest(new { message = "Invalid network. Valid values: TRC20, BEP20" });

        if (!Enum.TryParse<PaymentToken>(request.Token, true, out var token))
            return BadRequest(new { message = "Invalid token. Valid values: USDT, USDC" });

        var userId = GetUserId();
        var now = DateTime.UtcNow;

        var hasPendingSession = await _db.PaymentSessions.AnyAsync(s =>
            s.UserId == userId &&
            s.Status == PaymentSessionStatus.Pending &&
            s.ExpiresAt > now);

        if (hasPendingSession)
            return Conflict(new { message = "You already have an active pending payment session" });

        var availableWallet = await _db.PaymentWallets
            .Where(w => w.IsActive &&
                !w.PaymentSessions.Any(s =>
                    s.Status == PaymentSessionStatus.Pending &&
                    s.ExpiresAt > now))
            .FirstOrDefaultAsync();

        if (availableWallet == null)
            return Conflict(new { message = "All payment wallets are currently busy. Please try again in a few minutes." });

        var limits = PlanLimits.GetLimits(plan);

        var session = new PaymentSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            WalletId = availableWallet.Id,
            Plan = plan,
            Network = network,
            Token = token,
            ExpectedAmount = limits.PriceMonthly,
            Status = PaymentSessionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(BlockchainContracts.WalletLockMinutes)
        };

        _db.PaymentSessions.Add(session);
        await _db.SaveChangesAsync();

        return Ok(ToDto(session, availableWallet));
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    public async Task<IActionResult> GetById(Guid id)
    {
        var userId = GetUserId();
        var isAdmin = User.IsInRole("Admin");

        var session = await _db.PaymentSessions
            .AsNoTracking()
            .Include(s => s.Wallet)
            .Include(s => s.AssignedInviteCode)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
            return NotFound(new { message = "Payment session not found" });

        if (!isAdmin && session.UserId != userId)
            return Forbid();

        return Ok(ToDto(session, session.Wallet, session.AssignedInviteCode?.Code));
    }

    [HttpGet("my")]
    [Authorize]
    public async Task<IActionResult> GetMy()
    {
        var userId = GetUserId();

        var sessions = await _db.PaymentSessions
            .AsNoTracking()
            .Include(s => s.Wallet)
            .Include(s => s.AssignedInviteCode)
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        var result = sessions.Select(s => ToDto(s, s.Wallet, s.AssignedInviteCode?.Code));
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var userId = GetUserId();

        var session = await _db.PaymentSessions
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
            return NotFound(new { message = "Payment session not found" });

        if (session.UserId != userId)
            return Forbid();

        if (session.Status != PaymentSessionStatus.Pending)
            return BadRequest(new { message = "Only pending sessions can be cancelled" });

        session.Status = PaymentSessionStatus.Cancelled;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Payment session cancelled" });
    }

    // ─── Guest endpoints (no auth) ────────────────────────────────────────────

    [HttpPost("guest")]
    [AllowAnonymous]
    public async Task<IActionResult> CreateGuest([FromBody] CreatePaymentSessionRequest request)
    {
        if (!Enum.TryParse<SubscriptionPlan>(request.Plan, true, out var plan))
            return BadRequest(new { message = "Invalid plan. Valid values: Basic, Advanced, Pro" });

        if (!Enum.TryParse<PaymentNetwork>(request.Network, true, out var network))
            return BadRequest(new { message = "Invalid network. Valid values: TRC20, BEP20" });

        if (!Enum.TryParse<PaymentToken>(request.Token, true, out var token))
            return BadRequest(new { message = "Invalid token. Valid values: USDT, USDC" });

        var now = DateTime.UtcNow;

        var availableWallet = await _db.PaymentWallets
            .Where(w => w.IsActive &&
                !w.PaymentSessions.Any(s =>
                    s.Status == PaymentSessionStatus.Pending &&
                    s.ExpiresAt > now))
            .FirstOrDefaultAsync();

        if (availableWallet == null)
            return Conflict(new { message = "All payment wallets are currently busy. Please try again in a few minutes." });

        var limits = PlanLimits.GetLimits(plan);
        var guestToken = Guid.NewGuid();

        var session = new PaymentSession
        {
            Id = Guid.NewGuid(),
            UserId = null,
            GuestToken = guestToken,
            WalletId = availableWallet.Id,
            Plan = plan,
            Network = network,
            Token = token,
            ExpectedAmount = limits.PriceMonthly,
            Status = PaymentSessionStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(BlockchainContracts.WalletLockMinutes)
        };

        _db.PaymentSessions.Add(session);
        await _db.SaveChangesAsync();

        var dto = new GuestPaymentSessionDto
        {
            Id = session.Id,
            Plan = session.Plan.ToString(),
            Network = session.Network.ToString(),
            Token = session.Token.ToString(),
            ExpectedAmount = session.ExpectedAmount,
            WalletAddress = session.Network == PaymentNetwork.TRC20
                ? availableWallet.AddressTrc20
                : availableWallet.AddressBep20,
            Status = session.Status.ToString(),
            TxHash = session.TxHash,
            ReceivedAmount = session.ReceivedAmount,
            CreatedAt = session.CreatedAt,
            ExpiresAt = session.ExpiresAt,
            ConfirmedAt = session.ConfirmedAt,
            RemainingSeconds = Math.Max(0, (int)(session.ExpiresAt - DateTime.UtcNow).TotalSeconds),
            GuestToken = guestToken
        };

        return Ok(dto);
    }

    [HttpGet("guest/{id:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetGuestById(Guid id, [FromQuery] Guid token)
    {
        var session = await _db.PaymentSessions
            .AsNoTracking()
            .Include(s => s.Wallet)
            .Include(s => s.AssignedInviteCode)
            .FirstOrDefaultAsync(s => s.Id == id && s.GuestToken != null);

        if (session == null)
            return NotFound(new { message = "Payment session not found" });

        if (session.GuestToken != token)
            return Forbid();

        return Ok(ToDto(session, session.Wallet, session.AssignedInviteCode?.Code));
    }

    // ─── Admin endpoints ──────────────────────────────────────────────────────

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetAll()
    {
        var sessions = await _db.PaymentSessions
            .AsNoTracking()
            .Include(s => s.User)
            .Include(s => s.Wallet)
            .Include(s => s.ConfirmedByAdmin)
            .Include(s => s.AssignedInviteCode)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        var result = sessions.Select(s => new PaymentSessionAdminDto
        {
            Id = s.Id,
            Plan = s.Plan.ToString(),
            Network = s.Network.ToString(),
            Token = s.Token.ToString(),
            ExpectedAmount = s.ExpectedAmount,
            WalletAddress = s.Network == PaymentNetwork.TRC20 ? s.Wallet.AddressTrc20 : s.Wallet.AddressBep20,
            Status = s.Status.ToString(),
            TxHash = s.TxHash,
            ReceivedAmount = s.ReceivedAmount,
            CreatedAt = s.CreatedAt,
            ExpiresAt = s.ExpiresAt,
            ConfirmedAt = s.ConfirmedAt,
            RemainingSeconds = s.Status == PaymentSessionStatus.Pending
                ? Math.Max(0, (int)(s.ExpiresAt - DateTime.UtcNow).TotalSeconds)
                : 0,
            UserId = s.UserId,
            Username = s.User?.Username ?? "(guest)",
            WalletId = s.WalletId,
            ConfirmedByAdmin = s.ConfirmedByAdmin?.Username,
            IsGuest = s.UserId == null,
            InviteCode = s.AssignedInviteCode?.Code
        });

        return Ok(result);
    }

    [HttpPost("{id:guid}/confirm")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Confirm(Guid id, [FromBody] AdminConfirmPaymentRequest request)
    {
        var session = await _db.PaymentSessions
            .Include(s => s.Wallet)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
            return NotFound(new { message = "Payment session not found" });

        if (session.Status != PaymentSessionStatus.Pending)
            return BadRequest(new { message = "Only pending sessions can be confirmed" });

        var now = DateTime.UtcNow;
        session.Status = PaymentSessionStatus.ManuallyConfirmed;
        session.ConfirmedByAdminId = GetUserId();
        session.ConfirmedAt = now;

        if (request.TxHash != null)
            session.TxHash = request.TxHash;

        if (request.ReceivedAmount != null)
            session.ReceivedAmount = request.ReceivedAmount;

        // Guest session: assign an invite code instead of activating a subscription
        if (session.UserId == null)
        {
            var inviteCode = await AssignInviteCodeToGuestSessionAsync(session);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = inviteCode != null
                    ? "Guest payment confirmed and invite code assigned"
                    : "Guest payment confirmed but no invite codes are currently available",
                isGuest = true,
                inviteCode = inviteCode?.Code
            });
        }

        // Authenticated user session: upsert subscription
        var subscription = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == session.UserId);

        if (subscription == null)
        {
            subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                UserId = session.UserId.Value,
                Plan = session.Plan,
                Status = SubscriptionStatus.Active,
                StartedAt = now,
                ExpiresAt = now.AddMonths(1),
                AssignedByUserId = GetUserId(),
                CreatedAt = now
            };
            _db.Subscriptions.Add(subscription);
        }
        else
        {
            subscription.Plan = session.Plan;
            subscription.Status = SubscriptionStatus.Active;
            subscription.StartedAt = now;
            subscription.ExpiresAt = now.AddMonths(1);
            subscription.AssignedByUserId = GetUserId();
        }

        await _db.SaveChangesAsync();

        return Ok(new { message = "Payment confirmed and subscription activated", plan = session.Plan.ToString(), expiresAt = subscription.ExpiresAt });
    }
}
