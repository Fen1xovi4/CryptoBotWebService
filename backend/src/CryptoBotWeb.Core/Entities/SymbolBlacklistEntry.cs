using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

public class SymbolBlacklistEntry
{
    public Guid Id { get; set; }
    public ExchangeType ExchangeType { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
