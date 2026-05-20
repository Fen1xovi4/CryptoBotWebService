using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Helpers;

// Analytic min-max optimizer for SmartGridHedge's static short. Closed-form solution to
// Loss_S3 = Loss_S4 — both losses are linear in qHedge, so the balance point is exact.
// Direct port of SymmetricAnalyticHedgeOptimizer.cs in GridHedgeSimulator (decimal math
// instead of double because the rest of the project uses decimal).
//
//   A = (1+Step)^NUp,  B = (1-Step)^NDown
//   α₃ = P0 * (A − 1 + t*(1+A))
//   α₄ = P0 * (t*(1+B) − (1−B))
//   β₃ = FeesNoHedge_S3 − GridPnL_S3
//   β₄ = FeesNoHedge_S4 − GridPnL_S4
//   qHedge* = (β₄ − β₃) / (α₃ − α₄)
//
// Returns coins (BTC, ETH, …), NOT USDT — the live handler converts to a notional via the
// current mark price when calling OpenHedgeShortAsync.
public static class SymmetricHedgeOptimizer
{
    public record Result(decimal QHedgeCoins, decimal WorstCaseLoss);

    public static Result Optimize(
        decimal p0,
        decimal step,
        int nUp,
        int nDown,
        decimal lotUsd,
        SmartGridSkimMode skimMode,
        decimal makerBps,
        decimal takerBps)
    {
        if (p0 <= 0) throw new ArgumentOutOfRangeException(nameof(p0));
        if (step <= 0 || step >= 1) throw new ArgumentOutOfRangeException(nameof(step));
        if (nUp < 1) throw new ArgumentOutOfRangeException(nameof(nUp));
        if (nDown < 1) throw new ArgumentOutOfRangeException(nameof(nDown));
        if (lotUsd <= 0) throw new ArgumentOutOfRangeException(nameof(lotUsd));

        var m = makerBps / 10_000m;
        var t = takerBps / 10_000m;
        var a = Pow(1m + step, nUp);
        var b = Pow(1m - step, nDown);

        decimal gridS3;
        decimal feesNoHedgeS3;

        switch (skimMode)
        {
            case SmartGridSkimMode.FullRecycle:
                gridS3 = lotUsd * (nUp - (a - 1m) * (1m - step) / step);
                feesNoHedgeS3 =
                    lotUsd * m * nUp +
                    lotUsd * t * (1m + step) * (a - 1m) / step;
                break;
            case SmartGridSkimMode.ExcessRecycle:
                gridS3 = lotUsd * step * nUp;
                feesNoHedgeS3 =
                    lotUsd * m * (1m + (nUp - 1) * step) +
                    lotUsd * t * (2m * a - 1m - step);
                break;
            default: // OneShot
                gridS3 = lotUsd * step * nUp;
                feesNoHedgeS3 =
                    lotUsd * m * (1m + (nUp - 1) * step) +
                    lotUsd * (1m + step) * t;
                break;
        }

        var gridS4 = lotUsd * ((1m - step) * (1m - b) / step - nDown);
        var feesNoHedgeS4 =
            lotUsd * m * nDown +
            lotUsd * t * (1m - step) * (1m - b) / step;

        var alpha3 = p0 * (a - 1m + t * (1m + a));
        var alpha4 = p0 * (t * (1m + b) - (1m - b));
        var beta3 = feesNoHedgeS3 - gridS3;
        var beta4 = feesNoHedgeS4 - gridS4;

        var denom = alpha3 - alpha4;
        if (denom == 0m) return new Result(0m, Math.Max(0m, beta3));

        var qHedge = (beta4 - beta3) / denom;
        if (qHedge < 0m) qHedge = 0m;

        var lossS3 = qHedge * alpha3 + beta3;
        var wcl = Math.Max(0m, lossS3);
        return new Result(qHedge, wcl);
    }

    // (1 + x)^n for decimal — repeated multiplication is exact and cheap for our n ≤ 200.
    private static decimal Pow(decimal baseValue, int exponent)
    {
        if (exponent < 0) throw new ArgumentOutOfRangeException(nameof(exponent));
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= baseValue;
        return result;
    }
}
