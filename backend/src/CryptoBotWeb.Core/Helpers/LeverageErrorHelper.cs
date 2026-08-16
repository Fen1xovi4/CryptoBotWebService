namespace CryptoBotWeb.Core.Helpers;

/// <summary>
/// Shared classification of exchange responses to a "set leverage" call.
///
/// Every venue answers "the symbol is already on that leverage" with an ERROR rather than a
/// no-op success (Bybit retCode 110043 / 34036, Bitget and BingX with a text-only message).
/// Treating that as a failure produced a misleading "could not set leverage" warning on every
/// bot start, so it is classified as success here — the account already holds the target value.
/// </summary>
public static class LeverageErrorHelper
{
    // Bybit V5: 110043 "Set leverage not modified"; 34036 is the older contract-API equivalent.
    private static readonly int[] NotModifiedCodes = [110043, 34036];

    private static readonly string[] NotModifiedPhrases =
    [
        "not modified",
        "not been modified",
        "leverage not changed",
        "no need to modify"
    ];

    /// <summary>
    /// True when the rejection means "already at the requested leverage" and nothing is wrong.
    /// Deliberately narrow: anything else stays a real failure so it reaches the strategy log.
    /// </summary>
    public static bool IsAlreadyAtTargetLeverage(int? code, string? message)
    {
        if (code.HasValue && NotModifiedCodes.Contains(code.Value)) return true;
        if (string.IsNullOrWhiteSpace(message)) return false;

        return NotModifiedPhrases.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Compact "message (code=N)" text for logs; never null.</summary>
    public static string Describe(int? code, string? message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "unknown error" : message.Trim();
        return code.HasValue ? $"{text} (code={code.Value})" : text;
    }
}
