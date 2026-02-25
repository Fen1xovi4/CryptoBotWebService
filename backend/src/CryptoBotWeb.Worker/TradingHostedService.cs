using CryptoBotWeb.Core.Enums;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoBotWeb.Worker;

public class TradingHostedService : BackgroundService
{
    private readonly ILogger<TradingHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;

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
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var factory = scope.ServiceProvider.GetRequiredService<IExchangeServiceFactory>();
                var handlers = scope.ServiceProvider.GetServices<IStrategyHandler>();

                var runningStrategies = await db.Strategies
                    .Include(s => s.Account).ThenInclude(a => a.Proxy)
                    .Where(s => s.Status == StrategyStatus.Running)
                    .ToListAsync(stoppingToken);

                foreach (var strategy in runningStrategies)
                {
                    try
                    {
                        var handler = handlers.FirstOrDefault(h => h.StrategyType == strategy.Type);
                        if (handler == null)
                        {
                            _logger.LogWarning("No handler for strategy type {Type}", strategy.Type);
                            continue;
                        }

                        using var exchange = factory.CreateFutures(strategy.Account);
                        await handler.ProcessAsync(strategy, exchange, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing strategy {Id} ({Name})",
                            strategy.Id, strategy.Name);
                    }
                }
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
