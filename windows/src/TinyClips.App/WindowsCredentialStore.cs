using Windows.Security.Credentials;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Stores the optional Uploadcare signing key in the Windows Credential Locker, never in app settings.
/// </summary>
public sealed class WindowsCredentialStore : IUploadcareCredentialStore
{
    private const string ResourceName = "TinyClips.Uploadcare";
    private const string UserName = "secret-key";
    private readonly PasswordVault _vault = new();

    public string? GetSecretKey()
    {
        var credential = FindCredential();
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return credential.Password;
    }

    public void SaveSecretKey(string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        RemoveSecretKey();
        _vault.Add(new PasswordCredential(ResourceName, UserName, secretKey));
    }

    public void RemoveSecretKey()
    {
        foreach (var credential in FindCredentials())
        {
            _vault.Remove(credential);
        }
    }

    public bool HasSecretKey() => FindCredential() is not null;

    private PasswordCredential? FindCredential() => FindCredentials().FirstOrDefault();

    private IEnumerable<PasswordCredential> FindCredentials() =>
        _vault.RetrieveAll().Where(credential =>
            string.Equals(credential.Resource, ResourceName, StringComparison.Ordinal) &&
            string.Equals(credential.UserName, UserName, StringComparison.Ordinal));
}
