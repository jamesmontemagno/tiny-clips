using TinyClips.Core.Services;

namespace TinyClips.App.ViewModels.ClipsLibrary;

/// <summary>
/// Window-owned interactions the library view model cannot perform itself (dialogs need a
/// XamlRoot, Share needs an HWND, editors are owned by <c>App</c>). Implemented by the window.
/// </summary>
public interface IClipsLibraryInteraction
{
    Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, bool destructive);

    Task<string?> PromptTextAsync(string title, string label, string? initialValue, string primaryButtonText, string? placeholder = null);

    Task<string?> PromptChoiceAsync(string title, string label, IReadOnlyList<string> choices, string? current, string primaryButtonText, bool allowNew);

    void Share(IReadOnlyList<string> paths, string title);

    void OpenInEditor(RecentCapture capture);

    void OpenSettings();

    /// <summary>Asks the view to select every visible item (selection lives in the list controls).</summary>
    void SelectAllVisible();

    void ClearSelection();
}
