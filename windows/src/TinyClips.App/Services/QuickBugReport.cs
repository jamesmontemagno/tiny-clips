using System;
using System.Runtime.InteropServices;

namespace TinyClips.App;

internal static class QuickBugReport
{
    public static string GetAppVersion()
    {
        var version = "1.0.0";
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            var asmVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (asmVersion is not null)
            {
                version = asmVersion.ToString();
            }
        }

        return version;
    }

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

    public static Uri BuildQuickBugRequestUri(string title, string happened, string version, string build, string distribution)
    {
        var components = new UriBuilder("https://github.com/jamesmontemagno/tiny-clips/issues/new");
        var query =
            $"template={Uri.EscapeDataString("quick_bug_report.yml")}" +
            $"&labels={Uri.EscapeDataString("bug")}" +
            $"&title={Uri.EscapeDataString("[Bug]: " + title)}" +
            $"&happened={Uri.EscapeDataString(happened)}" +
            $"&platform={Uri.EscapeDataString("Windows")}" +
            $"&version={Uri.EscapeDataString(version)}" +
            $"&build={Uri.EscapeDataString(build)}" +
            $"&distribution={Uri.EscapeDataString(distribution)}" +
            $"&os={Uri.EscapeDataString(RuntimeInformation.OSDescription)}";
        components.Query = query;
        return components.Uri;
    }

}
