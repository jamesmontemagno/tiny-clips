namespace TinyClips.Core.Services;

public interface IUploadcareUploadService
{
    Task<UploadcareUploadResult> UploadAsync(string filePath, CancellationToken cancellationToken = default);
}
