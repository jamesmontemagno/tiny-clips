using System.Diagnostics;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace TinyClips.Core.Services;

/// <summary>
/// Launch-at-login backed by the MSIX <see cref="StartupTask"/> API. The startup
/// task is declared as a <c>windows.startupTask</c> extension in the package manifest
/// (<c>TaskId == <see cref="TaskId"/></c>) so the registration survives version updates —
/// unlike the old <c>HKCU\...\Run</c> value, which pointed at the versioned
/// <c>WindowsApps\&lt;PackageFullName&gt;</c> path and broke after every update.
///
/// When the app runs unpackaged (developer F5), the StartupTask API is unavailable, so
/// the service falls back to the legacy registry approach purely to keep dev runs working.
/// </summary>
public sealed class LaunchAtLoginService : ILaunchAtLoginService
{
    // Must match the TaskId of the windows.startupTask extension in Package.appxmanifest.
    private const string TaskId = "TinyClipsLaunchAtLogin";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TinyClips";

    private static bool? _isPackaged;

    // Only an unpackaged (developer F5) run may use the legacy registry path. A packaged
    // app must never write an HKCU\...\Run value — that is the stale-after-update bug this
    // service exists to remove — so we key off real package identity, not on a GetAsync failure.
    private static bool IsPackaged => _isPackaged ??= DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            return Package.Current is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<LaunchAtLoginState> GetStateAsync()
    {
        if (!IsPackaged)
        {
            return GetRegistryState();
        }

        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return Map(task.State);
        }
        catch (Exception)
        {
            return LaunchAtLoginState.Unavailable;
        }
    }

    public async Task<LaunchAtLoginState> SetEnabledAsync(bool enabled)
    {
        if (!IsPackaged)
        {
            ApplyRegistry(enabled);
            return GetRegistryState();
        }

        try
        {
            var task = await StartupTask.GetAsync(TaskId);

            if (enabled)
            {
                if (task.State == StartupTaskState.Disabled)
                {
                    // Can surface a one-time OS consent prompt; the awaited result is authoritative.
                    return Map(await task.RequestEnableAsync());
                }

                return Map(task.State);
            }

            if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            {
                task.Disable();
            }

            // Disable() mutates the registration; re-read so the returned state is current.
            var refreshed = await StartupTask.GetAsync(TaskId);
            return Map(refreshed.State);
        }
        catch (Exception)
        {
            return LaunchAtLoginState.Unavailable;
        }
    }

    private static LaunchAtLoginState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => LaunchAtLoginState.Enabled,
        StartupTaskState.Disabled => LaunchAtLoginState.Disabled,
        StartupTaskState.DisabledByUser => LaunchAtLoginState.DisabledByUser,
        StartupTaskState.DisabledByPolicy => LaunchAtLoginState.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => LaunchAtLoginState.EnabledByPolicy,
        _ => LaunchAtLoginState.Unavailable,
    };

    // --- Registry fallback (unpackaged dev runs only) ---

    private static LaunchAtLoginState GetRegistryState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var storedValue = ((string?)key?.GetValue(RunValueName))?.Trim('"');
            return string.Equals(storedValue, GetExecutablePath(), StringComparison.OrdinalIgnoreCase)
                ? LaunchAtLoginState.Enabled
                : LaunchAtLoginState.Disabled;
        }
        catch
        {
            return LaunchAtLoginState.Disabled;
        }
    }

    private static void ApplyRegistry(bool enabled)
    {
        try
        {
            var executablePath = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                key.SetValue(RunValueName, QuoteExecutablePath(executablePath));
                return;
            }

            key.DeleteValue(RunValueName, false);
        }
        catch
        {
        }
    }

    private static string GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        return Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
    }

    private static string QuoteExecutablePath(string executablePath) => $"\"{executablePath.Trim('"')}\"";
}
