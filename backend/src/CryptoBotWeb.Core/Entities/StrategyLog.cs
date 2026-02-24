namespace CryptoBotWeb.Core.Entities;

public class StrategyLog
{
    public Guid Id { get; set; }
    public Guid StrategyId { get; set; }
    public string Level { get; set; } = "Info";  // Info, Warning, Error
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Strategy Strategy { get; set; } = null!;
}
