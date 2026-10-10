using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

public sealed partial class QuickBugReportWindow : Window
{
	// 480×500 DIP: keeps the fixed button-bar footer and enough scrollable form area
	// visible at any display density. Form opens at 620×640 by default.
	private const int MinimumWidthDip  = 480;
	private const int MinimumHeightDip = 500;
	private const int FeatureRequestSelectionIndex = 1;

	private readonly string _version;
	private readonly string _build;
	private readonly string _distribution;
	private readonly WindowChromeController _chromeController;
	private QuickFeedbackType _activeFeedbackType = QuickFeedbackType.Bug;
	private string _bugTitle = string.Empty;
	private string _bugDescription = string.Empty;
	private string _featureTitle = string.Empty;
	private string _featureDescription = string.Empty;

	public QuickBugReportWindow(string version, string build, string distribution)
	{
		_version = version;
		_build = build;
		_distribution = distribution;

		InitializeComponent();

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
		AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, 620, 640);

		// WindowChromeController owns: icon-on-activation, DIP minimum enforcement, XamlRoot
		// scale tracking, and cleanup of all three on Closed. The window's own Close() calls
		// in event handlers are not lifecycle subscriptions, so no additive handler is needed.
		_chromeController = new WindowChromeController(this, RootGrid, MinimumWidthDip, MinimumHeightDip);

		var settings = App.Services.GetRequiredService<ICaptureSettings>();
		RootGrid.RequestedTheme = settings.Theme switch
		{
			AppTheme.Light => ElementTheme.Light,
			AppTheme.Dark => ElementTheme.Dark,
			_ => ElementTheme.Default,
		};

		AppInfoText.Text =
			$"Automatically included: Windows, Tiny Clips v{version} (build {build}), {distribution}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";

		FeedbackTypeButtons.SelectedIndex = 0;
		FeedbackTypeButtons.SelectionChanged += OnFeedbackTypeChanged;
		UpdateFeedbackType();
	}

	private void OnFeedbackTextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
	{
		UpdateSubmitState();
	}

	private void OnFeedbackTypeChanged(object sender, SelectionChangedEventArgs e)
	{
		StoreActiveDraft();
		_activeFeedbackType = SelectedFeedbackType;
		LoadActiveDraft();
		UpdateFeedbackType();
	}

	private void UpdateFeedbackType()
	{
		var isFeatureRequest = SelectedFeedbackType == QuickFeedbackType.FeatureRequest;
		HeadingText.Text = isFeatureRequest ? "Suggest a feature" : "Tell us what happened";
		TitleBox.Header = isFeatureRequest ? "Feature title" : "Bug title";
		TitleBox.PlaceholderText = isFeatureRequest ? "A short summary of your idea" : "A short summary of the problem";
		AutomationProperties.SetName(TitleBox, isFeatureRequest ? "Feature request title" : "Bug title");
		DescriptionBox.Header = isFeatureRequest ? "What feature would you like to see?" : "What happened?";
		DescriptionBox.PlaceholderText = isFeatureRequest
			? "Describe the feature you would like to see."
			: "What did you expect, and what happened instead?";
		AutomationProperties.SetName(
			DescriptionBox,
			isFeatureRequest ? "Feature request details" : "What happened");
		SubmitFeedbackButton.Content = isFeatureRequest ? "Request feature on GitHub" : "File bug on GitHub";
		AutomationProperties.SetName(
			SubmitFeedbackButton,
			isFeatureRequest ? "Request feature on GitHub" : "File bug on GitHub");
		Title = isFeatureRequest ? "Tiny Clips — Request a Feature" : "Tiny Clips — File a Bug";
		AppTitleBar.Title = isFeatureRequest ? "Request a Feature" : "File a Bug";
		UpdateSubmitState();
	}

	private void UpdateSubmitState()
	{
		SubmitFeedbackButton.IsEnabled =
			!string.IsNullOrWhiteSpace(TitleBox.Text) &&
			!string.IsNullOrWhiteSpace(DescriptionBox.Text);
	}

	private void StoreActiveDraft()
	{
		if (_activeFeedbackType == QuickFeedbackType.Bug)
		{
			_bugTitle = TitleBox.Text;
			_bugDescription = DescriptionBox.Text;
		}
		else
		{
			_featureTitle = TitleBox.Text;
			_featureDescription = DescriptionBox.Text;
		}
	}

	private void LoadActiveDraft()
	{
		if (_activeFeedbackType == QuickFeedbackType.Bug)
		{
			TitleBox.Text = _bugTitle;
			DescriptionBox.Text = _bugDescription;
		}
		else
		{
			TitleBox.Text = _featureTitle;
			DescriptionBox.Text = _featureDescription;
		}
	}

	private void OnCancelClicked(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnSubmitFeedbackClicked(object sender, RoutedEventArgs e)
	{
		var issueUri = QuickBugReport.BuildQuickRequestUri(
			SelectedFeedbackType,
			TitleBox.Text.Trim(),
			DescriptionBox.Text.Trim(),
			_version,
			_build,
			_distribution);

		Process.Start(new ProcessStartInfo(issueUri.ToString()) { UseShellExecute = true });
		Close();
	}

	private QuickFeedbackType SelectedFeedbackType =>
		FeedbackTypeButtons.SelectedIndex == FeatureRequestSelectionIndex
			? QuickFeedbackType.FeatureRequest
			: QuickFeedbackType.Bug;
}
