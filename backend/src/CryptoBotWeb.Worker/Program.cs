using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using CryptoBotWeb.Infrastructure.Services;
using CryptoBotWeb.Infrastructure.Strategies;
using CryptoBotWeb.Worker;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

var encryptionKey = builder.Configuration["Encryption:Key"] ?? "default-encryption-key-change-me!";
builder.Services.AddSingleton<IEncryptionService>(new EncryptionService(encryptionKey));
builder.Services.AddSingleton<IProxyHealthTracker, ProxyHealthTracker>();
builder.Services.AddSingleton<IExchangeServiceFactory, ExchangeServiceFactory>();

// Websocket top-of-book streams. Singleton because the connections must outlive the per-tick
// REST clients; currently consumed by FuturesArbitrage only.
builder.Services.AddSingleton<IQuoteStreamService, QuoteStreamService>();

builder.Services.AddScoped<IStrategyHandler, EmaBounceHandler>();
builder.Services.AddScoped<IStrategyHandler, HuntingFundingHandler>();
builder.Services.AddScoped<IStrategyHandler, FundingClaimHandler>();
builder.Services.AddScoped<IStrategyHandler, SmaDcaHandler>();
builder.Services.AddScoped<IStrategyHandler, GridFloatHandler>();
builder.Services.AddScoped<IStrategyHandler, GridHedgeHandler>();
builder.Services.AddScoped<IStrategyHandler, SmartGridHedgeHandler>();
builder.Services.AddScoped<IStrategyHandler, ArbitrageHandler>();
builder.Services.AddScoped<ITelegramSignalService, TelegramSignalService>();
builder.Services.AddScoped<IFundingTickerRotationService, FundingTickerRotationService>();
builder.Services.AddScoped<ISymbolBlacklistService, SymbolBlacklistService>();

builder.Services.AddHttpClient("TronGrid");
builder.Services.AddHttpClient("Telegram");
builder.Services.AddHttpClient("BscRpc");
builder.Services.AddScoped<TronGridService>();
builder.Services.AddScoped<BscScanService>();

builder.Services.AddHostedService<TradingHostedService>();

// FuturesArbitrage only — 1s tick on top of the websocket quote cache. Excluded from
// TradingHostedService's query so it is never processed by both loops.
builder.Services.AddHostedService<CryptoBotWeb.Worker.ArbitrageFastLoopService>();
builder.Services.AddHostedService<PaymentVerificationService>();
builder.Services.AddHostedService<CryptoBotWeb.Worker.TelegramBotPollingService>();

var host = builder.Build();

// Migrations are applied by the API (DbSeeder.SeedAsync → MigrateAsync); the worker only reads the
// schema. Both containers start in parallel behind the same `depends_on: postgres healthy`, so on
// any deploy that carries a migration the worker used to query the OLD schema with the NEW model
// and throw 42703 "column ... does not exist" out of the trading loop until the API caught up.
// Gate startup on the schema being current — this runs before host.Run(), so no hosted service
// (trading loop, payment verification, Telegram polling) touches the DB until then.
await WaitForMigrationsAsync(host);

host.Run();

static async Task WaitForMigrationsAsync(IHost host)
{
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    var startedAt = DateTime.UtcNow;
    var pollInterval = TimeSpan.FromSeconds(2);
    // No timeout by design: running the trading loop against a stale schema is exactly the failure
    // we're removing, so idling (and saying so, loudly, once it stops looking like a normal deploy
    // window) beats starting anyway. Escalate to Error after this so a stuck API can't fail quietly.
    var escalateAfter = TimeSpan.FromMinutes(5);

    while (true)
    {
        var waited = DateTime.UtcNow - startedAt;
        try
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

            if (pending.Count == 0)
            {
                if (waited > TimeSpan.Zero)
                    logger.LogInformation("Schema is current — starting worker after waiting {Seconds:F1}s for migrations",
                        waited.TotalSeconds);
                return;
            }

            logger.Log(waited < escalateAfter ? LogLevel.Information : LogLevel.Error,
                "Waiting for the API to apply {Count} pending migration(s) ({Waited:F0}s so far): {Migrations}",
                pending.Count, waited.TotalSeconds, string.Join(", ", pending));
        }
        catch (Exception ex)
        {
            // Postgres not accepting connections yet, or the history table isn't readable — same
            // wait-and-retry contract.
            logger.Log(waited < escalateAfter ? LogLevel.Warning : LogLevel.Error, ex,
                "Migration check failed ({Waited:F0}s so far) — retrying", waited.TotalSeconds);
        }

        await Task.Delay(pollInterval);
    }
}
