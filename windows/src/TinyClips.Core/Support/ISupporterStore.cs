namespace TinyClips.Core.Support;

/// <summary>A plan the Store offers right now, at the price it shows in the customer's market.</summary>
public sealed record SupporterOffer(SupporterPlan Plan, string FormattedPrice);

public enum SupporterPurchaseOutcome
{
    Purchased,
    AlreadyOwned,
    Canceled,
    NetworkError,
    ServerError,
    Unavailable,
}

/// <summary>
/// The store that sells supporter plans. <see cref="LoadOffersAsync"/> and
/// <see cref="GetActivePlanAsync"/> throw when the store cannot answer, so a failure is never
/// mistaken for "no offers" or "no purchases".
/// </summary>
public interface ISupporterStore : IDisposable
{
    /// <summary>Raised on the thread that created the store when the account's licenses change.</summary>
    event EventHandler? LicensesChanged;

    Task<IReadOnlyList<SupporterOffer>> LoadOffersAsync();

    Task<SupporterPlan?> GetActivePlanAsync();

    Task<SupporterPurchaseOutcome> PurchaseAsync(SupporterPlan plan);
}
