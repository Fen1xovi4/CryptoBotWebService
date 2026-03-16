namespace CryptoBotWeb.Core.Entities;

public class PaymentWallet
{
    public Guid Id { get; set; }
    public string AddressTrc20 { get; set; } = string.Empty;
    public string AddressBep20 { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PaymentSession> PaymentSessions { get; set; } = new List<PaymentSession>();
}
