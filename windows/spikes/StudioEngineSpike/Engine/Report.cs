using System.Globalization;
using System.Text;

namespace StudioEngineSpike.Engine;

/// <summary>Console output that is also written to <c>out\&lt;mode&gt;\report.txt</c>.</summary>
internal sealed class Report : IDisposable
{
    private readonly StreamWriter? _file;
    private readonly object _gate = new();

    public Report(string? path)
    {
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    public void Line(string text = "")
    {
        lock (_gate)
        {
            Console.WriteLine(text);
            _file?.WriteLine(text);
        }
    }

    public void Section(string title)
    {
        Line();
        Line($"== {title} ==");
    }

    public void Dispose() => _file?.Dispose();
}

/// <summary>A bag of samples with the percentiles the report quotes.</summary>
internal sealed class Samples
{
    private readonly List<double> _values = new();
    private bool _sorted;

    public int Count => _values.Count;

    public void Add(double value)
    {
        _values.Add(value);
        _sorted = false;
    }

    public void Clear() => _values.Clear();

    public IReadOnlyList<double> Values => _values;

    public double Mean => _values.Count == 0 ? 0 : _values.Average();

    public double Min => _values.Count == 0 ? 0 : _values.Min();

    public double Max => _values.Count == 0 ? 0 : _values.Max();

    public double StdDev
    {
        get
        {
            if (_values.Count < 2)
            {
                return 0;
            }

            var mean = Mean;
            return Math.Sqrt(_values.Sum(v => (v - mean) * (v - mean)) / (_values.Count - 1));
        }
    }

    public double Percentile(double p)
    {
        if (_values.Count == 0)
        {
            return 0;
        }

        if (!_sorted)
        {
            _values.Sort();
            _sorted = true;
        }

        var rank = p / 100.0 * (_values.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return _values[low] + ((_values[high] - _values[low]) * (rank - low));
    }

    /// <summary>"n=…, mean, p50, p95, p99, max" in the given unit.</summary>
    public string Summary(string unit = "ms", string format = "0.00")
    {
        if (_values.Count == 0)
        {
            return "n=0";
        }

        var c = CultureInfo.InvariantCulture;
        return $"n={Count} mean={Mean.ToString(format, c)} p50={Percentile(50).ToString(format, c)} p95={Percentile(95).ToString(format, c)} p99={Percentile(99).ToString(format, c)} max={Max.ToString(format, c)} {unit}";
    }
}

internal static class Fmt
{
    public static string F(this double value, string format = "0.00") => value.ToString(format, CultureInfo.InvariantCulture);

    public static string F(this float value, string format = "0.00") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Histogram of integer values as "value×count" pairs in ascending order.</summary>
    public static string Histogram(IEnumerable<int> values)
    {
        var groups = values.GroupBy(v => v).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}");
        var text = string.Join("  ", groups);
        return text.Length == 0 ? "(none)" : text;
    }
}
