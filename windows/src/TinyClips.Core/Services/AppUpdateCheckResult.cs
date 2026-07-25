namespace TinyClips.Core.Services;

public enum AppUpdateStatus
{
    UpToDate,
    UpdateAvailable,
    CheckFailed,
}

public sealed record AppUpdateCheckResult(
    AppUpdateStatus Status,
    Version CurrentVersion,
    Version? LatestVersion,
    Uri? ReleaseUri,
    string? Message)
{
    public static AppUpdateCheckResult UpToDate(Version currentVersion, Version latestVersion, Uri? releaseUri) =>
        new(AppUpdateStatus.UpToDate, currentVersion, latestVersion, releaseUri, null);

    public static AppUpdateCheckResult UpdateAvailable(Version currentVersion, Version latestVersion, Uri? releaseUri) =>
        new(AppUpdateStatus.UpdateAvailable, currentVersion, latestVersion, releaseUri, null);

    public static AppUpdateCheckResult Failed(Version currentVersion, string message) =>
        new(AppUpdateStatus.CheckFailed, currentVersion, null, null, message);
}
