using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyClips.Core.Automation;

/// <summary>One audit log line. Never holds tokens or command parameters.</summary>
public sealed record AutomationAuditEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Command { get; init; }

    /// <summary><c>ok</c>, <c>denied</c>, or <c>error</c>.</summary>
    public required string Outcome { get; init; }

    public string? ClientId { get; init; }

    public string? ClientName { get; init; }

    public string? ErrorCode { get; init; }

    /// <summary>True when the user was asked to approve the command.</summary>
    public bool? Approved { get; init; }
}

/// <summary>
/// Appends every automation command to <c>automation-audit.jsonl</c> (one JSON object per line),
/// rotating to <c>automation-audit.1.jsonl</c> once the file passes <see cref="MaxBytes"/>.
/// Thread-safe; write failures are swallowed so logging never breaks a command.
/// </summary>
public sealed class AutomationAuditLog
{
    public const string FileName = "automation-audit.jsonl";
    public const long DefaultMaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public AutomationAuditLog(string filePath, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        MaxBytes = maxBytes;
    }

    public static string DefaultFilePath(string localDataDirectory) =>
        Path.Combine(localDataDirectory, "TinyClips", FileName);

    public string FilePath { get; }

    public long MaxBytes { get; }

    public string RotatedFilePath => Path.Combine(
        Path.GetDirectoryName(FilePath) ?? string.Empty,
        Path.GetFileNameWithoutExtension(FilePath) + ".1" + Path.GetExtension(FilePath));

    public void Write(AutomationAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, _jsonOptions) + "\n";
        lock (_gate)
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length + Encoding.UTF8.GetByteCount(line) > MaxBytes)
                {
                    File.Move(FilePath, RotatedFilePath, overwrite: true);
                }

                File.AppendAllText(FilePath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"AutomationAuditLog: write failed: {ex.Message}");
            }
        }
    }
}
