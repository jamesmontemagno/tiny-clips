namespace TinyClips.Core.Services;

public interface IAppUpdateService
{
    AppUpdateCheckResult? LastResult { get; }

    Task<AppUpdateCheckResult> CheckForUpdatesAsync(Version currentVersion, CancellationToken cancellationToken = default);
}
