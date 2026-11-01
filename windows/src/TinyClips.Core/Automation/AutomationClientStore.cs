using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyClips.Core.Automation;

/// <summary>A paired automation client. Only a SHA-256 hash of its token is kept.</summary>
public sealed record AutomationClient
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 token.</summary>
    public required string TokenHash { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }
}

/// <summary>
/// Paired clients, persisted to <c>automation-clients.json</c>. Thread-safe. Raw tokens are
/// returned once from <see cref="Add"/> and never stored.
/// </summary>
public sealed class AutomationClientStore
{
    public const string FileName = "automation-clients.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Persisting on every command would rewrite the file constantly; LastUsedAt only needs to be roughly right.
    private static readonly TimeSpan LastUsedPersistInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly TimeProvider _time;
    private List<AutomationClient> _clients;

    public AutomationClientStore(string filePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _time = timeProvider ?? TimeProvider.System;
        _clients = ReadFile(filePath);
    }

    /// <summary>The default location next to the app's other local data.</summary>
    public static string DefaultFilePath(string localDataDirectory) =>
        Path.Combine(localDataDirectory, "TinyClips", FileName);

    public event EventHandler? Changed;

    public string FilePath => _filePath;

    public IReadOnlyList<AutomationClient> List()
    {
        lock (_gate)
        {
            return _clients.ToArray();
        }
    }

    /// <summary>Adds a client and returns it with its raw token, which is not kept.</summary>
    public (AutomationClient Client, string Token) Add(string name, IEnumerable<string> scopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(scopes);

        var token = CreateToken();
        var client = new AutomationClient
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Scopes = scopes.Distinct(StringComparer.Ordinal).ToArray(),
            TokenHash = HashToken(token),
            CreatedAt = _time.GetUtcNow(),
        };

        lock (_gate)
        {
            _clients.Add(client);
            WriteFile();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return (client, token);
    }

    /// <summary>Finds the client a raw token belongs to, or null.</summary>
    public AutomationClient? FindByToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var hash = Encoding.ASCII.GetBytes(HashToken(token));
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(client.TokenHash)))
                {
                    return client;
                }
            }
        }

        return null;
    }

    public bool Revoke(string clientId)
    {
        bool removed;
        lock (_gate)
        {
            removed = _clients.RemoveAll(c => c.Id == clientId) > 0;
            if (removed)
            {
                WriteFile();
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            if (_clients.Count == 0)
            {
                return;
            }

            _clients.Clear();
            WriteFile();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void TouchLastUsed(string clientId)
    {
        var now = _time.GetUtcNow();
        var persisted = false;
        lock (_gate)
        {
            var index = _clients.FindIndex(c => c.Id == clientId);
            if (index < 0)
            {
                return;
            }

            var previous = _clients[index].LastUsedAt;
            _clients[index] = _clients[index] with { LastUsedAt = now };
            if (previous is null || now - previous.Value >= LastUsedPersistInterval)
            {
                WriteFile();
                persisted = true;
            }
        }

        if (persisted)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>32 random bytes, base64url without padding.</summary>
    public static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 token.</summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<AutomationClient> ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<ClientsFile>(File.ReadAllText(path), _jsonOptions);
            return file?.Clients?.Where(c => !string.IsNullOrEmpty(c.TokenHash)).ToList() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"AutomationClientStore: could not read {path}: {ex.Message}");
            return [];
        }
    }

    private void WriteFile()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new ClientsFile { Version = 1, Clients = _clients }, _jsonOptions);
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _filePath, overwrite: true);
    }

    private sealed class ClientsFile
    {
        public int Version { get; set; }

        public List<AutomationClient>? Clients { get; set; }
    }
}
