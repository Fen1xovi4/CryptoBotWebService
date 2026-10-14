using CryptoBotWeb.Core.DTOs;
using CryptoBotWeb.Core.Entities;

namespace CryptoBotWeb.Core.Interfaces;

/// <summary>
/// Long-lived private websocket order streams, one per exchange account, shared by every bot on
/// that account.
///
/// Exists so grid bots stop polling every resting order over REST on every tick — those polls all
/// land in the account's (UID's) private rate limit, which is what caps how many grids one
/// account can run. The stream turns "ask about each order every 5s" into "hear about the few
/// orders that actually changed".
///
/// Trust model: a cached status is only handed out as authoritative when the stream has been
/// connected without interruption since that status was known — then any later change would
/// have been pushed to us, so silence really means "unchanged". Anything else (no entry, stream
/// reconnected since, entry older than the re-verify cap) is reported as untrusted and the caller
/// must ask REST, then <see cref="Seed"/> the answer back.
///
/// V1: Bybit linear only, GridFloat only. Other exchanges report untrusted forever (REST as before).
/// </summary>
public interface IOrderStreamService
{
    /// <summary>
    /// Idempotent and non-blocking. Starts the account's private stream on the first call and
    /// refreshes its keep-alive stamp on every later one. Never throws.
    /// </summary>
    Task EnsureSubscribedAsync(ExchangeAccount account, CancellationToken ct = default);

    /// <summary>True while the account's stream is connected and confirmed subscribed.</summary>
    bool IsHealthy(Guid accountId);

    /// <summary>
    /// Returns true with the cached status only when it can be trusted without asking REST
    /// (see the trust model above). False means "go ask REST".
    /// </summary>
    bool TryGetTrusted(Guid accountId, string orderId, out OrderStatusDto status);

    /// <summary>
    /// Records a status learned outside the stream — a REST lookup, or a placement we just made
    /// (status Open). <paramref name="knownAsOfUtc"/> must be taken BEFORE the REST call was sent,
    /// so a change racing the request is never covered by this entry. Merges monotonically: a
    /// status the stream already pushed (e.g. Filled) is never downgraded by an older REST view.
    /// </summary>
    void Seed(Guid accountId, OrderStatusDto status, DateTime knownAsOfUtc);
}
