namespace CryptoBotWeb.Core.Helpers;

public static class IndicatorCalculator
{
    public static decimal[] CalculateEma(decimal[] closePrices, int period)
    {
        var ema = new decimal[closePrices.Length];
        if (closePrices.Length < period) return ema;

        decimal sum = 0;
        for (int i = 0; i < period; i++)
            sum += closePrices[i];
        ema[period - 1] = sum / period;

        decimal multiplier = 2m / (period + 1);
        for (int i = period; i < closePrices.Length; i++)
            ema[i] = (closePrices[i] - ema[i - 1]) * multiplier + ema[i - 1];

        return ema;
    }

    public static decimal[] CalculateSma(decimal[] closePrices, int period)
    {
        var sma = new decimal[closePrices.Length];
        if (closePrices.Length < period) return sma;

        decimal sum = 0;
        for (int i = 0; i < period; i++)
            sum += closePrices[i];
        sma[period - 1] = sum / period;

        for (int i = period; i < closePrices.Length; i++)
        {
            sum = sum - closePrices[i - period] + closePrices[i];
            sma[i] = sum / period;
        }

        return sma;
    }

    public static decimal? GetCurrentMa(decimal[] closePrices, string indicatorType, int period)
    {
        if (closePrices.Length < period) return null;

        var values = indicatorType.Equals("SMA", StringComparison.OrdinalIgnoreCase)
            ? CalculateSma(closePrices, period)
            : CalculateEma(closePrices, period);

        return values[^1];
    }
}
