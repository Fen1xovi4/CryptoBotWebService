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
    private const int MaxFinishedJobsKept = 5;

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
        OptimizationRunRequest request, ExchangeAccount account, ExchangeAccount? secondAccount)
    {
        var combos = ConfigJsonMutator.BuildCombinations(request.ConfigJson, request.Parameters);

        Job job;
        lock (_startLock)
        {
            if (_jobs.Values.Any(j => !j.IsFinished))
                throw new InvalidOperationException("Оптимизация уже выполняется — дождитесь завершения или отмените её.");
            job = new Job { Total = combos.Count };
            _jobs[job.Id] = job;
            PruneFinished();
        }

        _ = Task.Run(() => RunJobAsync(job, request, account, secondAccount, combos));
        return new OptimizationStartResponse { JobId = job.Id, TotalCombinations = combos.Count };
    }

    public OptimizationJobStatusDto? GetStatus(Guid jobId)
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
            ElapsedSeconds = Math.Round(((job.FinishedUtc ?? DateTime.UtcNow) - job.StartedUtc).TotalSeconds, 1)
        };
        if (job.Status == "Done" && job.Results != null)
            dto.Results = job.Results.ToList();
        return dto;
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
        Job job, OptimizationRunRequest request, ExchangeAccount account, ExchangeAccount? secondAccount,
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
            using var secondExchange = secondAccount != null ? _exchangeFactory.CreateFutures(secondAccount) : null;

            job.Status = "Downloading";
            var (baseCtx, stats, simulator) = await engine.BuildContextAsync(
                request, exchange, secondExchange, ct, account.ExchangeType, secondAccount?.ExchangeType);

            job.History = engine.BuildHistoryStats(request, stats, account.ExchangeType);
            job.Warnings = baseCtx.Warnings.ToList();
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
                    var runCtx = new SimulationContext
                    {
                        StrategyType = baseCtx.StrategyType,
                        Symbol = baseCtx.Symbol,
                        ConfigJson = configJson,
                        PathCandles = baseCtx.PathCandles,
                        SecondSymbol = baseCtx.SecondSymbol,
                        SecondSymbolPathCandles = baseCtx.SecondSymbolPathCandles,
                        FundingEvents = baseCtx.FundingEvents,
                        MakerFeeRate = baseCtx.MakerFeeRate,
                        TakerFeeRate = baseCtx.TakerFeeRate,
                        Warnings = new List<string>()
                    };
                    entry.Summary = simulator.Run(runCtx).Summary;
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
