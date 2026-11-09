using System.Windows.Input;
using TinyClips.Core.Support;

namespace TinyClips.App.Models.Support;

/// <summary>One plan on the Support page: what it is called, what it costs, and how it is bought.</summary>
public sealed partial class SupporterOfferItem
{
    public SupporterOfferItem(SupporterOffer offer, ICommand purchaseCommand)
    {
        Plan = offer.Plan;
        PurchaseCommand = purchaseCommand;
        Title = TitleFor(offer.Plan);
        Description = offer.Plan switch
        {
            SupporterPlan.Monthly => "Billed every month until you cancel.",
            SupporterPlan.Yearly => "Billed every year until you cancel.",
            _ => "A single payment that doesn't renew.",
        };
        Badge = offer.Plan switch
        {
            SupporterPlan.Yearly => "Amazing",
            SupporterPlan.OneTime => "Most generous",
            _ => string.Empty,
        };
        PurchaseLabel = offer.Plan switch
        {
            SupporterPlan.Monthly => $"Subscribe: {offer.FormattedPrice}/month",
            SupporterPlan.Yearly => $"Subscribe: {offer.FormattedPrice}/year",
            _ => $"Buy: {offer.FormattedPrice}",
        };
        PurchaseAutomationName = $"{PurchaseLabel}. {Title}. Opens the Microsoft Store purchase dialog.";
    }

    public SupporterPlan Plan { get; }

    public ICommand PurchaseCommand { get; }

    public string Title { get; }

    public string Description { get; }

    public string Badge { get; }

    public bool HasBadge => Badge.Length > 0;

    public string PurchaseLabel { get; }

    public string PurchaseAutomationName { get; }

    public static string TitleFor(SupporterPlan plan) => plan switch
    {
        SupporterPlan.Monthly => "Monthly supporter",
        SupporterPlan.Yearly => "Yearly supporter",
        _ => "One-time supporter",
    };
}
