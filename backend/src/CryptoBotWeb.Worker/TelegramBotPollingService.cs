using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Worker;

public class TelegramBotPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramBotPollingService> _logger;

    private readonly ConcurrentDictionary<Guid, long> _offsets = new();

    public TelegramBotPollingService(IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramBotPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TelegramBotPollingService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllBotsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in TelegramBotPollingService loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task PollAllBotsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var bots = await db.TelegramBots
            .AsNoTracking()
            .Where(b => b.IsActive)
            .ToListAsync(ct);

        var tasks = bots.Select(bot => PollBotAsync(bot, db, ct));
        await Task.WhenAll(tasks);
    }

    private async Task PollBotAsync(TelegramBot bot, AppDbContext db, CancellationToken ct)
    {
        try
        {
            var offset = _offsets.GetValueOrDefault(bot.Id, 0);
            var client = _httpClientFactory.CreateClient("Telegram");
            var url = $"https://api.telegram.org/bot{bot.BotToken}/getUpdates?offset={offset}&timeout=1&allowed_updates=[\"message\"]";

            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadFromJsonAsync<TgUpdateResponse>(cancellationToken: ct);
            if (json?.Result == null || json.Result.Length == 0) return;

            foreach (var update in json.Result)
            {
                _offsets[bot.Id] = update.UpdateId + 1;

                var text = update.Message?.Text?.Trim();
                var chatId = update.Message?.Chat?.Id;
                if (chatId == null || string.IsNullOrEmpty(text)) continue;

                await HandleMessageAsync(bot, db, chatId.Value, update.Message!.Chat!.Username, text, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error polling TG bot {BotId} ({Name})", bot.Id, bot.Name);
        }
    }

    private async Task HandleMessageAsync(TelegramBot bot, AppDbContext db,
        long chatId, string? username, string text, CancellationToken ct)
    {
        if (text.StartsWith("/start"))
        {
            var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var password = parts.Length > 1 ? parts[1] : null;

            if (bot.Password != null && bot.Password != password)
            {
                await SendReplyAsync(bot.BotToken, chatId,
                    "🔒 This bot requires a password.\nSend: /start <password>", ct);
                return;
            }

            var existing = await db.TelegramSubscribers
                .FirstOrDefaultAsync(s => s.TelegramBotId == bot.Id && s.ChatId == chatId, ct);

            if (existing != null)
            {
                await SendReplyAsync(bot.BotToken, chatId,
                    "✅ You are already subscribed to signals.", ct);
                return;
            }

            db.TelegramSubscribers.Add(new TelegramSubscriber
            {
                TelegramBotId = bot.Id,
                ChatId = chatId,
                Username = username,
            });
            await db.SaveChangesAsync(ct);

            await SendReplyAsync(bot.BotToken, chatId,
                "✅ Subscribed! You will receive trading signals.", ct);
        }
        else if (text == "/stop" || text == "/unsubscribe")
        {
            var existing = await db.TelegramSubscribers
                .FirstOrDefaultAsync(s => s.TelegramBotId == bot.Id && s.ChatId == chatId, ct);

            if (existing != null)
            {
                db.TelegramSubscribers.Remove(existing);
                await db.SaveChangesAsync(ct);
                await SendReplyAsync(bot.BotToken, chatId,
                    "🔕 Unsubscribed from signals.", ct);
            }
            else
            {
                await SendReplyAsync(bot.BotToken, chatId,
                    "You are not subscribed.", ct);
            }
        }
    }

    private async Task SendReplyAsync(string botToken, long chatId, string text, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("Telegram");
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";

        await client.PostAsJsonAsync(url, new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
        }, ct);
    }

    private class TgUpdateResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("result")]
        public TgUpdate[]? Result { get; set; }
    }

    private class TgUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }

        [JsonPropertyName("message")]
        public TgMessage? Message { get; set; }
    }

    private class TgMessage
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("chat")]
        public TgChat? Chat { get; set; }
    }

    private class TgChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }
    }
}
