using System.Text.Json;
using CryptoBotWeb.Core.Constants;
using CryptoBotWeb.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CryptoBotWeb.Infrastructure.Services;

public class TronGridService : IBlockchainService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TronGridService> _logger;

    private const decimal Trc20Decimals = 1_000_000m; // 6 decimals

    public TronGridService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TronGridService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("TronGrid");
        _logger = logger;

        var apiKey = configuration["TronGrid:ApiKey"];
        if (!string.IsNullOrEmpty(apiKey))
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("TRON-PRO-API-KEY", apiKey);
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
            var sinceMs = new DateTimeOffset(since, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var url = $"https://api.trongrid.io/v1/accounts/{walletAddress}/transactions/trc20" +
                      $"?only_to=true&min_timestamp={sinceMs}&contract_address={contractAddress}&limit=50";

            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var dataArray) ||
                dataArray.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("TronGrid response for {Address} had no 'data' array", walletAddress);
                return null;
            }

            foreach (var tx in dataArray.EnumerateArray())
            {
                // to field
                if (!tx.TryGetProperty("to", out var toEl) ||
                    toEl.GetString() != walletAddress)
                    continue;

                // raw value
                if (!tx.TryGetProperty("value", out var valueEl))
                    continue;

                decimal rawValue;
                if (valueEl.ValueKind == JsonValueKind.Number)
                    rawValue = valueEl.GetDecimal();
                else if (!decimal.TryParse(valueEl.GetString(), out rawValue))
                    continue;

                var amount = rawValue / Trc20Decimals;
                if (Math.Abs(amount - expectedAmount) > BlockchainContracts.PaymentTolerance)
                    continue;

                // from field
                var fromAddress = tx.TryGetProperty("from", out var fromEl) ? fromEl.GetString() ?? string.Empty : string.Empty;

                // tx hash
                var txHash = tx.TryGetProperty("transaction_id", out var hashEl) ? hashEl.GetString() ?? string.Empty : string.Empty;

                // block timestamp (ms)
                DateTime timestamp = since;
                if (tx.TryGetProperty("block_timestamp", out var tsEl))
                {
                    var tsMs = tsEl.GetInt64();
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime;
                }

                _logger.LogInformation(
                    "TronGrid: found matching transfer {TxHash} amount={Amount} from={From}",
                    txHash, amount, fromAddress);

                return new BlockchainTransfer(txHash, amount, fromAddress, timestamp);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TronGrid error while checking wallet {Address} contract {Contract}", walletAddress, contractAddress);
            return null;
        }
    }
}
