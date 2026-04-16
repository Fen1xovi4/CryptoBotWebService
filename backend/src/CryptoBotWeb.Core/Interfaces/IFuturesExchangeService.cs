using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Core.Interfaces;

public interface IFuturesExchangeService : IDisposable
{
    Task<List<SymbolDto>> GetSymbolsAsync();
    Task<List<CandleDto>> GetKlinesAsync(string symbol, string timeframe, int limit);
    Task<decimal?> GetTickerPriceAsync(string symbol);
    Task<OrderResultDto> OpenLongAsync(string symbol, decimal quoteAmount);
    Task<OrderResultDto> OpenShortAsync(string symbol, decimal quoteAmount);
    Task<OrderResultDto> CloseLongAsync(string symbol, decimal quantity);
    Task<OrderResultDto> CloseShortAsync(string symbol, decimal quantity);

    // HuntingFunding methods — default implementations for backward compatibility
    Task<FundingRateDto?> GetFundingRateAsync(string symbol) =>
        throw new NotSupportedException("GetFundingRateAsync not implemented");

    Task<OrderResultDto> PlaceLimitOrderAsync(string symbol, string side, decimal price, decimal quantity, bool reduceOnly = false) =>
        throw new NotSupportedException("PlaceLimitOrderAsync not implemented");

    Task<bool> CancelAllOrdersAsync(string symbol) =>
        throw new NotSupportedException("CancelAllOrdersAsync not implemented");

    Task<bool> CancelOrderAsync(string symbol, string orderId) =>
        throw new NotSupportedException("CancelOrderAsync not implemented");

    Task<OrderStatusDto?> GetOrderAsync(string symbol, string orderId) =>
        throw new NotSupportedException("GetOrderAsync not implemented");

    Task<List<LimitOrderDto>> GetOpenOrdersAsync(string symbol) =>
        throw new NotSupportedException("GetOpenOrdersAsync not implemented");

    Task<PositionDto?> GetPositionAsync(string symbol, string side) =>
        throw new NotSupportedException("GetPositionAsync not implemented");

    Task<List<FundingRateDto>> GetAllFundingRatesAsync() =>
        throw new NotSupportedException("GetAllFundingRatesAsync not implemented");
}
