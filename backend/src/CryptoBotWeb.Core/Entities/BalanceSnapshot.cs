namespace CryptoBotWeb.Core.Entities;

public class BalanceSnapshot
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public decimal TotalUsdt { get; set; }
    public DateTime TakenAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
