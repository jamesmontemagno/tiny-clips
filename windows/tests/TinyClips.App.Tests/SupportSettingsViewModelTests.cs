using Microsoft.UI.Xaml.Controls;
using TinyClips.App.ViewModels;
using TinyClips.Core.Support;

namespace TinyClips.App.Tests;

public class SupportSettingsViewModelTests
{
    private static readonly SupporterOffer[] AllOffers =
    [
        new(SupporterPlan.Monthly, "$1.99"),
        new(SupporterPlan.Yearly, "$14.99"),
        new(SupporterPlan.OneTime, "$29.99"),
    ];

    [Fact]
    public async Task WithNoPlan_ShowsEveryOfferAtTheStoresPrice()
    {
        var vm = new SupportSettingsViewModel(new FakeStore());

        Assert.True(vm.IsLoading);
        await vm.LoadAsync();

        Assert.False(vm.IsLoading);
        Assert.True(vm.ShowsOffers);
        Assert.False(vm.IsSupporter);
        Assert.Equal(
            ["Subscribe: $1.99/month", "Subscribe: $14.99/year", "Buy: $29.99"],
            vm.Offers.Select(offer => offer.PurchaseLabel));
    }

    [Theory]
    [InlineData(SupporterPlan.Monthly, true)]
    [InlineData(SupporterPlan.Yearly, true)]
    [InlineData(SupporterPlan.OneTime, false)]
    public async Task WithAPlan_ThanksTheSupporter_AndOffersManageOnlyForSubscriptions(SupporterPlan plan, bool manages)
    {
        var vm = new SupportSettingsViewModel(new FakeStore { ActivePlan = plan });

        await vm.LoadAsync();

        Assert.True(vm.IsSupporter);
        Assert.False(vm.ShowsOffers);
        Assert.Equal(manages, vm.ShowsManageSubscription);
        Assert.Contains("Thank you", vm.ActivePlanText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenOffersFail_IsUnavailable_AndTryAgainRecovers()
    {
        var store = new FakeStore { OffersFail = true };
        var vm = new SupportSettingsViewModel(store);

        await vm.LoadAsync();
        Assert.True(vm.IsUnavailable);
        Assert.False(vm.ShowsOffers);

        store.OffersFail = false;
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.False(vm.IsUnavailable);
        Assert.True(vm.ShowsOffers);
    }

    [Fact]
    public async Task ASupporter_StaysThanked_WhenOffersFail()
    {
        var vm = new SupportSettingsViewModel(new FakeStore { ActivePlan = SupporterPlan.Yearly, OffersFail = true });

        await vm.LoadAsync();

        Assert.True(vm.IsSupporter);
        Assert.False(vm.IsUnavailable);
    }

    [Fact]
    public async Task APurchase_MakesASupporter_EvenBeforeTheLicenseCatchesUp()
    {
        var store = new FakeStore { Outcome = SupporterPurchaseOutcome.Purchased };
        var vm = new SupportSettingsViewModel(store);
        await vm.LoadAsync();

        await vm.PurchaseCommand.ExecuteAsync(vm.Offers[1]);

        Assert.Equal(SupporterPlan.Yearly, store.Purchased);
        Assert.Equal(SupporterPlan.Yearly, vm.ActivePlan);
        Assert.Equal(InfoBarSeverity.Success, vm.MessageSeverity);

        // A license event that still reads as nothing does not take the thank-you away.
        store.RaiseLicensesChanged();
        Assert.Equal(SupporterPlan.Yearly, vm.ActivePlan);
    }

    [Fact]
    public async Task ACanceledPurchase_SaysNothing()
    {
        var vm = new SupportSettingsViewModel(new FakeStore { Outcome = SupporterPurchaseOutcome.Canceled });
        await vm.LoadAsync();

        await vm.PurchaseCommand.ExecuteAsync(vm.Offers[0]);

        Assert.False(vm.HasMessage);
        Assert.False(vm.IsSupporter);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(SupporterPurchaseOutcome.NetworkError)]
    [InlineData(SupporterPurchaseOutcome.ServerError)]
    [InlineData(SupporterPurchaseOutcome.Unavailable)]
    public async Task AFailedPurchase_ShowsAnError_AndGrantsNothing(SupporterPurchaseOutcome outcome)
    {
        var vm = new SupportSettingsViewModel(new FakeStore { Outcome = outcome });
        await vm.LoadAsync();

        await vm.PurchaseCommand.ExecuteAsync(vm.Offers[0]);

        Assert.Equal(InfoBarSeverity.Error, vm.MessageSeverity);
        Assert.True(vm.HasMessage);
        Assert.False(vm.IsSupporter);
    }

    [Fact]
    public async Task APurchaseThatThrows_ShowsAnError_AndEndsTheBusyState()
    {
        var vm = new SupportSettingsViewModel(new FakeStore { PurchaseFails = true });
        await vm.LoadAsync();

        await vm.PurchaseCommand.ExecuteAsync(vm.Offers[0]);

        Assert.Equal(InfoBarSeverity.Error, vm.MessageSeverity);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Restore_TellsFound_FromNothingFound_FromCouldNotCheck()
    {
        var store = new FakeStore();
        var vm = new SupportSettingsViewModel(store);
        await vm.LoadAsync();

        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(InfoBarSeverity.Informational, vm.MessageSeverity);
        Assert.Contains("couldn't find", vm.MessageText, StringComparison.Ordinal);

        store.LicenseFails = true;
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(InfoBarSeverity.Error, vm.MessageSeverity);

        store.LicenseFails = false;
        store.ActivePlan = SupporterPlan.OneTime;
        await vm.RestoreCommand.ExecuteAsync(null);
        Assert.Equal(InfoBarSeverity.Success, vm.MessageSeverity);
        Assert.Equal(SupporterPlan.OneTime, vm.ActivePlan);
    }

    [Fact]
    public async Task ALicenseChange_UpdatesThePage_AndAFailedReadLeavesItAlone()
    {
        var store = new FakeStore { ActivePlan = SupporterPlan.Monthly };
        var vm = new SupportSettingsViewModel(store);
        await vm.LoadAsync();

        store.LicenseFails = true;
        store.ActivePlan = null;
        store.RaiseLicensesChanged();
        Assert.Equal(SupporterPlan.Monthly, vm.ActivePlan);
        Assert.False(vm.HasMessage);

        store.LicenseFails = false;
        store.RaiseLicensesChanged();
        Assert.False(vm.IsSupporter);
        Assert.True(vm.ShowsOffers);
    }

    [Fact]
    public async Task Detach_ReleasesTheStore_AndALateAnswerChangesNothing()
    {
        var store = new FakeStore { ActivePlan = SupporterPlan.Yearly, LicenseGate = new TaskCompletionSource() };
        var vm = new SupportSettingsViewModel(store);
        var load = vm.LoadAsync();

        vm.Detach();
        vm.Detach();
        store.LicenseGate.SetResult();
        await load;

        Assert.Equal(1, store.Disposals);
        Assert.Null(vm.ActivePlan);
        Assert.Empty(vm.Offers);
        Assert.False(store.HasLicenseListeners);
    }

    [Fact]
    public void TheNavigationItem_StartsHidden_SoOnlyTheStoreBuildShowsIt()
    {
        var item = SettingsMarkup.Elements("SettingsWindow.xaml")
            .Single(element => element.Name.LocalName == "NavigationViewItem" && element.Attr("Tag") == "Support");

        Assert.Equal("Collapsed", item.Attr("Visibility"));
        Assert.Contains(
            "SupportNavigationItem.Visibility = BuildFlavor.IsStoreBuild ? Visibility.Visible : Visibility.Collapsed",
            SettingsMarkup.Read("SettingsWindow.xaml.cs"),
            StringComparison.Ordinal);
    }

    private sealed class FakeStore : ISupporterStore
    {
        private EventHandler? _licensesChanged;

        public event EventHandler? LicensesChanged
        {
            add => _licensesChanged += value;
            remove => _licensesChanged -= value;
        }

        public SupporterPlan? ActivePlan { get; set; }

        public SupporterPurchaseOutcome Outcome { get; init; } = SupporterPurchaseOutcome.Canceled;

        public bool OffersFail { get; set; }

        public bool LicenseFails { get; set; }

        public bool PurchaseFails { get; init; }

        public TaskCompletionSource? LicenseGate { get; init; }

        public SupporterPlan? Purchased { get; private set; }

        public int Disposals { get; private set; }

        public bool HasLicenseListeners => _licensesChanged is not null;

        public Task<IReadOnlyList<SupporterOffer>> LoadOffersAsync() => OffersFail
            ? throw new InvalidOperationException("No offers.")
            : Task.FromResult<IReadOnlyList<SupporterOffer>>(AllOffers);

        public async Task<SupporterPlan?> GetActivePlanAsync()
        {
            if (LicenseGate is not null)
            {
                await LicenseGate.Task;
            }

            return LicenseFails ? throw new InvalidOperationException("No license.") : ActivePlan;
        }

        public Task<SupporterPurchaseOutcome> PurchaseAsync(SupporterPlan plan)
        {
            Purchased = plan;
            return PurchaseFails ? throw new InvalidOperationException("No purchase.") : Task.FromResult(Outcome);
        }

        public void RaiseLicensesChanged() => _licensesChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose() => Disposals++;
    }
}
