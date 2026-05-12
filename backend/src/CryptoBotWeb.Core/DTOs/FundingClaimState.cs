namespace CryptoBotWeb.Core.DTOs;

public enum FundingClaimPhase
{
    Idle = 0,
    InPosition = 1
}

public class FundingClaimState
{
    public FundingClaimPhase Phase { get; set; } = FundingClaimPhase.Idle;
    public string? Direction { get; set; } // "Long" or "Short"
    public string? Symbol { get; set; } // symbol of current position
    public decimal? CurrentFundingRate { get; set; }
    public DateTime? NextFundingTime { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? EntryQuantity { get; set; }
    public decimal? EntrySizeUsdt { get; set; }
    public DateTime? PositionOpenedAt { get; set; }
    public int CycleCount { get; set; }
    public decimal CycleTotalPnl { get; set; }
    public decimal CycleTotalFundingPnl { get; set; }
    public decimal CurrentCycleFundingPnl { get; set; } // funding earned in current open position
    public decimal? LastPrice { get; set; }
    public DateTime? LastHourlyCheckAt { get; set; } // throttle: one check per hour
    public int MissedPositionChecks { get; set; } // consecutive null GetPosition results — debounces ExternalClose
}
