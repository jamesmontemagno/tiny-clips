using System.Globalization;

namespace TinyClips.Tools.StudioWindowCheck.Host;

/// <summary>
/// What the checks did and when, in seconds since the tool started. Written next to the report
/// together with every refused attempt to take the keyboard focus or the foreground, so that an
/// attempt can be put down to the step that caused it.
/// </summary>
internal static class Timeline
{
    private static readonly object Gate = new();
    private static readonly List<(double At, string Text)> Marks = [];

    /// <summary>Notes a step, and makes it the step later attempts are put down to.</summary>
    public static void Mark(string step)
    {
        ForegroundGuard.Step = step;
        lock (Gate)
        {
            Marks.Add((ForegroundGuard.Elapsed, step));
        }
    }

    public static void Save(string path, IEnumerable<string> foreground)
    {
        (double At, string Text)[] marks;
        lock (Gate)
        {
            marks = [.. Marks];
        }

        var lines = marks.Select(m => (m.At, Text: m.Text))
            .Concat(ForegroundGuard.Events().Select(e => (e.At, Text: $"    refused: {e.What} for a window of class {(e.WindowClass.Length == 0 ? "(none)" : e.WindowClass)}")))
            .OrderBy(line => line.At)
            .Select(line => string.Create(CultureInfo.InvariantCulture, $"{line.At,8:0.000}  {line.Text}"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllLines(path, lines.Concat(["", "The foreground window, in order:"]).Concat(foreground.Select(f => "  " + f)));
    }
}
