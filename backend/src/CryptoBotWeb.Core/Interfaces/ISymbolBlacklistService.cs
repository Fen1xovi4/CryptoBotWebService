using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Interfaces;

public interface ISymbolBlacklistService
{
    Task AddOrRefreshAsync(ExchangeType exchangeType, string symbol, string? reason, CancellationToken ct);
    Task<HashSet<string>> GetActiveSetAsync(ExchangeType exchangeType, CancellationToken ct);
}
