using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Entities;

public class PaymentSession
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid WalletId { get; set; }
    public SubscriptionPlan Plan { get; set; }
    public PaymentNetwork Network { get; set; }
    public PaymentToken Token { get; set; }
    public decimal ExpectedAmount { get; set; }
    public PaymentSessionStatus Status { get; set; } = PaymentSessionStatus.Pending;
    public string? TxHash { get; set; }
    public decimal? ReceivedAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public Guid? ConfirmedByAdminId { get; set; }
    public Guid? GuestToken { get; set; }
    public Guid? AssignedInviteCodeId { get; set; }

    public User? User { get; set; }
    public PaymentWallet Wallet { get; set; } = null!;
    public User? ConfirmedByAdmin { get; set; }
    public InviteCode? AssignedInviteCode { get; set; }
}
