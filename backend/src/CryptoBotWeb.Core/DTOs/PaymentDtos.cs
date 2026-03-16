namespace CryptoBotWeb.Core.DTOs;

public class PaymentWalletDto
{
    public Guid Id { get; set; }
    public string AddressTrc20 { get; set; } = string.Empty;
    public string AddressBep20 { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsLocked { get; set; }
}

public class CreatePaymentWalletRequest
{
    public string AddressTrc20 { get; set; } = string.Empty;
    public string AddressBep20 { get; set; } = string.Empty;
}

public class CreatePaymentSessionRequest
{
    public string Plan { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public class PaymentSessionDto
{
    public Guid Id { get; set; }
    public string Plan { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public decimal ExpectedAmount { get; set; }
    public string WalletAddress { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? TxHash { get; set; }
    public decimal? ReceivedAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public int RemainingSeconds { get; set; }
    public string? InviteCode { get; set; }
}

public class GuestPaymentSessionDto : PaymentSessionDto
{
    public Guid GuestToken { get; set; }
}

public class PaymentSessionAdminDto : PaymentSessionDto
{
    public Guid? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public Guid WalletId { get; set; }
    public string? ConfirmedByAdmin { get; set; }
    public bool IsGuest { get; set; }
}

public class AdminConfirmPaymentRequest
{
    public string? TxHash { get; set; }
    public decimal? ReceivedAmount { get; set; }
}
