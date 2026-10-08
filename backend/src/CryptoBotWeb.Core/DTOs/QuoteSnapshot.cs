namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// Latest top-of-book seen on a websocket stream, with the moment it arrived.
///
/// The timestamp is the whole point: a silent socket keeps serving its last quote forever, and
/// trading a spread computed from a stale book is the worst failure mode this module has. Every
/// consumer must check <see cref="AgeMs"/> before acting on the numbers.
///
/// <see cref="ExchangeTimeUtc"/> is the exchange's own event time for the update (when the
/// stream carries one) — arrival minus exchange time is how late the venue's feed is running.
/// <see cref="Seq"/> counts updates on this stream; a consumer that remembers it can tell
/// whether a venue has sent anything new since some moment.
/// </summary>
public record QuoteSnapshot(decimal Bid, decimal Ask, DateTime UpdatedAtUtc,
    DateTime? ExchangeTimeUtc = null, long Seq = 0)
{
    public bool IsValid => Bid > 0 && Ask > 0 && Ask >= Bid;

    public double AgeMs(DateTime nowUtc) => (nowUtc - UpdatedAtUtc).TotalMilliseconds;

    /// <summary>Arrival time minus exchange event time, in ms; null when the stream has no event time.</summary>
    public double? FeedLagMs => ExchangeTimeUtc.HasValue
        ? (UpdatedAtUtc - ExchangeTimeUtc.Value).TotalMilliseconds
        : null;
}

/// <summary>Diagnostics for one (exchange, symbol) stream — surfaced in logs, not used for trading.</summary>
public record QuoteStreamStatus(
    bool Connected,
    DateTime? LastUpdateUtc,
    long UpdateCount,
    string? LastError);
