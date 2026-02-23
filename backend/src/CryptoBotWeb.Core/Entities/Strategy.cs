using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

public class Strategy
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string ConfigJson { get; set; } = "{}";
    public string StateJson { get; set; } = "{}";
    public StrategyStatus Status { get; set; } = StrategyStatus.Idle;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }

    public ExchangeAccount Account { get; set; } = null!;
    public Workspace? Workspace { get; set; }
    public ICollection<Trade> Trades { get; set; } = new List<Trade>();
}
