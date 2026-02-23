using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;

namespace CryptoBotWeb.Infrastructure.Strategies;

public static class EmaBounceSimulator
{
    public static SimulationResult Run(List<CandleDto> candles, EmaBounceConfig config)
    {
        var result = new SimulationResult();

        if (candles.Count < config.IndicatorLength)
            return result;

        // 1. Pre-calculate indicator array
        var closePrices = candles.Select(c => c.Close).ToArray();
        var maValues = config.IndicatorType.Equals("SMA", StringComparison.OrdinalIgnoreCase)
            ? IndicatorCalculator.CalculateSma(closePrices, config.IndicatorLength)
            : IndicatorCalculator.CalculateEma(closePrices, config.IndicatorLength);

        // 2. Build indicator points for chart overlay
        for (int i = config.IndicatorLength - 1; i < candles.Count; i++)
        {
            if (maValues[i] != 0)
            {
                result.IndicatorValues.Add(new IndicatorPoint
                {
                    Time = candles[i].OpenTime,
                    Value = Math.Round(maValues[i], 8)
                });
            }
        }

        // 3. Replay candles through strategy logic
        var state = new SimState { CurrentOrderSize = config.OrderSize };

        for (int i = config.IndicatorLength; i < candles.Count; i++)
        {
            var candle = candles[i];
            var ma = maValues[i];
            if (ma == 0) continue;

            // Check TP/SL on open positions
            CheckTpSl(config, state, candle, result.Trades);

            // Process counter logic
            ProcessLong(config, state, candle, ma);
            ProcessShort(config, state, candle, ma);

            // Check entries
            if (ShouldOpenLong(config, state, candle, ma))
                SimOpenLong(config, state, candle, result.Trades);

            if (ShouldOpenShort(config, state, candle, ma))
                SimOpenShort(config, state, candle, result.Trades);
        }

        // 4. Build summary
        BuildSummary(result, state);
        return result;
    }

    #region Martingale

    private static decimal GetCurrentOrderSize(EmaBounceConfig config, SimState state)
    {
        if (!config.UseMartingale)
            return config.OrderSize;

        var baseSize = config.OrderSize;

        if (config.UseDrawdownScale && config.DrawdownBalance > 0)
        {
            // Drawdown mode: scale by cumulative dollar loss (replaces classic martingale)
            var drawdownThreshold = config.DrawdownBalance * config.DrawdownPercent / 100m;
            var targetThreshold = config.DrawdownBalance * config.DrawdownTarget / 100m;

            if (state.RunningPnlDollar <= -drawdownThreshold)
            {
                var levels = (int)Math.Floor(-state.RunningPnlDollar / drawdownThreshold);
                baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, levels);
            }
            else if (state.RunningPnlDollar >= targetThreshold)
            {
                state.RunningPnlDollar = 0;
            }
        }
        else if (state.ConsecutiveLosses > 0)
        {
            // Classic/stepped martingale: only when drawdown scaling is OFF
            if (config.UseSteppedMartingale && config.MartingaleStep > 0)
            {
                var steps = state.ConsecutiveLosses / config.MartingaleStep;
                if (steps > 0)
                    baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, steps);
            }
            else
            {
                baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, state.ConsecutiveLosses);
            }
        }

        return Math.Round(baseSize, 2);
    }

    private static void UpdateMartingaleState(EmaBounceConfig config, SimState state, decimal pnlPercent, decimal orderSize)
    {
        if (!config.UseMartingale) return;

        var pnlDollar = orderSize * pnlPercent / 100m;
        state.RunningPnlDollar += pnlDollar;

        if (pnlPercent > 0)
        {
            // Win — reset consecutive losses
            state.ConsecutiveLosses = 0;
        }
        else
        {
            // Loss — increment
            state.ConsecutiveLosses++;
        }
    }

    #endregion

    #region Counter Logic (from EmaBounceHandler)

    private static void ProcessLong(EmaBounceConfig config, SimState state, CandleDto candle, decimal ma)
    {
        if (state.WaitNextCandleAfterLongClose)
        {
            state.WaitNextCandleAfterLongClose = false;
            return;
        }

        if (state.OpenLong != null || state.OpenShort != null) return;

        if (state.LongCounter >= config.CandleCount)
        {
            var offsetLine = ma + ma * config.OffsetPercent / 100m;
            if (candle.Low <= offsetLine)
                return;
        }

        if (candle.Low > ma)
            state.LongCounter++;
        else
            state.LongCounter = 0;
    }

    private static void ProcessShort(EmaBounceConfig config, SimState state, CandleDto candle, decimal ma)
    {
        if (state.WaitNextCandleAfterShortClose)
        {
            state.WaitNextCandleAfterShortClose = false;
            return;
        }

        if (state.OpenShort != null || state.OpenLong != null) return;

        if (state.ShortCounter >= config.CandleCount)
        {
            var offsetLine = ma - ma * config.OffsetPercent / 100m;
            if (candle.High >= offsetLine)
                return;
        }

        if (candle.High < ma)
            state.ShortCounter++;
        else
            state.ShortCounter = 0;
    }

    private static bool ShouldOpenLong(EmaBounceConfig config, SimState state, CandleDto candle, decimal ma)
    {
        if (state.OpenLong != null || state.OpenShort != null) return false;
        if (state.LongCounter < config.CandleCount) return false;

        var offsetLine = ma + ma * config.OffsetPercent / 100m;
        return candle.Low <= offsetLine;
    }

    private static bool ShouldOpenShort(EmaBounceConfig config, SimState state, CandleDto candle, decimal ma)
    {
        if (state.OpenShort != null || state.OpenLong != null) return false;
        if (state.ShortCounter < config.CandleCount) return false;

        var offsetLine = ma - ma * config.OffsetPercent / 100m;
        return candle.High >= offsetLine;
    }

    #endregion

    #region Simulation Actions

    private static void SimOpenLong(EmaBounceConfig config, SimState state, CandleDto candle, List<SimulatedTrade> trades)
    {
        var entryPrice = candle.Close;
        var orderSize = GetCurrentOrderSize(config, state);
        state.CurrentOrderSize = orderSize;

        state.OpenLong = new SimPosition
        {
            EntryPrice = entryPrice,
            OpenedAt = candle.OpenTime,
            TakeProfit = entryPrice * (1 + config.TakeProfitPercent / 100m),
            StopLoss = entryPrice * (1 - config.StopLossPercent / 100m),
            OrderSize = orderSize
        };
        state.LongCounter = 0;

        trades.Add(new SimulatedTrade
        {
            Side = "Long",
            Action = "Open",
            Price = entryPrice,
            Time = candle.OpenTime,
            Reason = "Entry",
            OrderSize = orderSize
        });
    }

    private static void SimOpenShort(EmaBounceConfig config, SimState state, CandleDto candle, List<SimulatedTrade> trades)
    {
        var entryPrice = candle.Close;
        var orderSize = GetCurrentOrderSize(config, state);
        state.CurrentOrderSize = orderSize;

        state.OpenShort = new SimPosition
        {
            EntryPrice = entryPrice,
            OpenedAt = candle.OpenTime,
            TakeProfit = entryPrice * (1 - config.TakeProfitPercent / 100m),
            StopLoss = entryPrice * (1 + config.StopLossPercent / 100m),
            OrderSize = orderSize
        };
        state.ShortCounter = 0;

        trades.Add(new SimulatedTrade
        {
            Side = "Short",
            Action = "Open",
            Price = entryPrice,
            Time = candle.OpenTime,
            Reason = "Entry",
            OrderSize = orderSize
        });
    }

    private static void CheckTpSl(EmaBounceConfig config, SimState state, CandleDto candle, List<SimulatedTrade> trades)
    {
        // Check Long TP/SL
        if (state.OpenLong != null)
        {
            bool tpHit = candle.High >= state.OpenLong.TakeProfit;
            bool slHit = candle.Low <= state.OpenLong.StopLoss;

            if (tpHit && slHit)
                CloseLongSim(config, state, candle, trades, state.OpenLong.StopLoss, "StopLoss");
            else if (tpHit)
                CloseLongSim(config, state, candle, trades, state.OpenLong.TakeProfit, "TakeProfit");
            else if (slHit)
                CloseLongSim(config, state, candle, trades, state.OpenLong.StopLoss, "StopLoss");
        }

        // Check Short TP/SL
        if (state.OpenShort != null)
        {
            bool tpHit = candle.Low <= state.OpenShort.TakeProfit;
            bool slHit = candle.High >= state.OpenShort.StopLoss;

            if (tpHit && slHit)
                CloseShortSim(config, state, candle, trades, state.OpenShort.StopLoss, "StopLoss");
            else if (tpHit)
                CloseShortSim(config, state, candle, trades, state.OpenShort.TakeProfit, "TakeProfit");
            else if (slHit)
                CloseShortSim(config, state, candle, trades, state.OpenShort.StopLoss, "StopLoss");
        }
    }

    private static void CloseLongSim(EmaBounceConfig config, SimState state, CandleDto candle, List<SimulatedTrade> trades,
        decimal closePrice, string reason)
    {
        var entry = state.OpenLong!.EntryPrice;
        var orderSize = state.OpenLong.OrderSize;
        var pnlPercent = (closePrice - entry) / entry * 100m;
        var pnlDollar = orderSize * pnlPercent / 100m;

        trades.Add(new SimulatedTrade
        {
            Side = "Long",
            Action = "Close",
            Price = closePrice,
            Time = candle.OpenTime,
            Reason = reason,
            PnlPercent = Math.Round(pnlPercent, 4),
            OrderSize = orderSize,
            PnlDollar = Math.Round(pnlDollar, 2)
        });

        UpdateMartingaleState(config, state, pnlPercent, orderSize);

        state.OpenLong = null;
        state.LongCounter = 0;
        state.WaitNextCandleAfterLongClose = true;
    }

    private static void CloseShortSim(EmaBounceConfig config, SimState state, CandleDto candle, List<SimulatedTrade> trades,
        decimal closePrice, string reason)
    {
        var entry = state.OpenShort!.EntryPrice;
        var orderSize = state.OpenShort.OrderSize;
        var pnlPercent = (entry - closePrice) / entry * 100m;
        var pnlDollar = orderSize * pnlPercent / 100m;

        trades.Add(new SimulatedTrade
        {
            Side = "Short",
            Action = "Close",
            Price = closePrice,
            Time = candle.OpenTime,
            Reason = reason,
            PnlPercent = Math.Round(pnlPercent, 4),
            OrderSize = orderSize,
            PnlDollar = Math.Round(pnlDollar, 2)
        });

        UpdateMartingaleState(config, state, pnlPercent, orderSize);

        state.OpenShort = null;
        state.ShortCounter = 0;
        state.WaitNextCandleAfterShortClose = true;
    }

    #endregion

    #region Summary

    private static void BuildSummary(SimulationResult result, SimState state)
    {
        var closedTrades = result.Trades.Where(t => t.Action == "Close").ToList();

        var wins = closedTrades.Count(t => t.PnlPercent > 0);
        var losses = closedTrades.Count(t => t.PnlPercent <= 0);
        var totalPnlPercent = closedTrades.Sum(t => t.PnlPercent ?? 0);
        var totalPnlDollar = closedTrades.Sum(t => t.PnlDollar ?? 0);
        var openPositions = (state.OpenLong != null ? 1 : 0) + (state.OpenShort != null ? 1 : 0);
        var maxOrderSize = result.Trades.Any() ? result.Trades.Max(t => t.OrderSize) : 0;

        result.Summary = new SimulationSummary
        {
            TotalTrades = closedTrades.Count,
            WinningTrades = wins,
            LosingTrades = losses,
            TotalPnlPercent = Math.Round(totalPnlPercent, 4),
            TotalPnlDollar = Math.Round(totalPnlDollar, 2),
            WinRate = closedTrades.Count > 0 ? Math.Round((decimal)wins / closedTrades.Count * 100, 2) : 0,
            AveragePnlPercent = closedTrades.Count > 0 ? Math.Round(totalPnlPercent / closedTrades.Count, 4) : 0,
            OpenPositions = openPositions,
            MaxOrderSize = maxOrderSize
        };
    }

    #endregion

    #region Internal State

    private class SimState
    {
        public int LongCounter { get; set; }
        public int ShortCounter { get; set; }
        public SimPosition? OpenLong { get; set; }
        public SimPosition? OpenShort { get; set; }
        public bool WaitNextCandleAfterLongClose { get; set; }
        public bool WaitNextCandleAfterShortClose { get; set; }

        // Martingale state
        public decimal CurrentOrderSize { get; set; }
        public int ConsecutiveLosses { get; set; }
        public decimal RunningPnlDollar { get; set; }
    }

    private class SimPosition
    {
        public decimal EntryPrice { get; set; }
        public DateTime OpenedAt { get; set; }
        public decimal TakeProfit { get; set; }
        public decimal StopLoss { get; set; }
        public decimal OrderSize { get; set; }
    }

    #endregion
}
