namespace CryptoBotWeb.Core.DTOs;

public class ProxyServerDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool HasAuth { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public int UsedByAccounts { get; set; }
}

public class CreateProxyRequest
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class UpdateProxyRequest
{
    public string? Name { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool? IsActive { get; set; }
}
