namespace CryptoBotWeb.Core.DTOs;

public class BalanceSnapshotDto
{
    public Guid Id { get; set; }
    public decimal TotalUsdt { get; set; }
    public DateTime TakenAt { get; set; }
}
