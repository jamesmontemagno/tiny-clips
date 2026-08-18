using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

public sealed partial class QuickBugReportWindow : Window
{
	// 480×500 DIP: keeps the fixed button-bar footer and enough scrollable form area
	// visible at any display density. Form opens at 620×640 by default.
	private const int MinimumWidthDip  = 480;
	private const int MinimumHeightDip = 500;

	private readonly string _version;
	private readonly string _distribution;
	private readonly WindowChromeController _chromeController;

	public QuickBugReportWindow(string version, string distribution)
	{
		_version = version;
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
			$"Automatically included: Windows, Tiny Clips v{version}, {distribution}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
	}

	private void OnReportTextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
	{
		FileBugButton.IsEnabled =
			!string.IsNullOrWhiteSpace(TitleBox.Text) &&
			!string.IsNullOrWhiteSpace(HappenedBox.Text);
	}

	private void OnCancelClicked(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnFileBugClicked(object sender, RoutedEventArgs e)
	{
		var bugUri = QuickBugReport.BuildQuickBugRequestUri(
			TitleBox.Text.Trim(),
			HappenedBox.Text.Trim(),
			_version,
			_version,
			_distribution);

		Process.Start(new ProcessStartInfo(bugUri.ToString()) { UseShellExecute = true });
		Close();
	}
}
