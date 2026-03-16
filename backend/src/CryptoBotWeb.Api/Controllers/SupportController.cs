using System.Security.Claims;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SupportController : ControllerBase
{
    private readonly AppDbContext _db;

    public SupportController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private bool IsAdmin() => User.IsInRole("Admin");

    // -------------------------------------------------------------------------
    // User endpoints
    // -------------------------------------------------------------------------

    [HttpPost]
    public async Task<ActionResult<SupportTicketDto>> CreateTicket([FromBody] CreateTicketRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Subject))
            return BadRequest(new { message = "Subject is required" });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "Message is required" });

        var userId = GetUserId();
        var username = User.FindFirstValue(ClaimTypes.Name)!;

        var ticket = new SupportTicket
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Subject = request.Subject.Trim(),
            Status = TicketStatus.Open,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.SupportTickets.Add(ticket);

        var message = new SupportMessage
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            SenderId = userId,
            Text = request.Message.Trim(),
            IsFromAdmin = false,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.SupportMessages.Add(message);
        await _db.SaveChangesAsync();

        return Ok(new SupportTicketDto
        {
            Id = ticket.Id,
            UserId = ticket.UserId,
            Username = username,
            Subject = ticket.Subject,
            Status = ticket.Status.ToString(),
            LastMessage = message.Text.Length > 100 ? message.Text[..100] : message.Text,
            UnreadCount = 0,
            CreatedAt = ticket.CreatedAt,
            UpdatedAt = ticket.UpdatedAt
        });
    }

    [HttpGet]
    public async Task<ActionResult<List<SupportTicketDto>>> GetMyTickets()
    {
        var userId = GetUserId();
        var username = User.FindFirstValue(ClaimTypes.Name)!;

        var tickets = await _db.SupportTickets
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new SupportTicketDto
            {
                Id = t.Id,
                UserId = t.UserId,
                Username = username,
                Subject = t.Subject,
                Status = t.Status.ToString(),
                LastMessage = t.Messages
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.Text.Length > 100 ? m.Text.Substring(0, 100) : m.Text)
                    .FirstOrDefault(),
                UnreadCount = t.Messages.Count(m => m.IsFromAdmin && !m.IsRead),
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(tickets);
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCountDto>> GetUnreadCount()
    {
        var userId = GetUserId();

        var count = await _db.SupportMessages
            .AsNoTracking()
            .CountAsync(m => m.Ticket.UserId == userId && m.IsFromAdmin && !m.IsRead);

        return Ok(new UnreadCountDto { Count = count });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<List<SupportMessageDto>>> GetTicketMessages(Guid id)
    {
        var userId = GetUserId();

        var ticket = await _db.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

        if (ticket == null)
            return NotFound(new { message = "Ticket not found" });

        // Mark all admin messages as read
        var unread = await _db.SupportMessages
            .Where(m => m.TicketId == id && m.IsFromAdmin && !m.IsRead)
            .ToListAsync();

        foreach (var msg in unread)
            msg.IsRead = true;

        if (unread.Count > 0)
            await _db.SaveChangesAsync();

        var messages = await _db.SupportMessages
            .AsNoTracking()
            .Where(m => m.TicketId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new SupportMessageDto
            {
                Id = m.Id,
                SenderId = m.SenderId,
                SenderName = m.Sender.Username,
                Text = m.Text,
                IsFromAdmin = m.IsFromAdmin,
                IsRead = m.IsRead,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync();

        return Ok(messages);
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<ActionResult<SupportMessageDto>> SendMessage(Guid id, [FromBody] SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { message = "Text is required" });

        var userId = GetUserId();

        var ticket = await _db.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);

        if (ticket == null)
            return NotFound(new { message = "Ticket not found" });

        if (ticket.Status == TicketStatus.Closed)
            return BadRequest(new { message = "Cannot send messages to a closed ticket" });

        var username = User.FindFirstValue(ClaimTypes.Name)!;

        var message = new SupportMessage
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            SenderId = userId,
            Text = request.Text.Trim(),
            IsFromAdmin = false,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.SupportMessages.Add(message);

        ticket.Status = TicketStatus.Open;
        ticket.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new SupportMessageDto
        {
            Id = message.Id,
            SenderId = message.SenderId,
            SenderName = username,
            Text = message.Text,
            IsFromAdmin = message.IsFromAdmin,
            IsRead = message.IsRead,
            CreatedAt = message.CreatedAt
        });
    }

    // -------------------------------------------------------------------------
    // Admin endpoints
    // -------------------------------------------------------------------------

    [HttpGet("admin")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<SupportTicketDto>>> GetAllTickets(
        [FromQuery] string? status,
        [FromQuery] string? search)
    {
        var query = _db.SupportTickets
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<TicketStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(t => t.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(t => t.User.Username.ToLower().Contains(searchLower));
        }

        var tickets = await query
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new SupportTicketDto
            {
                Id = t.Id,
                UserId = t.UserId,
                Username = t.User.Username,
                Subject = t.Subject,
                Status = t.Status.ToString(),
                LastMessage = t.Messages
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.Text.Length > 100 ? m.Text.Substring(0, 100) : m.Text)
                    .FirstOrDefault(),
                UnreadCount = t.Messages.Count(m => !m.IsFromAdmin && !m.IsRead),
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt
            })
            .ToListAsync();

        return Ok(tickets);
    }

    [HttpGet("admin/unread-count")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<UnreadCountDto>> GetAdminUnreadCount()
    {
        var count = await _db.SupportMessages
            .AsNoTracking()
            .CountAsync(m => !m.IsFromAdmin && !m.IsRead);

        return Ok(new UnreadCountDto { Count = count });
    }

    [HttpGet("admin/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<SupportMessageDto>>> GetAdminTicketMessages(Guid id)
    {
        var ticket = await _db.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null)
            return NotFound(new { message = "Ticket not found" });

        // Mark all user messages as read
        var unread = await _db.SupportMessages
            .Where(m => m.TicketId == id && !m.IsFromAdmin && !m.IsRead)
            .ToListAsync();

        foreach (var msg in unread)
            msg.IsRead = true;

        if (unread.Count > 0)
            await _db.SaveChangesAsync();

        var messages = await _db.SupportMessages
            .AsNoTracking()
            .Where(m => m.TicketId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new SupportMessageDto
            {
                Id = m.Id,
                SenderId = m.SenderId,
                SenderName = m.Sender.Username,
                Text = m.Text,
                IsFromAdmin = m.IsFromAdmin,
                IsRead = m.IsRead,
                CreatedAt = m.CreatedAt
            })
            .ToListAsync();

        return Ok(messages);
    }

    [HttpPost("admin/{id:guid}/messages")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SupportMessageDto>> AdminReply(Guid id, [FromBody] SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { message = "Text is required" });

        var ticket = await _db.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null)
            return NotFound(new { message = "Ticket not found" });

        var adminId = GetUserId();
        var adminName = User.FindFirstValue(ClaimTypes.Name)!;

        var message = new SupportMessage
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            SenderId = adminId,
            Text = request.Text.Trim(),
            IsFromAdmin = true,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.SupportMessages.Add(message);

        ticket.Status = TicketStatus.Answered;
        ticket.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new SupportMessageDto
        {
            Id = message.Id,
            SenderId = message.SenderId,
            SenderName = adminName,
            Text = message.Text,
            IsFromAdmin = message.IsFromAdmin,
            IsRead = message.IsRead,
            CreatedAt = message.CreatedAt
        });
    }

    [HttpPut("admin/{id:guid}/close")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CloseTicket(Guid id)
    {
        var ticket = await _db.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == id);

        if (ticket == null)
            return NotFound(new { message = "Ticket not found" });

        ticket.Status = TicketStatus.Closed;
        ticket.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new { message = "Ticket closed" });
    }
}
