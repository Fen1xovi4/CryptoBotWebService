namespace CryptoBotWeb.Core.Entities;

public class SupportMessage
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid SenderId { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsFromAdmin { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public SupportTicket Ticket { get; set; } = null!;
    public User Sender { get; set; } = null!;
}
