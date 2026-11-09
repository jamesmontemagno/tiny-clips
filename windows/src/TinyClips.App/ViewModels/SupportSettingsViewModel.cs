using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Models.Support;
using TinyClips.Core.Support;

namespace TinyClips.App.ViewModels;

/// <summary>
/// Settings → Support: the optional supporter plans, and the thank-you once the account has one.
/// Nothing here gates a feature. Every store failure becomes a message on the page.
/// </summary>
public sealed partial class SupportSettingsViewModel : ObservableObject
{
    private readonly ISupporterStore _store;
    private SupporterPlan? _confirmedPlan;
    private int _latestLicenseRead;
    private bool _detached;

    public SupportSettingsViewModel(ISupporterStore store)
    {
        _store = store;
        _store.LicensesChanged += OnLicensesChanged;
    }

    public ObservableCollection<SupporterOfferItem> Offers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsOffers), nameof(IsUnavailable), nameof(ShowsRestore))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(PurchaseCommand), nameof(LoadCommand))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(PurchaseCommand), nameof(LoadCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsSupporter),
        nameof(ActivePlanText),
        nameof(ShowsManageSubscription),
        nameof(ShowsOffers),
        nameof(IsUnavailable))]
    private SupporterPlan? _activePlan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _messageText = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    public bool HasMessage => MessageText.Length > 0;

    public bool IsSupporter => ActivePlan is not null;

    public string ActivePlanText => ActivePlan is { } plan
        ? $"{SupporterOfferItem.TitleFor(plan)}. Thank you for supporting independent development!"
        : string.Empty;

    public bool ShowsManageSubscription => ActivePlan is { } plan && SupporterPlans.IsSubscription(plan);

    public bool ShowsOffers => !IsLoading && !IsSupporter && Offers.Count > 0;

    public bool IsUnavailable => !IsLoading && !IsSupporter && Offers.Count == 0;

    public bool ShowsRestore => !IsLoading;

    private bool CanAct => !IsLoading && !IsBusy;

    /// <summary>Reads the license and the offers. Safe to await from any UI event: it never throws.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    public async Task LoadAsync()
    {
        IsLoading = true;
        ClearMessage();

        var plan = await TryReadActivePlanAsync();
        var offers = await TryLoadOffersAsync();
        if (_detached)
        {
            return;
        }

        if (plan.Succeeded)
        {
            ActivePlan = plan.Plan ?? _confirmedPlan;
        }

        Offers.Clear();
        foreach (var offer in offers)
        {
            Offers.Add(new SupporterOfferItem(offer, PurchaseCommand));
        }

        IsLoading = false;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task PurchaseAsync(SupporterOfferItem? offer)
    {
        if (offer is null)
        {
            return;
        }

        IsBusy = true;
        ClearMessage();
        try
        {
            var outcome = await _store.PurchaseAsync(offer.Plan);
            if (outcome is SupporterPurchaseOutcome.Purchased or SupporterPurchaseOutcome.AlreadyOwned)
            {
                // The Store's answer is final, even if the license it hands back has not caught up.
                _confirmedPlan = offer.Plan;
                var plan = await TryReadActivePlanAsync();
                if (_detached)
                {
                    return;
                }

                ActivePlan = plan.Plan ?? offer.Plan;
            }

            if (!_detached)
            {
                ShowPurchaseOutcome(outcome);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"The supporter purchase failed: {ex}");
            if (!_detached)
            {
                ShowMessage(InfoBarSeverity.Error, "The Microsoft Store could not finish the purchase. Try again later.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        ClearMessage();
        try
        {
            var plan = await TryReadActivePlanAsync();
            if (_detached)
            {
                return;
            }

            if (!plan.Succeeded)
            {
                ShowMessage(InfoBarSeverity.Error, "The Microsoft Store could not be reached. Check your connection and try again.");
                return;
            }

            ActivePlan = plan.Plan ?? _confirmedPlan;
            if (IsSupporter)
            {
                ShowMessage(InfoBarSeverity.Success, "Your supporter status is active. Thank you!");
            }
            else
            {
                ShowMessage(
                    InfoBarSeverity.Informational,
                    "We couldn't find a Tiny Clips supporter purchase on this Microsoft account. If you purchased with a different account, sign in to the Microsoft Store with that account and try again.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Stops listening to the store and releases it, for when the Settings window closes.</summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _store.LicensesChanged -= OnLicensesChanged;
        _store.Dispose();
    }

    private async void OnLicensesChanged(object? sender, EventArgs args)
    {
        // A purchase or restore reads the license itself when it finishes. A read that fails here
        // leaves the page as it is: nobody asked, so an error would come from nowhere.
        if (IsBusy || IsLoading)
        {
            return;
        }

        var plan = await TryReadActivePlanAsync();
        if (!_detached && plan.Succeeded)
        {
            ActivePlan = plan.Plan ?? _confirmedPlan;
        }
    }

    private async Task<(bool Succeeded, SupporterPlan? Plan)> TryReadActivePlanAsync()
    {
        var read = ++_latestLicenseRead;
        try
        {
            var plan = await _store.GetActivePlanAsync();

            // A newer read has the newer answer, so this one no longer counts.
            return read == _latestLicenseRead ? (true, plan) : (false, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reading the supporter license failed: {ex}");
            return (false, null);
        }
    }

    private async Task<IReadOnlyList<SupporterOffer>> TryLoadOffersAsync()
    {
        try
        {
            return await _store.LoadOffersAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loading the supporter plans failed: {ex}");
            return [];
        }
    }

    private void ShowPurchaseOutcome(SupporterPurchaseOutcome outcome)
    {
        switch (outcome)
        {
            case SupporterPurchaseOutcome.Purchased:
                ShowMessage(InfoBarSeverity.Success, "Thank you for supporting Tiny Clips!");
                break;
            case SupporterPurchaseOutcome.AlreadyOwned:
                ShowMessage(InfoBarSeverity.Success, "You're already a supporter. Thank you!");
                break;
            case SupporterPurchaseOutcome.NetworkError:
                ShowMessage(InfoBarSeverity.Error, "The Microsoft Store could not be reached. Check your connection and try again.");
                break;
            case SupporterPurchaseOutcome.ServerError:
                ShowMessage(InfoBarSeverity.Error, "The Microsoft Store could not finish the purchase. Try again later.");
                break;
            case SupporterPurchaseOutcome.Unavailable:
                ShowMessage(InfoBarSeverity.Error, "This plan isn't available right now. Try again later.");
                break;
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string text)
    {
        MessageSeverity = severity;
        MessageText = text;
    }

    private void ClearMessage() => MessageText = string.Empty;
}
