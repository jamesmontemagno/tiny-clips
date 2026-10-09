using System.Runtime.InteropServices;
using TinyClips.App;

namespace TinyClips.App.Tests;

public sealed class QuickBugReportTests
{
    [Fact]
    public void BuildQuickRequestUri_BugPrefillsBugTemplateAndAppInfo()
    {
        var uri = QuickBugReport.BuildQuickRequestUri(
            QuickFeedbackType.Bug,
            "Capture & save",
            "It stopped working.\nI expected a screenshot.",
            "1.9.0",
            "42",
            "Direct Download / Winget");
        var values = QueryValues(uri);

        Assert.Equal("quick_bug_report.yml", values["template"]);
        Assert.Equal("bug", values["labels"]);
        Assert.Equal("[Bug]: Capture & save", values["title"]);
        Assert.Equal("It stopped working.\nI expected a screenshot.", values["happened"]);
        Assert.False(values.ContainsKey("request"));
        Assert.Equal("Windows", values["platform"]);
        Assert.Equal("1.9.0", values["version"]);
        Assert.Equal("42", values["build"]);
        Assert.Equal("Direct Download / Winget", values["distribution"]);
        Assert.Equal(RuntimeInformation.OSDescription, values["os"]);
    }

    [Fact]
    public void BuildQuickRequestUri_FeaturePrefillsFeatureTemplateAndAppInfo()
    {
        var uri = QuickBugReport.BuildQuickRequestUri(
            QuickFeedbackType.FeatureRequest,
            "Capture & save",
            "Add a save option.",
            "1.9.0",
            "42",
            "Microsoft Store");
        var values = QueryValues(uri);

        Assert.Equal("quick_feature_request.yml", values["template"]);
        Assert.Equal("enhancement", values["labels"]);
        Assert.Equal("[Feature]: Capture & save", values["title"]);
        Assert.Equal("Add a save option.", values["request"]);
        Assert.False(values.ContainsKey("happened"));
        Assert.Equal("Windows", values["platform"]);
        Assert.Equal("1.9.0", values["version"]);
        Assert.Equal("42", values["build"]);
        Assert.Equal("Microsoft Store", values["distribution"]);
        Assert.Equal(RuntimeInformation.OSDescription, values["os"]);
    }

    private static Dictionary<string, string> QueryValues(Uri uri) =>
        uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .ToDictionary(
                parameter => Uri.UnescapeDataString(parameter[0]),
                parameter => Uri.UnescapeDataString(parameter[1]));
}
