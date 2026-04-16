namespace CryptoBotWeb.Core.Interfaces;

public interface IFundingTickerRotationService
{
    Task RotateTickersAsync(CancellationToken ct);
}
