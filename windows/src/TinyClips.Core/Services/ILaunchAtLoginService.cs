namespace TinyClips.Core.Services;

public interface ILaunchAtLoginService
{
    /// <summary>Reads the current launch-at-login state from the OS.</summary>
    Task<LaunchAtLoginState> GetStateAsync();

    /// <summary>
    /// Requests the desired launch-at-login state and returns the actual resulting
    /// state. The OS may refuse the change (e.g. the user disabled the startup task
    /// in Windows Settings), so callers must reconcile their UI with the return value.
    /// Enabling can surface a one-time OS consent prompt.
    /// </summary>
    Task<LaunchAtLoginState> SetEnabledAsync(bool enabled);
}
