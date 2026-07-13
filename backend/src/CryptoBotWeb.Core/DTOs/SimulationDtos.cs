namespace CryptoBotWeb.Core.DTOs;

// The legacy single-strategy SimulationRequest/SimulationResult DTOs lived here.
// They were replaced by SimulationRunDtos.cs when the Tester module went multi-strategy;
// only IndicatorPoint (the MA overlay point) is still part of the contract.

public class IndicatorPoint
{
    public DateTime Time { get; set; }
    public decimal Value { get; set; }
}
