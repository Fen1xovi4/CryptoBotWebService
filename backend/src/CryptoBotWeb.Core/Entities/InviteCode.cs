using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

public class InviteCode
{
    public Guid Id { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Code { get; set; } = string.Empty;
    public UserRole AssignedRole { get; set; } = UserRole.User;
    public int MaxUses { get; set; } = 1;
    public int UsedCount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }

    public User CreatedByUser { get; set; } = null!;
    public ICollection<InviteCodeUsage> Usages { get; set; } = new List<InviteCodeUsage>();
}
