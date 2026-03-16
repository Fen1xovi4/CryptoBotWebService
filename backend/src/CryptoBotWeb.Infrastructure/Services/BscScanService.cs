using System.Globalization;
using System.Text;
using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Services;

public class BscScanService : IBlockchainService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BscScanService> _logger;

    // BEP20 tokens use 18 decimals
    private static readonly decimal Bep20Decimals = (decimal)Math.Pow(10, 18);

    // balanceOf(address) function selector
    private const string BalanceOfSelector = "0x70a08231";

    // Multiple BSC RPC endpoints for redundancy
    private static readonly string[] RpcEndpoints =
    [
        "https://bsc-dataseed.binance.org/",
        "https://bsc-dataseed1.defibit.io/",
        "https://bsc-dataseed1.ninicoin.io/"
    ];

    public BscScanService(IHttpClientFactory httpClientFactory, ILogger<BscScanService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("BscRpc");
        _logger = logger;
    }

    public async Task<BlockchainTransfer?> FindTransferAsync(
        string walletAddress,
        string contractAddress,
        decimal expectedAmount,
        DateTime since,
        CancellationToken ct = default)
    {
        try
        {
            var balance = await GetTokenBalanceAsync(walletAddress, contractAddress, ct);

            if (balance is null)
            {
                _logger.LogWarning("BscRpc: could not retrieve balance for wallet {Wallet} contract {Contract}",
                    walletAddress, contractAddress);
                return null;
            }

            _logger.LogDebug("BscRpc: wallet {Wallet} balance = {Balance} (expected {Expected})",
                walletAddress, balance.Value, expectedAmount);

            if (balance.Value < expectedAmount - BlockchainContracts.PaymentTolerance)
                return null;

            var txHash = $"rpc-balance-verified-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            _logger.LogInformation(
                "BscRpc: balance check passed for wallet {Wallet}, balance={Balance}, expected={Expected}, synthetic tx={TxHash}",
                walletAddress, balance.Value, expectedAmount, txHash);

            return new BlockchainTransfer(txHash, balance.Value, string.Empty, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BscRpc: error checking wallet {Wallet} contract {Contract}",
                walletAddress, contractAddress);
            return null;
        }
    }

    private async Task<decimal?> GetTokenBalanceAsync(
        string walletAddress,
        string contractAddress,
        CancellationToken ct)
    {
        // Build the eth_call data: balanceOf(address)
        // Wallet address stripped of 0x prefix, left-padded to 64 hex chars
        var addressHex = walletAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? walletAddress[2..]
            : walletAddress;

        var paddedAddress = addressHex.ToLowerInvariant().PadLeft(64, '0');
        var data = $"{BalanceOfSelector}{paddedAddress}";

        var requestBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "eth_call",
            @params = new object[]
            {
                new { to = contractAddress, data },
                "latest"
            },
            id = 1
        });

        // Try each RPC endpoint until one succeeds
        foreach (var endpoint in RpcEndpoints)
        {
            try
            {
                using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(endpoint, content, ct);
                response.EnsureSuccessStatusCode();

                using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

                if (doc.RootElement.TryGetProperty("error", out var errorEl))
                {
                    _logger.LogWarning("BscRpc: endpoint {Endpoint} returned error: {Error}",
                        endpoint, errorEl.ToString());
                    continue;
                }

                if (!doc.RootElement.TryGetProperty("result", out var resultEl))
                {
                    _logger.LogWarning("BscRpc: endpoint {Endpoint} returned no result", endpoint);
                    continue;
                }

                var hexResult = resultEl.GetString();
                if (string.IsNullOrEmpty(hexResult) || hexResult == "0x")
                    return 0m;

                var rawBalance = HexToDecimal(hexResult);
                return rawBalance / Bep20Decimals;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "BscRpc: endpoint {Endpoint} failed, trying next", endpoint);
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a hex string (with 0x prefix) to decimal.
    /// Handles the large uint256 values returned by BEP20 balanceOf.
    /// </summary>
    private static decimal HexToDecimal(string hex)
    {
        var cleanHex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? hex[2..]
            : hex;

        // Remove leading zeros but keep at least one digit
        cleanHex = cleanHex.TrimStart('0');
        if (string.IsNullOrEmpty(cleanHex))
            return 0m;

        // Parse as BigInteger then convert to decimal
        // BigInteger handles the full uint256 range
        var bigInt = System.Numerics.BigInteger.Parse("0" + cleanHex, NumberStyles.HexNumber);
        return (decimal)bigInt;
    }
}
