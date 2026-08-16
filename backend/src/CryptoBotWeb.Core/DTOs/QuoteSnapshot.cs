namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// Latest top-of-book seen on a websocket stream, with the moment it arrived.
///
/// The timestamp is the whole point: a silent socket keeps serving its last quote forever, and
/// trading a spread computed from a stale book is the worst failure mode this module has. Every
/// consumer must check <see cref="AgeMs"/> before acting on the numbers.
/// </summary>
public record QuoteSnapshot(decimal Bid, decimal Ask, DateTime UpdatedAtUtc)
{
    public bool IsValid => Bid > 0 && Ask > 0 && Ask >= Bid;

    public double AgeMs(DateTime nowUtc) => (nowUtc - UpdatedAtUtc).TotalMilliseconds;
}

/// <summary>Diagnostics for one (exchange, symbol) stream — surfaced in logs, not used for trading.</summary>
public record QuoteStreamStatus(
    bool Connected,
    DateTime? LastUpdateUtc,
    long UpdateCount,
    string? LastError);
