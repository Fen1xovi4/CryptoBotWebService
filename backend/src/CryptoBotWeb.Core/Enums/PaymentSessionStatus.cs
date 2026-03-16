namespace CryptoBotWeb.Core.Enums;

public enum PaymentSessionStatus
{
    Pending = 0,
    Confirmed = 1,
    Expired = 2,
    Cancelled = 3,
    ManuallyConfirmed = 4
}
