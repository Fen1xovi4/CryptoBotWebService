namespace CryptoBotWeb.Core.DTOs;

public class SubscriptionDto
{
    public string Plan { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int MaxAccounts { get; set; }
    public int MaxActiveBots { get; set; }
    public int CurrentAccounts { get; set; }
    public int CurrentActiveBots { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsAdmin { get; set; }
}

public class PlanInfoDto
{
    public string Plan { get; set; } = string.Empty;
    public string NameRu { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public int MaxAccounts { get; set; }
    public int MaxActiveBots { get; set; }
    public decimal PriceMonthly { get; set; }
    public string PriceLabel { get; set; } = string.Empty;
}

public class UpdateSubscriptionRequest
{
    public string Plan { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
}
