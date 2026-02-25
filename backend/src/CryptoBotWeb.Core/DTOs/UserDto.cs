namespace CryptoBotWeb.Core.DTOs;

public class UserDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? InvitedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public int AccountsCount { get; set; }
    public int StrategiesCount { get; set; }
}

public class UpdateUserRoleRequest
{
    public string Role { get; set; } = string.Empty;
}

public class UpdateUserEnabledRequest
{
    public bool IsEnabled { get; set; }
}
