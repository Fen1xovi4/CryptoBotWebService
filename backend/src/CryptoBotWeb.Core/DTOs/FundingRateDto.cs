namespace CryptoBotWeb.Core.DTOs;

public class FundingRateDto
{
    public decimal Rate { get; set; }
    public DateTime NextFundingTime { get; set; }
}
