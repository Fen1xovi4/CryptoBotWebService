using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;
using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Interfaces;

/// <summary>
/// Long-lived websocket top-of-book streams, shared by (exchange, symbol) across every consumer.
///
/// Exists because the trading loop builds and disposes a REST client on every 5s tick, which is
/// the opposite of what a websocket needs. Streams live here instead: subscribed on demand,
/// kept warm while someone keeps asking for them, dropped when nobody has for a while.
///
/// V1 consumer is FuturesArbitrage only — the other strategies keep polling REST.
/// </summary>
public interface IQuoteStreamService
{
    /// <summary>
    /// Idempotent. Starts the stream for this account's exchange + symbol on the first call and
    /// refreshes its keep-alive stamp on every later one. Uses the account's proxy so the stream
    /// shares the egress IP of that account's REST calls. Never throws — a stream that fails to
    /// start is reported through <see cref="GetStatus"/> and callers fall back to REST.
    /// </summary>
    Task EnsureSubscribedAsync(ExchangeAccount account, string symbol, CancellationToken ct = default);

    /// <summary>
    /// Last quote received, or null if the stream isn't up yet. The caller MUST check
    /// <see cref="QuoteSnapshot.AgeMs"/> — this returns the last value seen, however old.
    /// </summary>
    QuoteSnapshot? TryGetQuote(ExchangeType exchange, string symbol);

    QuoteStreamStatus? GetStatus(ExchangeType exchange, string symbol);
}
