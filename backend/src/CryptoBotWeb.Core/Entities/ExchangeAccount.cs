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
    public string? DzengiAccountId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;

    /// <summary>Proxies for this account, in failover order (use <see cref="OrderedProxies"/>).</summary>
    public ICollection<ExchangeAccountProxy> AccountProxies { get; set; } = new List<ExchangeAccountProxy>();

    public ICollection<Strategy> Strategies { get; set; } = new List<Strategy>();
    public ICollection<Trade> Trades { get; set; } = new List<Trade>();

    /// <summary>Proxies ordered by failover priority (primary first). Requires AccountProxies (and their Proxy) to be loaded.</summary>
    public IEnumerable<ProxyServer> OrderedProxies =>
        AccountProxies.OrderBy(x => x.Priority).Select(x => x.Proxy).Where(p => p != null);
}
