using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

public class ExchangeAccount
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ExchangeType ExchangeType { get; set; }
    public string ApiKeyEncrypted { get; set; } = string.Empty;
    public string ApiSecretEncrypted { get; set; } = string.Empty;
    public string? PassphraseEncrypted { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<Strategy> Strategies { get; set; } = new List<Strategy>();
    public ICollection<Trade> Trades { get; set; } = new List<Trade>();
}
