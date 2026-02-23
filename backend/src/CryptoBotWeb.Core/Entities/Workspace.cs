namespace CryptoBotWeb.Core.Entities;

public class Workspace
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StrategyType { get; set; } = "MaratG";
    public string ConfigJson { get; set; } = "{}";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<Strategy> Strategies { get; set; } = new List<Strategy>();
}
