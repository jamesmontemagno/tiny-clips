using System;
using System.Runtime.InteropServices;

namespace TinyClips.App;

internal enum QuickFeedbackType
{
    Bug,
    FeatureRequest,
}

internal static class QuickBugReport
{
    public static string GetAppVersion() => GetPackageVersion().ToString();

    public static string GetAppFeedbackVersion()
    {
        var version = GetPackageVersion();
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    public static string GetAppBuild() => Math.Max(GetPackageVersion().Revision, 0).ToString();

    public static string GetDistributionChannel() => BuildFlavor.IsStoreBuild ? "Microsoft Store" : "Direct Download / Winget";

    public static Uri BuildDetailedIssueRequestUri(string version)
    {
        const string repositoryIssuesNewUrl = "https://github.com/jamesmontemagno/tiny-clips/issues/new";
        var runtime = RuntimeInformation.OSDescription;
        var body =
            "### Details" + "\n" +
            "- App: Tiny Clips for Windows" + "\n" +
            $"- Version: {version}" + "\n" +
            $"- OS: {runtime}" + "\n\n" +
            "### Describe your issue or feature request" + "\n" +
            "<!-- Tell us what happened or what you'd like to see -->";

        var title = "[Issue/Feature]: ";
        var query = $"title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        return new Uri($"{repositoryIssuesNewUrl}?{query}");
    }

    public static Uri BuildQuickRequestUri(
        QuickFeedbackType type,
        string title,
        string description,
        string version,
        string build,
        string distribution)
    {
        var isFeatureRequest = type == QuickFeedbackType.FeatureRequest;
        var components = new UriBuilder("https://github.com/jamesmontemagno/tiny-clips/issues/new");
        var query =
            $"template={Uri.EscapeDataString(isFeatureRequest ? "quick_feature_request.yml" : "quick_bug_report.yml")}" +
            $"&labels={Uri.EscapeDataString(isFeatureRequest ? "enhancement" : "bug")}" +
            $"&title={Uri.EscapeDataString((isFeatureRequest ? "[Feature]: " : "[Bug]: ") + title)}" +
            $"&{Uri.EscapeDataString(isFeatureRequest ? "request" : "happened")}={Uri.EscapeDataString(description)}" +
            $"&platform={Uri.EscapeDataString("Windows")}" +
            $"&version={Uri.EscapeDataString(version)}" +
            $"&build={Uri.EscapeDataString(build)}" +
            $"&distribution={Uri.EscapeDataString(distribution)}" +
            $"&os={Uri.EscapeDataString(RuntimeInformation.OSDescription)}";
        components.Query = query;
        return components.Uri;
    }

    private static Version GetPackageVersion()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(version.Major, version.Minor, version.Build, version.Revision);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);
        }
    }

}
