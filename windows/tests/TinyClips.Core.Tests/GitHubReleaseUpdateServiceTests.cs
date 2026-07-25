using System.Net;
using System.Net.Http;
using System.Text;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class GitHubReleaseUpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsUpdateAvailable_WhenLatestIsGreater()
    {
        var json = """
            [
              { "tag_name": "v1.6.0-windows", "html_url": "https://github.com/jamesmontemagno/tiny-clips/releases/tag/v1.6.0-windows", "draft": false, "prerelease": false }
            ]
            """;
        var service = CreateService(json);

        var result = await service.CheckForUpdatesAsync(new Version(1, 5, 2));

        Assert.Equal(AppUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 6, 0), result.LatestVersion);
        Assert.NotNull(result.ReleaseUri);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsUpToDate_WhenLatestIsNotGreater()
    {
        var json = """
            [
              { "tag_name": "1.5.2-windows", "html_url": "https://github.com/jamesmontemagno/tiny-clips/releases/tag/v1.5.2-windows", "draft": false, "prerelease": false }
            ]
            """;
        var service = CreateService(json);

        var result = await service.CheckForUpdatesAsync(new Version(1, 5, 2));

        Assert.Equal(AppUpdateStatus.UpToDate, result.Status);
        Assert.Equal(new Version(1, 5, 2), result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsPrereleaseAndDraft_WhenSelectingLatestStable()
    {
        var json = """
            [
              { "tag_name": "v2.0.0-beta.1-windows", "html_url": "https://example.invalid/beta", "draft": false, "prerelease": true },
              { "tag_name": "v1.6.0-windows", "html_url": "https://github.com/jamesmontemagno/tiny-clips/releases/tag/v1.6.0-windows", "draft": false, "prerelease": false }
            ]
            """;
        var service = CreateService(json);

        var result = await service.CheckForUpdatesAsync(new Version(1, 5, 2));

        Assert.Equal(AppUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 6, 0), result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsFailed_OnServerError()
    {
        var service = CreateService("[]", HttpStatusCode.Forbidden);

        var result = await service.CheckForUpdatesAsync(new Version(1, 5, 2));

        Assert.Equal(AppUpdateStatus.CheckFailed, result.Status);
        Assert.Contains("403", result.Message);
    }

    [Fact]
    public void TryParseVersionTag_ReturnsFalse_ForNegativeSegment()
    {
        var parsed = GitHubReleaseUpdateService.TryParseVersionTag("v1.-1.0", out _);

        Assert.False(parsed);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_IgnoresNegativeVersionTag_AndContinues()
    {
        var json = """
            [
              { "tag_name": "v1.-1.0-windows", "html_url": "https://example.invalid/invalid", "draft": false, "prerelease": false },
              { "tag_name": "v1.6.0-windows", "html_url": "https://github.com/jamesmontemagno/tiny-clips/releases/tag/v1.6.0-windows", "draft": false, "prerelease": false }
            ]
            """;
        var service = CreateService(json);

        var result = await service.CheckForUpdatesAsync(new Version(1, 5, 2));

        Assert.Equal(AppUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 6, 0), result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_IgnoresMacReleases_AndSelectsHighestWindowsVersion()
    {
        var json = """
            [
              { "tag_name": "v9.0.0-mac", "html_url": "https://example.invalid/mac", "draft": false, "prerelease": false },
              { "tag_name": "v1.5.3-windows", "html_url": "https://example.invalid/windows-older", "draft": false, "prerelease": false },
              { "tag_name": "v1.6.0-windows", "html_url": "https://example.invalid/windows-latest", "draft": false, "prerelease": false }
            ]
            """;
        var service = CreateService(json);

        var result = await service.CheckForUpdatesAsync(new Version(1, 5, 2));

        Assert.Equal(AppUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 6, 0), result.LatestVersion);
        Assert.Equal("https://example.invalid/windows-latest", result.ReleaseUri?.ToString().TrimEnd('/'));
    }

    private static GitHubReleaseUpdateService CreateService(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var client = new HttpClient(handler);
        return new GitHubReleaseUpdateService(client, "https://example.invalid/releases");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
