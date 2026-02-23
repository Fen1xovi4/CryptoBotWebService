namespace CryptoBotWeb.Core.DTOs;

public class DashboardSummary
{
    public int TotalAccounts { get; set; }
    public int ActiveAccounts { get; set; }
    public int RunningStrategies { get; set; }
    public int TotalTrades { get; set; }
    public List<AccountBalanceSummary> Accounts { get; set; } = new();
}

public class AccountBalanceSummary
{
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public decimal? TotalUsdEstimate { get; set; }
}
