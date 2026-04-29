using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBotWeb.Worker;

public class TradingHostedService : BackgroundService
{
    private readonly ILogger<TradingHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private DateTime _lastRotationCheck = DateTime.MinValue;

    public TradingHostedService(ILogger<TradingHostedService> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Trading engine started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Funding ticker rotation at :50 of each hour
                var now = DateTime.UtcNow;
                if (now.Minute >= 50 && now.Minute < 55 &&
                    (now - _lastRotationCheck).TotalMinutes > 10)
                {
                    _lastRotationCheck = now;
                    try
                    {
                        using var rotationScope = _serviceProvider.CreateScope();
                        var rotationService = rotationScope.ServiceProvider
                            .GetRequiredService<IFundingTickerRotationService>();
                        await rotationService.RotateTickersAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in funding ticker rotation");
                    }
                }

                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var factory = scope.ServiceProvider.GetRequiredService<IExchangeServiceFactory>();
                var handlers = scope.ServiceProvider.GetServices<IStrategyHandler>();

                var runningStrategies = await db.Strategies
                    .Include(s => s.Account).ThenInclude(a => a.Proxy)
                    .Where(s => s.Status == StrategyStatus.Running)
                    .ToListAsync(stoppingToken);

                await Parallel.ForEachAsync(runningStrategies,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 20,
                        CancellationToken = stoppingToken
                    },
                    async (strategy, ct) =>
                    {
                        try
                        {
                            using var innerScope = _serviceProvider.CreateScope();
                            var innerFactory = innerScope.ServiceProvider.GetRequiredService<IExchangeServiceFactory>();
                            var innerHandlers = innerScope.ServiceProvider.GetServices<IStrategyHandler>();

                            var handler = innerHandlers.FirstOrDefault(h => h.StrategyType == strategy.Type);
                            if (handler == null)
                            {
                                _logger.LogWarning("No handler for strategy type {Type}", strategy.Type);
                                return;
                            }

                            // Dzengi requires accountId on signed calls; auto-fetch and persist if missing.
                            if (strategy.Account != null &&
                                strategy.Account.ExchangeType == ExchangeType.Dzengi &&
                                string.IsNullOrEmpty(strategy.Account.DzengiAccountId))
                            {
                                IExchangeService? spot = null;
                                try
                                {
                                    spot = innerFactory.Create(strategy.Account);
                                    if (spot is DzengiExchangeService dzengi)
                                    {
                                        var accountId = await dzengi.FetchPrimaryAccountIdAsync();
                                        if (string.IsNullOrEmpty(accountId))
                                        {
                                            _logger.LogWarning("Dzengi accountId fetch returned null for {AccountId}: {Error}",
                                                strategy.Account.Id, dzengi.LastFetchAccountIdError ?? "(no error)");
                                        }
                                        if (!string.IsNullOrEmpty(accountId))
                                        {
                                            var innerDb = innerScope.ServiceProvider.GetRequiredService<AppDbContext>();
                                            var tracked = await innerDb.ExchangeAccounts.FirstOrDefaultAsync(
                                                a => a.Id == strategy.Account.Id, ct);
                                            if (tracked != null)
                                            {
                                                tracked.DzengiAccountId = accountId;
                                                await innerDb.SaveChangesAsync(ct);
                                            }
                                            strategy.Account.DzengiAccountId = accountId;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to fetch Dzengi accountId for account {AccountId}",
                                        strategy.Account.Id);
                                }
                                finally
                                {
                                    (spot as IDisposable)?.Dispose();
                                }
                            }

                            using var exchange = innerFactory.CreateFutures(strategy.Account);
                            await handler.ProcessAsync(strategy, exchange, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing strategy {Id} ({Name})",
                                strategy.Id, strategy.Name);
                        }
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in trading loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        _logger.LogInformation("Trading engine stopped");
    }
}
