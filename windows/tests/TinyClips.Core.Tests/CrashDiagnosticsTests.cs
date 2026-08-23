using TinyClips.Core.Services;

namespace TinyClips.Core.Tests;

public sealed class CrashDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TinyClipsTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Log_CreatesDirectoryAndWritesEntry()
    {
        var logPath = Path.Combine(_root, "nested", "crash.log");

        CrashDiagnostics.Log(logPath, "TestSource", new InvalidOperationException("boom"), handled: true);

        var content = File.ReadAllText(logPath);
        Assert.Contains("TestSource", content);
        Assert.Contains("handled", content);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("boom", content);
    }

    [Fact]
    public void Log_WithoutExceptionObject_WritesPlaceholder()
    {
        var logPath = Path.Combine(_root, "crash.log");

        CrashDiagnostics.Log(logPath, "TestSource", null, handled: false);

        var content = File.ReadAllText(logPath);
        Assert.Contains("unhandled", content);
        Assert.Contains("(no exception object)", content);
    }

    [Fact]
    public void Log_RollsOversizedFileToPrevious()
    {
        Directory.CreateDirectory(_root);
        var logPath = Path.Combine(_root, "crash.log");
        File.WriteAllText(logPath, new string('x', 600 * 1024));

        CrashDiagnostics.Log(logPath, "TestSource", new Exception("after roll"), handled: true);

        var previousPath = Path.Combine(_root, "crash.previous.log");
        Assert.True(File.Exists(previousPath));
        Assert.True(new FileInfo(logPath).Length < 4 * 1024);
        Assert.Contains("after roll", File.ReadAllText(logPath));
    }

    [Fact]
    public void DirectoryPath_IsSiblingOfTemporaryFiles()
    {
        var tempParent = Path.GetDirectoryName(TinyClipsTemporaryFiles.DirectoryPath);
        var logsParent = Path.GetDirectoryName(CrashDiagnostics.DirectoryPath);

        Assert.Equal(tempParent, logsParent);
        Assert.NotEqual(TinyClipsTemporaryFiles.DirectoryPath, CrashDiagnostics.DirectoryPath);
    }
}
