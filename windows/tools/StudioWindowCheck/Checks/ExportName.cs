using System.Text;
using TinyClips.Core.Studio;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

internal sealed partial class WindowChecks
{
    private void ExportWhoseNameIsTaken()
    {
        Timeline.Mark("5: an explicitly selected export name is taken while rendering");
        var folder = NewScreenProject("Export, name taken", p => p with { Edits = new StudioEdits { TrimStart = 2.0, TrimEnd = 3.5 } });
        if (OpenReady(folder, "export, name taken") is not { } editor) return;
        var givenBefore = _services.Storage.Given().Length;
        var marker = Encoding.UTF8.GetBytes("Saved by something else while the video was being made.");
        Invoke(editor, "StudioExportButton");
        var promised = Until(() => _services.Storage.Given().Skip(givenBefore).FirstOrDefault(), name => name is not null, 3);
        var underWay = IsExporting(editor);
        if (promised is not null && underWay) File.WriteAllBytes(promised, marker);
        var gone = Gone(editor, "StudioCancelExportButton", 120);
        var error = ErrorOf(editor);
        _report.Check(
            "a late collision at the selected filename preserves the other file, reports a conflict, and never silently renames the export",
            promised is not null && underWay && gone && File.ReadAllBytes(promised).SequenceEqual(marker)
                && error.Contains("changed while exporting", StringComparison.Ordinal)
                && _services.Storage.Given().Length == givenBefore + 1
                && _services.Store.Load(editor.Id).Exports.Length == 0
                && !_services.Exports.Any(e => e.ProjectId == editor.Id),
            $"selected {promised}; export running before collision: {underWay}; finished: {gone}; error: {error}");
        CloseQuietly(editor);
    }
}
