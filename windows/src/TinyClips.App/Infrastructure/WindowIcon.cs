using System.Diagnostics;
using Microsoft.UI.Windowing;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Sets the taskbar / Alt+Tab icon of a standard window.
/// <para>
/// <see cref="AppWindow.SetIcon(string)"/> requires a fully qualified path: a relative path is
/// resolved against the process working directory, which is only the package folder when the app
/// is started from Start or Explorer. Launchers that start the exe with some other working
/// directory (the winget validation harness uses <c>E:\</c>) would otherwise get a
/// <see cref="System.IO.FileNotFoundException"/> thrown from <c>Window.Activated</c>, which XAML
/// treats as fatal once startup has completed (0xC000027B). So the path is resolved against the
/// executable's directory and any failure is logged instead of propagated.
/// </para>
/// </summary>
internal static class WindowIcon
{
    private const string RelativeIconPath = @"Assets\AppIcon.ico";

    private static readonly Lazy<string?> ResolvedIconPath = new(ResolveIconPath);

    public static void Apply(AppWindow appWindow)
    {
        var path = ResolvedIconPath.Value;
        if (path is null)
        {
            return;
        }

        try
        {
            appWindow.SetIcon(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AppWindow.SetIcon failed for '{path}': {ex}");
            CrashDiagnostics.Log("WindowIcon.Apply", ex, handled: true);
        }
    }

    private static string? ResolveIconPath()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, RelativeIconPath);
            if (File.Exists(path))
            {
                return path;
            }

            Debug.WriteLine($"Window icon not found at '{path}'.");
            CrashDiagnostics.Log(
                "WindowIcon.Resolve",
                new FileNotFoundException("Window icon not found.", path),
                handled: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Window icon path resolution failed: {ex}");
            CrashDiagnostics.Log("WindowIcon.Resolve", ex, handled: true);
        }

        return null;
    }
}
