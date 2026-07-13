using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Helpers;
using CryptoBotWeb.Core.Interfaces;

namespace CryptoBotWeb.Infrastructure.Simulation;

/// <summary>
/// Orchestrates a backtest: resolves the simulator for the requested strategy type,
/// downloads the 1m price path (and funding history / second symbol when needed),
/// runs the simulator and post-fills chart candles. Simulators themselves are pure —
/// all I/O lives here.
/// </summary>
public class SimulationEngine
{
    private const int MaxWindowDays = 365;
    private const int MaxChartCandles = 3000;

    private readonly IEnumerable<IStrategySimulator> _simulators;

    public SimulationEngine(IEnumerable<IStrategySimulator> simulators)
    {
        _simulators = simulators;
    }

    public IReadOnlyList<string> SupportedStrategyTypes =>
        _simulators.Select(s => s.StrategyType).ToList();

    public async Task<SimulationRunResult> RunAsync(
        SimulationRunRequest request, IFuturesExchangeService exchange, CancellationToken ct = default)
    {
        var simulator = _simulators.FirstOrDefault(s => s.StrategyType == request.StrategyType)
            ?? throw new InvalidOperationException(
                $"Нет симулятора для стратегии '{request.StrategyType}'. Доступны: {string.Join(", ", SupportedStrategyTypes)}");

        var to = request.ToUtc ?? DateTime.UtcNow;
        var from = request.FromUtc ?? to.AddDays(-Math.Clamp(request.Days, 1, MaxWindowDays));
        if (from >= to)
            throw new ArgumentException("Начало периода должно быть раньше конца.");
        if ((to - from).TotalDays > MaxWindowDays)
            throw new ArgumentException($"Период симуляции ограничен {MaxWindowDays} днями.");

        var ctx = new SimulationContext
        {
            StrategyType = request.StrategyType,
            Symbol = request.Symbol,
            ConfigJson = request.ConfigJson,
            MakerFeeRate = request.MakerFeeRate ?? exchange.MakerFeeRate,
            TakerFeeRate = request.TakerFeeRate ?? exchange.TakerFeeRate
        };

        ctx.PathCandles = await exchange.GetKlinesRangeAsync(request.Symbol, "1m", from, to, ct);
        if (ctx.PathCandles.Count == 0)
            throw new InvalidOperationException($"Биржа не вернула свечи по {request.Symbol} за запрошенный период.");

        if (!string.IsNullOrWhiteSpace(request.SecondSymbol))
        {
            ctx.SecondSymbol = request.SecondSymbol;
            ctx.SecondSymbolPathCandles =
                await exchange.GetKlinesRangeAsync(request.SecondSymbol, "1m", from, to, ct);
        }

        if (request.StrategyType is StrategyTypes.HuntingFunding or StrategyTypes.FundingClaim)
        {
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

        var result = simulator.Run(ctx);

        foreach (var w in ctx.Warnings.Where(w => !result.Warnings.Contains(w)))
            result.Warnings.Add(w);

        if (result.ChartCandles.Count == 0)
            result.ChartCandles = BuildChartCandles(ctx.PathCandles, request.ConfigJson);

        return result;
    }

    /// <summary>
    /// Aggregates the 1m path to the strategy's configured timeframe for charting,
    /// stepping up to coarser timeframes when the window would produce too many candles.
    /// </summary>
    private static List<CandleDto> BuildChartCandles(List<CandleDto> path, string configJson)
    {
        var ladder = new[] { "1m", "5m", "15m", "30m", "1h", "4h", "1d" };
        var tf = ReadTimeframe(configJson) ?? "1h";

        var start = Array.IndexOf(ladder, tf.ToLowerInvariant());
        if (start < 0) start = Array.IndexOf(ladder, "1h");

        for (var i = start; i < ladder.Length; i++)
        {
            var candles = CandleAggregator.Aggregate(path, ladder[i]);
            if (candles.Count <= MaxChartCandles || i == ladder.Length - 1)
                return candles;
        }

        return CandleAggregator.Aggregate(path, "1d");
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
