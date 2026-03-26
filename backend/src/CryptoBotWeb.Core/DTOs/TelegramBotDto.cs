namespace CryptoBotWeb.Core.DTOs;

public class TelegramBotDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool HasPassword { get; set; }
    public bool IsActive { get; set; }
    public int SubscriberCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateTelegramBotRequest
{
    public string Name { get; set; } = string.Empty;
    public string BotToken { get; set; } = string.Empty;
    public string? Password { get; set; }
}

public class UpdateTelegramBotRequest
{
    public string? Name { get; set; }
    public string? BotToken { get; set; }
    public string? Password { get; set; }
    public bool? IsActive { get; set; }
}
