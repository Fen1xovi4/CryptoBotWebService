using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

public interface IExchangeServiceFactory
{
    IExchangeService Create(ExchangeAccount account);
    IFuturesExchangeService CreateFutures(ExchangeAccount account);
}
