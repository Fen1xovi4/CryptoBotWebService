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
}
