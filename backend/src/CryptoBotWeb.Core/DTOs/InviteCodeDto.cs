namespace CryptoBotWeb.Core.DTOs;

public class InviteCodeDto
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string AssignedRole { get; set; } = string.Empty;
    public int MaxUses { get; set; }
    public int UsedCount { get; set; }
    public bool IsActive { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<InviteCodeUsageDto> Usages { get; set; } = new();
}

public class InviteCodeUsageDto
{
    public string Username { get; set; } = string.Empty;
    public DateTime UsedAt { get; set; }
}

public class CreateInviteCodeRequest
{
    public string Role { get; set; } = "User";
    public int MaxUses { get; set; } = 1;
    public DateTime? ExpiresAt { get; set; }
}

public class RegisterRequest
{
    public string InviteCode { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
