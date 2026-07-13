namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// A single historical funding settlement.
/// Rate uses the same convention as <see cref="FundingRateDto.Rate"/>:
/// the raw exchange fraction (0.0001 = 0.01%), NOT percent.
/// </summary>
public class FundingEventDto
{
    /// <summary>UTC moment the funding was settled/charged.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Raw funding rate fraction. Positive: longs pay shorts.</summary>
    public decimal Rate { get; set; }
}
