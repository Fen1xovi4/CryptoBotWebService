using System.Security.Claims;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/telegram-bots")]
[Authorize]
public class TelegramBotsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TelegramBotsController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin() => User.IsInRole("Admin");

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var userId = GetUserId();
        var bots = await _db.TelegramBots
            .Where(b => b.UserId == userId)
            .Select(b => new TelegramBotDto
            {
                Id = b.Id,
                Name = b.Name,
                HasPassword = b.Password != null,
                IsActive = b.IsActive,
                SubscriberCount = b.Subscribers.Count,
                CreatedAt = b.CreatedAt,
            })
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();

        return Ok(bots);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTelegramBotRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.BotToken))
            return BadRequest(new { message = "Name and BotToken are required" });

        var userId = GetUserId();
        var isAdmin = IsAdmin();

        if (!isAdmin)
        {
            var subscription = await _db.Subscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.UserId == userId);

            var plan = subscription?.Plan ?? SubscriptionPlan.Basic;
            var limits = PlanLimits.GetLimits(plan);
            var currentCount = await _db.TelegramBots.CountAsync(b => b.UserId == userId);

            if (currentCount >= limits.MaxTelegramBots)
                return BadRequest(new { message = $"Telegram bot limit reached ({limits.MaxTelegramBots}). Upgrade your plan." });
        }

        var bot = new TelegramBot
        {
            UserId = userId,
            Name = request.Name.Trim(),
            BotToken = request.BotToken.Trim(),
            Password = string.IsNullOrWhiteSpace(request.Password) ? null : request.Password.Trim(),
        };

        _db.TelegramBots.Add(bot);
        await _db.SaveChangesAsync();

        return Ok(new TelegramBotDto
        {
            Id = bot.Id,
            Name = bot.Name,
            HasPassword = bot.Password != null,
            IsActive = bot.IsActive,
            SubscriberCount = 0,
            CreatedAt = bot.CreatedAt,
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTelegramBotRequest request)
    {
        var userId = GetUserId();
        var bot = await _db.TelegramBots.FirstOrDefaultAsync(b => b.Id == id && b.UserId == userId);
        if (bot == null) return NotFound(new { message = "Bot not found" });

        if (request.Name != null) bot.Name = request.Name.Trim();
        if (request.BotToken != null) bot.BotToken = request.BotToken.Trim();
        if (request.Password != null) bot.Password = string.IsNullOrWhiteSpace(request.Password) ? null : request.Password.Trim();
        if (request.IsActive.HasValue) bot.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();

        var subscriberCount = await _db.TelegramSubscribers.CountAsync(s => s.TelegramBotId == bot.Id);
        return Ok(new TelegramBotDto
        {
            Id = bot.Id,
            Name = bot.Name,
            HasPassword = bot.Password != null,
            IsActive = bot.IsActive,
            SubscriberCount = subscriberCount,
            CreatedAt = bot.CreatedAt,
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetUserId();
        var bot = await _db.TelegramBots.FirstOrDefaultAsync(b => b.Id == id && b.UserId == userId);
        if (bot == null) return NotFound(new { message = "Bot not found" });

        _db.TelegramBots.Remove(bot);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Deleted" });
    }
}
