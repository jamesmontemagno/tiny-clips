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
