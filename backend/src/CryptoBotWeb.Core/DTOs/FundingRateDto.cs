namespace CryptoBotWeb.Core.DTOs;

public class FundingRateDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public DateTime NextFundingTime { get; set; }
}
