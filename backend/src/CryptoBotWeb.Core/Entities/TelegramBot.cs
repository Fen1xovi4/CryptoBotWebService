namespace CryptoBotWeb.Core.Entities;

public class TelegramBot
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BotToken { get; set; } = string.Empty;
    public string? Password { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<TelegramSubscriber> Subscribers { get; set; } = new List<TelegramSubscriber>();
    public ICollection<Strategy> Strategies { get; set; } = new List<Strategy>();
}
