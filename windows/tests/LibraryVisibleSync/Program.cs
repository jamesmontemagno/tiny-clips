using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private static int _cases;

    private static void Main(string[] args)
    {
        Test();
        Console.WriteLine($"Passed {_cases} differential/identity/notification cases.");
        if (args.Length == 0)
            return;
        if (args.Length != 2 || args[1] is not ("before-first" or "after-first"))
            throw new ArgumentException("Usage: [output.json before-first|after-first]");
        Measure(args[0], args[1]);
    }

    private static void Test()
    {
        foreach (var size in new[] { 0, 1, 12, 1000, 10000 })
        {
            var items = Items(size);
            Compare(items, items.ToList(), noEvents: true);
            var host = new After();
            foreach (var item in items)
                host.Clips.Add(item);
            var events = Observe(host);
            host.SyncVisibleClips(host.Clips);
            Require(events.Count == 0, "Aliased identical list notified");
            _cases++;
        }

        var a = new ClipItemViewModel(1, "same");
        var b = new ClipItemViewModel(2, "same");
        var c = new ClipItemViewModel(3, "third");
        var d = new ClipItemViewModel(4, "fourth");
        Compare([a], [b], noEvents: false);
        Compare([a, b], [b, a]);
        Compare([a, b, c], [a, b, d]);
        Compare([a, b, c], [d, a, b]);
        Compare([a, b, c], [a, c]);
        Compare([a, c], [a, b, c]);
        Compare([a, b], []);
        Compare([], [a, b]);
        // Duplicate references are not produced by ApplyQuery's path-keyed items.
        // If a changed target contains them, the legacy fallback still owns semantics.
        Compare([a, b], [a, a, b], expectedFailure: true);
        Compare([a, b, a], [a, b]);
        Compare([a, a, b], [b, a, a]);

        var random = new Random(73519);
        var pool = Items(30);
        for (var i = 0; i < 250; i++)
        {
            var initial = pool.OrderBy(_ => random.Next()).Take(random.Next(31)).ToList();
            var target = pool.OrderBy(_ => random.Next()).Take(random.Next(31)).ToList();
            Compare(initial, target);
        }

        var before = new Before();
        var after = new After();
        foreach (var target in new List<ClipItemViewModel>[] { [a, b, c], [b], [b], [], [c, b, a], [a] })
        {
            CompareHosts(before, after, target, noEvents: false);
        }

        var metadataHost = new After();
        metadataHost.Clips.Add(a);
        var notifications = Observe(metadataHost);
        a.DisplayName = "edited outside synchronization";
        metadataHost.SyncVisibleClips([a]);
        Require(ReferenceEquals(a, metadataHost.Clips[0]) && metadataHost.Clips[0].DisplayName == a.DisplayName,
            "Updated item was replaced");
        Require(notifications.Count == 0, "Metadata-only same-reference sync notified");
        _cases++;
    }

    private static void Compare(List<ClipItemViewModel> initial, List<ClipItemViewModel> target,
        bool noEvents = false, bool expectedFailure = false)
    {
        var before = new Before();
        var after = new After();
        foreach (var item in initial)
        {
            before.Clips.Add(item);
            after.Clips.Add(item);
        }
        CompareHosts(before, after, target, noEvents, expectedFailure);
    }

    private static void CompareHosts(Host before, Host after, IReadOnlyList<ClipItemViewModel> target,
        bool noEvents, bool expectedFailure = false)
    {
        var oldEvents = Observe(before);
        var newEvents = Observe(after);
        Require(SyncFails(before, target) == expectedFailure, "Unexpected baseline exception behavior");
        Require(SyncFails(after, target) == expectedFailure, "Exception behavior changed");
        Require(before.Clips.Count == after.Clips.Count, "Count differs");
        for (var i = 0; i < before.Clips.Count; i++)
            Require(ReferenceEquals(before.Clips[i], after.Clips[i]), "Reference/order differs");
        Require(oldEvents.SequenceEqual(newEvents), "Observable event sequence differs");
        Require(!noEvents || newEvents.Count == 0, "No-op notified");
        _cases++;
    }

    private static bool SyncFails(Host host, IReadOnlyList<ClipItemViewModel> target)
    {
        try
        {
            host.SyncVisibleClips(target);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static List<string> Observe(Host host)
    {
        var events = new List<string>();
        ((INotifyPropertyChanged)host.Clips).PropertyChanged += (_, e) => events.Add("Property:" + e.PropertyName);
        host.Clips.CollectionChanged += (_, e) => events.Add(
            $"{e.Action}:{e.OldStartingIndex}:{e.NewStartingIndex}:{Ids(e.OldItems)}:{Ids(e.NewItems)}");
        return events;
    }

    private static string Ids(IList? items) =>
        items is null ? "" : string.Join(",", items.Cast<ClipItemViewModel>().Select(i => i.Id));

    private static List<ClipItemViewModel> Items(int size) =>
        Enumerable.Range(0, size).Select(i => new ClipItemViewModel(i, $"clip-{i}")).ToList();

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Measure(string output, string order)
    {
        var results = new List<object>();
        foreach (var size in new[] { 0, 12, 1000, 10000 })
        {
            var target = Items(size);
            var hosts = order == "before-first" ? new Host[] { new Before(), new After() } : [new After(), new Before()];
            foreach (var host in hosts)
            {
                foreach (var item in target)
                    host.Clips.Add(item);
                var notifications = 0;
                host.Clips.CollectionChanged += (_, _) => notifications++;
                var batch = size <= 12 ? 100 : 1;
                for (var warmup = 0; warmup < 5; warmup++)
                    for (var i = 0; i < batch; i++)
                        host.SyncVisibleClips(target);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var samples = new List<object>();
                using var process = Process.GetCurrentProcess();
                for (var sample = 0; sample < 20; sample++)
                {
                    process.Refresh();
                    var rssBefore = process.WorkingSet64;
                    var cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
                    var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var start = Stopwatch.GetTimestamp();
                    for (var i = 0; i < batch; i++)
                        host.SyncVisibleClips(target);
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                    var gc = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - gcBefore[g]).ToArray();
                    process.Refresh();
                    samples.Add(new
                    {
                        msPerOperation = elapsed / batch,
                        bytesPerOperation = allocated / (double)batch,
                        cpuMsPerOperation = (process.TotalProcessorTime.TotalMilliseconds - cpuBefore) / batch,
                        gc, rssBefore, rssAfter = process.WorkingSet64,
                    });
                }
                Require(notifications == 0, "Measured no-op emitted notifications");
                results.Add(new { algorithm = host.GetType().Name, size, batch, notifications, samples });
                Console.WriteLine($"Measured {host.GetType().Name} N={size}");
            }
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            utc = DateTimeOffset.UtcNow, order, cases = _cases,
            source = JsonDocument.Parse(File.ReadAllText("obj/source-manifest.json")).RootElement.Clone(),
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            processors = Environment.ProcessorCount, gcServer = System.Runtime.GCSettings.IsServerGC,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            results,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal abstract class Host
{
    public ObservableCollection<ClipItemViewModel> Clips { get; } = [];
    public abstract void SyncVisibleClips(IReadOnlyList<ClipItemViewModel> target);
}

// UI-free stand-in: the sealed production item also uses object reference equality.
internal sealed class ClipItemViewModel(int id, string displayName)
{
    public int Id { get; } = id;
    public string DisplayName { get; set; } = displayName;
}
