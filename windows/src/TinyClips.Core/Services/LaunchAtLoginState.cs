namespace TinyClips.Core.Services;

/// <summary>
/// Reflects the real launch-at-login state as reported by the OS. For an
/// MSIX-packaged app this maps onto <c>Windows.ApplicationModel.StartupTaskState</c>;
/// the <c>DisabledByUser</c>/<c>DisabledByPolicy</c>/<c>EnabledByPolicy</c> values let
/// the Settings UI explain when Windows — not the app — owns the toggle.
/// </summary>
public enum LaunchAtLoginState
{
    /// <summary>Off, and the app may turn it on.</summary>
    Disabled,

    /// <summary>On, and the app may turn it off.</summary>
    Enabled,

    /// <summary>The user turned it off in Windows Settings/Task Manager; the app cannot re-enable it.</summary>
    DisabledByUser,

    /// <summary>Group policy forces it off; the app cannot change it.</summary>
    DisabledByPolicy,

    /// <summary>Group policy forces it on; the app cannot change it.</summary>
    EnabledByPolicy,

    /// <summary>Launch at login could not be queried (e.g. unsupported install).</summary>
    Unavailable,
}

public static class LaunchAtLoginStateExtensions
{
    /// <summary>True when Tiny Clips will start at sign-in, whoever made that choice.</summary>
    public static bool IsOn(this LaunchAtLoginState state) =>
        state is LaunchAtLoginState.Enabled or LaunchAtLoginState.EnabledByPolicy;

    /// <summary>True when Windows lets the app change the state.</summary>
    public static bool CanChange(this LaunchAtLoginState state) =>
        state is LaunchAtLoginState.Enabled or LaunchAtLoginState.Disabled;

    /// <summary>Explains a state the app cannot change; empty when the app can change it.</summary>
    public static string Describe(this LaunchAtLoginState state) => state switch
    {
        LaunchAtLoginState.DisabledByUser =>
            "Turned off in Windows Settings \u2192 Apps \u2192 Startup. Re-enable Tiny Clips there to allow launch at login.",
        LaunchAtLoginState.DisabledByPolicy => "Launch at login is disabled by your organization's policy.",
        LaunchAtLoginState.EnabledByPolicy => "Launch at login is enabled by your organization's policy.",
        LaunchAtLoginState.Unavailable => "Launch at login isn't available for this installation.",
        _ => string.Empty,
    };
}
