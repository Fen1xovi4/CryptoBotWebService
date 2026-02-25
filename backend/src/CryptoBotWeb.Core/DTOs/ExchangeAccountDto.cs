using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.DTOs;

public class ExchangeAccountDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ExchangeType ExchangeType { get; set; }
    public Guid? ProxyId { get; set; }
    public string? ProxyName { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateExchangeAccountRequest
{
    public string Name { get; set; } = string.Empty;
    public ExchangeType ExchangeType { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string? Passphrase { get; set; }
    public Guid? ProxyId { get; set; }
}

public class UpdateExchangeAccountRequest
{
    public string? Name { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
    public string? Passphrase { get; set; }
    public Guid? ProxyId { get; set; }
    public bool? IsActive { get; set; }
}
