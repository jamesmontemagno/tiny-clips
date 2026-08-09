namespace TinyClips.Core.Services;

/// <summary>
/// Provides the Uploadcare secret key from platform secure credential storage.
/// </summary>
public interface IUploadcareCredentialStore
{
    string? GetSecretKey();

    void SaveSecretKey(string secretKey);

    void RemoveSecretKey();

    bool HasSecretKey();
}
