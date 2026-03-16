using System.Security.Claims;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SubscriptionsController : ControllerBase
{
    private readonly AppDbContext _db;

    public SubscriptionsController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("plans")]
    [AllowAnonymous]
    public IActionResult GetPlans()
    {
        var plans = PlanLimits.GetAllPlans().Select(p => new PlanInfoDto
        {
            Plan = p.Plan.ToString(),
            NameRu = p.NameRu,
            NameEn = p.NameEn,
            MaxAccounts = p.MaxAccounts,
            MaxActiveBots = p.MaxActiveBots,
            PriceMonthly = p.PriceMonthly,
            PriceLabel = p.PriceLabel
        });

        return Ok(plans);
    }

    [HttpGet("my")]
    [Authorize]
    public async Task<IActionResult> GetMy()
    {
        var userId = GetUserId();
        var isAdmin = User.IsInRole("Admin");

        var subscription = await _db.Subscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId);

        var plan = subscription?.Plan ?? SubscriptionPlan.Basic;
        var limits = PlanLimits.GetLimits(plan);

        var currentAccounts = await _db.ExchangeAccounts.CountAsync(a => a.UserId == userId);
        var currentActiveBots = await _db.Strategies
            .CountAsync(s => s.Account.UserId == userId && s.Status == StrategyStatus.Running);

        return Ok(new SubscriptionDto
        {
            Plan = isAdmin ? "Admin" : plan.ToString(),
            Status = (subscription?.Status ?? SubscriptionStatus.Active).ToString(),
            MaxAccounts = isAdmin ? 999 : limits.MaxAccounts,
            MaxActiveBots = isAdmin ? 999 : limits.MaxActiveBots,
            CurrentAccounts = currentAccounts,
            CurrentActiveBots = currentActiveBots,
            StartedAt = subscription?.StartedAt ?? DateTime.UtcNow,
            ExpiresAt = isAdmin ? null : subscription?.ExpiresAt,
            IsAdmin = isAdmin
        });
    }

    [HttpPut("{userId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdatePlan(Guid userId, [FromBody] UpdateSubscriptionRequest request)
    {
        if (!Enum.TryParse<SubscriptionPlan>(request.Plan, true, out var plan))
            return BadRequest(new { message = "Invalid plan. Valid values: Basic, Advanced, Pro" });

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new { message = "User not found" });

        var subscription = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);

        if (subscription == null)
        {
            subscription = new Core.Entities.Subscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Plan = plan,
                Status = SubscriptionStatus.Active,
                StartedAt = DateTime.UtcNow,
                ExpiresAt = request.ExpiresAt?.ToUniversalTime(),
                AssignedByUserId = GetUserId(),
                CreatedAt = DateTime.UtcNow
            };
            _db.Subscriptions.Add(subscription);
        }
        else
        {
            subscription.Plan = plan;
            subscription.ExpiresAt = request.ExpiresAt?.ToUniversalTime();
            subscription.AssignedByUserId = GetUserId();
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Subscription updated", plan = plan.ToString(), expiresAt = subscription.ExpiresAt });
    }
}
