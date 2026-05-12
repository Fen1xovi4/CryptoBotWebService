namespace CryptoBotWeb.Core.DTOs;

public class FundingClaimConfig
{
    public string Symbol { get; set; } = string.Empty;
    public int MaxCycles { get; set; } = 0; // 0 = infinite
    public bool AutoRotateTicker { get; set; } = true;
    public int CheckBeforeFundingMinutes { get; set; } = 10;
}

// Stored inside Workspace.ConfigJson (shared across all FundingClaim bots in the workspace).
// Properties are prefixed with "Fc" to avoid collisions with other strategy configs in the same JSON.
public class WorkspaceFundingClaimConfig
{
    public decimal FcSizeUsdt { get; set; } = 100m;
    public decimal FcMinFundingRatePercent { get; set; } = 0.3m;
    public decimal FcMaxFundingRatePercent { get; set; } = 2.0m;
    public decimal FcStopLossPercent { get; set; } = 1.5m;
    public int FcLeverage { get; set; } = 3;
}
