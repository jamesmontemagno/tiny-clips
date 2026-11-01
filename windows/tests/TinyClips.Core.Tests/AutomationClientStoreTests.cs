using System.Text.Json;
using TinyClips.Core.Automation;

namespace TinyClips.Core.Tests;

public sealed class AutomationClientStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TinyClipsAutomationTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, AutomationClientStore.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void CreateToken_Is32RandomBytesAsBase64Url()
    {
        var token = AutomationClientStore.CreateToken();
        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.NotEqual(token, AutomationClientStore.CreateToken());
    }

    [Fact]
    public void HashToken_IsLowercaseSha256Hex()
    {
        Assert.Equal(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            AutomationClientStore.HashToken("hello"));
    }

    [Fact]
    public void Add_PersistsOnlyTheHash_AndFindsByToken()
    {
        var store = new AutomationClientStore(FilePath);
        var (client, token) = store.Add("Copilot CLI", ["read", "capture", "read"]);

        Assert.Equal(["read", "capture"], client.Scopes);
        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain(token, json);
        Assert.Contains(AutomationClientStore.HashToken(token), json);

        var reloaded = new AutomationClientStore(FilePath);
        Assert.Equal(client.Id, reloaded.FindByToken(token)?.Id);
        Assert.Null(reloaded.FindByToken("not-a-token"));
        Assert.Null(reloaded.FindByToken(null));
    }

    [Fact]
    public void Revoke_RemovesClient_AndPersists()
    {
        var store = new AutomationClientStore(FilePath);
        var (client, token) = store.Add("Agent", ["read"]);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        Assert.True(store.Revoke(client.Id));
        Assert.False(store.Revoke(client.Id));
        Assert.Equal(1, changes);
        Assert.Null(store.FindByToken(token));
        Assert.Empty(new AutomationClientStore(FilePath).List());
    }

    [Fact]
    public void TouchLastUsed_RecordsTime()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new AutomationClientStore(FilePath, time);
        var (client, _) = store.Add("Agent", ["read"]);
        Assert.Null(client.LastUsedAt);

        time.Now = time.Now.AddMinutes(5);
        store.TouchLastUsed(client.Id);
        Assert.Equal(time.Now, store.List().Single().LastUsedAt);
        Assert.Equal(time.Now, new AutomationClientStore(FilePath).List().Single().LastUsedAt);
    }

    [Fact]
    public void TouchLastUsed_PersistsRegularActivityFromTheLastWrite()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new AutomationClientStore(FilePath, time);
        var (client, _) = store.Add("Agent", ["read"]);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        store.TouchLastUsed(client.Id);
        var firstWrite = time.Now;
        time.Now = time.Now.AddSeconds(30);
        store.TouchLastUsed(client.Id);
        Assert.Equal(time.Now, store.List().Single().LastUsedAt);
        Assert.Equal(firstWrite, new AutomationClientStore(FilePath).List().Single().LastUsedAt);
        Assert.Equal(1, changes);

        time.Now = time.Now.AddSeconds(30);
        store.TouchLastUsed(client.Id);
        Assert.Equal(time.Now, new AutomationClientStore(FilePath).List().Single().LastUsedAt);
        Assert.Equal(2, changes);

        time.Now = time.Now.AddSeconds(30);
        store.TouchLastUsed(client.Id);
        store.Add("Another agent", ["read"]);
        var writeFromAdd = time.Now;
        time.Now = time.Now.AddSeconds(30);
        store.TouchLastUsed(client.Id);
        Assert.Equal(writeFromAdd, new AutomationClientStore(FilePath).List().Single(c => c.Id == client.Id).LastUsedAt);
        Assert.Equal(3, changes);

        time.Now = time.Now.AddSeconds(30);
        store.TouchLastUsed(client.Id);
        Assert.Equal(time.Now, new AutomationClientStore(FilePath).List().Single(c => c.Id == client.Id).LastUsedAt);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void CorruptFile_LoadsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(new AutomationClientStore(FilePath).List());
    }

    [Fact]
    public void AuditLog_WritesJsonLines_AndRotates()
    {
        var path = Path.Combine(_directory, AutomationAuditLog.FileName);
        var log = new AutomationAuditLog(path, maxBytes: 400);
        for (var i = 0; i < 10; i++)
        {
            log.Write(new AutomationAuditEntry
            {
                Timestamp = DateTimeOffset.UnixEpoch,
                Command = "app.status",
                Outcome = "ok",
                ClientName = "Agent",
            });
        }

        Assert.True(File.Exists(log.RotatedFilePath));
        Assert.True(new FileInfo(path).Length <= 400);
        foreach (var line in File.ReadAllLines(path))
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("app.status", document.RootElement.GetProperty("command").GetString());
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
