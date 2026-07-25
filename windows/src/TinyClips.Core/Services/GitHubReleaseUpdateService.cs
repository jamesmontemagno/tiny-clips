using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyClips.Core.Services;

public sealed class GitHubReleaseUpdateService : IAppUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly string _releasesApiUrl;

    public AppUpdateCheckResult? LastResult { get; private set; }

    public GitHubReleaseUpdateService(HttpClient httpClient, string? releasesApiUrl = null)
    {
        _httpClient = httpClient;
        _releasesApiUrl = string.IsNullOrWhiteSpace(releasesApiUrl)
            ? "https://api.github.com/repos/jamesmontemagno/tiny-clips/releases?per_page=100"
            : releasesApiUrl;

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TinyClips.Windows", "1.0"));
        }
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Save(AppUpdateCheckResult.Failed(currentVersion, $"GitHub Releases returned {(int)response.StatusCode}."));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (releases is null || releases.Count == 0)
            {
                return Save(AppUpdateCheckResult.Failed(currentVersion, "No releases were returned."));
            }

            GitHubRelease? latestRelease = null;
            Version? latestVersion = null;
            foreach (var release in releases)
            {
                if (release.Draft || release.PreRelease || !IsWindowsReleaseTag(release.TagName))
                {
                    continue;
                }

                if (!TryParseVersionTag(release.TagName, out var releaseVersion))
                {
                    continue;
                }

                if (latestVersion is null || releaseVersion > latestVersion)
                {
                    latestRelease = release;
                    latestVersion = releaseVersion;
                }
            }

            if (latestRelease is null || latestVersion is null)
            {
                return Save(AppUpdateCheckResult.Failed(currentVersion, "No stable Windows release with a parseable version tag was found."));
            }

            Uri? releaseUri = null;
            if (Uri.TryCreate(latestRelease.HtmlUrl, UriKind.Absolute, out var parsedReleaseUri))
            {
                releaseUri = parsedReleaseUri;
            }

            return latestVersion > currentVersion
                ? Save(AppUpdateCheckResult.UpdateAvailable(currentVersion, latestVersion, releaseUri))
                : Save(AppUpdateCheckResult.UpToDate(currentVersion, latestVersion, releaseUri));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Save(AppUpdateCheckResult.Failed(currentVersion, ex.Message));
        }
    }

    internal static bool TryParseVersionTag(string? tag, out Version version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var normalized = tag.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var prereleaseSeparator = normalized.IndexOfAny(['-', '+']);
        if (prereleaseSeparator >= 0)
        {
            normalized = normalized[..prereleaseSeparator];
        }

        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out values[i]) || values[i] < 0)
            {
                return false;
            }
        }

        try
        {
            version = values.Length switch
            {
                2 => new Version(values[0], values[1]),
                3 => new Version(values[0], values[1], values[2]),
                _ => new Version(values[0], values[1], values[2], values[3]),
            };
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool IsWindowsReleaseTag(string? tag) =>
        tag?.EndsWith("-windows", StringComparison.OrdinalIgnoreCase) == true;

    private AppUpdateCheckResult Save(AppUpdateCheckResult result)
    {
        LastResult = result;
        return result;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool PreRelease { get; init; }
    }
}
