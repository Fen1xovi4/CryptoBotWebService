using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Helpers;

/// <summary>One point of the intrabar price walk.</summary>
public readonly record struct PricePoint(DateTime Time, decimal Price);

/// <summary>
/// Deterministic intrabar price path used by all simulators so fills/TP/SL resolve
/// in a consistent order. Convention: bullish candle (Close >= Open) walks
/// Open → Low → High → Close; bearish walks Open → High → Low → Close.
/// This is the standard pessimism-neutral heuristic: the adverse extreme is visited
/// before the favorable one relative to the candle direction.
/// </summary>
public static class CandlePathHelper
{
    /// <summary>
    /// Returns the 4-point walk for a candle. Points are spaced at 0%, 25%, 50%, 75%
    /// of the candle duration so simulators have monotonically increasing timestamps.
    /// </summary>
    public static PricePoint[] GetTicks(CandleDto c)
    {
        var duration = c.CloseTime > c.OpenTime ? c.CloseTime - c.OpenTime : TimeSpan.FromMinutes(1);
        var q = TimeSpan.FromTicks(duration.Ticks / 4);
        var bullish = c.Close >= c.Open;

        return bullish
            ? new[]
            {
                new PricePoint(c.OpenTime, c.Open),
                new PricePoint(c.OpenTime + q, c.Low),
                new PricePoint(c.OpenTime + q + q, c.High),
                new PricePoint(c.OpenTime + q + q + q, c.Close)
            }
            : new[]
            {
                new PricePoint(c.OpenTime, c.Open),
                new PricePoint(c.OpenTime + q, c.High),
                new PricePoint(c.OpenTime + q + q, c.Low),
                new PricePoint(c.OpenTime + q + q + q, c.Close)
            };
    }
}
