using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Helpers;

/// <summary>
/// Aggregates 1-minute path candles into a higher timeframe so simulators can compute
/// indicators on the same series the live handler would fetch from the exchange.
/// </summary>
public static class CandleAggregator
{
    /// <summary>
    /// Aggregates ascending candles into <paramref name="timeframe"/> buckets aligned to
    /// the epoch (UTC midnight boundaries for 1d, hour boundaries for 1h, etc.).
    /// The trailing partial bucket IS included (its CloseTime reflects the last source candle) —
    /// callers that must only act on closed candles should drop the last element or compare
    /// CloseTime against the current path time.
    /// </summary>
    public static List<CandleDto> Aggregate(IReadOnlyList<CandleDto> minuteCandles, string timeframe)
    {
        var span = SymbolHelper.GetTimeframeSpan(timeframe);
        var result = new List<CandleDto>();
        if (minuteCandles.Count == 0) return result;

        var ticksPerBucket = span.Ticks;
        CandleDto? current = null;
        long currentBucket = -1;

        foreach (var c in minuteCandles)
        {
            var bucket = c.OpenTime.Ticks / ticksPerBucket;
            if (bucket != currentBucket)
            {
                if (current != null) result.Add(current);
                currentBucket = bucket;
                current = new CandleDto
                {
                    OpenTime = new DateTime(bucket * ticksPerBucket, DateTimeKind.Utc),
                    CloseTime = c.CloseTime,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = c.Volume
                };
            }
            else
            {
                current!.High = Math.Max(current.High, c.High);
                current.Low = Math.Min(current.Low, c.Low);
                current.Close = c.Close;
                current.CloseTime = c.CloseTime;
                current.Volume += c.Volume;
            }
        }

        if (current != null) result.Add(current);
        return result;
    }
}
