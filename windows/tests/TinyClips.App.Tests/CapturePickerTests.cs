using System.Xml.Linq;

namespace TinyClips.App.Tests;

public sealed class CapturePickerTests
{
    [Fact]
    public void ScreenshotPicker_DoesNotDuplicateTrayTextRecognition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "CapturePickerWindow.xaml");
        var document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var automationIds = document
            .Descendants()
            .Select(element => (string?)element.Attribute("AutomationProperties.AutomationId"))
            .Where(id => id is not null)
            .ToArray();
        var names = document
            .Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToArray();

        Assert.Contains("CaptureRegionButton", automationIds);
        Assert.Contains("CaptureScreenButton", automationIds);
        Assert.Contains("CaptureWindowButton", automationIds);
        Assert.Contains("CaptureScrollButton", automationIds);
        Assert.Equal("Tiny Clips capture picker", (string?)document.Root?.Attribute("Title"));
        Assert.DoesNotContain("RecognizeTextButton", automationIds);
        Assert.DoesNotContain("RecognizeTextButton", names);
    }

    [Fact]
    public void TrayAndCaptureStatusWindows_HaveMeaningfulTitles()
    {
        Assert.Contains("Title = \"Tiny Clips\";", ReadWindowSource("TrayPopupWindow.cs"), StringComparison.Ordinal);
        Assert.Contains("Title = \"Tiny Clips capture status\";", ReadWindowSource("AutomationNotificationAnnouncer.cs"), StringComparison.Ordinal);
    }

    private static string ReadWindowSource(string name)
    {
        using var stream = typeof(CapturePickerTests).Assembly.GetManifestResourceStream($"WindowTitles.{name}")
            ?? throw new InvalidOperationException($"{name} is not in the test assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
