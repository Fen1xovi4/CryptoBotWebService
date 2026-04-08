using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
    private readonly ITelegramSignalService _telegramSignalService;

    public EmaBounceHandler(AppDbContext db, ILogger<EmaBounceHandler> logger,
        ITelegramSignalService telegramSignalService)
    {
        _db = db;
        _logger = logger;
        _telegramSignalService = telegramSignalService;
    }

    public async Task ProcessAsync(Strategy strategy, IFuturesExchangeService exchange, CancellationToken ct)
    {
        // Reload from DB to get freshest state (prevents overwriting API changes, e.g. manual close)
        await _db.Entry(strategy).ReloadAsync(ct);

        var config = JsonSerializer.Deserialize<EmaBounceConfig>(strategy.ConfigJson, JsonOptions);
        if (config == null || string.IsNullOrEmpty(config.Symbol))
        {
            _logger.LogError("Invalid config for strategy {Id}", strategy.Id);
            Log(strategy, "Error", "Invalid config — symbol is empty");
            return;
        }

        var state = JsonSerializer.Deserialize<EmaBounceState>(strategy.StateJson, JsonOptions)
                    ?? new EmaBounceState();

        // 1. Check TP/SL for open positions (runs every poll)
        var posLongBefore = state.OpenLong;
        var posShortBefore = state.OpenShort;
        await CheckTpSl(strategy, config, state, exchange, ct);

        // If a position was just closed, persist martingale state immediately
        // to prevent data loss on early returns or exceptions below
        var positionJustClosed = (posLongBefore != null && state.OpenLong == null)
                              || (posShortBefore != null && state.OpenShort == null);
        if (positionJustClosed)
        {
            SaveState(strategy, config, state);
            await _db.SaveChangesAsync(ct);
        }

        // 2. Check for new closed candle
        var isFirstRun = !state.LastProcessedCandleTime.HasValue;
        // Request 600 candles minimum so EMA has enough warm-up beyond the SMA seed
        // (for EMA200 need ~3x period; 600 covers up to EMA200 comfortably)
        var minCandles = 600;
        var needsCandles = isFirstRun
            ? Math.Max(config.IndicatorLength + config.CandleCount + 10, minCandles)
            : Math.Max(config.IndicatorLength + 10, minCandles);
        var candles = await exchange.GetKlinesAsync(config.Symbol, config.Timeframe, needsCandles);
        if (candles.Count < config.IndicatorLength)
        {
            _logger.LogWarning("Not enough candles ({Count}/{Need}) for strategy {Id}",
                candles.Count, config.IndicatorLength, strategy.Id);
            return;
        }

        var lastClosed = GetLastClosedCandle(candles, config.Timeframe);
        if (lastClosed == null) return;

        if (state.LastProcessedCandleTime.HasValue && lastClosed.CloseTime <= state.LastProcessedCandleTime.Value)
        {
            // No new closed candle — but do intrabar entry check on the currently forming candle
            await CheckIntrabarEntry(strategy, config, state, exchange, candles, ct);

            // Check for external close before saving
            if (await SyncIfClosedExternally(strategy, state, ct))
                return;
            SaveState(strategy, config, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // 3. New candle closed — process strategy logic
        state.LastProcessedCandleTime = lastClosed.CloseTime;

        var closePrices = candles.Select(c => c.Close).ToArray();
        var maValues = config.IndicatorType.Equals("SMA", StringComparison.OrdinalIgnoreCase)
            ? IndicatorCalculator.CalculateSma(closePrices, config.IndicatorLength)
            : IndicatorCalculator.CalculateEma(closePrices, config.IndicatorLength);

        var maValue = maValues[^1];
        if (maValue == 0)
        {
            if (await SyncIfClosedExternally(strategy, state, ct))
                return;
            SaveState(strategy, config, state);
            await _db.SaveChangesAsync(ct);
            return;
        }

        // On fresh start (no previous candle processed), recalculate counters from history
        if (isFirstRun)
        {
            InitializeCountersFromHistory(strategy, config, state, candles, maValues);
        }

        var ema = maValue;
        var offsetLong = ema + ema * config.OffsetPercent / 100m;
        var offsetShort = ema - ema * config.OffsetPercent / 100m;

        Log(strategy, "Info",
            $"Новая свеча: O={lastClosed.Open} H={lastClosed.High} L={lastClosed.Low} C={lastClosed.Close} | " +
            $"{config.IndicatorType}{config.IndicatorLength}={Math.Round(ema, 6)} | " +
            $"OffsetL={Math.Round(offsetLong, 6)} OffsetS={Math.Round(offsetShort, 6)}");

        _logger.LogDebug("Strategy {Id}: candle Low={Low} High={High} EMA={EMA} LongC={LC} ShortC={SC}",
            strategy.Id, lastClosed.Low, lastClosed.High, ema, state.LongCounter, state.ShortCounter);

        // 4. LONG logic (skip if OnlyShort)
        if (!config.OnlyShort)
            ProcessLong(strategy, config, state, lastClosed, ema);

        // 5. SHORT logic (skip if OnlyLong)
        if (!config.OnlyLong)
            ProcessShort(strategy, config, state, lastClosed, ema);

        // 6. Check LONG entry (skip if OnlyShort)
        var hadLongBefore = state.OpenLong != null;
        var hadShortBefore = state.OpenShort != null;

        if (!config.OnlyShort && ShouldOpenLong(config, state, lastClosed, ema))
            await OpenLong(strategy, config, state, exchange, ema, lastClosed, ct);

        // 7. Check SHORT entry (skip if OnlyLong)
        if (!config.OnlyLong && ShouldOpenShort(config, state, lastClosed, ema))
            await OpenShort(strategy, config, state, exchange, ema, lastClosed, ct);

        // 8. Save state — check for external close before saving,
        //    but skip sync if a position was just opened this cycle
        //    (DB doesn't have it yet, so sync would falsely detect "closed externally")
        var positionJustOpened = (!hadLongBefore && state.OpenLong != null)
                              || (!hadShortBefore && state.OpenShort != null);
        if (!positionJustOpened && await SyncIfClosedExternally(strategy, state, ct))
            return;
        SaveState(strategy, config, state);
        await _db.SaveChangesAsync(ct);
    }

    private void ProcessLong(Strategy strategy, EmaBounceConfig config, EmaBounceState state, CandleDto candle, decimal ema)
    {
        // Skip if waiting for next candle after position close
        if (state.WaitNextCandleAfterLongClose)
        {
            Log(strategy, "Info", "Long: пропуск свечи после закрытия позиции");
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
            {
                Log(strategy, "Info",
                    $"Long ВХОД: Low={candle.Low} <= Offset={Math.Round(offsetLine, 6)} при счётчике {state.LongCounter}/{config.CandleCount}");
                return; // Entry will handle this
            }
        }

        // Counter logic
        var prev = state.LongCounter;
        if (candle.Low > ema)
        {
            state.LongCounter++;
            Log(strategy, "Info", $"Long счётчик: {prev} → {state.LongCounter}/{config.CandleCount} (Low={candle.Low} > EMA={Math.Round(ema, 6)})");
        }
        else
        {
            state.LongCounter = 0;
            if (prev > 0)
                Log(strategy, "Info", $"Long счётчик СБРОС: {prev} → 0 (Low={candle.Low} <= EMA={Math.Round(ema, 6)})");
        }
    }

    private void ProcessShort(Strategy strategy, EmaBounceConfig config, EmaBounceState state, CandleDto candle, decimal ema)
    {
        if (state.WaitNextCandleAfterShortClose)
        {
            Log(strategy, "Info", "Short: пропуск свечи после закрытия позиции");
            state.WaitNextCandleAfterShortClose = false;
            return;
        }

        if (state.OpenShort != null || state.OpenLong != null) return;

        if (state.ShortCounter >= config.CandleCount)
        {
            var offsetLine = ema - ema * config.OffsetPercent / 100m;
            if (candle.High >= offsetLine)
            {
                Log(strategy, "Info",
                    $"Short ВХОД: High={candle.High} >= Offset={Math.Round(offsetLine, 6)} при счётчике {state.ShortCounter}/{config.CandleCount}");
                return; // Entry will handle this
            }
        }

        var prev = state.ShortCounter;
        if (candle.High < ema)
        {
            state.ShortCounter++;
            Log(strategy, "Info", $"Short счётчик: {prev} → {state.ShortCounter}/{config.CandleCount} (High={candle.High} < EMA={Math.Round(ema, 6)})");
        }
        else
        {
            state.ShortCounter = 0;
            if (prev > 0)
                Log(strategy, "Info", $"Short счётчик СБРОС: {prev} → 0 (High={candle.High} >= EMA={Math.Round(ema, 6)})");
        }
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

    public static (decimal orderSize, string reason) GetCurrentOrderSize(EmaBounceConfig config, EmaBounceState state)
    {
        if (!config.UseMartingale)
            return (config.OrderSize, "martingale=OFF");

        var baseSize = config.OrderSize;

        if (config.UseDrawdownScale && config.DrawdownBalance > 0)
        {
            var drawdownThreshold = config.DrawdownBalance * config.DrawdownPercent / 100m;
            var targetThreshold = config.DrawdownBalance * config.DrawdownTarget / 100m;

            if (state.RunningPnlDollar <= -drawdownThreshold)
            {
                var levels = (int)Math.Floor(-state.RunningPnlDollar / drawdownThreshold);
                baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, levels);
                return (Math.Round(baseSize, 2),
                    $"drawdown: pnl=${Math.Round(state.RunningPnlDollar, 2)}, threshold=${Math.Round(drawdownThreshold, 2)}, levels={levels}, coeff={config.MartingaleCoeff}");
            }

            if (state.RunningPnlDollar >= targetThreshold)
                state.RunningPnlDollar = 0;

            return (Math.Round(baseSize, 2),
                $"drawdown: pnl=${Math.Round(state.RunningPnlDollar, 2)}, threshold=${Math.Round(drawdownThreshold, 2)} — не достигнут, losses={state.ConsecutiveLosses}");
        }

        if (state.ConsecutiveLosses > 0)
        {
            if (config.UseSteppedMartingale && config.MartingaleStep > 0)
            {
                var steps = state.ConsecutiveLosses / config.MartingaleStep;
                if (steps > 0)
                    baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, steps);
                return (Math.Round(baseSize, 2),
                    $"stepped: losses={state.ConsecutiveLosses}, step={config.MartingaleStep}, steps={steps}, coeff={config.MartingaleCoeff}");
            }

            baseSize *= (decimal)Math.Pow((double)config.MartingaleCoeff, state.ConsecutiveLosses);
            return (Math.Round(baseSize, 2),
                $"classic: losses={state.ConsecutiveLosses}, coeff={config.MartingaleCoeff}");
        }

        return (Math.Round(baseSize, 2), $"base: losses=0");
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
        var (orderSize, sizeReason) = GetCurrentOrderSize(config, state);

        Log(strategy, "Info", $"Открытие LONG: {config.Symbol}, orderSize={orderSize} ({sizeReason}), base={config.OrderSize}, EMA={Math.Round(ema, 6)}, Low={candle.Low}");
        _logger.LogInformation("Strategy {Id}: Opening LONG on {Symbol} at EMA={EMA}, candle.Low={Low}, orderSize={OrderSize} ({Reason})",
            strategy.Id, config.Symbol, ema, candle.Low, orderSize, sizeReason);

        var result = await exchange.OpenLongAsync(config.Symbol, orderSize);
        if (!result.Success)
        {
            Log(strategy, "Error", $"Ошибка открытия LONG: {result.ErrorMessage}");
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

        Log(strategy, "Info",
            $"LONG открыт: цена={entryPrice}, qty={state.OpenLong.Quantity}, TP={Math.Round(state.OpenLong.TakeProfit, 6)}, SL={Math.Round(state.OpenLong.StopLoss, 6)}");
        _logger.LogInformation("Strategy {Id}: LONG opened at {Price}, TP={TP}, SL={SL}, OrderSize={OrderSize}",
            strategy.Id, entryPrice, state.OpenLong.TakeProfit, state.OpenLong.StopLoss, orderSize);

        await _telegramSignalService.SendOpenPositionSignalAsync(strategy, config.Symbol, "Long",
            orderSize, entryPrice, state.OpenLong.TakeProfit, state.OpenLong.StopLoss, ct);
    }

    private async Task OpenShort(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal ema, CandleDto candle, CancellationToken ct)
    {
        var (orderSize, sizeReason) = GetCurrentOrderSize(config, state);

        Log(strategy, "Info", $"Открытие SHORT: {config.Symbol}, orderSize={orderSize} ({sizeReason}), base={config.OrderSize}, EMA={Math.Round(ema, 6)}, High={candle.High}");
        _logger.LogInformation("Strategy {Id}: Opening SHORT on {Symbol} at EMA={EMA}, candle.High={High}, orderSize={OrderSize} ({Reason})",
            strategy.Id, config.Symbol, ema, candle.High, orderSize, sizeReason);

        var result = await exchange.OpenShortAsync(config.Symbol, orderSize);
        if (!result.Success)
        {
            Log(strategy, "Error", $"Ошибка открытия SHORT: {result.ErrorMessage}");
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

        Log(strategy, "Info",
            $"SHORT открыт: цена={entryPrice}, qty={state.OpenShort.Quantity}, TP={Math.Round(state.OpenShort.TakeProfit, 6)}, SL={Math.Round(state.OpenShort.StopLoss, 6)}");
        _logger.LogInformation("Strategy {Id}: SHORT opened at {Price}, TP={TP}, SL={SL}, OrderSize={OrderSize}",
            strategy.Id, entryPrice, state.OpenShort.TakeProfit, state.OpenShort.StopLoss, orderSize);

        await _telegramSignalService.SendOpenPositionSignalAsync(strategy, config.Symbol, "Short",
            orderSize, entryPrice, state.OpenShort.TakeProfit, state.OpenShort.StopLoss, ct);
    }

    private async Task CheckTpSl(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, CancellationToken ct)
    {
        if (state.OpenLong == null && state.OpenShort == null)
            return;

        var currentPrice = await exchange.GetTickerPriceAsync(config.Symbol);
        if (currentPrice == null) return;

        state.LastPrice = currentPrice;

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
            Log(strategy, "Error", $"Ошибка закрытия LONG: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: Failed to close LONG: {Error}", strategy.Id, result.ErrorMessage);

            // Position already closed on exchange side — check DB for correct state
            if (result.ErrorMessage != null && result.ErrorMessage.Contains("No position", StringComparison.OrdinalIgnoreCase))
            {
                // Check if API already handled the close (manual close) with correct PnL
                var dbValues = await _db.Entry(strategy).GetDatabaseValuesAsync(ct);
                var dbStateJson = dbValues?.GetValue<string>(nameof(Strategy.StateJson));
                EmaBounceState? dbState = null;
                if (!string.IsNullOrEmpty(dbStateJson))
                    dbState = JsonSerializer.Deserialize<EmaBounceState>(dbStateJson, JsonOptions);

                if (dbState?.OpenLong == null)
                {
                    // DB confirms position is closed — adopt DB's martingale state (correct PnL)
                    state.OpenLong = null;
                    state.LastPrice = null;
                    state.LongCounter = 0;
                    state.WaitNextCandleAfterLongClose = true;
                    state.ConsecutiveLosses = dbState!.ConsecutiveLosses;
                    state.RunningPnlDollar = dbState.RunningPnlDollar;
                    Log(strategy, "Info", "LONG позиция закрыта извне — state синхронизирован из БД");
                }
                else
                {
                    // DB still shows position open — estimate PnL as fallback
                    var estPnlPercent = (closePrice - position.EntryPrice) / position.EntryPrice * 100m;
                    var estPnlDollar = position.OrderSize * estPnlPercent / 100m;
                    var estCommission = position.OrderSize * 2m * 0.0005m;
                    UpdateMartingaleState(config, state, estPnlPercent, position.OrderSize);
                    RecordTrade(strategy, config.Symbol, "Sell", position.Quantity, closePrice, null, reason + " (exchange-closed)",
                        pnlDollar: estPnlDollar - estCommission, commission: estCommission);
                    state.OpenLong = null;
                    state.LastPrice = null;
                    state.LongCounter = 0;
                    state.WaitNextCandleAfterLongClose = true;
                    Log(strategy, "Warning", $"LONG позиция уже закрыта на бирже — state очищен (примерный PnL={Math.Round(estPnlPercent, 4)}%)");
                }
            }
            return;
        }

        var pnlPercent = (closePrice - position.EntryPrice) / position.EntryPrice * 100m;
        UpdateMartingaleState(config, state, pnlPercent, position.OrderSize);

        var pnlDollar = position.OrderSize * pnlPercent / 100m;
        // Taker fee ~0.05% per side, round-trip = orderSize * 2 * 0.0005
        var commission = position.OrderSize * 2m * 0.0005m;
        var netPnl = pnlDollar - commission;

        RecordTrade(strategy, config.Symbol, "Sell", position.Quantity, closePrice, result.OrderId, reason,
            pnlDollar: netPnl, commission: commission);
        state.OpenLong = null;
        state.LastPrice = null;
        state.LongCounter = 0;
        state.WaitNextCandleAfterLongClose = true;

        var (nextSize, nextReason) = GetCurrentOrderSize(config, state);
        Log(strategy, reason == "TakeProfit" ? "Info" : "Warning",
            $"LONG закрыт ({reason}): цена={closePrice}, вход={position.EntryPrice}, PnL={Math.Round(pnlPercent, 4)}% (${Math.Round(netPnl, 2)}, комиссия=${Math.Round(commission, 2)})");
        Log(strategy, "Info",
            $"Мартингейл: losses={state.ConsecutiveLosses}, runningPnl=${Math.Round(state.RunningPnlDollar, 2)}, следующая ставка={nextSize} ({nextReason})");
        _logger.LogInformation("Strategy {Id}: LONG closed ({Reason}) at {Price}, entry was {Entry}, PnL%={PnlPct}, NetPnl={NetPnl}, Commission={Commission}",
            strategy.Id, reason, closePrice, position.EntryPrice, Math.Round(pnlPercent, 4), Math.Round(netPnl, 2), Math.Round(commission, 2));
    }

    private async Task CloseShortPosition(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, decimal closePrice, string reason, CancellationToken ct)
    {
        var position = state.OpenShort!;
        var result = await exchange.CloseShortAsync(config.Symbol, position.Quantity);
        if (!result.Success)
        {
            Log(strategy, "Error", $"Ошибка закрытия SHORT: {result.ErrorMessage}");
            _logger.LogError("Strategy {Id}: Failed to close SHORT: {Error}", strategy.Id, result.ErrorMessage);

            // Position already closed on exchange side — check DB for correct state
            if (result.ErrorMessage != null && result.ErrorMessage.Contains("No position", StringComparison.OrdinalIgnoreCase))
            {
                // Check if API already handled the close (manual close) with correct PnL
                var dbValues = await _db.Entry(strategy).GetDatabaseValuesAsync(ct);
                var dbStateJson = dbValues?.GetValue<string>(nameof(Strategy.StateJson));
                EmaBounceState? dbState = null;
                if (!string.IsNullOrEmpty(dbStateJson))
                    dbState = JsonSerializer.Deserialize<EmaBounceState>(dbStateJson, JsonOptions);

                if (dbState?.OpenShort == null)
                {
                    // DB confirms position is closed — adopt DB's martingale state (correct PnL)
                    state.OpenShort = null;
                    state.LastPrice = null;
                    state.ShortCounter = 0;
                    state.WaitNextCandleAfterShortClose = true;
                    state.ConsecutiveLosses = dbState!.ConsecutiveLosses;
                    state.RunningPnlDollar = dbState.RunningPnlDollar;
                    Log(strategy, "Info", "SHORT позиция закрыта извне — state синхронизирован из БД");
                }
                else
                {
                    // DB still shows position open — estimate PnL as fallback
                    var estPnlPercent = (position.EntryPrice - closePrice) / position.EntryPrice * 100m;
                    var estPnlDollar = position.OrderSize * estPnlPercent / 100m;
                    var estCommission = position.OrderSize * 2m * 0.0005m;
                    UpdateMartingaleState(config, state, estPnlPercent, position.OrderSize);
                    RecordTrade(strategy, config.Symbol, "Buy", position.Quantity, closePrice, null, reason + " (exchange-closed)",
                        pnlDollar: estPnlDollar - estCommission, commission: estCommission);
                    state.OpenShort = null;
                    state.LastPrice = null;
                    state.ShortCounter = 0;
                    state.WaitNextCandleAfterShortClose = true;
                    Log(strategy, "Warning", $"SHORT позиция уже закрыта на бирже — state очищен (примерный PnL={Math.Round(estPnlPercent, 4)}%)");
                }
            }
            return;
        }

        var pnlPercent = (position.EntryPrice - closePrice) / position.EntryPrice * 100m;
        UpdateMartingaleState(config, state, pnlPercent, position.OrderSize);

        var pnlDollar = position.OrderSize * pnlPercent / 100m;
        var commission = position.OrderSize * 2m * 0.0005m;
        var netPnl = pnlDollar - commission;

        RecordTrade(strategy, config.Symbol, "Buy", position.Quantity, closePrice, result.OrderId, reason,
            pnlDollar: netPnl, commission: commission);
        state.OpenShort = null;
        state.LastPrice = null;
        state.ShortCounter = 0;
        state.WaitNextCandleAfterShortClose = true;

        var (nextSize, nextReason) = GetCurrentOrderSize(config, state);
        Log(strategy, reason == "TakeProfit" ? "Info" : "Warning",
            $"SHORT закрыт ({reason}): цена={closePrice}, вход={position.EntryPrice}, PnL={Math.Round(pnlPercent, 4)}% (${Math.Round(netPnl, 2)}, комиссия=${Math.Round(commission, 2)})");
        Log(strategy, "Info",
            $"Мартингейл: losses={state.ConsecutiveLosses}, runningPnl=${Math.Round(state.RunningPnlDollar, 2)}, следующая ставка={nextSize} ({nextReason})");
        _logger.LogInformation("Strategy {Id}: SHORT closed ({Reason}) at {Price}, entry was {Entry}, PnL%={PnlPct}, NetPnl={NetPnl}, Commission={Commission}",
            strategy.Id, reason, closePrice, position.EntryPrice, Math.Round(pnlPercent, 4), Math.Round(netPnl, 2), Math.Round(commission, 2));
    }

    private void RecordTrade(Strategy strategy, string symbol, string side, decimal quantity, decimal price,
        string? orderId, string? status = null, decimal? pnlDollar = null, decimal? commission = null)
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
            ExecutedAt = DateTime.UtcNow,
            PnlDollar = pnlDollar,
            Commission = commission
        });
    }

    private void InitializeCountersFromHistory(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        List<CandleDto> candles, decimal[] maValues)
    {
        var now = DateTime.UtcNow;

        // Find index of the last closed candle (the one we're about to process)
        int lastClosedIdx = -1;
        for (int i = candles.Count - 1; i >= 0; i--)
        {
            if (candles[i].CloseTime <= now)
            {
                lastClosedIdx = i;
                break;
            }
        }

        if (lastClosedIdx < 0) return;

        // Scan backward from the candle BEFORE the last closed one
        // (the last closed candle will be processed normally by ProcessLong/ProcessShort)
        int longCounter = 0;
        int shortCounter = 0;

        for (int i = lastClosedIdx - 1; i >= config.IndicatorLength - 1; i--)
        {
            if (maValues[i] == 0) break;

            bool longOk = candles[i].Low > maValues[i];
            bool shortOk = candles[i].High < maValues[i];

            // Long: count consecutive candles with Low > EMA
            if (longOk && longCounter == (lastClosedIdx - 1 - i))
                longCounter++;

            // Short: count consecutive candles with High < EMA
            if (shortOk && shortCounter == (lastClosedIdx - 1 - i))
                shortCounter++;

            // Stop if both chains are broken
            if (longCounter < (lastClosedIdx - i) && shortCounter < (lastClosedIdx - i))
                break;
        }

        state.LongCounter = longCounter;
        state.ShortCounter = shortCounter;

        Log(strategy, "Info",
            $"Инициализация счётчиков из истории: Long={longCounter}/{config.CandleCount}, Short={shortCounter}/{config.CandleCount}");
        _logger.LogInformation("Strategy {Id}: Initialized counters from history — Long={LC}, Short={SC}",
            strategy.Id, longCounter, shortCounter);
    }

    // Minimum seconds between intrabar entry attempts, to prevent cascade retries on failed orders.
    private const int IntrabarCooldownSeconds = 30;

    /// <summary>
    /// Checks the currently forming (unclosed) candle for an entry trigger when the counter is already armed.
    /// Counter logic itself stays bar-close based — this only fires entries intrabar so we don't wait
    /// up to a full timeframe after the price touches the offset line.
    /// </summary>
    private async Task CheckIntrabarEntry(Strategy strategy, EmaBounceConfig config, EmaBounceState state,
        IFuturesExchangeService exchange, List<CandleDto> candles, CancellationToken ct)
    {
        // Nothing to do if no side is armed
        var longArmed = !config.OnlyShort && state.LongCounter >= config.CandleCount
                        && !state.WaitNextCandleAfterLongClose;
        var shortArmed = !config.OnlyLong && state.ShortCounter >= config.CandleCount
                         && !state.WaitNextCandleAfterShortClose;
        if (!longArmed && !shortArmed) return;

        // Skip if any position already open (in-memory state)
        if (state.OpenLong != null || state.OpenShort != null) return;

        // Cooldown between attempts
        if (state.LastIntrabarAttemptAt.HasValue)
        {
            var elapsed = (DateTime.UtcNow - state.LastIntrabarAttemptAt.Value).TotalSeconds;
            if (elapsed < IntrabarCooldownSeconds) return;
        }

        // Find the currently forming (unclosed) candle — should be the last item in the list
        var now = DateTime.UtcNow;
        CandleDto? forming = null;
        if (candles.Count > 0 && candles[^1].CloseTime > now)
            forming = candles[^1];
        if (forming == null) return; // no intrabar data available

        // Compute EMA from closed candles only (identical to main flow semantics)
        var closedCloses = new List<decimal>(candles.Count);
        foreach (var c in candles)
        {
            if (c.CloseTime <= now) closedCloses.Add(c.Close);
        }
        if (closedCloses.Count < config.IndicatorLength) return;

        var closeArr = closedCloses.ToArray();
        var maValues = config.IndicatorType.Equals("SMA", StringComparison.OrdinalIgnoreCase)
            ? IndicatorCalculator.CalculateSma(closeArr, config.IndicatorLength)
            : IndicatorCalculator.CalculateEma(closeArr, config.IndicatorLength);
        var ema = maValues[^1];
        if (ema == 0) return;

        var offsetLong = ema + ema * config.OffsetPercent / 100m;
        var offsetShort = ema - ema * config.OffsetPercent / 100m;

        // Update LastPrice for UI progress (cheap — we already have it)
        state.LastPrice = forming.Close;

        var longTouched = longArmed && forming.Low <= offsetLong;
        var shortTouched = shortArmed && forming.High >= offsetShort;
        if (!longTouched && !shortTouched) return;

        // Before firing the order, make sure there is no open position on the exchange for this symbol.
        // Guards against the edge case where a previous failed order actually went through.
        if (await HasExchangePositionAsync(exchange, config.Symbol, ct))
        {
            Log(strategy, "Warning",
                $"Интрабар вход отменён: на бирже уже есть открытая позиция по {config.Symbol}. Пропускаем до синхронизации.");
            state.LastIntrabarAttemptAt = DateTime.UtcNow; // enforce cooldown before re-checking
            return;
        }

        state.LastIntrabarAttemptAt = DateTime.UtcNow;

        if (longTouched)
        {
            Log(strategy, "Info",
                $"Long ИНТРАБАР ВХОД: Low={forming.Low} <= Offset={Math.Round(offsetLong, 6)} при счётчике {state.LongCounter}/{config.CandleCount} (свеча не закрыта)");
            await OpenLong(strategy, config, state, exchange, ema, forming, ct);
            return;
        }

        if (shortTouched)
        {
            Log(strategy, "Info",
                $"Short ИНТРАБАР ВХОД: High={forming.High} >= Offset={Math.Round(offsetShort, 6)} при счётчике {state.ShortCounter}/{config.CandleCount} (свеча не закрыта)");
            await OpenShort(strategy, config, state, exchange, ema, forming, ct);
        }
    }

    /// <summary>
    /// Returns true if the exchange reports any open futures position for the symbol (either side).
    /// Falls back to false (proceed) if the exchange doesn't implement GetPositionAsync.
    /// </summary>
    private async Task<bool> HasExchangePositionAsync(IFuturesExchangeService exchange, string symbol, CancellationToken ct)
    {
        try
        {
            var longPos = await exchange.GetPositionAsync(symbol, "Long");
            if (longPos != null && longPos.Quantity > 0) return true;
            var shortPos = await exchange.GetPositionAsync(symbol, "Short");
            if (shortPos != null && shortPos.Quantity > 0) return true;
            return false;
        }
        catch (NotSupportedException)
        {
            // Exchange doesn't implement position query — proceed without verification
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetPositionAsync failed for {Symbol} — assuming no position", symbol);
            return false;
        }
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

    /// <summary>
    /// Detects if a position was closed externally (e.g. manual close via API).
    /// If yes, reloads entity from DB (preserving correct PnL/martingale) and saves logs.
    /// Returns true if caller should return immediately.
    /// </summary>
    private async Task<bool> SyncIfClosedExternally(Strategy strategy, EmaBounceState state, CancellationToken ct)
    {
        if (state.OpenLong == null && state.OpenShort == null)
            return false;

        var dbValues = await _db.Entry(strategy).GetDatabaseValuesAsync(ct);
        if (dbValues == null) return false;

        var dbStateJson = dbValues.GetValue<string>(nameof(Strategy.StateJson));
        if (string.IsNullOrEmpty(dbStateJson)) return false;

        var dbState = JsonSerializer.Deserialize<EmaBounceState>(dbStateJson, JsonOptions);
        if (dbState == null) return false;

        var longClosedExternally = state.OpenLong != null && dbState.OpenLong == null;
        var shortClosedExternally = state.OpenShort != null && dbState.OpenShort == null;

        if (!longClosedExternally && !shortClosedExternally)
            return false;

        _logger.LogInformation("Strategy {Id}: position closed externally — syncing state from DB", strategy.Id);
        Log(strategy, "Info", "Позиция закрыта извне (ручное закрытие) — состояние синхронизировано из БД");

        // Reload entity to adopt the DB state (correct martingale values from API)
        await _db.Entry(strategy).ReloadAsync(ct);
        await _db.SaveChangesAsync(ct); // saves log entries
        return true;
    }

    private static void SaveState(Strategy strategy, EmaBounceConfig config, EmaBounceState state)
    {
        state.NextOrderSize = GetCurrentOrderSize(config, state).orderSize;
        strategy.StateJson = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private void Log(Strategy strategy, string level, string message)
    {
        _db.StrategyLogs.Add(new StrategyLog
        {
            Id = Guid.NewGuid(),
            StrategyId = strategy.Id,
            Level = level,
            Message = message,
            CreatedAt = DateTime.UtcNow
        });
    }
}
