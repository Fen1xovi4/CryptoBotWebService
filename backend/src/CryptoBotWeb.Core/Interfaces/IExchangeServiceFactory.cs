using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

public interface IExchangeServiceFactory
{
    IExchangeService Create(ExchangeAccount account);
    IFuturesExchangeService CreateFutures(ExchangeAccount account);

    // V1 ships Bybit-only spot support. Other ExchangeType values throw NotSupportedException
    // — GridHedge SameTicker (spot+futures hedge) refuses to start on those exchanges.
    ISpotExchangeService CreateSpot(ExchangeAccount account);
}
