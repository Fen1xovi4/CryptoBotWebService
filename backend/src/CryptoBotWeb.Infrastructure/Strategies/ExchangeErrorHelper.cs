namespace CryptoBotWeb.Infrastructure.Strategies;

/// <summary>
/// Shared classification of exchange rejection messages for strategy handlers.
/// </summary>
public static class ExchangeErrorHelper
{
    /// <summary>
    /// True when an exchange rejection means "the position this reduce-only order targets no
    /// longer exists" — our in-memory qty is a phantom and retrying can never succeed.
    /// Phrasings seen in production:
    ///   Bybit:  "current position is zero, cannot fix reduce-only order qty"
    ///           "orderQty will be truncated to zero"
    ///   Bitget: "No position to close" / "number of closed positions cannot exceed..."
    ///   BingX:  "The Reduce Only order can only decrease the position..."
    /// Hyphenation differs per exchange ("reduce-only" vs "Reduce Only"), so hyphens are
    /// normalised to spaces before matching — the missing hyphen variant is exactly why the
    /// Bybit message used to fall through to the generic error branch and loop every tick.
    /// Keep every handler on this one matcher: divergent per-handler copies are how the Bybit
    /// phrasing got missed in SmaDca while HuntingFunding already covered it.
    /// </summary>
    public static bool IsPositionGoneError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;

        var e = error.Replace('-', ' ');
        return e.Contains("no position", StringComparison.OrdinalIgnoreCase)
            || e.Contains("position is zero", StringComparison.OrdinalIgnoreCase)
            || e.Contains("position not found", StringComparison.OrdinalIgnoreCase)
            || e.Contains("number of closed positions", StringComparison.OrdinalIgnoreCase)
            || e.Contains("reduce only order", StringComparison.OrdinalIgnoreCase)
            || e.Contains("decrease the position", StringComparison.OrdinalIgnoreCase)
            || e.Contains("truncated to zero", StringComparison.OrdinalIgnoreCase);
    }
}
