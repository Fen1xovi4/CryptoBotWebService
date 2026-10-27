using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Simulation;

/// <summary>
/// Orchestrates a backtest: resolves the simulator for the requested strategy type,
/// downloads the price path at the requested timeframe (and funding history / second symbol
/// when needed), runs the simulator and post-fills chart candles. Simulators themselves are
/// pure — all I/O lives here.
/// </summary>
public class SimulationEngine
{
    private const int MaxWindowDays = 1461; // 4 years
    /// <summary>
    /// Chart candles per result. Large on purpose: the chart must show the modeled timeframe to
    /// verify fills (4 years of 15m ≈ 140k bars); lightweight-charts handles this and nginx gzips the JSON.
    /// </summary>
    private const int MaxChartCandles = 200_000;

    /// <summary>
    /// Cap on path candles per series, not on days: 1m stays limited to ~a year (525 600 bars),
    /// while 5m reaches 4 years and 15m+ cover any allowed window. Kept below the exchange
    /// clients' _rangeHardCap (600 000).
    /// </summary>
    private const int MaxPathCandles = 530_000;

    /// <summary>Path timeframes every supported exchange (Bybit/Bitget/BingX) can serve.</summary>
    private static readonly string[] AllowedPathTimeframes =
        { "1m", "3m", "5m", "15m", "30m", "1h", "4h", "6h", "12h", "1d" };

    private readonly IEnumerable<IStrategySimulator> _simulators;
    private readonly KlineHistoryCache? _klineCache;

    public SimulationEngine(IEnumerable<IStrategySimulator> simulators, KlineHistoryCache? klineCache = null)
    {
        _simulators = simulators;
        _klineCache = klineCache;
    }

    public IReadOnlyList<string> SupportedStrategyTypes =>
        _simulators.Select(s => s.StrategyType).ToList();

    /// <param name="exchange">Client of the account (request.AccountId).</param>
    /// <param name="exchangeType">Exchange of the account — the kline-cache key. Null disables caching.</param>
    public async Task<SimulationRunResult> RunAsync(
        SimulationRunRequest request, IFuturesExchangeService exchange,
        CancellationToken ct = default, ExchangeType? exchangeType = null)
    {
        var (ctx, stats, simulator) = await BuildContextAsync(request, exchange, ct, exchangeType);

        var result = simulator.Run(ctx);

        foreach (var w in ctx.Warnings.Where(w => !result.Warnings.Contains(w)))
            result.Warnings.Add(w);

        result.History = BuildHistoryStats(request, stats, exchangeType);

        if (result.ChartCandles.Count == 0)
            (result.ChartCandles, result.ChartTimeframe) =
                BuildChartCandles(ctx.PathCandles, request.ConfigJson, request.PathTimeframe, result.Warnings);

        return result;
    }

    /// <summary>
    /// Validates the request and builds a ready-to-run <see cref="SimulationContext"/> (price path,
    /// second series, funding history). Split out of <see cref="RunAsync"/> so the parameter
    /// optimizer can pay for the history I/O once and re-run the pure simulator per combination.
    /// </summary>
    public async Task<(SimulationContext Context, KlineHistoryCache.FetchStats Stats, IStrategySimulator Simulator)>
        BuildContextAsync(
            SimulationRunRequest request, IFuturesExchangeService exchange,
            CancellationToken ct = default, ExchangeType? exchangeType = null)
    {
        var simulator = _simulators.FirstOrDefault(s => s.StrategyType == request.StrategyType)
            ?? throw new InvalidOperationException(
                $"Нет симулятора для стратегии '{request.StrategyType}'. Доступны: {string.Join(", ", SupportedStrategyTypes)}");

        // Reject obviously broken configs before paying for the history download.
        simulator.ValidateConfig(request.ConfigJson);

        var pathTf = string.IsNullOrWhiteSpace(request.PathTimeframe)
            ? "1m"
            : request.PathTimeframe.Trim().ToLowerInvariant();
        if (!AllowedPathTimeframes.Contains(pathTf))
            throw new ArgumentException(
                $"Таймфрейм данных '{request.PathTimeframe}' не поддерживается. Доступны: {string.Join(", ", AllowedPathTimeframes)}.");
        var pathSpan = SymbolHelper.GetTimeframeSpan(pathTf);

        // The path must be at least as fine as the strategy's indicator timeframe —
        // CandleAggregator can only aggregate up, never split a bar.
        var strategyTf = ReadTimeframe(request.ConfigJson);
        if (strategyTf != null && pathSpan > SymbolHelper.GetTimeframeSpan(strategyTf))
            throw new ArgumentException(
                $"Таймфрейм данных ({pathTf}) грубее таймфрейма стратегии ({strategyTf}) — " +
                $"индикаторы построить нельзя. Выберите ТФ данных не крупнее {strategyTf}.");

        var to = request.ToUtc ?? DateTime.UtcNow;
        var from = request.FromUtc ?? to.AddDays(-Math.Clamp(request.Days, 1, MaxWindowDays));
        // Align the window start to the path-timeframe grid so repeated runs map onto the same
        // cached coverage ranges and the first bar of the window is a whole one.
        from = new DateTime(from.Ticks - from.Ticks % pathSpan.Ticks, DateTimeKind.Utc);
        if (from >= to)
            throw new ArgumentException("Начало периода должно быть раньше конца.");
        if ((to - from).TotalDays > MaxWindowDays)
            throw new ArgumentException($"Период симуляции ограничен {MaxWindowDays} днями (4 года).");

        var expectedBars = (to - from).Ticks / pathSpan.Ticks;
        if (expectedBars > MaxPathCandles)
            throw new ArgumentException(
                $"Окно {(to - from).TotalDays:0} дн. на таймфрейме {pathTf} — это ~{expectedBars:N0} баров " +
                $"(лимит {MaxPathCandles:N0}). Возьмите более крупный таймфрейм данных или сократите период.");

        var ctx = new SimulationContext
        {
            StrategyType = request.StrategyType,
            Symbol = request.Symbol,
            ConfigJson = request.ConfigJson,
            MakerFeeRate = request.MakerFeeRate ?? exchange.MakerFeeRate,
            TakerFeeRate = request.TakerFeeRate ?? exchange.TakerFeeRate
        };

        if (pathTf != "1m")
            ctx.Warnings.Add(
                $"Ценовой путь построен по барам {pathTf} (4 тика на бар) — " +
                "точность внутрибарных заполнений ниже, чем на 1m.");

        var stats = new KlineHistoryCache.FetchStats();
        ctx.PathCandles = await LoadKlinesAsync(exchange, exchangeType, request.Symbol, pathTf, from, to, request.BypassCache, stats, ct);
        if (ctx.PathCandles.Count == 0)
            throw new InvalidOperationException($"Биржа не вернула свечи по {request.Symbol} за запрошенный период.");

        if (!string.IsNullOrWhiteSpace(request.SecondSymbol))
        {
            ctx.SecondSymbol = request.SecondSymbol;
            ctx.SecondSymbolPathCandles =
                await LoadKlinesAsync(exchange, exchangeType, request.SecondSymbol, pathTf, from, to, request.BypassCache, stats, ct);
        }

        if (request.StrategyType == StrategyTypes.HuntingFunding)
        {
            // Funding strategies act in minute-sized windows around settlements; with coarse bars
            // (ticks every span/4) those windows can fall between ticks and entries get skipped.
            if (pathSpan > TimeSpan.FromMinutes(5))
                ctx.Warnings.Add(
                    $"Funding-стратегии чувствительны к ТФ данных: на {pathTf} окна проверки перед выплатой " +
                    "могут не попадать в тики и входы будут пропускаться. Рекомендуется 1m или 5m.");

            try
            {
                ctx.FundingEvents = await exchange.GetFundingHistoryAsync(request.Symbol, from, to, ct);
                if (ctx.FundingEvents.Count == 0)
                    ctx.Warnings.Add($"История funding по {request.Symbol} пуста — funding-события не моделируются.");
            }
            catch (NotSupportedException)
            {
                throw new InvalidOperationException(
                    "Эта биржа не поддерживает загрузку истории funding — симуляция funding-стратегий недоступна на выбранном аккаунте.");
            }
        }

        return (ctx, stats, simulator);
    }

    public SimulationHistoryStats BuildHistoryStats(
        SimulationRunRequest request, KlineHistoryCache.FetchStats stats, ExchangeType? exchangeType) => new()
    {
        CandlesFromCache = stats.FromCache,
        CandlesDownloaded = stats.Downloaded,
        GapsFilled = stats.GapsFilled,
        DownloadSeconds = Math.Round(stats.DownloadSeconds, 1),
        CacheReadSeconds = Math.Round(stats.CacheReadSeconds, 1),
        CacheUsed = _klineCache != null && !request.BypassCache && exchangeType.HasValue
    };

    /// <summary>
    /// Price path for one symbol at the requested timeframe: through the kline cache when we know
    /// the exchange (cache key) and the caller hasn't asked to bypass it; straight from the
    /// exchange otherwise.
    /// </summary>
    private async Task<List<CandleDto>> LoadKlinesAsync(
        IFuturesExchangeService service, ExchangeType? exchangeType, string symbol, string timeframe,
        DateTime from, DateTime to, bool bypassCache, KlineHistoryCache.FetchStats stats, CancellationToken ct)
    {
        if (_klineCache == null || bypassCache || !exchangeType.HasValue)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var candles = await service.GetKlinesRangeAsync(symbol, timeframe, from, to, ct);
            stats.Downloaded += candles.Count;
            stats.DownloadSeconds += sw.Elapsed.TotalSeconds;
            return candles;
        }

        return await _klineCache.GetOrDownloadAsync(
            exchangeType.Value, symbol, timeframe, from, to,
            (gapFrom, gapTo, token) => service.GetKlinesRangeAsync(symbol, timeframe, gapFrom, gapTo, token),
            stats, ct);
    }

    /// <summary>
    /// Aggregates the price path to the modeled timeframe for charting — the strategy's own
    /// timeframe, or the path timeframe for tick-driven strategies (grids/funding) — so fills can
    /// be checked bar by bar. Steps up to a coarser timeframe only when the window would exceed
    /// <see cref="MaxChartCandles"/>, with a warning. Never charts finer than the path itself.
    /// </summary>
    private static (List<CandleDto> Candles, string Timeframe) BuildChartCandles(
        List<CandleDto> path, string configJson, string pathTimeframe, List<string> warnings)
    {
        var pathTf = string.IsNullOrWhiteSpace(pathTimeframe) ? "1m" : pathTimeframe.Trim().ToLowerInvariant();
        var pathSpan = SymbolHelper.GetTimeframeSpan(pathTf);
        var modeledTf = ReadTimeframe(configJson)?.Trim().ToLowerInvariant() ?? pathTf;
        if (SymbolHelper.GetTimeframeSpan(modeledTf) < pathSpan) modeledTf = pathTf;

        var candles = CandleAggregator.Aggregate(path, modeledTf);
        if (candles.Count <= MaxChartCandles)
            return (candles, modeledTf);

        var modeledSpan = SymbolHelper.GetTimeframeSpan(modeledTf);
        var ladder = new[] { "5m", "15m", "30m", "1h", "4h", "1d" };
        foreach (var tf in ladder)
        {
            var span = SymbolHelper.GetTimeframeSpan(tf);
            if (span <= modeledSpan || span.Ticks % pathSpan.Ticks != 0) continue;
            candles = CandleAggregator.Aggregate(path, tf);
            if (candles.Count <= MaxChartCandles || tf == "1d")
            {
                warnings.Add(
                    $"График укрупнён до {tf}: на {modeledTf} окно дало бы больше {MaxChartCandles:N0} свечей. " +
                    $"Чтобы проверить сделки на {modeledTf}, сократите период.");
                return (candles, tf);
            }
        }

        return (candles, modeledTf);
    }

    private static string? ReadTimeframe(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("timeframe", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            }
        }
        catch (JsonException)
        {
            // malformed config — simulator will raise its own, clearer error
        }

        return null;
    }
}
