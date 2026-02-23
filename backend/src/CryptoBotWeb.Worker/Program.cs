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
builder.Services.AddSingleton<IExchangeServiceFactory, ExchangeServiceFactory>();

builder.Services.AddScoped<IStrategyHandler, EmaBounceHandler>();

builder.Services.AddHostedService<TradingHostedService>();

var host = builder.Build();
host.Run();
