using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

public interface IFuturesExchangeService : IDisposable
{
    Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit);
    Task<decimal?> GetTickerPriceAsync(string symbol);
    Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount);
    Task<OrderResultDto> OpenShortAsync(string symbol, decimal quoteAmount);
    Task<OrderResultDto> CloseLongAsync(string symbol, decimal quantity);
    Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity);
}
