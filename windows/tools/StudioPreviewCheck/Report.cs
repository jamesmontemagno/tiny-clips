using System.Globalization;

namespace TinyClips.Tools.StudioPreviewCheck;

/// <summary>What the tool prints, also written to a file, and the checks that did not hold.</summary>
internal sealed class Report : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly object _gate = new();
    private readonly List<string> _failures = [];
    private string _group = string.Empty;
    private int _checks;

    /// <param name="directory">The folder the report file is written to.</param>
    /// <param name="stamp">When the run started. It names the report file, and the folder that takes what a failed check leaves behind.</param>
    public Report(string directory, string stamp)
    {
        Stamp = stamp;
        Directory.CreateDirectory(directory);
        _file = new StreamWriter(Path.Combine(directory, $"report-{stamp}.txt"), append: false) { AutoFlush = true };
    }

    /// <summary>When the run started, as it stands in the name of the report file.</summary>
    public string Stamp { get; }

    public int Checks => _checks;

    public int FailureCount
    {
        get
        {
            lock (_gate)
            {
                return _failures.Count;
            }
        }
    }

    public void Section(string title)
    {
        _group = title;
        Line();
        Line($"== {title}");
    }

    public void Line(string text = "")
    {
        lock (_gate)
        {
            Console.WriteLine(text);
            _file?.WriteLine(text);
        }
    }

    /// <summary>Records one check. Returns <paramref name="ok"/>.</summary>
    public bool Check(string name, bool ok, string? detail = null)
    {
        lock (_gate)
        {
            _checks++;
            var text = detail is null ? name : $"{name}: {detail}";
            if (ok)
            {
                Console.WriteLine($"  pass  {text}");
                _file?.WriteLine($"  pass  {text}");
            }
            else
            {
                Console.WriteLine($"  FAIL  {text}");
                _file?.WriteLine($"  FAIL  {text}");
                _failures.Add($"[{_group}] {text}");
            }
        }

        return ok;
    }

    /// <summary>A measured number that is reported and not judged.</summary>
    public void Note(string text) => Line($"  note  {text}");

    /// <summary>Prints one line per failed check and returns the process exit code.</summary>
    public int Finish()
    {
        Line();
        lock (_gate)
        {
            if (_failures.Count == 0)
            {
                // A run that checked nothing has not shown anything to be right.
                var all = _checks == 0 ? "RESULT: no check ran" : $"RESULT: all {_checks} checks passed";
                Console.WriteLine(all);
                _file?.WriteLine(all);
                return _checks == 0 ? 1 : 0;
            }

            foreach (var failure in _failures)
            {
                Console.WriteLine($"FAILED: {failure}");
                _file?.WriteLine($"FAILED: {failure}");
            }

            var summary = $"RESULT: {_failures.Count} of {_checks} checks failed";
            Console.WriteLine(summary);
            _file?.WriteLine(summary);
            return 1;
        }
    }

    public void Dispose() => _file?.Dispose();
}

/// <summary>A list of measurements and its usual summary.</summary>
internal sealed class Samples
{
    private readonly List<double> _values = [];

    public int Count => _values.Count;

    public double Mean => _values.Count == 0 ? 0 : _values.Average();

    public double Max => _values.Count == 0 ? 0 : _values.Max();

    public double Min => _values.Count == 0 ? 0 : _values.Min();

    public void Add(double value) => _values.Add(value);

    /// <summary>How many of the measurements are exactly this value.</summary>
    public int CountOf(double value) => _values.Count(v => v == value);

    public double Percentile(double percent)
    {
        if (_values.Count == 0)
        {
            return 0;
        }

        var sorted = _values.OrderBy(v => v).ToArray();
        var rank = (percent / 100.0) * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
    }

    public string Summary(string unit = "ms") => _values.Count == 0
        ? "no samples"
        : string.Create(CultureInfo.InvariantCulture, $"mean {Mean:0.0} {unit}, p50 {Percentile(50):0.0}, p95 {Percentile(95):0.0}, max {Max:0.0} (n={Count})");
}

/// <summary>"--key value --flag" options.</summary>
internal sealed class CheckOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CheckOptions Parse(string[] args)
    {
        var options = new CheckOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{key}'.");
            }

            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options._values[key[2..]] = args[++index];
            }
            else
            {
                options._values[key[2..]] = "true";
            }
        }

        return options;
    }

    /// <summary>The options given that are not among <paramref name="known"/>.</summary>
    public string[] Unknown(params string[] known) =>
        [.. _values.Keys.Where(key => !known.Contains(key, StringComparer.OrdinalIgnoreCase))];

    public bool Flag(string key) => _values.TryGetValue(key, out var value) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public string Text(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;

    public int Number(string key, int fallback) => _values.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public (int Width, int Height) Size(string key, int width, int height)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            return (width, height);
        }

        var parts = value.Split('x', 'X');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>The names in a comma separated option such as <c>--only a,b</c>.</summary>
    public string[] Names(string key) =>
        Text(key, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when a group of checks is wanted: no <c>--only</c> list, or the group is in it; and not in <c>--skip</c>.</summary>
    public bool Wants(string group)
    {
        var only = Names("only");
        return (only.Length == 0 || only.Contains(group, StringComparer.OrdinalIgnoreCase)) && !Names("skip").Contains(group, StringComparer.OrdinalIgnoreCase);
    }
}
