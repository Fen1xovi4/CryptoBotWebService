namespace CryptoBotWeb.Core.Entities;

/// <summary>
/// Join row binding an <see cref="ExchangeAccount"/> to one of its proxies, with a
/// failover priority (0 = primary, 1 = first fallback, ...). An account can list any
/// number of proxies; the factory tries them in <see cref="Priority"/> order.
/// </summary>
public class ExchangeAccountProxy
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid ProxyId { get; set; }

    /// <summary>0 = primary; higher = later fallback.</summary>
    public int Priority { get; set; }

    public ExchangeAccount Account { get; set; } = null!;
    public ProxyServer Proxy { get; set; } = null!;
}
