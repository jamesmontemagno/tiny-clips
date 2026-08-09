namespace TinyClips.Core.Services;

public sealed class UploadcareUploadException : Exception
{
    public UploadcareUploadException(string message)
        : base(message)
    {
    }

    public UploadcareUploadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
