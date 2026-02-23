namespace CryptoBotWeb.Core.DTOs;

public class BalanceDto
{
    public string Asset { get; set; } = string.Empty;
    public decimal Free { get; set; }
    public decimal Locked { get; set; }
    public decimal Total => Free + Locked;
}

public class AccountBalanceResponse
{
    public Guid AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public List<BalanceDto> Balances { get; set; } = new();
}
