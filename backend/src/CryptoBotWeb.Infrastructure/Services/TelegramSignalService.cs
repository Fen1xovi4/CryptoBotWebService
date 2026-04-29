using System.Globalization;
using System.Net.Http.Json;
using System.Text;
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
        decimal orderSize, decimal entryPrice, decimal takeProfit, decimal? stopLoss,
        CancellationToken ct = default)
    {
        var isLong = direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var emoji = isLong ? "📈" : "📉";

        var message = new StringBuilder();
        message.AppendLine("🔔 <b>Open position</b>");
        message.AppendLine($"Ticker: <code>{symbol}</code>");
        message.AppendLine($"{(isLong ? "LONG" : "SHORT")} {emoji}");
        message.AppendLine($"Size: ~{FormatUsd(orderSize)} USDT");
        message.AppendLine($"Entry: <code>{FormatPrice(entryPrice)}</code>");
        message.AppendLine($"Take Profit: <code>{FormatPrice(takeProfit)}</code>");
        if (stopLoss is > 0)
            message.AppendLine($"Stop Loss: <code>{FormatPrice(stopLoss.Value)}</code>");

        await BroadcastAsync(strategy, message.ToString(), ct);
    }

    public async Task SendDcaSignalAsync(Strategy strategy, string symbol, string direction,
        int dcaLevel, decimal dcaQuoteAmount, decimal newAveragePrice, decimal newTakeProfit,
        CancellationToken ct = default)
    {
        var isLong = direction.Equals("Long", StringComparison.OrdinalIgnoreCase);
        var emoji = isLong ? "📈" : "📉";

        var message = new StringBuilder();
        message.AppendLine($"➕ <b>Position averaging (DCA #{dcaLevel})</b>");
        message.AppendLine($"Ticker: <code>{symbol}</code>");
        message.AppendLine($"{(isLong ? "LONG" : "SHORT")} {emoji}");
        message.AppendLine($"Add-in: ~{FormatUsd(dcaQuoteAmount)} USDT");
        message.AppendLine($"New avg: <code>{FormatPrice(newAveragePrice)}</code>");
        message.AppendLine($"New TP: <code>{FormatPrice(newTakeProfit)}</code>");

        await BroadcastAsync(strategy, message.ToString(), ct);
    }

    public async Task SendPositionClosedSignalAsync(Strategy strategy, string symbol, string direction,
        decimal pnlDollar, decimal pnlPercent,
        CancellationToken ct = default)
    {
        var profit = pnlDollar >= 0;
        var headEmoji = profit ? "💰" : "🔻";
        var sign = profit ? "+" : "";

        var message = new StringBuilder();
        message.AppendLine($"{headEmoji} <b>Position closed</b>");
        message.AppendLine($"Ticker: <code>{symbol}</code> ({direction})");
        message.AppendLine(profit ? "Полностью закрыта ✅" : "Полностью закрыта ❗");
        message.AppendLine(
            $"PnL: <b>{sign}{FormatUsd(pnlDollar)} USDT</b> ({sign}{pnlPercent.ToString("0.##", CultureInfo.InvariantCulture)}%)");

        await BroadcastAsync(strategy, message.ToString(), ct);
    }

    private async Task BroadcastAsync(Strategy strategy, string text, CancellationToken ct)
    {
        if (strategy.TelegramBotId == null)
            return;

        var bot = await _db.TelegramBots
            .AsNoTracking()
            .Include(b => b.Subscribers)
            .FirstOrDefaultAsync(b => b.Id == strategy.TelegramBotId && b.IsActive, ct);

        if (bot == null || bot.Subscribers.Count == 0)
            return;

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

    private static string FormatUsd(decimal value)
        => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    // Variable-precision formatter so micro-cap prices (e.g. 0.00012345) keep meaningful digits
    // while round prices (50000) stay short.
    private static string FormatPrice(decimal value)
    {
        var abs = Math.Abs(value);
        var digits = abs >= 1000m ? 2
                   : abs >= 10m ? 4
                   : abs >= 0.1m ? 6
                   : 8;
        return Math.Round(value, digits).ToString("0.########", CultureInfo.InvariantCulture);
    }
}
