namespace CryptoBotWeb.Core.Interfaces;

public record BlockchainTransfer(
    string TxHash,
    decimal Amount,
    string FromAddress,
    DateTime Timestamp);

public interface IBlockchainService
{
    Task<BlockchainTransfer?> FindTransferAsync(
        string walletAddress,
        string contractAddress,
        decimal expectedAmount,
        DateTime since,
        CancellationToken ct = default);
}
