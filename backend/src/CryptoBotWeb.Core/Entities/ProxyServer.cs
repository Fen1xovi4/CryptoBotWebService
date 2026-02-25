namespace CryptoBotWeb.Core.Entities;

public class ProxyServer
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? PasswordEncrypted { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<ExchangeAccount> ExchangeAccounts { get; set; } = new List<ExchangeAccount>();
}
