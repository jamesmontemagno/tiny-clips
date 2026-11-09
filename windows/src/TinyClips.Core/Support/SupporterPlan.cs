namespace TinyClips.Core.Support;

/// <summary>An optional way to support Tiny Clips. No plan unlocks or removes a feature.</summary>
public enum SupporterPlan
{
    Monthly,
    Yearly,
    OneTime,
}

/// <summary>The Store products behind each plan, shared with the macOS app's product IDs.</summary>
public static class SupporterPlans
{
    public const string MonthlyProductId = "com.refractored.tinyclips.pro.monthly";
    public const string YearlyProductId = "com.refractored.tinyclips.pro.yearly";
    public const string OneTimeProductId = "com.refractored.tinyclips.pro.lifetime";

    public static bool IsSubscription(SupporterPlan plan) => plan is not SupporterPlan.OneTime;

    public static SupporterPlan? FromProductId(string? productId) => productId switch
    {
        MonthlyProductId => SupporterPlan.Monthly,
        YearlyProductId => SupporterPlan.Yearly,
        OneTimeProductId => SupporterPlan.OneTime,
        _ => null,
    };

    /// <summary>
    /// The plan to show for an account that may hold several: a one-time purchase never lapses,
    /// so it outranks a yearly plan, which outranks a monthly one. Null when none is a supporter product.
    /// </summary>
    public static SupporterPlan? MostLasting(IEnumerable<string?> productIds)
    {
        SupporterPlan? best = null;
        foreach (var productId in productIds)
        {
            if (FromProductId(productId) is { } plan && (best is null || plan > best))
            {
                best = plan;
            }
        }

        return best;
    }
}
