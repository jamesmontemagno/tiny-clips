using Microsoft.UI.Dispatching;
using TinyClips.Core.Support;
using Windows.Services.Store;

namespace TinyClips.App.Services.Support;

/// <summary>
/// Supporter plans as Microsoft Store add-ons. One instance belongs to one window: the Store's
/// purchase dialog is owned by that window, so a desktop app must hand <see cref="StoreContext"/>
/// its handle. Products and licenses are matched by the Partner Center product ID.
/// </summary>
internal sealed class MicrosoftStoreSupporterStore : ISupporterStore
{
    // Microsoft documents both durable and subscription add-ons as the "Durable" product kind.
    private static readonly string[] AddOnKinds = ["Durable"];

    private readonly StoreContext _context;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<SupporterPlan, string> _storeIds = [];

    public MicrosoftStoreSupporterStore(nint ownerWindow)
    {
        _context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(_context, ownerWindow);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _context.OfflineLicensesChanged += OnOfflineLicensesChanged;
    }

    public event EventHandler? LicensesChanged;

    public async Task<IReadOnlyList<SupporterOffer>> LoadOffersAsync()
    {
        var result = await _context.GetAssociatedStoreProductsAsync(AddOnKinds);
        if (result.ExtendedError is { } error)
        {
            throw new InvalidOperationException("The Microsoft Store did not return its add-ons.", error);
        }

        _storeIds.Clear();
        var offers = new List<SupporterOffer>();
        foreach (var product in result.Products.Values)
        {
            if (SupporterPlans.FromProductId(product.InAppOfferToken) is { } plan)
            {
                _storeIds[plan] = product.StoreId;
                offers.Add(new SupporterOffer(plan, product.Price.FormattedPrice));
            }
        }

        offers.Sort((left, right) => left.Plan.CompareTo(right.Plan));
        return offers;
    }

    public async Task<SupporterPlan?> GetActivePlanAsync()
    {
        var license = await _context.GetAppLicenseAsync()
            ?? throw new InvalidOperationException("The Microsoft Store did not return a license.");

        // Only valid licenses are in this collection. StoreLicense.IsActive is reserved and always true.
        return SupporterPlans.MostLasting(license.AddOnLicenses.Values.Select(addOn => addOn.InAppOfferToken));
    }

    public async Task<SupporterPurchaseOutcome> PurchaseAsync(SupporterPlan plan)
    {
        if (!_storeIds.TryGetValue(plan, out var storeId))
        {
            return SupporterPurchaseOutcome.Unavailable;
        }

        var result = await _context.RequestPurchaseAsync(storeId);
        return result.Status switch
        {
            StorePurchaseStatus.Succeeded => SupporterPurchaseOutcome.Purchased,
            StorePurchaseStatus.AlreadyPurchased => SupporterPurchaseOutcome.AlreadyOwned,
            StorePurchaseStatus.NotPurchased => SupporterPurchaseOutcome.Canceled,
            StorePurchaseStatus.NetworkError => SupporterPurchaseOutcome.NetworkError,
            _ => SupporterPurchaseOutcome.ServerError,
        };
    }

    public void Dispose() => _context.OfflineLicensesChanged -= OnOfflineLicensesChanged;

    private void OnOfflineLicensesChanged(StoreContext sender, object args) =>
        _dispatcher.TryEnqueue(() => LicensesChanged?.Invoke(this, EventArgs.Empty));
}
