using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

/// <summary>
/// One historical kline, cached for the backtesting simulator so repeated runs over the same
/// symbol/window don't re-download months of 1m history from the exchange. Closed candles are
/// immutable, so rows are written once and never updated. Keyed by
/// (ExchangeType, Symbol, Timeframe, OpenTime). Which windows are fully present is tracked
/// separately in <see cref="MarketCandleRange"/> — the absence of a row is NOT evidence of a
/// gap (the exchange may simply have no candle for that minute).
/// </summary>
public class MarketCandle
{
    public ExchangeType ExchangeType { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1m";
    public DateTime OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
}

/// <summary>
/// A half-open UTC window [FromUtc, ToUtc) of (ExchangeType, Symbol, Timeframe) whose candles
/// have been fully downloaded into <see cref="MarketCandle"/>. The cache serves any sub-window
/// of a covered range from the DB and only asks the exchange for uncovered gaps. Adjacent and
/// overlapping ranges are coalesced on write, so there are few rows per key.
/// </summary>
public class MarketCandleRange
{
    public Guid Id { get; set; }
    public ExchangeType ExchangeType { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = "1m";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public DateTime DownloadedAt { get; set; } = DateTime.UtcNow;
}
