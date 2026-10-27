using System.Collections.Concurrent;
using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Simulation;

/// <summary>
/// In-memory parameter-sweep jobs for the Tester (admin-only, dev-machine tool). One job at a
/// time: the price path is built once through <see cref="SimulationEngine"/>, then every
/// parameter combination re-runs the pure simulator on that shared path in parallel. Results
/// live in memory only — an API restart loses them, which matches how the Tester is used.
/// </summary>
public class OptimizationJobService
{
    // Finished jobs stay queryable (re-sorting the result table hits memory, not a re-run). With
    // sweeps up to ConfigJsonMutator.MaxCombinations summaries each, keep only a few.
    private const int MaxFinishedJobsKept = 3;

    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExchangeServiceFactory _exchangeFactory;
    private readonly ILogger<OptimizationJobService> _logger;
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly object _startLock = new();

    public OptimizationJobService(
        IServiceScopeFactory scopeFactory, IExchangeServiceFactory exchangeFactory,
        ILogger<OptimizationJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _exchangeFactory = exchangeFactory;
        _logger = logger;
    }

    private sealed class Job
    {
        public Guid Id { get; } = Guid.NewGuid();
        public volatile string Status = "Queued";
        public int Total;
        public int Completed; // Interlocked
        public string? Error;
        public SimulationHistoryStats? History;
        public List<string> Warnings = new();
        public OptimizationComboResult[]? Results;
        public int OutOfSamplePercent;
        public DateTime? InSampleFrom, InSampleTo, OosFrom, OosTo;
        public readonly CancellationTokenSource Cts = new();
        public readonly DateTime StartedUtc = DateTime.UtcNow;
        public DateTime? FinishedUtc;
        public bool IsFinished => Status is "Done" or "Failed" or "Cancelled";
    }

    /// <summary>
    /// Validates the sweep and launches the background run. Throws <see cref="ArgumentException"/>
    /// on a bad sweep spec and <see cref="InvalidOperationException"/> when a job is already running.
    /// </summary>
    public OptimizationStartResponse Start(
        OptimizationRunRequest request, ExchangeAccount account)
    {
        var combos = ConfigJsonMutator.BuildCombinations(request.ConfigJson, request.Parameters);
        if (request.OutOfSamplePercent is < 0 or > 50)
            throw new ArgumentException("Доля проверочного периода (out-of-sample) должна быть от 0 до 50%.");

        Job job;
        lock (_startLock)
        {
            if (_jobs.Values.Any(j => !j.IsFinished))
                throw new InvalidOperationException("Оптимизация уже выполняется — дождитесь завершения или отмените её.");
            job = new Job { Total = combos.Count, OutOfSamplePercent = request.OutOfSamplePercent };
            _jobs[job.Id] = job;
            PruneFinished();
        }

        _ = Task.Run(() => RunJobAsync(job, request, account, combos));
        return new OptimizationStartResponse { JobId = job.Id, TotalCombinations = combos.Count };
    }

    /// <summary>
    /// Job progress plus, once Done, the successful combinations sorted by <paramref name="sortBy"/>
    /// (a SimulationRunSummary metric name such as "netPnlUsd", the derived "pnlToDd", or
    /// "p:&lt;path&gt;" for a swept parameter) and cut to <paramref name="limit"/> rows.
    /// </summary>
    public OptimizationJobStatusDto? GetStatus(
        Guid jobId, string? sortBy = null, bool desc = true, int limit = DefaultPageSize, int minTrades = 0)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return null;

        var dto = new OptimizationJobStatusDto
        {
            JobId = job.Id,
            Status = job.Status,
            Completed = Volatile.Read(ref job.Completed),
            Total = job.Total,
            Error = job.Error,
            Warnings = job.Warnings,
            History = job.History,
            OutOfSamplePercent = job.OutOfSamplePercent,
            InSampleFromUtc = job.InSampleFrom,
            InSampleToUtc = job.InSampleTo,
            OutOfSampleFromUtc = job.OosFrom,
            OutOfSampleToUtc = job.OosTo,
            ElapsedSeconds = Math.Round(((job.FinishedUtc ?? DateTime.UtcNow) - job.StartedUtc).TotalSeconds, 1)
        };
        if (job.Status == "Done" && job.Results != null)
        {
            var ok = job.Results.Where(r => r.Summary != null).ToList();
            var failed = job.Results.Where(r => r.Summary == null).ToList();
            dto.OkCount = ok.Count;
            dto.FailedCount = failed.Count;
            dto.FirstError = failed.FirstOrDefault()?.Error;

            // Too few trades = no statistics: the filter applies to the in-sample run (and the
            // out-of-sample one when present), so a "winner" with 3 lucky fills doesn't top the table.
            if (minTrades > 0)
            {
                var kept = ok.Where(r => r.Summary!.TotalTrades >= minTrades
                                         && (r.OosSummary == null || r.OosSummary.TotalTrades >= minTrades)).ToList();
                dto.FilteredOutCount = ok.Count - kept.Count;
                ok = kept;
            }

            var metric = MetricSelector(string.IsNullOrWhiteSpace(sortBy) ? "netPnlUsd" : sortBy!);
            var ordered = desc ? ok.OrderByDescending(metric) : ok.OrderBy(metric);
            dto.Results = ordered.Take(Math.Clamp(limit, 1, MaxPageSize)).ToList();
        }
        return dto;
    }

    /// <summary>
    /// Sort key → metric. "p:&lt;path&gt;" sorts by a swept parameter, "oos:&lt;metric&gt;" by the
    /// out-of-sample summary, "minNetPnlUsd" by the worse of the two PnLs (a robustness rank:
    /// high only when the set made money both where it was fitted and where it wasn't).
    /// </summary>
    private static Func<OptimizationComboResult, decimal> MetricSelector(string sortBy)
    {
        if (sortBy.StartsWith("p:", StringComparison.Ordinal))
        {
            var path = sortBy[2..];
            return r => r.Parameters.TryGetValue(path, out var v) ? v : 0m;
        }
        if (sortBy.StartsWith("oos:", StringComparison.Ordinal))
        {
            var inner = SummaryMetric(sortBy[4..]);
            return r => r.OosSummary == null ? decimal.MinValue : inner(r.OosSummary);
        }
        if (sortBy.Equals("minNetPnlUsd", StringComparison.OrdinalIgnoreCase))
            return r => r.OosSummary == null ? r.Summary!.NetPnlUsd : Math.Min(r.Summary!.NetPnlUsd, r.OosSummary.NetPnlUsd);

        var m = SummaryMetric(sortBy);
        return r => m(r.Summary!);
    }

    private static Func<SimulationRunSummary, decimal> SummaryMetric(string name) =>
        name.ToLowerInvariant() switch
        {
            "pnltodd" => PnlToDd,
            "maxdrawdownusd" => s => s.MaxDrawdownUsd,
            "maxdrawdownpercent" => s => s.MaxDrawdownPercent,
            "winrate" => s => s.WinRate,
            "totaltrades" => s => s.TotalTrades,
            "feesusd" => s => s.FeesUsd,
            "grosspnlusd" => s => s.GrossPnlUsd,
            "fundingpnlusd" => s => s.FundingPnlUsd,
            "maxnotionalusd" => s => s.MaxNotionalUsd,
            "unrealizedpnlatendusd" => s => s.UnrealizedPnlAtEndUsd,
            "completedcycles" => s => s.CompletedCycles,
            "openpositionsatend" => s => s.OpenPositionsAtEnd,
            _ => s => s.NetPnlUsd,
        };

    // Same convention as the UI's PnL/DD column: no drawdown → +∞ for a profit, 0 otherwise.
    private static decimal PnlToDd(SimulationRunSummary s)
    {
        var dd = Math.Abs(s.MaxDrawdownUsd);
        if (dd < 0.000000001m) return s.NetPnlUsd > 0 ? decimal.MaxValue : 0m;
        return s.NetPnlUsd / dd;
    }

    /// <summary>Requests cancellation; the job transitions to Cancelled asynchronously.</summary>
    public bool Cancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return false;
        if (!job.IsFinished)
            job.Cts.Cancel();
        return true;
    }

    private async Task RunJobAsync(
        Job job, OptimizationRunRequest request, ExchangeAccount account,
        List<Dictionary<string, decimal>> combos)
    {
        var ct = job.Cts.Token;
        try
        {
            // The controller's request scope is gone by now — the job owns its own scope
            // (SimulationEngine → KlineHistoryCache → DbContext) and its own exchange clients.
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<SimulationEngine>();

            using var exchange = _exchangeFactory.CreateFutures(account);

            job.Status = "Downloading";
            var (baseCtx, stats, simulator) = await engine.BuildContextAsync(
                request, exchange, ct, account.ExchangeType);

            job.History = engine.BuildHistoryStats(request, stats, account.ExchangeType);
            job.Warnings = baseCtx.Warnings.ToList();

            // Out-of-sample split: the tail of the window is held out and replayed separately.
            // The OOS run starts cold at the boundary, so indicator strategies spend their warm-up
            // bars there without trading — a small, known bias toward fewer OOS trades.
            var (isCtx, oosCtx) = SplitContext(baseCtx, request.OutOfSamplePercent);
            if (oosCtx != null)
            {
                job.InSampleFrom = isCtx.PathCandles[0].OpenTime;
                job.InSampleTo = isCtx.PathCandles[^1].CloseTime;
                job.OosFrom = oosCtx.PathCandles[0].OpenTime;
                job.OosTo = oosCtx.PathCandles[^1].CloseTime;
                job.Warnings.Add(
                    $"Out-of-sample {request.OutOfSamplePercent}%: обучение {job.InSampleFrom:yyyy-MM-dd} → {job.InSampleTo:yyyy-MM-dd}, " +
                    $"проверка {job.OosFrom:yyyy-MM-dd} → {job.OosTo:yyyy-MM-dd}. Проверочный прогон стартует «с нуля» — " +
                    "индикаторные стратегии теряют первые бары на прогрев.");
            }
            job.Status = "Running";

            var results = new OptimizationComboResult[combos.Count];
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                CancellationToken = ct
            };

            Parallel.For(0, combos.Count, options, i =>
            {
                var entry = new OptimizationComboResult { Parameters = combos[i] };
                try
                {
                    var configJson = ConfigJsonMutator.Apply(request.ConfigJson, combos[i]);
                    simulator.ValidateConfig(configJson);

                    // Simulators are pure and the candle/funding lists are read-only for them,
                    // so combos share the heavy series and differ only in ConfigJson + Warnings.
                    entry.Summary = simulator.Run(WithConfig(isCtx, configJson)).Summary;
                    if (oosCtx != null)
                        entry.OosSummary = simulator.Run(WithConfig(oosCtx, configJson)).Summary;
                }
                catch (Exception ex)
                {
                    entry.Error = ex.Message;
                }
                results[i] = entry;
                Interlocked.Increment(ref job.Completed);
            });

            job.Results = results;
            job.Status = "Done";
            _logger.LogInformation("Optimization job {JobId}: {Total} combos in {Sec:0.0}s",
                job.Id, job.Total, (DateTime.UtcNow - job.StartedUtc).TotalSeconds);
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            job.Status = "Cancelled";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Optimization job {JobId} failed", job.Id);
            job.Error = ex.Message;
            job.Status = "Failed";
        }
        finally
        {
            job.FinishedUtc = DateTime.UtcNow;
        }
    }

    private static SimulationContext WithConfig(SimulationContext src, string configJson) => new()
    {
        StrategyType = src.StrategyType,
        Symbol = src.Symbol,
        ConfigJson = configJson,
        PathCandles = src.PathCandles,
        SecondSymbol = src.SecondSymbol,
        SecondSymbolPathCandles = src.SecondSymbolPathCandles,
        FundingEvents = src.FundingEvents,
        MakerFeeRate = src.MakerFeeRate,
        TakerFeeRate = src.TakerFeeRate,
        Warnings = new List<string>()
    };

    /// <summary>
    /// Cuts the price path (and the second series / funding events, by time) at the candle index
    /// that leaves <paramref name="oosPercent"/> of the bars after the boundary. Returns the base
    /// context unchanged and null when the split is off or either side would be too short.
    /// </summary>
    private static (SimulationContext InSample, SimulationContext? OutOfSample) SplitContext(
        SimulationContext baseCtx, int oosPercent)
    {
        if (oosPercent <= 0)
            return (baseCtx, null);

        var candles = baseCtx.PathCandles;
        var splitIdx = (int)Math.Round(candles.Count * (100 - oosPercent) / 100.0);
        const int minBars = 50;
        if (splitIdx < minBars || candles.Count - splitIdx < minBars)
            throw new ArgumentException(
                $"Слишком короткое окно для разбиения {100 - oosPercent}/{oosPercent}: нужно хотя бы {minBars} баров с каждой стороны.");

        var boundary = candles[splitIdx].OpenTime;

        SimulationContext Part(List<CandleDto> path, Func<DateTime, bool> inPart) => new()
        {
            StrategyType = baseCtx.StrategyType,
            Symbol = baseCtx.Symbol,
            ConfigJson = baseCtx.ConfigJson,
            PathCandles = path,
            SecondSymbol = baseCtx.SecondSymbol,
            SecondSymbolPathCandles = baseCtx.SecondSymbolPathCandles?.Where(c => inPart(c.OpenTime)).ToList(),
            FundingEvents = baseCtx.FundingEvents.Where(f => inPart(f.Timestamp)).ToList(),
            MakerFeeRate = baseCtx.MakerFeeRate,
            TakerFeeRate = baseCtx.TakerFeeRate,
            Warnings = new List<string>()
        };

        return (
            Part(candles.GetRange(0, splitIdx), t => t < boundary),
            Part(candles.GetRange(splitIdx, candles.Count - splitIdx), t => t >= boundary));
    }

    private void PruneFinished()
    {
        var stale = _jobs.Values.Where(j => j.IsFinished)
            .OrderByDescending(j => j.FinishedUtc ?? DateTime.MinValue)
            .Skip(MaxFinishedJobsKept)
            .ToList();
        foreach (var j in stale)
            _jobs.TryRemove(j.Id, out _);
    }
}
