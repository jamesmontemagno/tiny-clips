using System.Globalization;
using System.Text;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 5, continued. An export never takes the place of a file. A video is made under a name of its
// own and gets the name the app made for it only when it is complete. Making it takes time, and
// whatever else is saved in that time is offered the same name, because no file has it yet. The
// video then gets the name the app would give one that is saved now, and the other file stays.
internal sealed partial class WindowChecks
{
    private void ExportWhoseNameIsTaken()
    {
        Timeline.Mark("5: an export whose name is taken while it runs");
        const int First = 60;
        const int Last = 104;
        var folder = NewScreenProject("Export, name taken", p => p with { Edits = new StudioEdits { TrimStart = 2.0, TrimEnd = 3.5 } });
        if (OpenReady(folder, "export, name taken") is not { } editor)
        {
            return;
        }

        LookFor(editor, s => s.Shown == Both(editor, First), 5);
        var filesBefore = ExportFiles();
        var givenBefore = _services.Storage.Given().Length;
        var exportsBefore = _services.Exports.Count(e => e.ProjectId == editor.Id);
        var marker = Encoding.UTF8.GetBytes("Saved by something else while the video was being made.");

        // The export starts, and is promised a name that no file has yet.
        var pressed = Invoke(editor, "StudioExportButton");
        var overlay = Find(editor, "StudioCancelExportButton", 3) is not null;
        var promised = Until(() => _services.Storage.Given().Skip(givenBefore).FirstOrDefault(), name => name is not null, 3);
        var freeAtFirst = promised is not null && !File.Exists(promised);

        // Under way, another file is saved under that name.
        var values = WatchProgress(editor, value => value >= 3, 30);
        var underWay = IsExporting(editor);
        if (promised is not null && freeAtFirst && underWay)
        {
            File.WriteAllBytes(promised, marker);
        }

        var takenWhileExporting = promised is not null && File.Exists(promised) && IsExporting(editor);
        var finished = Until(() => _services.Exports.Where(e => e.ProjectId == editor.Id).Skip(exportsBefore).FirstOrDefault(), e => e is not null, 120, 30);
        var gone = Gone(editor, "StudioCancelExportButton", 10);
        var message = ErrorOf(editor);
        var given = _services.Storage.Given().Skip(givenBefore).ToArray();
        var files = Until(() => ExportFiles().Except(filesBefore).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(), now => now.Length == 2, 3, 50);
        var project = _services.Store.Load(editor.Id);
        var markerKept = promised is not null && File.Exists(promised) && File.ReadAllBytes(promised).AsSpan().SequenceEqual(marker);
        var reportedOnce = _services.Exports.Count(e => e.ProjectId == editor.Id) - exportsBefore;

        // What the file under the next name is, by ffprobe.
        var size = StudioExportLimits.GetExportSize(editor.Expected);
        var wanted = string.Create(CultureInfo.InvariantCulture, $"h264,{(int)size.Width},{(int)size.Height},{Fps}/1,{Last - First + 1}");
        var stream = "not read";
        if (finished is not null && File.Exists(finished.Path))
        {
            try
            {
                stream = RunTool("ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_read_packets", "-of", "csv=p=0", finished.Path).Trim();
            }
            catch (Exception ex)
            {
                stream = ex.Message;
            }
        }

        _report.Check(
            "a video whose name was taken by another file while it was being made gets the next name and replaces nothing: the file that took the name is as it was, the video is whole under the next name, and that name is the one the project links to and the app is told",
            pressed && overlay && freeAtFirst && underWay && takenWhileExporting && gone && message.Length == 0
                && finished is not null && given.Length == 2 && given[0] == promised && string.Equals(finished.Path, given[1], StringComparison.OrdinalIgnoreCase) && markerKept && stream == wanted
                && files.Length == 2 && files.SequenceEqual(given.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
                && project.Exports.Length == 1 && string.Equals(project.Exports[0].Path, given[1], StringComparison.OrdinalIgnoreCase) && reportedOnce == 1,
            $"the name promised at the start, {(promised is null ? "none" : Path.GetFileName(promised))}, was free then: {freeAtFirst}; another file was saved under it at {(values.Count == 0 ? "?" : F(values[^1], "0.#"))} % with the export still running: {takenWhileExporting}; "
                + $"names asked for: {string.Join(", ", given.Select(Path.GetFileName))}; the video the app was told of: {(finished is null ? "none" : Path.GetFileName(finished.Path))}, {reportedOnce} time(s); the file that took the name is as it was: {markerKept}; "
                + $"new files where videos go: {string.Join(", ", files.Select(path => $"{Path.GetFileName(path)} ({new FileInfo(path).Length} bytes)"))}; "
                + $"the project links to {(project.Exports.Length == 0 ? "nothing" : string.Join(", ", project.Exports.Select(export => Path.GetFileName(export.Path))))}; ffprobe: {stream} (expected {wanted})"
                + $"{(message.Length > 0 ? $"; the window says \"{message}\"" : string.Empty)}");
        CloseQuietly(editor);
    }
}
