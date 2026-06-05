using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

public interface IExchangeServiceFactory
{
    IExchangeService Create(ExchangeAccount account);
    IFuturesExchangeService CreateFutures(ExchangeAccount account);

    /// <summary>
    /// Build a spot/general client bound to a specific proxy (or null = direct), bypassing
    /// failover selection. Used by the test-connection endpoint to probe each proxy in turn.
    /// </summary>
    IExchangeService CreateWithProxy(ExchangeAccount account, ProxyServer? proxy);

    // V1 ships Bybit-only spot support. Other ExchangeType values throw NotSupportedException
    // — GridHedge SameTicker (spot+futures hedge) refuses to start on those exchanges.
    ISpotExchangeService CreateSpot(ExchangeAccount account);
}
