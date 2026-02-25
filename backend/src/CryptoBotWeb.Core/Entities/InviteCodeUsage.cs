namespace CryptoBotWeb.Core.Entities;

public class InviteCodeUsage
{
    public Guid Id { get; set; }
    public Guid InviteCodeId { get; set; }
    public Guid UserId { get; set; }
    public DateTime UsedAt { get; set; } = DateTime.UtcNow;

    public InviteCode InviteCode { get; set; } = null!;
    public User User { get; set; } = null!;
}
