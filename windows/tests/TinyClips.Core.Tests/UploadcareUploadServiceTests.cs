using System.Net;
using System.Text;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class UploadcareUploadServiceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"TinyClips.Tests.{Guid.NewGuid():N}");

    public UploadcareUploadServiceTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task UploadAsync_StreamsDirectUpload_AndDoesNotSendSecretKey()
    {
        var path = WriteFile("capture.png", "capture bytes");
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("https://upload.uploadcare.com/base/", request.RequestUri!.ToString());
            return JsonResponse("""{"capture.png":"22222222-2222-2222-2222-222222222222"}""");
        });
        using var client = new HttpClient(handler);
        var service = CreateService(client, publicKey: "public-key", secretKey: "secret-key");

        var result = await service.UploadAsync(path);

        Assert.Equal("22222222-2222-2222-2222-222222222222", result.FileId);
        Assert.Equal("https://ucarecdn.com/22222222-2222-2222-2222-222222222222/", result.DeliveryUri.AbsoluteUri);
        var body = Assert.Single(handler.RequestBodies).Body;
        Assert.Contains("UPLOADCARE_PUB_KEY", body, StringComparison.Ordinal);
        Assert.Contains("public-key", body, StringComparison.Ordinal);
        Assert.Contains("signature", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", body, StringComparison.Ordinal);
        Assert.Contains("capture bytes", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_UsesMultipartForFilesAtConfiguredLimit()
    {
        var path = WriteFileWithLength("capture.mp4", 8L * 1024 * 1024 + 1);
        var requestPaths = new List<string>();
        var handler = new RecordingHandler(request =>
        {
            requestPaths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/multipart/start/" => JsonResponse(
                    """{"uuid":"33333333-3333-3333-3333-333333333333","parts":["https://parts.example/one","https://parts.example/two"]}"""),
                "/one" or "/two" => new HttpResponseMessage(HttpStatusCode.OK),
                "/multipart/complete/" => JsonResponse("""{"uuid":"33333333-3333-3333-3333-333333333333"}"""),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
            };
        });
        using var client = new HttpClient(handler);
        var service = CreateService(
            client,
            publicKey: "public-key",
            secretKey: "secret-key",
            directUploadLimitBytes: 1);

        var result = await service.UploadAsync(path);

        Assert.Equal("33333333-3333-3333-3333-333333333333", result.FileId);
        Assert.Equal(
            new[] { "/multipart/start/", "/one", "/two", "/multipart/complete/" },
            requestPaths);
        var multipartRequestBodies = handler.RequestBodies.ToDictionary(request => request.Path, request => request.Body);
        Assert.Contains("signature", multipartRequestBodies["/multipart/start/"], StringComparison.Ordinal);
        Assert.Contains("expire", multipartRequestBodies["/multipart/start/"], StringComparison.Ordinal);
        Assert.Contains("signature", multipartRequestBodies["/multipart/complete/"], StringComparison.Ordinal);
        Assert.Contains("expire", multipartRequestBodies["/multipart/complete/"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_RejectsMissingPublicKeyBeforeMakingRequest()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No request expected."));
        using var client = new HttpClient(handler);
        var service = CreateService(client, publicKey: string.Empty);

        var exception = await Assert.ThrowsAsync<UploadcareUploadException>(
            () => service.UploadAsync(WriteFile("capture.gif", "file")));

        Assert.Equal("Enter an Uploadcare public key before uploading.", exception.Message);
        Assert.Empty(handler.RequestBodies);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private UploadcareUploadService CreateService(
        HttpClient client,
        string publicKey,
        string? secretKey = null,
        long directUploadLimitBytes = 100L * 1024 * 1024)
    {
        var settings = new CaptureSettings(new TestSettingsService())
        {
            UploadcarePublicKey = publicKey,
        };
        return new UploadcareUploadService(
            client,
            settings,
            new TestCredentialStore(secretKey),
            directUploadLimitBytes);
    }

    private string WriteFile(string fileName, string contents)
    {
        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllText(path, contents, Encoding.UTF8);
        return path;
    }

    private string WriteFileWithLength(string fileName, long length)
    {
        var path = Path.Combine(_temporaryDirectory, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(length);
        return path;
    }

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null && request.RequestUri?.Host == "upload.uploadcare.com")
            {
                RequestBodies.Add(new RecordedRequest(
                    request.RequestUri.AbsolutePath,
                    await request.Content.ReadAsStringAsync(cancellationToken)));
            }

            return responseFactory(request);
        }

        public sealed record RecordedRequest(string Path, string Body);
    }

    private sealed class TestCredentialStore(string? secretKey) : IUploadcareCredentialStore
    {
        private string? _secretKey = secretKey;

        public string? GetSecretKey() => _secretKey;

        public void SaveSecretKey(string secretKey) => _secretKey = secretKey;

        public void RemoveSecretKey() => _secretKey = null;

        public bool HasSecretKey() => !string.IsNullOrWhiteSpace(_secretKey);
    }

    private sealed class TestSettingsService : ISettingsService
    {
        private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

        public AppTheme Theme { get; set; }

        public string SaveDirectory { get; set; } = string.Empty;

        public T Get<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value!;
    }
}
