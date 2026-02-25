using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public bool IsEnabled { get; set; } = true;
    public Guid? InvitedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsAdmin => Role == UserRole.Admin;

    public User? InvitedByUser { get; set; }
    public ICollection<ExchangeAccount> ExchangeAccounts { get; set; } = new List<ExchangeAccount>();
    public ICollection<ProxyServer> ProxyServers { get; set; } = new List<ProxyServer>();
    public ICollection<Workspace> Workspaces { get; set; } = new List<Workspace>();
    public ICollection<InviteCode> InviteCodes { get; set; } = new List<InviteCode>();
    public ICollection<InviteCodeUsage> InviteCodeUsages { get; set; } = new List<InviteCodeUsage>();
}
