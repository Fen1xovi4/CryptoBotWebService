using System.Security.Claims;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CryptoBotWeb.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class WorkspacesController : ControllerBase
{
    private readonly AppDbContext _db;

    public WorkspacesController(AppDbContext db) => _db = db;

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var workspaces = await _db.Workspaces
            .Where(w => w.UserId == GetUserId())
            .OrderBy(w => w.SortOrder)
            .Select(w => new
            {
                w.Id,
                w.Name,
                w.StrategyType,
                w.ConfigJson,
                w.SortOrder,
                w.CreatedAt
            })
            .ToListAsync();

        return Ok(workspaces);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceRequest request)
    {
        var userId = GetUserId();
        var maxOrder = await _db.Workspaces
            .Where(w => w.UserId == userId)
            .MaxAsync(w => (int?)w.SortOrder) ?? -1;

        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = request.Name,
            StrategyType = request.StrategyType ?? "MaratG",
            ConfigJson = request.ConfigJson ?? "{}",
            SortOrder = maxOrder + 1,
            CreatedAt = DateTime.UtcNow
        };

        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetAll), new
        {
            workspace.Id,
            workspace.Name,
            workspace.StrategyType,
            workspace.ConfigJson,
            workspace.SortOrder,
            workspace.CreatedAt
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorkspaceRequest request)
    {
        var workspace = await _db.Workspaces
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == GetUserId());

        if (workspace == null) return NotFound();

        if (request.Name != null)
            workspace.Name = request.Name;
        if (request.StrategyType != null)
            workspace.StrategyType = request.StrategyType;
        if (request.ConfigJson != null)
            workspace.ConfigJson = request.ConfigJson;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            workspace.Id,
            workspace.Name,
            workspace.StrategyType,
            workspace.ConfigJson,
            workspace.SortOrder,
            workspace.CreatedAt
        });
    }

    [HttpPut("{id:guid}/config")]
    public async Task<IActionResult> UpdateConfig(Guid id, [FromBody] UpdateWorkspaceConfigRequest request)
    {
        var workspace = await _db.Workspaces
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == GetUserId());

        if (workspace == null) return NotFound();

        workspace.ConfigJson = request.ConfigJson ?? "{}";
        await _db.SaveChangesAsync();

        return Ok(new { workspace.Id, workspace.ConfigJson });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = GetUserId();
        var workspace = await _db.Workspaces
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId);

        if (workspace == null) return NotFound();

        var hasRunning = await _db.Strategies
            .AnyAsync(s => s.WorkspaceId == id && s.Status == Core.Enums.StrategyStatus.Running);

        if (hasRunning)
            return BadRequest(new { message = "Остановите всех ботов перед удалением пространства" });

        _db.Workspaces.Remove(workspace);
        await _db.SaveChangesAsync();

        return NoContent();
    }
}

public class CreateWorkspaceRequest
{
    public string Name { get; set; } = string.Empty;
    public string? StrategyType { get; set; }
    public string? ConfigJson { get; set; }
}

public class UpdateWorkspaceRequest
{
    public string? Name { get; set; }
    public string? StrategyType { get; set; }
    public string? ConfigJson { get; set; }
}

public class UpdateWorkspaceConfigRequest
{
    public string? ConfigJson { get; set; }
}
