using System.Xml.Linq;

namespace TinyClips.App.Tests;

public sealed class ScreenshotOutputUiMarkupTests
{
    [Fact]
    public void EditorHasAnAccessibleInitiallyClosedOutputWarning()
    {
        var document = XDocument.Parse(Read("ScreenshotEditorWindow.xaml"));
        var warning = Assert.Single(document.Descendants(), element =>
            Attribute(element, "AutomationProperties.AutomationId") == "ScreenshotEditorOutputStatus");

        Assert.Equal("InfoBar", warning.Name.LocalName);
        Assert.Equal("Polite", Attribute(warning, "AutomationProperties.LiveSetting"));
        Assert.Equal("False", Attribute(warning, "IsOpen"));
        Assert.Equal("Warning", Attribute(warning, "Severity"));
    }

    [Fact]
    public void RecoveryProvidesAWindowOwnedPickerAndKeepsTheImageOnCancellation()
    {
        var source = Read("ScreenshotRecoveryWindow.cs");
        Assert.Contains("ScreenshotRecoverySaveAsButton", source);
        Assert.Contains("InitializeWithWindow.Initialize(picker", source);
        Assert.Contains("await picker.PickSaveFileAsync()", source);
        Assert.Contains("file is not null && await _saveAs(file.Path)", source);
        Assert.Contains("Your screenshot is still available", source);
    }

    [Fact]
    public void StandaloneWarningOffersAccessibleActionsWithoutDependingOnToastNotifications()
    {
        var source = Read("ScreenshotOutputNoticeWindow.cs");
        Assert.Contains("ScreenshotOutputNoticeStatus", source);
        Assert.Contains("ScreenshotOutputNoticeActionButton", source);
        Assert.Contains("ScreenshotOutputNoticeCloseButton", source);
        Assert.DoesNotContain("ShowMessageNotification", source);
    }

    private static string Read(string name)
    {
        using var stream = typeof(ScreenshotOutputUiMarkupTests).Assembly
            .GetManifestResourceStream("ScreenshotOutputMarkup." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;
}
