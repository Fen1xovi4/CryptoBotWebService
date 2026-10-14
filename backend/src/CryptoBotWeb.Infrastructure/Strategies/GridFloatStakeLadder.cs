using System.Text.Json;
using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Infrastructure.Strategies;

/// <summary>
/// Pure rules of GridFloat's restart-on-range-exit ("stake ladder") mode, shared by the live
/// <see cref="GridFloatHandler"/> and the backtest simulator so both build the same grid and
/// decide range exits identically. See <see cref="GridFloatConfig.RestartOnRangeExit"/>.
/// </summary>
public static class GridFloatStakeLadder
{
    /// <summary>Number of stakes in the ladder (≥ 1).</summary>
    public static int StageCount(GridFloatConfig config) => Math.Clamp(config.MaxStakeStages, 1, 20);

    /// <summary>Multiplier applied per stage; values below 1 (or unset) fall back to 1.</summary>
    public static decimal Multiplier(GridFloatConfig config) => config.StakeMultiplier >= 1m ? config.StakeMultiplier : 1m;

    /// <summary>Per-order stake at <paramref name="stage"/>: base (T1) × multiplier^stage, rounded to cents.</summary>
    public static decimal StakeAt(GridFloatConfig config, int stage)
    {
        if (config.Tiers.Count == 0) return 0m;
        var stake = config.Tiers[0].SizeUsdt;
        var mult = Multiplier(config);
        for (var i = 0; i < Math.Clamp(stage, 0, StageCount(config) - 1); i++) stake *= mult;
        return Math.Round(stake, 2);
    }

    /// <summary>True when <paramref name="stage"/> is the last stake of the ladder.</summary>
    public static bool IsLastStage(GridFloatConfig config, int stage) => stage + 1 >= StageCount(config);

    /// <summary>
    /// The config the grid is built from at <paramref name="stage"/>. Outside the mode this is
    /// the config itself. Inside it, a copy whose tier list is a single tier with Tiers[0]'s
    /// geometry (width, DCA/TP steps) and this stage's stake, static range forced off — so the
    /// existing ladder/TP helpers work on it unchanged. Expects tiers already normalised.
    /// </summary>
    public static GridFloatConfig ForStage(GridFloatConfig config, int stage)
    {
        if (!config.RestartOnRangeExit || config.Tiers.Count == 0) return config;

        var view = JsonSerializer.Deserialize<GridFloatConfig>(JsonSerializer.Serialize(config))!;
        var geometry = config.Tiers[0];
        view.Tiers = new List<GridFloatTier>
        {
            new()
            {
                UpToPercent = geometry.UpToPercent,
                SizeUsdt = StakeAt(config, stage),
                DcaStepPercent = geometry.DcaStepPercent,
                TpStepPercent = geometry.TpStepPercent,
            }
        };
        view.UseStaticRange = false;
        return view;
    }

    /// <summary>Losing-side edge of the current range: anchor·(1−width%) for Long, ·(1+width%) for Short.</summary>
    public static decimal RangeBound(GridFloatConfig config, decimal anchorPrice, bool isLong)
    {
        var widthPct = config.Tiers.Count > 0 ? config.Tiers[0].UpToPercent : 0m;
        return isLong ? anchorPrice * (1m - widthPct / 100m) : anchorPrice * (1m + widthPct / 100m);
    }

    /// <summary>
    /// True once price has gone strictly past the range's losing edge. Strict so the last DCA,
    /// which sits exactly on the edge when the width is a multiple of the step, gets to fill
    /// before the exit fires.
    /// </summary>
    public static bool IsOutsideRange(GridFloatConfig config, decimal anchorPrice, decimal price, bool isLong)
    {
        if (!config.RestartOnRangeExit || anchorPrice <= 0 || price <= 0) return false;
        var bound = RangeBound(config, anchorPrice, isLong);
        return isLong ? price < bound : price > bound;
    }

    /// <summary>True when <see cref="GridFloatConfig.AnchorPriceLimit"/> forbids a new anchor at this price.</summary>
    public static bool IsAnchorBlocked(GridFloatConfig config, decimal price, bool isLong)
        => config.AnchorPriceLimit is > 0 && (isLong ? price > config.AnchorPriceLimit.Value
                                                     : price < config.AnchorPriceLimit.Value);
}
