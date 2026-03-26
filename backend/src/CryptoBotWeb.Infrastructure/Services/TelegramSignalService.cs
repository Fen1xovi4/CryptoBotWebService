using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Interfaces;
using CryptoBotWeb.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Services;

public class TelegramSignalService : ITelegramSignalService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramSignalService> _logger;

    public TelegramSignalService(AppDbContext db, IHttpClientFactory httpClientFactory,
        ILogger<TelegramSignalService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendOpenPositionSignalAsync(Strategy strategy, string symbol, string direction,
        decimal orderSize, decimal entryPrice, decimal takeProfit, decimal stopLoss,
        CancellationToken ct = default)
    {
        if (strategy.TelegramBotId == null)
            return;

        var bot = await _db.TelegramBots
            .AsNoTracking()
            .Include(b => b.Subscribers)
            .FirstOrDefaultAsync(b => b.Id == strategy.TelegramBotId && b.IsActive, ct);

        if (bot == null || bot.Subscribers.Count == 0)
            return;

        var isLong = direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var emoji = isLong ? "📈" : "📉";

        var message = new StringBuilder();
        message.AppendLine("🔔 <b>Open position</b>");
        message.AppendLine($"Ticker: <code>{symbol}</code>");
        message.AppendLine($"{(isLong ? "LONG" : "SHORT")} {emoji}");
        message.AppendLine($"Size: ~{orderSize} USDT");
        message.AppendLine($"Entry: <code>{entryPrice}</code>");
        message.AppendLine($"Take Profit: <code>{takeProfit}</code>");
        message.AppendLine($"Stop Loss: <code>{stopLoss}</code>");

        var text = message.ToString();

        foreach (var subscriber in bot.Subscribers)
        {
            try
            {
                await SendTelegramMessageAsync(bot.BotToken, subscriber.ChatId, text, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send TG signal to chat {ChatId} via bot {BotId}",
                    subscriber.ChatId, bot.Id);
            }
        }
    }

    private async Task SendTelegramMessageAsync(string botToken, long chatId, string text,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("Telegram");
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";

        var payload = new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            disable_web_page_preview = true,
        };

        var response = await client.PostAsJsonAsync(url, payload, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Telegram API error {Status}: {Body}", response.StatusCode, body);
        }
    }
}
