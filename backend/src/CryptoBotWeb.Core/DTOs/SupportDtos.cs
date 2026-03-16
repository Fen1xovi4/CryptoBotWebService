namespace CryptoBotWeb.Core.DTOs;

public class SupportTicketDto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? LastMessage { get; set; }
    public int UnreadCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class SupportMessageDto
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsFromAdmin { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateTicketRequest
{
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class SendMessageRequest
{
    public string Text { get; set; } = string.Empty;
}

public class UnreadCountDto
{
    public int Count { get; set; }
}
