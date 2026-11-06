using System.Text.Json.Nodes;
using TinyClips.Core.Capture;
using TinyClips.Core.Editing.Themes;
using TinyClips.Core.Models;

namespace TinyClips.App;

/// <summary>What the recording UI is doing, as reported by <c>recording.status</c>.</summary>
internal sealed record AutomationRecordingSnapshot(string State, bool Busy, CaptureType? Kind);
internal sealed record AutomationScrollingSnapshot(bool Active, bool Stopping, int FrameCount);

/// <summary>
/// The parts of <see cref="App"/> that agent commands drive. Every member is called on the UI
/// thread by <see cref="AppAutomationCommandHandler"/>.
/// </summary>
internal interface IAutomationAppActions
{
    AutomationRecordingSnapshot GetRecordingSnapshot();

    /// <summary>Opens <c>settings</c>, <c>library</c>, or <c>guide</c>.</summary>
    void OpenAutomationWindow(string window);

    /// <summary>The display a screen capture uses when no displayId is given.</summary>
    MonitorInfo? GetPreferredAutomationMonitor();

    /// <summary>
    /// Captures a display (optionally a display-local region) or a window without a picker,
    /// saves it to the library, and returns the saved path and pixel size.
    /// </summary>
    Task<(string Path, int Width, int Height)> CaptureForAutomationAsync(CaptureTarget target, PixelRect? region, CancellationToken cancellationToken);

    /// <summary>Recognizes text in a display, region, or window without a picker or clipboard change.</summary>
    Task<string> RecognizeTextForAutomationAsync(CaptureTarget target, PixelRect? region, CancellationToken cancellationToken);

    /// <summary>Adds a file the agent created to recent captures and shows the saved toast.</summary>
    void RecordAutomationScreenshot(string path);

    /// <summary>Adds a video, GIF, or screenshot the agent created to recent captures and shows the saved toast.</summary>
    void RecordAutomationCapture(string path, CaptureType type);

    /// <summary>Starts a targeted recording without showing the picker or recording setup.</summary>
    Task BeginTargetedAutomationRecordingAsync(CaptureType type, bool studio, CaptureTarget target, PixelRect? region);

    Task StartAutomationScrollingCaptureAsync(CaptureTarget target, PixelRect? region);

    AutomationScrollingSnapshot GetScrollingSnapshot();

    Task<string> StopAutomationScrollingCaptureAsync();

    void CancelAutomationScrollingCapture();

    Task StopRecordingForAutomationAsync();

    Task PauseRecordingForAutomationAsync();

    Task ResumeRecordingForAutomationAsync();

    /// <summary>Returns false when there is no recording that can be restarted.</summary>
    Task<bool> RestartRecordingForAutomationAsync();

    Task DiscardRecordingForAutomationAsync();

    /// <summary>
    /// Opens a clip or Studio project and returns the editor it opened in. A screenshot editor
    /// uses <paramref name="theme"/> instead of the active theme.
    /// </summary>
    Task<string> OpenEditorForAutomationAsync(string path, CaptureType? type, EditorTheme? theme);

    /// <summary>Reads or edits a Studio project, suggests zooms, or exports its current snapshot.</summary>
    Task<JsonNode> ExecuteStudioAutomationCommandAsync(string commandName, JsonObject parameters, CancellationToken cancellationToken);

    void RevealForAutomation(string path);

    void RecordUploadLinkForAutomation(string path, string url);

    /// <summary>Lets open windows pick up a setting an agent changed.</summary>
    void OnAutomationSettingChanged(string key);
}
