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

    /// <summary>
    /// The proxy this account's clients would use right now (failover order + health checks
    /// applied), or null for a direct connection. Lets non-REST clients — the FuturesArbitrage
    /// websocket quote streams — share the account's egress IP instead of connecting directly.
    /// </summary>
    ProxyServer? SelectProxyFor(ExchangeAccount account);

    // V1 ships Bybit-only spot support. Other ExchangeType values throw NotSupportedException
    // — GridHedge SameTicker (spot+futures hedge) refuses to start on those exchanges.
    ISpotExchangeService CreateSpot(ExchangeAccount account);
}
