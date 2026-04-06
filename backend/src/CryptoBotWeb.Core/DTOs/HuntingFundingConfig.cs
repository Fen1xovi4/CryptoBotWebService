namespace CryptoBotWeb.Core.DTOs;

public class HuntingFundingConfig
{
    public string Symbol { get; set; } = string.Empty;
    public List<OrderLevel> Levels { get; set; } = new();
    public decimal TakeProfitPercent { get; set; } = 1.0m;
    public decimal StopLossPercent { get; set; } = 0.5m;
    public int SecondsBeforeFunding { get; set; } = 10;
    public int CloseAfterMinutes { get; set; } = 10;
    public int MaxCycles { get; set; } = 0; // 0 = infinite
    public bool EnableLong { get; set; } = true;
    public decimal MinFundingLong { get; set; } = 1.0m; // abs %, trade Long only if rate ≤ -this
    public bool EnableShort { get; set; } = true;
    public decimal MinFundingShort { get; set; } = 1.0m; // abs %, trade Short only if rate ≥ +this
}

public class OrderLevel
{
    public decimal OffsetPercent { get; set; }
    public decimal SizeUsdt { get; set; }
}
