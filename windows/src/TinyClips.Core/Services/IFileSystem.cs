namespace TinyClips.Core.Services;

public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    string GetFolderPath(Environment.SpecialFolder folder);
    IEnumerable<string> EnumerateFiles(string directory);
    DateTimeOffset GetFileLastWriteTime(string path);
    long GetFileSizeBytes(string path);
    void DeleteFile(string path);
}
