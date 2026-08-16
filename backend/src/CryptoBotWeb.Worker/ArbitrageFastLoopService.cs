using System.Collections.Concurrent;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBotWeb.Worker;

/// <summary>
/// FuturesArbitrage runs here instead of in <see cref="TradingHostedService"/>'s 5s loop.
///
/// Arbitrage is the one strategy whose signal is a comparison between two venues that can appear
/// and vanish inside a couple of seconds, and since its quotes now come from websocket streams
/// (memory reads, no REST per tick), evaluating once a second costs the exchanges nothing.
/// Nothing else moved: every other strategy keeps its 5s cadence untouched.
///
/// Two properties this loop must hold, both learned from what a faster loop breaks:
///  - A bot never ticks twice concurrently. A tick that is still placing orders can easily outlive
///    one second, and a second tick joining it could open the same level twice.
///  - Faster ticking must not mean a faster ladder. The per-level spacing lives in the handler
///    (MinSecondsBetweenOpens) and is expressed in seconds precisely so this interval can change
///    without changing how aggressively the bot fills.
/// </summary>
public class ArbitrageFastLoopService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<ArbitrageFastLoopService> _logger;
    private readonly IServiceProvider _serviceProvider;

    // Strategy ids whose tick is still running. Not a lock — a busy bot simply skips this second.
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public ArbitrageFastLoopService(ILogger<ArbitrageFastLoopService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Arbitrage fast loop started ({Interval}s tick)", TickInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var strategies = await db.Strategies
                    .Include(s => s.Account).ThenInclude(a => a.AccountProxies).ThenInclude(ap => ap.Proxy)
                    .Where(s => s.Status == StrategyStatus.Running && s.Type == StrategyTypes.FuturesArbitrage)
                    .ToListAsync(stoppingToken);

                if (strategies.Count > 0)
                {
                    await Parallel.ForEachAsync(strategies,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 10,
                            CancellationToken = stoppingToken
                        },
                        async (strategy, ct) => await RunOneAsync(strategy, ct));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in arbitrage fast loop");
            }

            // Pace on a fixed period: a tick that took 700ms waits 300ms, not a full second, so the
            // cadence stays ~1s instead of drifting out with every slow tick.
            var remaining = TickInterval - (DateTime.UtcNow - startedAt);
            if (remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(remaining, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Arbitrage fast loop stopped");
    }

    private async Task RunOneAsync(Core.Entities.Strategy strategy, CancellationToken ct)
    {
        // Skip rather than queue: if the previous tick is still working, the market it would act on
        // has moved anyway, and the next second brings a fresh evaluation.
        if (!_inFlight.TryAdd(strategy.Id, 0)) return;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IExchangeServiceFactory>();
            var handler = scope.ServiceProvider.GetServices<IStrategyHandler>()
                .FirstOrDefault(h => h.StrategyType == StrategyTypes.FuturesArbitrage);

            if (handler == null)
            {
                _logger.LogWarning("No handler registered for {Type}", StrategyTypes.FuturesArbitrage);
                return;
            }

            using var exchange = factory.CreateFutures(strategy.Account);
            await handler.ProcessAsync(strategy, exchange, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing arbitrage strategy {Id} ({Name})", strategy.Id, strategy.Name);
        }
        finally
        {
            _inFlight.TryRemove(strategy.Id, out _);
        }
    }
}
