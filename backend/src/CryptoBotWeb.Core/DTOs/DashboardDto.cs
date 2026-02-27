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

public class WorkspaceDashboardDto
{
    public Guid WorkspaceId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
    public int TotalBots { get; set; }
    public int RunningBots { get; set; }
    public int BotsInPosition { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal WinRate { get; set; }
    public List<PnlPoint> PnlCurve { get; set; } = new();
}

public class PnlPoint
{
    public DateTime Date { get; set; }
    public decimal CumPnl { get; set; }
}

public class WorkspaceDetailDto : WorkspaceDashboardDto
{
    public decimal AvgTradePnl { get; set; }
    public decimal MaxDrawdown { get; set; }
    public List<TradeDto> RecentTrades { get; set; } = new();
    public List<BotSummaryDto> Bots { get; set; } = new();
}

public class TradeDto
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal? PnlDollar { get; set; }
    public string? Status { get; set; }
    public DateTime ExecutedAt { get; set; }
}

public class BotSummaryDto
{
    public Guid StrategyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool HasPosition { get; set; }
    public string? PositionDirection { get; set; }
    public decimal RealizedPnl { get; set; }
    public int TotalTrades { get; set; }
}
