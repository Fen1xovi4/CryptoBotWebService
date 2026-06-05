using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.DTOs;

public class ExchangeAccountDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ExchangeType ExchangeType { get; set; }

    /// <summary>Proxies in failover order (priority 0 = primary).</summary>
    public List<AccountProxyDto> Proxies { get; set; } = new();

    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AccountProxyDto
{
    public Guid ProxyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
}

public class CreateExchangeAccountRequest
{
    public string Name { get; set; } = string.Empty;
    public ExchangeType ExchangeType { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string? Passphrase { get; set; }

    /// <summary>Proxies in failover order (index = priority, first = primary).</summary>
    public List<Guid> ProxyIds { get; set; } = new();
}

public class UpdateExchangeAccountRequest
{
    public string? Name { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string? Passphrase { get; set; }

    /// <summary>If non-null, replaces the proxy list (ordered). Empty list clears all (admin only).</summary>
    public List<Guid>? ProxyIds { get; set; }

    public bool? IsActive { get; set; }
}

public class ClosePositionRequest
{
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
}
