using CryptoBotWeb.Core.Enums;

namespace CryptoBotWeb.Core.Constants;

public record PlanLimitInfo(
    SubscriptionPlan Plan,
    string NameRu,
    string NameEn,
    int MaxAccounts,
    int MaxActiveBots,
    decimal PriceMonthly,
    string PriceLabel
);

public static class PlanLimits
{
    private static readonly Dictionary<SubscriptionPlan, PlanLimitInfo> Limits = new()
    {
        [SubscriptionPlan.Basic] = new(SubscriptionPlan.Basic, "Базовая", "Basic", 2, 5, 9m, "$9/mo"),
        [SubscriptionPlan.Advanced] = new(SubscriptionPlan.Advanced, "Продвинутая", "Advanced", 5, 15, 29m, "$29/mo"),
        [SubscriptionPlan.Pro] = new(SubscriptionPlan.Pro, "Про", "Pro", 20, 50, 99m, "$99/mo"),
    };

    public static PlanLimitInfo GetLimits(SubscriptionPlan plan) =>
        Limits.TryGetValue(plan, out var info) ? info : Limits[SubscriptionPlan.Basic];

    public static IReadOnlyList<PlanLimitInfo> GetAllPlans() =>
        Limits.Values.OrderBy(p => p.Plan).ToList();
}
