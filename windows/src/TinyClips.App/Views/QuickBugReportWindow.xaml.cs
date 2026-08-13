using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace TinyClips.App;

public sealed partial class QuickBugReportWindow : Window
{
	private readonly string _version;
	private readonly string _distribution;

	public QuickBugReportWindow(string version, string distribution)
	{
		_version = version;
		_distribution = distribution;

		InitializeComponent();

		ExtendsContentIntoTitleBar = true;
		SetTitleBar(AppTitleBar);
		var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
		AppWindowPlacement.CenterInCurrentWorkAreaAtDipSize(AppWindow, hwnd, 620, 640);

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
