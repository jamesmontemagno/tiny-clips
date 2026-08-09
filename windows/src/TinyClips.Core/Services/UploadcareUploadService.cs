using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TinyClips.Core.Services;

/// <summary>
/// Streams Tiny Clips files to Uploadcare's direct or multipart upload APIs.
/// </summary>
public sealed class UploadcareUploadService : IUploadcareUploadService
{
    private const long DefaultDirectUploadLimitBytes = 100L * 1024 * 1024;
    private const int MultipartPartSizeBytes = 8 * 1024 * 1024;
    private static readonly Uri BaseUploadUri = new("https://upload.uploadcare.com/base/");
    private static readonly Uri MultipartStartUri = new("https://upload.uploadcare.com/multipart/start/");
    private static readonly Uri MultipartCompleteUri = new("https://upload.uploadcare.com/multipart/complete/");

    private readonly HttpClient _httpClient;
    private readonly ICaptureSettings _settings;
    private readonly IUploadcareCredentialStore _credentials;
    private readonly long _directUploadLimitBytes;

    public UploadcareUploadService(
        HttpClient httpClient,
        ICaptureSettings settings,
        IUploadcareCredentialStore credentials,
        long directUploadLimitBytes = DefaultDirectUploadLimitBytes)
    {
        if (directUploadLimitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(directUploadLimitBytes));
        }

        _httpClient = httpClient;
        _settings = settings;
        _credentials = credentials;
        _directUploadLimitBytes = directUploadLimitBytes;
    }

    public async Task<UploadcareUploadResult> UploadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var publicKey = _settings.UploadcarePublicKey.Trim();
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new UploadcareUploadException("Enter an Uploadcare public key before uploading.");
        }

        var file = new FileInfo(filePath);
        if (!file.Exists)
        {
            throw new UploadcareUploadException("The capture is no longer available to upload.");
        }

        if (file.Length == 0)
        {
            throw new UploadcareUploadException("Empty files cannot be uploaded.");
        }

        var signing = CreateSigningParameters(_credentials.GetSecretKey());
        try
        {
            return file.Length < _directUploadLimitBytes
                ? await UploadDirectAsync(file, publicKey, signing, cancellationToken)
                : await UploadMultipartAsync(file, publicKey, signing, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UploadcareUploadException("Uploadcare upload timed out. Try again.", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UploadcareUploadException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new UploadcareUploadException(
                "Uploadcare could not be reached. Check your connection and try again.",
                ex);
        }
        catch (IOException ex)
        {
            throw new UploadcareUploadException("The capture could not be read for upload.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UploadcareUploadException("Tiny Clips cannot read this capture for upload.", ex);
        }
        catch (JsonException ex)
        {
            throw new UploadcareUploadException("Uploadcare returned an unexpected response.", ex);
        }
    }

    private async Task<UploadcareUploadResult> UploadDirectAsync(
        FileInfo file,
        string publicKey,
        SigningParameters? signing,
        CancellationToken cancellationToken)
    {
        using var content = CreateFormContent(publicKey, signing);
        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(file.Extension));
        content.Add(fileContent, "file", file.Name);

        using var response = await _httpClient.PostAsync(BaseUploadUri, content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var fileId = ReadDirectUploadFileId(responseBody);
        return CreateResult(fileId);
    }

    private async Task<UploadcareUploadResult> UploadMultipartAsync(
        FileInfo file,
        string publicKey,
        SigningParameters? signing,
        CancellationToken cancellationToken)
    {
        using var startContent = CreateFormContent(publicKey, signing);
        startContent.Add(new StringContent(file.Name), "filename");
        startContent.Add(new StringContent(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), "size");
        startContent.Add(new StringContent(MultipartPartSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)), "part_size");
        startContent.Add(new StringContent(GetContentType(file.Extension)), "content_type");

        using var startResponse = await _httpClient.PostAsync(MultipartStartUri, startContent, cancellationToken);
        await EnsureSuccessAsync(startResponse, cancellationToken);
        var startBody = await startResponse.Content.ReadAsStringAsync(cancellationToken);
        var upload = ReadMultipartStart(startBody);
        var expectedPartCount = (file.Length + MultipartPartSizeBytes - 1) / MultipartPartSizeBytes;
        if (upload.Parts.Count != expectedPartCount)
        {
            throw new UploadcareUploadException("Uploadcare returned an invalid multipart upload session.");
        }

        for (var index = 0; index < upload.Parts.Count; index++)
        {
            var offset = index * (long)MultipartPartSizeBytes;
            var length = Math.Min(MultipartPartSizeBytes, file.Length - offset);
            using var partContent = new FileRangeContent(file.FullName, offset, length);
            using var request = new HttpRequestMessage(HttpMethod.Put, upload.Parts[index])
            {
                Content = partContent,
            };
            using var partResponse = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            await EnsureSuccessAsync(partResponse, cancellationToken);
        }

        using var completeContent = new MultipartFormDataContent();
        completeContent.Add(new StringContent(publicKey), "UPLOADCARE_PUB_KEY");
        completeContent.Add(new StringContent(upload.FileId), "uuid");
        AddSigningParameters(completeContent, signing);
        using var completeResponse = await _httpClient.PostAsync(MultipartCompleteUri, completeContent, cancellationToken);
        await EnsureSuccessAsync(completeResponse, cancellationToken);
        var completeBody = await completeResponse.Content.ReadAsStringAsync(cancellationToken);
        var fileId = ReadMultipartCompleteFileId(completeBody);
        return CreateResult(fileId);
    }

    private static MultipartFormDataContent CreateFormContent(string publicKey, SigningParameters? signing)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(publicKey), "UPLOADCARE_PUB_KEY");
        content.Add(new StringContent("auto"), "UPLOADCARE_STORE");
        AddSigningParameters(content, signing);
        return content;
    }

    private static void AddSigningParameters(MultipartFormDataContent content, SigningParameters? signing)
    {
        if (signing is { } parameters)
        {
            content.Add(new StringContent(parameters.Signature), "signature");
            content.Add(new StringContent(parameters.Expire), "expire");
        }
    }

    private static SigningParameters? CreateSigningParameters(string? secretKey)
    {
        var normalizedSecretKey = secretKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSecretKey))
        {
            return null;
        }

        var expire = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(normalizedSecretKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(expire))).ToLowerInvariant();
        return new SigningParameters(signature, expire);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        await response.Content.LoadIntoBufferAsync(cancellationToken);
        throw new UploadcareUploadException(
            $"Uploadcare rejected the upload ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }

    private static MultipartStart ReadMultipartStart(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var fileId = ReadFileId(root, "uuid");
        if (!root.TryGetProperty("parts", out var partsElement) ||
            partsElement.ValueKind != JsonValueKind.Array)
        {
            throw new UploadcareUploadException("Uploadcare returned an invalid multipart upload session.");
        }

        var parts = partsElement.EnumerateArray()
            .Select(part => part.GetString())
            .Where(part => Uri.TryCreate(part, UriKind.Absolute, out _))
            .Cast<string>()
            .ToList();
        if (parts.Count == 0)
        {
            throw new UploadcareUploadException("Uploadcare returned an invalid multipart upload session.");
        }

        return new MultipartStart(fileId, parts);
    }

    private static string ReadDirectUploadFileId(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                IsFileId(property.Value.GetString()))
            {
                return property.Value.GetString()!;
            }
        }

        throw new UploadcareUploadException("Uploadcare returned an invalid file identifier.");
    }

    private static string ReadMultipartCompleteFileId(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return ReadFileId(document.RootElement, "uuid");
    }

    private static string ReadFileId(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            IsFileId(property.GetString()))
        {
            return property.GetString()!;
        }

        throw new UploadcareUploadException("Uploadcare returned an invalid file identifier.");
    }

    private static bool IsFileId(string? value) => Guid.TryParse(value, out _);

    private static UploadcareUploadResult CreateResult(string fileId) =>
        new(fileId, new Uri($"https://ucarecdn.com/{fileId}/", UriKind.Absolute));

    private static string GetContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private sealed record SigningParameters(string Signature, string Expire);

    private sealed record MultipartStart(string FileId, IReadOnlyList<string> Parts);

    private sealed class FileRangeContent : HttpContent
    {
        private readonly string _path;
        private readonly long _offset;
        private readonly long _length;

        public FileRangeContent(string path, long offset, long length)
        {
            _path = path;
            _offset = offset;
            _length = length;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await SerializeRangeAsync(stream, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            SerializeRangeAsync(stream, cancellationToken);

        private async Task SerializeRangeAsync(Stream destination, CancellationToken cancellationToken)
        {
            await using var source = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            source.Seek(_offset, SeekOrigin.Begin);
            var remaining = _length;
            var buffer = new byte[Math.Min(81_920, (int)Math.Min(remaining, 81_920))];
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException("The capture changed while it was being uploaded.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
    }
}
