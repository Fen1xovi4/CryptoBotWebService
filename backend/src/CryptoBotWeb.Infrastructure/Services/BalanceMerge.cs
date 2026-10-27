using CryptoBotWeb.Core.DTOs;

namespace CryptoBotWeb.Infrastructure.Services;

// Folds wallet rows from several sub-accounts (futures + spot) into one row per asset.
internal static class BalanceMerge
{
    public static void Add(Dictionary<string, BalanceDto> merged, BalanceDto b)
    {
        if (string.IsNullOrEmpty(b.Asset)) return;
        if (merged.TryGetValue(b.Asset, out var existing))
        {
            existing.Free += b.Free;
            existing.Locked += b.Locked;
        }
        else
        {
            merged[b.Asset] = new BalanceDto { Asset = b.Asset, Free = b.Free, Locked = b.Locked };
        }
    }
}
