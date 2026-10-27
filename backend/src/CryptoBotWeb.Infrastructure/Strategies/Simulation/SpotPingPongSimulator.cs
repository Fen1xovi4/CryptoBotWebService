using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Strategies.Simulation;

/// <summary>
/// Backtest counterpart of <see cref="SpotPingPongHandler"/>. Each configured level loops
/// buy-limit → sell-limit → buy-limit on the 4-tick candle path: a resting buy fills when a tick
/// trades at or below BuyPrice, the sell when a tick trades at or above SellPrice, both at their
/// limit price with the maker fee (ctx.MakerFeeRate — the engine takes it from the account's
/// exchange; spot is 0.1% on all three, so pass makerFeeRate explicitly if the account is a
/// futures-fee one). One fill per level per tick, and a buy that fills on a tick does not sell on
/// the same tick — mirrors the 5s poll granularity loosely.
///
/// Approximations: the price path is the futures series (the engine has no spot klines) — spot
/// trades a few bps away from the perp; the buy fee is modeled as a base-coin deduction (what we
/// sell is fill × (1 − maker)), which is the common case on all three exchanges; no partial fills.
/// Levels still holding coins at the end are reported as open positions with unrealized PnL at the
/// last price.
/// </summary>
public class SpotPingPongSimulator : IStrategySimulator
{
    public string StrategyType => StrategyTypes.SpotPingPong;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public void ValidateConfig(string configJson)
    {
        SpotPingPongConfig? config;
        try { config = JsonSerializer.Deserialize<SpotPingPongConfig>(configJson, JsonOptions); }
        catch (Exception ex) { throw new ArgumentException($"SpotPingPong: ConfigJson не распарсился: {ex.Message}"); }
        var error = SpotPingPongHandler.ValidateConfig(config);
        if (error != null) throw new ArgumentException($"SpotPingPong: {error}");
    }

    public SimulationRunResult Run(SimulationContext context)
    {
        var result = new SimulationRunResult();

        SpotPingPongConfig? config;
        try { config = JsonSerializer.Deserialize<SpotPingPongConfig>(context.ConfigJson, JsonOptions); }
        catch (Exception ex)
        {
            result.Warnings.Add($"SpotPingPong: ConfigJson не распарсился: {ex.Message}");
            return result;
        }
        var error = SpotPingPongHandler.ValidateConfig(config, context.MakerFeeRate);
        if (error != null) { result.Warnings.Add($"SpotPingPong: {error}"); return result; }

        var candles = context.PathCandles;
        if (candles == null || candles.Count == 0)
        {
            result.Warnings.Add("SpotPingPong: пустой PathCandles — нечего симулировать");
            return result;
        }

        result.Warnings.Add("SpotPingPong: ценовой путь взят с фьючерсов (спотовых свечей в движке нет) — спот отличается на доли процента.");

        var maker = context.MakerFeeRate;
        var ledger = new SimLedger(context.MakerFeeRate, context.TakerFeeRate);
        var levels = config!.Levels;
        var holding = new bool[levels.Count];
        var grossQtyArr = new decimal[levels.Count];   // coins bought before the fee
        var heldQty = new decimal[levels.Count];       // net coins after the buy fee
        var costUsd = new decimal[levels.Count];       // gross qty × buy price
        var entryFee = new decimal[levels.Count];
        var cycles = 0;
        var lastPrice = candles[0].Open;
        var lastTime = candles[0].OpenTime;
        DateTime? nextSample = null;

        decimal OpenNotional()
        {
            var sum = 0m;
            for (var i = 0; i < levels.Count; i++) if (holding[i]) sum += costUsd[i];
            return sum;
        }

        decimal Unrealized(decimal price)
        {
            var sum = 0m;
            for (var i = 0; i < levels.Count; i++)
                if (holding[i]) sum += heldQty[i] * price - costUsd[i];
            return sum;
        }

        foreach (var candle in candles)
        {
            // Hourly equity samples keep the curve small on year-long windows.
            if (nextSample == null || candle.OpenTime >= nextSample)
            {
                ledger.SampleEquity(candle.OpenTime, Unrealized(candle.Open));
                nextSample = new DateTime(candle.OpenTime.Year, candle.OpenTime.Month, candle.OpenTime.Day,
                    candle.OpenTime.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
            }

            foreach (var tick in CandlePathHelper.GetTicks(candle))
            {
                lastPrice = tick.Price;
                lastTime = tick.Time;

                for (var i = 0; i < levels.Count; i++)
                {
                    var lvl = levels[i];
                    if (!holding[i])
                    {
                        if (tick.Price > lvl.BuyPrice) continue;
                        var grossQty = lvl.AmountUsdt / lvl.BuyPrice;
                        var fee = ledger.RecordOpen(tick.Time, "Long", "Open", lvl.BuyPrice, grossQty, taker: false,
                            reason: $"L{i + 1} buy @ {lvl.BuyPrice}", holdForPosition: false);
                        holding[i] = true;
                        grossQtyArr[i] = grossQty;
                        heldQty[i] = grossQty * (1m - maker);
                        costUsd[i] = grossQty * lvl.BuyPrice;
                        entryFee[i] = fee;
                        ledger.TrackOpenNotional(OpenNotional());
                    }
                    else
                    {
                        if (tick.Price < lvl.SellPrice) continue;
                        // Gross before fees = what the full bought qty would fetch minus what it cost;
                        // the ledger then subtracts this level's own entry fee and the sell fee on
                        // the net qty actually sold. (Off from the exact base-coin-fee arithmetic by
                        // maker × step, i.e. ~0.1% of the profit — not worth a second code path.)
                        var gross = grossQtyArr[i] * lvl.SellPrice - costUsd[i];
                        var pct = costUsd[i] > 0 ? gross / costUsd[i] * 100m : 0m;
                        ledger.RecordClose(tick.Time, "Long", "TakeProfit", lvl.SellPrice, heldQty[i], taker: false,
                            gross, pct, $"L{i + 1} sell @ {lvl.SellPrice}", ownEntryFee: entryFee[i]);
                        holding[i] = false;
                        grossQtyArr[i] = 0;
                        heldQty[i] = 0;
                        costUsd[i] = 0;
                        entryFee[i] = 0;
                        cycles++;
                    }
                }
            }
        }

        ledger.SampleEquity(lastTime, Unrealized(lastPrice));

        result.Trades = ledger.Trades;
        result.EquityCurve = ledger.EquityCurve;
        var openCount = holding.Count(h => h);
        result.Summary = new SimulationRunSummary
        {
            TotalTrades = ledger.Trades.Count,
            WinningTrades = ledger.WinningTrades,
            LosingTrades = ledger.LosingTrades,
            WinRate = ledger.ClosedTrades > 0 ? Math.Round((decimal)ledger.WinningTrades / ledger.ClosedTrades * 100m, 2) : 0m,
            GrossPnlUsd = Math.Round(ledger.GrossRealizedUsd, 8),
            FeesUsd = Math.Round(ledger.FeesUsd, 8),
            FundingPnlUsd = 0m,
            NetPnlUsd = Math.Round(ledger.RealizedNetUsd, 8),
            MaxDrawdownUsd = Math.Round(ledger.MaxDrawdownUsd, 8),
            MaxDrawdownPercent = ledger.MaxNotionalUsd > 0 ? Math.Round(ledger.MaxDrawdownUsd / ledger.MaxNotionalUsd * 100m, 4) : 0m,
            MaxNotionalUsd = Math.Round(ledger.MaxNotionalUsd, 8),
            OpenPositionsAtEnd = openCount,
            UnrealizedPnlAtEndUsd = Math.Round(Unrealized(lastPrice), 8),
            CompletedCycles = cycles,
            StartTime = candles[0].OpenTime,
            EndTime = candles[^1].CloseTime,
            PathCandlesProcessed = candles.Count
        };

        var days = Math.Max(1m, (decimal)(candles[^1].CloseTime - candles[0].OpenTime).TotalDays);
        result.Summary.Extras.Add(new SimSummaryExtra { Label = "Циклов в день", Value = Math.Round(cycles / days, 2).ToString("0.##") });
        result.Summary.Extras.Add(new SimSummaryExtra { Label = "Уровней", Value = levels.Count.ToString() });
        if (openCount > 0)
            result.Summary.Extras.Add(new SimSummaryExtra { Label = "Висят в монете", Value = $"{openCount} ур., {Math.Round(OpenNotional(), 2)}$" });

        return result;
    }
}
