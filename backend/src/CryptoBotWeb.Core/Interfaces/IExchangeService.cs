using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

public interface IExchangeService
{
    Task<(bool Success, string? Error)> TestConnectionAsync();
    Task<List<BalanceDto>> GetBalancesAsync();
    Task<decimal?> GetTickerPriceAsync(string symbol);
}
