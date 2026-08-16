namespace CryptoBotWeb.Core.DTOs;

/// <summary>
/// Outcome of a leverage change, carrying the exchange's rejection text so callers can log WHY
/// the pin failed instead of a bare "could not set leverage".
///
/// <paramref name="AlreadySet"/> marks the case where the exchange rejected the call only because
/// the symbol already sits on the requested leverage (Bybit retCode 110043 and friends). That is
/// a success for our purposes — the account ends up with exactly the leverage we asked for.
/// </summary>
public record LeverageSetResult(bool Success, string? Error = null, bool AlreadySet = false)
{
    public static LeverageSetResult Ok() => new(true);
    public static LeverageSetResult Unchanged() => new(true, null, true);
    public static LeverageSetResult Fail(string? error) => new(false, error);
}
