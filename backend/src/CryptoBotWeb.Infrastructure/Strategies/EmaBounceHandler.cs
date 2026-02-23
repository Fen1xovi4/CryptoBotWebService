using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Strategies;

public class EmaBounceHandler : IStrategyHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string StrategyType => StrategyTypes.MaratG;

    private readonly AppDbContext _db;
    private readonly ILogger<EmaBounceHandler> _logger;

    public EmaBounceHandler(AppDbContext db, ILogger<EmaBounceHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<EmaBounceConfig>(strategy.ConfigJson, JsonOptions);
        if (config == null || string.IsNullOrEmpty(config.Symbol))
        {
            _logger.LogError("Invalid config for strategy {Id}", strategy.Id);
            return;
        }

        var state = JsonSerializer.Deserialize<EmaBounceState>(strategy.StateJson, JsonOptions)
                    ?? new EmaBounceState();

        // 1. Check TP/SL for open positions (runs every poll)
        await CheckTpSl(strategy, config, state, exchange, ct);

        // 2. Check for new closed candle
        var needsCandles = config.IndicatorLength + 10;
        var candles = await exchange.GetKlinesAsync(config.Symbol, config.Timeframe, needsCandles);
        if (candles.Count < config.IndicatorLength)
        {
            _logger.LogDebug("Not enough candles ({Count}/{Need}) for strategy {Id}",
                candles.Count, config.IndicatorLength, strategy.Id);
            return;
        }

        var lastClosed = GetLastClosedCandle(candles, config.Timeframe);
        if (lastClosed == null) return;

        if (state.LastProcessedCandleTime.HasValue && lastClosed.CloseTime <= state.LastProcessedCandleTime.Value)
        {
            // No new candle — save state from TP/SL checks and return
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // 3. New candle closed — process strategy logic
        state.LastProcessedCandleTime = lastClosed.CloseTime;

        var closePrices = candles.Select(c => c.Close).ToArray();
        var maValue = IndicatorCalculator.GetCurrentMa(closePrices, config.IndicatorType, config.IndicatorLength);
        if (maValue == null)
        {
            SaveState(strategy, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var ema = maValue.Value;

        _logger.LogDebug("Strategy {Id}: candle Low={Low} High={High} EMA={EMA} LongC={LC} ShortC={SC}",
            strategy.Id, lastClosed.Low, lastClosed.High, ema, state.LongCounter, state.ShortCounter);

        // 4. LONG logic (skip if OnlyShort)
        if (!config.OnlyShort)
            ProcessLong(config, state, lastClosed, ema);

        // 5. SHORT logic (skip if OnlyLong)
        if (!config.OnlyLong)
            ProcessShort(config, state, lastClosed, ema);

        // 6. Check LONG entry (skip if OnlyShort)
        if (!config.OnlyShort && ShouldOpenLong(config, state, lastClosed, ema))
            await OpenLong(strategy, config, state, exchange, ema, lastClosed, ct);

        // 7. Check SHORT entry (skip if OnlyLong)
        if (!config.OnlyLong && ShouldOpenShort(config, state, lastClosed, ema))
            await OpenShort(strategy, config, state, exchange, ema, lastClosed, ct);

        // 8. Save state
        SaveState(strategy, state);
        await _db.SaveChangesAsync(ct);
    }

    private void ProcessLong(EmaBounceConfig config, EmaBounceState state, CandleDto candle, decimal ema)
    {
        // Skip if waiting for next candle after position close
        if (state.WaitNextCandleAfterLongClose)
        {
            state.WaitNextCandleAfterLongClose = false;
            return;
        }

        // Skip if any position is open — counter paused
        if (state.OpenLong != null || state.OpenShort != null) return;

        // Don't update counter if entry will trigger (entry takes priority)
        if (state.LongCounter >= config.CandleCount)
        {
            var offsetLine = ema + ema * config.OffsetPercent / 100m;
            if (candle.Low <= offsetLine)
                return; // Entry will handle this
        }

        // Counter logic
        if (candle.Low > ema)
            state.LongCounter++;
        else
            state.LongCounter = 0;
    }

    private void ProcessShort(EmaBounceConfig config, EmaBounceState state, CandleDto candle, decimal ema)
    {
        if (state.WaitNextCandleAfterShortClose)
        {
            state.WaitNextCandleAfterShortClose = false;
            return;
        }

        if (state.OpenShort != null || state.OpenLong != null) return;

        if (state.ShortCounter >= config.CandleCount)
        {
            var offsetLine = ema - ema * config.OffsetPercent / 100m;
            if (candle.High >= offsetLine)
                return; // Entry will handle this
        }

        if (candle.High < ema)
            state.ShortCounter++;
        else
            state.ShortCounter = 0;
    }

    private static bool ShouldOpenLong(EmaBounceConfig config, EmaBounceState state, CandleDto candle, decimal ema)
    {
        if (state.OpenLong != null || state.OpenShort != null) return false;
        if (state.LongCounter < config.CandleCount) return false;

        var offsetLine = ema + ema * config.OffsetPercent / 100m;
        return candle.Low <= offsetLine;
    }

    private static bool ShouldOpenShort(EmaBounceConfig config, EmaBounceState state, CandleDto candle, decimal ema)
    {
        if (state.OpenShort != null || state.OpenLong != null) return false;
        if (state.ShortCounter < config.CandleCount) return false;

        var offsetLine = ema - ema * config.OffsetPercent / 100m;
        return candle.High >= offsetLine;
    }

    private static decimal GetCurrentOrderSize(EmaBounceConfig config, EmaBounceState state)
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

    private static void UpdateMartingaleState(EmaBounceConfig config, EmaBounceState state, decimal pnlPercent, decimal orderSize)
    {
        if (!config.UseMartingale) return;

        var pnlDollar = orderSize * pnlPercent / 100m;
        state.RunningPnlDollar += pnlDollar;

        if (pnlPercent > 0)
            state.ConsecutiveLosses = 0;
        else
            state.ConsecutiveLosses++;
    }

    private async Task OpenLong(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal ema, CandleDto candle, CancellationToken ct)
    {
        var orderSize = GetCurrentOrderSize(config, state);

        _logger.LogInformation("Strategy {Id}: Opening LONG on {Symbol} at EMA={EMA}, candle.Low={Low}, orderSize={OrderSize}",
            strategy.Id, config.Symbol, ema, candle.Low, orderSize);

        var result = await exchange.OpenLongAsync(config.Symbol, orderSize);
        if (!result.Success)
        {
            _logger.LogError("Strategy {Id}: Failed to open LONG: {Error}", strategy.Id, result.ErrorMessage);
            return;
        }

        var entryPrice = result.FilledPrice ?? candle.Close;
        state.OpenLong = new OpenPositionInfo
        {
            Direction = "Long",
            EntryPrice = entryPrice,
            Quantity = result.FilledQuantity ?? 0,
            OpenedAt = DateTime.UtcNow,
            TakeProfit = entryPrice * (1 + config.TakeProfitPercent / 100m),
            StopLoss = entryPrice * (1 - config.StopLossPercent / 100m),
            ExchangeOrderId = result.OrderId,
            OrderSize = orderSize
        };
        state.LongCounter = 0;

        RecordTrade(strategy, config.Symbol, "Buy", state.OpenLong.Quantity, entryPrice, result.OrderId);

        _logger.LogInformation("Strategy {Id}: LONG opened at {Price}, TP={TP}, SL={SL}, OrderSize={OrderSize}",
            strategy.Id, entryPrice, state.OpenLong.TakeProfit, state.OpenLong.StopLoss, orderSize);
    }

    private async Task OpenShort(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal ema, CandleDto candle, CancellationToken ct)
    {
        var orderSize = GetCurrentOrderSize(config, state);

        _logger.LogInformation("Strategy {Id}: Opening SHORT on {Symbol} at EMA={EMA}, candle.High={High}, orderSize={OrderSize}",
            strategy.Id, config.Symbol, ema, candle.High, orderSize);

        var result = await exchange.OpenShortAsync(config.Symbol, orderSize);
        if (!result.Success)
        {
            _logger.LogError("Strategy {Id}: Failed to open SHORT: {Error}", strategy.Id, result.ErrorMessage);
            return;
        }

        var entryPrice = result.FilledPrice ?? candle.Close;
        state.OpenShort = new OpenPositionInfo
        {
            Direction = "Short",
            EntryPrice = entryPrice,
            Quantity = result.FilledQuantity ?? 0,
            OpenedAt = DateTime.UtcNow,
            TakeProfit = entryPrice * (1 - config.TakeProfitPercent / 100m),
            StopLoss = entryPrice * (1 + config.StopLossPercent / 100m),
            ExchangeOrderId = result.OrderId,
            OrderSize = orderSize
        };
        state.ShortCounter = 0;

        RecordTrade(strategy, config.Symbol, "Sell", state.OpenShort.Quantity, entryPrice, result.OrderId);

        _logger.LogInformation("Strategy {Id}: SHORT opened at {Price}, TP={TP}, SL={SL}, OrderSize={OrderSize}",
            strategy.Id, entryPrice, state.OpenShort.TakeProfit, state.OpenShort.StopLoss, orderSize);
    }

    private async Task CheckTpSl(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        if (state.OpenLong == null && state.OpenShort == null)
            return;

        var currentPrice = await exchange.GetTickerPriceAsync(config.Symbol);
        if (currentPrice == null) return;

        // Check Long TP/SL
        if (state.OpenLong != null)
        {
            if (currentPrice >= state.OpenLong.TakeProfit)
            {
                _logger.LogInformation("Strategy {Id}: LONG TP hit at {Price} (TP={TP})",
                    strategy.Id, currentPrice, state.OpenLong.TakeProfit);
                await CloseLongPosition(strategy, config, state, exchange, currentPrice.Value, "TakeProfit", ct);
            }
            else if (currentPrice <= state.OpenLong.StopLoss)
            {
                _logger.LogInformation("Strategy {Id}: LONG SL hit at {Price} (SL={SL})",
                    strategy.Id, currentPrice, state.OpenLong.StopLoss);
                await CloseLongPosition(strategy, config, state, exchange, currentPrice.Value, "StopLoss", ct);
            }
        }

        // Check Short TP/SL
        if (state.OpenShort != null)
        {
            if (currentPrice <= state.OpenShort.TakeProfit)
            {
                _logger.LogInformation("Strategy {Id}: SHORT TP hit at {Price} (TP={TP})",
                    strategy.Id, currentPrice, state.OpenShort.TakeProfit);
                await CloseShortPosition(strategy, config, state, exchange, currentPrice.Value, "TakeProfit", ct);
            }
            else if (currentPrice >= state.OpenShort.StopLoss)
            {
                _logger.LogInformation("Strategy {Id}: SHORT SL hit at {Price} (SL={SL})",
                    strategy.Id, currentPrice, state.OpenShort.StopLoss);
                await CloseShortPosition(strategy, config, state, exchange, currentPrice.Value, "StopLoss", ct);
            }
        }
    }

    private async Task CloseLongPosition(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal closePrice, string reason, CancellationToken ct)
    {
        var position = state.OpenLong!;
        var result = await exchange.CloseLongAsync(config.Symbol, position.Quantity);
        if (!result.Success)
        {
            _logger.LogError("Strategy {Id}: Failed to close LONG: {Error}", strategy.Id, result.ErrorMessage);
            return;
        }

        var pnlPercent = (closePrice - position.EntryPrice) / position.EntryPrice * 100m;
        UpdateMartingaleState(config, state, pnlPercent, position.OrderSize);

        RecordTrade(strategy, config.Symbol, "Sell", position.Quantity, closePrice, result.OrderId, reason);
        state.OpenLong = null;
        state.LongCounter = 0;
        state.WaitNextCandleAfterLongClose = true;

        _logger.LogInformation("Strategy {Id}: LONG closed ({Reason}) at {Price}, entry was {Entry}, PnL%={PnlPct}, OrderSize={OrderSize}",
            strategy.Id, reason, closePrice, position.EntryPrice, Math.Round(pnlPercent, 4), position.OrderSize);
    }

    private async Task CloseShortPosition(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal closePrice, string reason, CancellationToken ct)
    {
        var position = state.OpenShort!;
        var result = await exchange.CloseShortAsync(config.Symbol, position.Quantity);
        if (!result.Success)
        {
            _logger.LogError("Strategy {Id}: Failed to close SHORT: {Error}", strategy.Id, result.ErrorMessage);
            return;
        }

        var pnlPercent = (position.EntryPrice - closePrice) / position.EntryPrice * 100m;
        UpdateMartingaleState(config, state, pnlPercent, position.OrderSize);

        RecordTrade(strategy, config.Symbol, "Buy", position.Quantity, closePrice, result.OrderId, reason);
        state.OpenShort = null;
        state.ShortCounter = 0;
        state.WaitNextCandleAfterShortClose = true;

        _logger.LogInformation("Strategy {Id}: SHORT closed ({Reason}) at {Price}, entry was {Entry}, PnL%={PnlPct}, OrderSize={OrderSize}",
            strategy.Id, reason, closePrice, position.EntryPrice, Math.Round(pnlPercent, 4), position.OrderSize);
    }

    private void RecordTrade(Strategy strategy, string symbol, string side, decimal quantity, decimal price,
        string? orderId, string? status = null)
    {
        _db.Trades.Add(new Trade
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            AccountId = strategy.AccountId,
            ExchangeOrderId = orderId ?? "",
            Symbol = symbol,
            Side = side,
            Quantity = quantity,
            Price = price,
            Status = status ?? "Filled",
            ExecutedAt = DateTime.UtcNow
        });
    }

    private static CandleDto? GetLastClosedCandle(List<CandleDto> candles, string timeframe)
    {
        var now = DateTime.UtcNow;
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            if (candles[i].CloseTime <= now)
                return candles[i];
        }
        return null;
    }

    private static void SaveState(Strategy strategy, EmaBounceState state)
    {
        strategy.StateJson = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
