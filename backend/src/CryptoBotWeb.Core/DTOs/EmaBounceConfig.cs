namespace CryptoBotWeb.Core.DTOs;

public class EmaBounceConfig
{
    public string IndicatorType { get; set; } = "EMA";
    public int IndicatorLength { get; set; } = 50;
    public int CandleCount { get; set; } = 50;
    public decimal OffsetPercent { get; set; } = 0;
    public decimal TakeProfitPercent { get; set; } = 3;
    public decimal StopLossPercent { get; set; } = 3;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1h";
    public decimal OrderSize { get; set; }

    // Martingale
    public bool UseMartingale { get; set; }
    public decimal MartingaleCoeff { get; set; } = 2;
    public bool UseSteppedMartingale { get; set; }
    public int MartingaleStep { get; set; } = 3;

    // Direction filter
    public bool OnlyLong { get; set; }
    public bool OnlyShort { get; set; }

    // Drawdown scaling
    public bool UseDrawdownScale { get; set; }
    public decimal DrawdownBalance { get; set; }
    public decimal DrawdownPercent { get; set; } = 10;
    public decimal DrawdownTarget { get; set; } = 5;

    // Grid (MaratGGrid only; ignored by plain MaratG).
    // GridLotSize comes from the workspace (gridLotSize) and is scaled by the martingale multiplier
    // like OrderSize. Levels sit at entry ∓ k·GridStepPercent up to (not including) the stop loss.
    public decimal GridLotSize { get; set; }
    public decimal GridStepPercent { get; set; } = 1;
    public int GridMaxLots { get; set; } // 0 = every level up to the stop loss
}
