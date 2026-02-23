using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

public interface IExchangeService
{
    Task<bool> TestConnectionAsync();
    Task<List<BalanceDto>> GetBalancesAsync();
    Task<decimal?> GetTickerPriceAsync(string symbol);
}
