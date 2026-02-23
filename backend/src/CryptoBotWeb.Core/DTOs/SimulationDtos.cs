namespace CryptoBotWeb.Core.DTOs;

public class SimulationRequest
{
    public Guid AccountId { get; set; }
    public string StrategyType { get; set; } = "MaratG";
    public string IndicatorType { get; set; } = "EMA";
    public int IndicatorLength { get; set; } = 50;
    public int CandleCount { get; set; } = 50;
    public decimal OffsetPercent { get; set; } = 0;
    public decimal TakeProfitPercent { get; set; } = 3;
    public decimal StopLossPercent { get; set; } = 3;
    public decimal OrderSize { get; set; } = 100;
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "1h";
    public int CandleLimit { get; set; } = 300;

    // Martingale
    public bool UseMartingale { get; set; }
    public decimal MartingaleCoeff { get; set; } = 2;
    public bool UseSteppedMartingale { get; set; }
    public int MartingaleStep { get; set; } = 3;

    // Drawdown scaling
    public bool UseDrawdownScale { get; set; }
    public decimal DrawdownBalance { get; set; }
    public decimal DrawdownPercent { get; set; } = 10;
    public decimal DrawdownTarget { get; set; } = 5;
}

public class SimulationResult
{
    public List<SimulatedTrade> Trades { get; set; } = new();
    public List<IndicatorPoint> IndicatorValues { get; set; } = new();
    public SimulationSummary Summary { get; set; } = new();
}

public class SimulatedTrade
{
    public string Side { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime Time { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal? PnlPercent { get; set; }
    public decimal OrderSize { get; set; }
    public decimal? PnlDollar { get; set; }
}

public class IndicatorPoint
{
    public DateTime Time { get; set; }
    public decimal Value { get; set; }
}

public class SimulationSummary
{
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public int LosingTrades { get; set; }
    public decimal TotalPnlPercent { get; set; }
    public decimal TotalPnlDollar { get; set; }
    public decimal WinRate { get; set; }
    public decimal AveragePnlPercent { get; set; }
    public int OpenPositions { get; set; }
    public decimal MaxOrderSize { get; set; }
}
