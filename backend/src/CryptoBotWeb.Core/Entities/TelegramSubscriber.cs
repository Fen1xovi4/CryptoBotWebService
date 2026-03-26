namespace CryptoBotWeb.Core.Entities;

public class TelegramSubscriber
{
    public Guid Id { get; set; }
    public Guid TelegramBotId { get; set; }
    public long ChatId { get; set; }
    public string? Username { get; set; }
    public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;

    public TelegramBot TelegramBot { get; set; } = null!;
}
