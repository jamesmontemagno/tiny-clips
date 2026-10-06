using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Editing;
using TinyClips.Tools.StudioPreviewCheck.Media;
using TinyClips.Tools.StudioWindowCheck.Automation;
using TinyClips.Tools.StudioWindowCheck.Host;

namespace TinyClips.Tools.StudioWindowCheck.Checks;

// 5. Exporting with the real exporter: the overlay, cancelling, and what a finished export holds.
// An export whose name is taken while it runs is in ExportName.cs.
internal sealed partial class WindowChecks
{
    private void Exporting()
    {
        ExportCancelled();
        ExportFinished();
        ExportWhoseNameIsTaken();
    }

    private string[] ExportFiles() =>
        Directory.Exists(_services.ExportDirectory) ? Directory.GetFiles(_services.ExportDirectory, "*", SearchOption.AllDirectories) : [];

    /// <summary>What the progress bar reported while it was looked at, until the overlay went or the time was up.</summary>
    private static List<double> WatchProgress(Editor editor, Func<double, bool> enough, double seconds)
    {
        var values = new List<double>();
        Until(
            () =>
            {
                var bar = editor.Root.Find("StudioExportProgressBar");
                if (bar?.Range is { } range)
                {
                    values.Add(range.Value);
                    return enough(range.Value);
                }

                return bar is null;
            },
            done => done,
            seconds,
            20);
        return values;
    }

    private bool IsExporting(Editor editor) => OnUi(() => editor.Window.ViewModel.IsExporting);

    private string ErrorOf(Editor editor) => OnUi(() => editor.Window.ViewModel.ErrorMessage);

    /// <summary>A video of the whole recording, long enough to stop: the overlay, Cancel, what Esc runs, and closing the window.</summary>
    private void ExportCancelled()
    {
        Timeline.Mark("5: an export that is cancelled");
        var folder = NewCameraProject("Export, cancelled");
        if (OpenReady(folder, "export, cancelled") is not { } editor)
        {
            return;
        }

        var rest = LookFor(editor, s => s.Shown == Both(editor, FrameOf(CameraOffset)), 5);
        var filesBefore = ExportFiles();
        if (rest is null)
        {
            _report.Check("the editor shows the project before the export", false, "no screenshot");
            return;
        }

        // Start, by the Export button.
        Timeline.Mark("5: Export");
        var watch = Stopwatch.StartNew();
        var pressed = Invoke(editor, "StudioExportButton");
        var cancel = Find(editor, "StudioCancelExportButton", 3);
        var appeared = watch.Elapsed.TotalMilliseconds;
        var heading = NameOf(editor, "StudioExportingText", 1);
        var bar = Find(editor, "StudioExportProgressBar", 1);
        var range = bar?.Range;
        var play = Find(editor, "StudioPlayPauseButton", 0.5);
        var export = Find(editor, "StudioExportButton", 0.5);
        var dimmed = LookFor(editor, s => Luma(Corners(s)[0]) < Luma(Corners(rest)[0]) - 20, 2);
        _report.Check(
            "Export puts the overlay over the editor: Exporting, a progress bar from 0 to 100 and Cancel; the editor under it is disabled and its picture is dimmed",
            pressed && cancel is { IsEnabled: true, Name: "Cancel export" } && heading.StartsWith("Exporting", StringComparison.Ordinal)
                && bar is { Name: "Export progress" } && range is { Minimum: 0, Maximum: 100 }
                && play is { IsEnabled: false } && export is { IsEnabled: false }
                && dimmed is not null && Luma(Corners(dimmed)[0]) < Luma(Corners(rest)[0]) - 20,
            $"the overlay was there {F(appeared, "0")} ms after Export; \"{heading}\", {bar} {F(range?.Minimum ?? double.NaN)} to {F(range?.Maximum ?? double.NaN)}, {cancel}; Play enabled {play?.IsEnabled}, Export enabled {export?.IsEnabled}; the corner of the picture went from {Corners(rest)[0]} to {(dimmed is null ? "nothing" : Corners(dimmed)[0].ToString())}{(ErrorOf(editor) is { Length: > 0 } error ? $"; the window says \"{error}\"" : string.Empty)}");

        var focus = Until(() => FocusedId(editor), id => id == "StudioCancelExportButton", 2);
        _report.Check("in the active window the keyboard focus goes to Cancel while the export runs", focus == "StudioCancelExportButton", $"focus is on \"{focus}\"");

        // Cancel, by the button, once the export is under way.
        Timeline.Mark("5: Cancel");
        var values = WatchProgress(editor, value => value >= 3, 20);
        var underWay = IsExporting(editor);
        watch.Restart();
        var cancelled = Invoke(editor, "StudioCancelExportButton");
        Stopped("Cancel stops the export", cancelled && underWay, values, focusWanted: "StudioExportButton");

        // Again, stopped by what Esc runs.
        Timeline.Mark("5: Export, then what Esc runs");
        Invoke(editor, "StudioExportButton");
        Find(editor, "StudioCancelExportButton", 3);
        values = WatchProgress(editor, value => value >= 3, 20);
        underWay = IsExporting(editor);
        watch.Restart();
        var escape = Key(editor, StudioShortcutKey.Escape);
        Stopped("what Esc runs while exporting stops the export", escape == StudioShortcutAction.CancelExport && underWay, values, focusWanted: "StudioExportButton");

        ExportThatFails(editor, filesBefore);
        CloseWhileExporting(editor, folder, filesBefore);

        // After a stopped export: the overlay is gone, the editor works again, and nothing was written.
        void Stopped(string name, bool done, List<double> progress, string focusWanted)
        {
            var gone = Gone(editor, "StudioCancelExportButton", 15);
            var took = watch.Elapsed.TotalMilliseconds;
            var enabled = Until(() => Find(editor, "StudioPlayPauseButton", 0)?.IsEnabled == true, ok => ok, 3);
            var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), left => left.Length == 0, 3, 50);
            var listed = _services.Store.Load(editor.Id).Exports.Length;
            var bright = LookFor(editor, s => Near(Corners(s)[0], Corners(rest)[0], 6) && s.Shown == rest.Shown, 3);
            var focused = Until(() => FocusedId(editor), id => id == focusWanted, 2);
            var message = ErrorOf(editor);
            _report.Check(
                $"{name}: the overlay goes, the editor works again with its picture as before and the focus back on Export, nothing is left in the export folder, the project lists no export, and no error is shown",
                done && gone && enabled && files.Length == 0 && listed == 0 && bright is not null && Near(Corners(bright)[0], Corners(rest)[0], 6) && bright.Shown == rest.Shown && focused == focusWanted && message.Length == 0 && !_services.Exports.Any(e => e.ProjectId == editor.Id),
                $"progress when it was stopped: {(progress.Count == 0 ? "not read" : F(progress[^1], "0.#") + " %")}; the overlay was gone {F(took, "0")} ms later: {gone}; editor enabled {enabled}; files left: {(files.Length == 0 ? "none" : string.Join(", ", files.Select(Path.GetFileName)))}; exports listed {listed}; picture {bright?.Shown}; focus on \"{focused}\"{(message.Length > 0 ? $"; the window says \"{message}\"" : string.Empty)}");
        }
    }

    /// <summary>
    /// Makes the next export fail: it is told to write into a folder that is a file. Returns the
    /// file that is in the way, to delete afterwards.
    /// </summary>
    private string BlockNextExport()
    {
        var inTheWay = Path.Combine(_services.ExportDirectory, "not-a-folder");
        File.WriteAllText(inTheWay, string.Empty);
        _services.Storage.SendNextTo(Path.Combine(inTheWay, "video.mp4"));
        return inTheWay;
    }

    /// <summary>
    /// The message bar of the window, with the texts in it and its close button, once it is shown
    /// and has built its parts.
    /// </summary>
    private static (UiaElement? Bar, string[] Texts, UiaElement? Close) MessageBar(Editor editor, double seconds) => Until(
        () =>
        {
            var bar = editor.Root.Find("StudioErrorBar");
            return (bar, bar?.FindAll(ControlTypeNames.Text).Select(text => text.Name).ToArray() ?? [], bar?.FindAll(ControlTypeNames.Button).FirstOrDefault());
        },
        found => found is { bar: not null, Item2.Length: > 0, Item3: not null },
        seconds);

    /// <summary>An export that cannot be written: the window says so, and goes on working.</summary>
    private void ExportThatFails(Editor editor, string[] filesBefore)
    {
        Timeline.Mark("5: an export that cannot be written");
        var inTheWay = BlockNextExport();
        var pressed = Invoke(editor, "StudioExportButton");
        var (bar, texts, close) = MessageBar(editor, 20);
        var message = ErrorOf(editor);
        var idle = Gone(editor, "StudioCancelExportButton", 5) && Until(() => Find(editor, "StudioPlayPauseButton", 0)?.IsEnabled == true, ok => ok, 3);
        File.Delete(inTheWay);
        var files = ExportFiles().Except(filesBefore).ToArray();
        _report.Check(
            "an export that cannot be written: the window says that the export failed, and why, in its message bar; the overlay goes, the editor works again, and nothing is left behind",
            pressed && bar is not null && message.StartsWith("Studio export failed: ", StringComparison.Ordinal) && texts.Contains(message) && idle
                && files.Length == 0 && _services.Store.Load(editor.Id).Exports.Length == 0 && Native.Exists(editor.Handle),
            $"{(bar is null ? "no message bar" : bar.ToString())}: {string.Join(" | ", texts.Select(text => $"\"{text}\""))}; editor working again: {idle}; files left: {(files.Length == 0 ? "none" : string.Join(", ", files.Select(Path.GetFileName)))}");

        // The bar's own close button takes the message away.
        var dismissed = close?.Invoke() ?? false;
        var gone = Gone(editor, "StudioErrorBar", 3);
        _report.Check(
            "the message bar's close button takes the message away",
            dismissed && gone && ErrorOf(editor).Length == 0,
            $"{close}: pressed {dismissed}, the bar gone {gone}");
    }

    /// <summary>The window's close button while an export runs: the question, Keep exporting, and Stop and close.</summary>
    private void CloseWhileExporting(Editor editor, TestFolder folder, string[] filesBefore)
    {
        // A square canvas has nearly twice the pixels, so this export leaves more time to answer.
        Timeline.Mark("5: closing the window while an export runs");
        SetCombo(editor, "StudioCanvasComboBox", (int)StudioCanvasAspect.Square);
        Expect(editor, p => p with { Canvas = p.Canvas with { Aspect = StudioCanvasAspect.Square } });
        LookFor(editor, s => s.Shown == Both(editor, FrameOf(CameraOffset)), 3);
        var exportStarted = Stopwatch.StartNew();
        Invoke(editor, "StudioExportButton");
        Find(editor, "StudioCancelExportButton", 3);
        var closePressed = PressClose(editor);
        var question = Dialog(editor);
        var keep = question?.Find("CloseButton");
        var stop = question?.Find("PrimaryButton");
        var asked = keep is { Name: "Keep exporting" } && stop is { Name: "Stop and close" };
        var title = question?.Name ?? string.Empty;
        var stillRunning = IsExporting(editor);
        _report.Check(
            "the close button while an export runs asks first: Keep exporting, or Stop and close",
            closePressed && asked && title == "An export is still running." && stillRunning,
            $"close pressed {closePressed}; question \"{title}\" with {keep} and {stop}, {F(exportStarted.Elapsed.TotalMilliseconds, "0")} ms into the export; still exporting {stillRunning}; focus on \"{FocusedId(editor)}\"");
        if (!asked)
        {
            CloseQuietly(editor);
            return;
        }

        // Keep exporting: the question goes, the export goes on.
        var before = Find(editor, "StudioExportProgressBar", 0.5)?.Range?.Value ?? double.NaN;
        var kept = keep!.Invoke();
        var questionGone = Until(() => !HasDialog(editor), gone => gone, 3);
        var after = WatchProgress(editor, value => value > before + 1, 10);
        var goesOn = IsExporting(editor) && Native.Exists(editor.Handle);
        _report.Check(
            "Keep exporting: the question goes, the window stays and the export goes on",
            kept && questionGone && goesOn && after.Count > 0 && after[^1] > before,
            $"progress {F(before, "0.#")} % when it was asked, {(after.Count == 0 ? "not read" : F(after[^1], "0.#") + " %")} after; still exporting {goesOn}");

        // That export is stopped, and the question is given the time to finish closing: the
        // window asks one question at a time, and passes over a close button pressed before then.
        Invoke(editor, "StudioCancelExportButton");
        Gone(editor, "StudioCancelExportButton", 15);
        Thread.Sleep(700);

        // Stop and close, on an export of its own.
        Timeline.Mark("5: Stop and close");
        Invoke(editor, "StudioExportButton");
        Find(editor, "StudioCancelExportButton", 3);
        var again = PressClose(editor);
        var stopButton = Dialog(editor)?.Find("PrimaryButton");
        var wasRunning = IsExporting(editor);
        var watch = Stopwatch.StartNew();
        var stopped = stopButton?.Invoke() ?? false;
        var gone = WindowGone(editor, 15);
        var took = watch.Elapsed.TotalMilliseconds;
        Release(editor);
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), left => left.Length == 0, 5, 50);
        var project = Until(() => _services.Store.Load(folder.Paths.ProjectId), p => p.Exports.Length == 0, 1);
        _report.Check(
            "Stop and close: the window closes, the export stops and leaves no file, and the project stays, without an export",
            again && wasRunning && stopped && gone && files.Length == 0 && project.Exports.Length == 0 && File.Exists(folder.Paths.ScreenPath) && !_services.Exports.Any(e => e.ProjectId == folder.Paths.ProjectId),
            $"the question was there: {stopButton is not null}, with the export still running: {wasRunning}; the window was gone {F(took, "0")} ms after the answer: {gone}; files left: {(files.Length == 0 ? "none" : string.Join(", ", files.Select(Path.GetFileName)))}; exports listed {project.Exports.Length}; the recording is still there: {File.Exists(folder.Paths.ScreenPath)}");
    }


    /// <summary>
    /// A short part of the recording with a look of its own, exported to the end in a window that
    /// is not the active one. The file is decoded and every frame is read.
    /// </summary>
    private void ExportFinished()
    {
        Timeline.Mark("5: an export that finishes");
        var folder = NewCameraProject("Export, finished", p => p with
        {
            Canvas = p.Canvas with { Padding = 0.1, Background = new StudioBackground { Style = StudioBackgroundStyle.Solid, Preset = "lemon", Primary = "#FFE040", Secondary = null } },
            Screen = p.Screen with { Shadow = 0, CornerRadius = 0.05 },
            Camera = p.Camera with { Shape = StudioCameraShape.RoundedRectangle, Mirror = false, Shadow = 0 },
        });
        if (OpenReady(folder, "export, finished") is not { } editor)
        {
            return;
        }

        // Edits made in the window: the trim, by the handles, and the size of the camera.
        const int First = 60;
        const int Last = 104;
        const int Rest = 80;
        var trimmed = SetSlider(editor, "StudioTrimStart", 2.0) & SetSlider(editor, "StudioTrimEnd", 3.5) & SetSlider(editor, "StudioCameraSizeSlider", 0.3);
        Expect(editor, p => WithScene(p with { Edits = p.Edits with { TrimStart = 2.0, TrimEnd = 3.5 } }, s => s with { Bubble = s.Bubble with { Size = 0.3 } }));
        SetSlider(editor, "StudioPlayhead", MiddleOf(Rest));
        var preview = LookFor(editor, s => s.Shown == Both(editor, Rest), 5);
        var size = StudioExportLimits.GetExportSize(editor.Expected);
        var (width, height) = ((int)size.Width, (int)size.Height);
        var sizeText = NameOf(editor, "StudioExportSizeText");
        var time = TimeText(editor);
        if (!_report.Check(
            "before the export the preview shows the trimmed video with its look, and the header and the time say what will be exported",
            trimmed && preview?.Shown == Both(editor, Rest) && sizeText == $"Exports at {width} × {height}" && time.EndsWith("/ 0:01.5", StringComparison.Ordinal),
            $"{preview?.Shown} (expected {Both(editor, Rest)}); \"{sizeText}\"; time \"{time}\"") || preview is null)
        {
            CloseQuietly(editor);
            return;
        }

        // The user goes on to another window, and the export runs behind it.
        Timeline.Mark("5: Export, in a window that is not the active one");
        var filesBefore = ExportFiles();
        TellActive(editor, isActive: false);
        Thread.Sleep(150);
        var began = ForegroundGuard.Elapsed;
        var watch = Stopwatch.StartNew();
        var pressed = Invoke(editor, "StudioExportButton");
        var overlay = Find(editor, "StudioCancelExportButton", 3) is not null;
        var values = WatchProgress(editor, _ => false, 120);
        var finished = Until(() => _services.Exports.FirstOrDefault(e => e.ProjectId == editor.Id), e => e is not null, 30, 30);
        var took = watch.Elapsed.TotalSeconds;
        var gone = Gone(editor, "StudioCancelExportButton", 5);
        var message = ErrorOf(editor);
        var rising = values.Zip(values.Skip(1), (a, b) => b >= a).All(ok => ok);
        _report.Check(
            "the export runs to its end: the overlay shows progress that only rises, then goes, and the window service reports the video",
            pressed && overlay && gone && finished is not null && values.Count > 0 && rising && message.Length == 0,
            $"{F(took, "0.0")} s for 1.5 s of video; progress read {values.Count} times, from {(values.Count == 0 ? "?" : F(values[0], "0.#"))} % to {(values.Count == 0 ? "?" : F(values[^1], "0.#"))} %, only rising: {rising}; reported: {(finished is null ? "nothing" : Path.GetFileName(finished.Path))}{(message.Length > 0 ? $"; the window says \"{message}\"" : string.Empty)}");

        // The window was not the active one all that time.
        Thread.Sleep(300);
        var requests = ForegroundGuard.Events().Where(e => e.At >= began).ToArray();
        var focusBefore = FocusedId(editor);
        TellActive(editor, isActive: true);
        var focusAfter = Until(() => FocusedId(editor), id => id == "StudioExportButton", 2);
        _report.Check(
            "a window that is not the active one does not ask Windows for the keyboard focus when its export starts or ends; the focus is put on Export when the user comes back to it",
            requests.Length == 0 && focusBefore != "StudioCancelExportButton" && focusAfter == "StudioExportButton",
            $"{requests.Length} request(s) while it was not active{(requests.Length == 0 ? string.Empty : ": " + string.Join(", ", requests.Select(e => $"{e.What} at {F(e.At, "0.00")} s")))}; focus then on \"{focusBefore}\", and on \"{focusAfter}\" once the window was active again");

        if (finished is null)
        {
            CloseQuietly(editor);
            return;
        }

        var path = finished.Path;
        var files = Until(() => ExportFiles().Except(filesBefore).ToArray(), left => left.Length == 1, 3, 50);
        var project = _services.Store.Load(editor.Id);
        var poster = File.Exists(folder.Paths.PosterPath);
        _report.Check(
            "the video is the one new file in the export folder, the project lists it as its export, and the project has its poster image",
            files.Length == 1 && string.Equals(files[0], path, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(path) == _services.ExportDirectory
                && project.Exports.Length == 1 && string.Equals(project.Exports[0].Path, path, StringComparison.OrdinalIgnoreCase) && poster,
            $"new files: {string.Join(", ", files.Select(Path.GetFileName))} ({(File.Exists(path) ? new FileInfo(path).Length / 1024 : 0)} KB); exports listed: {string.Join(", ", project.Exports.Select(e => Path.GetFileName(e.Path)))}; poster.jpg {poster}");

        ExportedFrames(editor, path, width, height, First, Last, Rest, preview);
        CloseQuietly(editor);
    }

    /// <summary>What the file is, by ffprobe, and what each of its frames shows, decoded by ffmpeg.</summary>
    private void ExportedFrames(Editor editor, string path, int width, int height, int first, int last, int shownFrame, Sight preview)
    {
        Timeline.Mark("5: decoding the exported video");
        string stream;
        string colors;
        try
        {
            stream = RunTool("ffprobe", "-v", "error", "-select_streams", "v:0", "-count_packets", "-show_entries", "stream=codec_name,width,height,r_frame_rate,nb_read_packets", "-of", "csv=p=0", path).Trim();
            colors = RunTool("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=pix_fmt,color_range,color_space", "-of", "csv=p=0", path).Trim();
        }
        catch (Exception ex)
        {
            _report.Check("the exported video can be read by ffprobe", false, ex.Message);
            return;
        }

        var count = last - first + 1;
        var wanted = string.Create(CultureInfo.InvariantCulture, $"h264,{width},{height},{Fps}/1,{count}");
        _report.Check(
            "the file is an H.264 video of the size the header named, at the recording's frame rate, with one frame for each frame of the trimmed range",
            stream == wanted,
            $"ffprobe: {stream} (expected {wanted}); {colors}");

        // The sound is not listened to. What the file says about it is reported, not judged.
        try
        {
            var audio = RunTool("ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_name,channels,duration", "-of", "csv=p=0", path).Trim();
            _report.Note($"the file's audio, by ffprobe (codec, channels, seconds): {(audio.Length == 0 ? "none" : audio.ReplaceLineEndings("; "))}; the recording has an audio track, and the project is not muted");
        }
        catch (Exception ex)
        {
            _report.Note($"the file's audio could not be read: {ex.Message}");
        }

        // Where the layout puts the clips in a picture of the export size.
        var view = SceneView.Resolve(editor.Expected, editor.Folder.Screen, editor.Folder.Camera, width, height);
        var wrong = new List<string>();
        var corners = new List<string>();
        var read = 0;
        var compared = 0;
        var alike = 0;

        // A file without a colour space is read as BT.709, as players read a video of this size.
        var filter = colors.Contains("bt709", StringComparison.OrdinalIgnoreCase) ? "format=bgra" : "scale=in_color_matrix=bt709,format=bgra";
        try
        {
            foreach (var frame in DecodedFrames(path, width, height, filter))
            {
                var source = first + read;
                var shown = Shown.Read(frame, width, height, view, editor.Folder.Screen, editor.Folder.Camera);
                var expected = Both(editor, source);
                Rgb[] colours =
                [
                    FrameCode.Color(frame, width, height, 6, 6, 2),
                    FrameCode.Color(frame, width, height, width - 7, 6, 2),
                    FrameCode.Color(frame, width, height, 6, height - 7, 2),
                    FrameCode.Color(frame, width, height, width - 7, height - 7, 2),
                ];
                if (shown != expected && wrong.Count < 6)
                {
                    wrong.Add($"frame {read} shows {shown} and should show {expected}");
                }

                if (!colours.All(c => Near(c, Lemon, 24)) && corners.Count < 3)
                {
                    corners.Add($"frame {read}: {string.Join(" ", colours)}");
                }

                if (source == shownFrame)
                {
                    // The same frame as the preview showed it: the colour at each point of a grid over the canvas.
                    for (var row = 1; row < 12; row++)
                    {
                        for (var column = 1; column < 20; column++)
                        {
                            var (u, v) = (column / 20.0, row / 12.0);
                            var inPreview = preview.Shot.Color(preview.Canvas.X + (u * preview.Canvas.Width), preview.Canvas.Y + (v * preview.Canvas.Height), 2);
                            var inExport = FrameCode.Color(frame, width, height, u * width, v * height, 3);
                            compared++;
                            alike += inPreview.Distance(inExport) <= 40 ? 1 : 0;
                        }
                    }
                }

                read++;
            }
        }
        catch (Exception ex)
        {
            _report.Check("the exported video can be decoded by ffmpeg", false, ex.Message);
            return;
        }

        _report.Check(
            $"decoded, the video holds the trimmed range and nothing else: frame by frame the screen recording from frame {first} to frame {last}, each with the camera frame that belongs to it, where the layout puts them",
            read == count && wrong.Count == 0,
            wrong.Count == 0 ? $"{read} frames decoded, screen {first} to {first + read - 1}, camera {Both(editor, first).Camera} to {Both(editor, first + read - 1).Camera}" : $"{read} frames decoded; {string.Join("; ", wrong)}");
        _report.Check(
            "every frame has the background the inspector showed in its four corners",
            read > 0 && corners.Count == 0,
            corners.Count == 0 ? $"{Lemon} within 24 in {read} frames" : string.Join("; ", corners));
        _report.Check(
            "the frame the preview showed before the export looks the same in the video: the colours at a grid of points over the canvas",
            compared > 0 && alike >= compared * 0.95,
            $"{alike} of {compared} points within 40 of each other; preview canvas {preview.Canvas.Width}x{preview.Canvas.Height}, video {width}x{height}");
    }

    /// <summary>The frames of a video as tightly packed top-down BGRA, decoded by ffmpeg. The array is reused from frame to frame.</summary>
    private static IEnumerable<byte[]> DecodedFrames(string path, int width, int height, string filter)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-nostdin", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-vf", filter, "-f", "rawvideo", "-pix_fmt", "bgra", "-" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg could not be started.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.BaseStream;
        var frame = new byte[width * height * 4];
        try
        {
            while (true)
            {
                var filled = 0;
                while (filled < frame.Length)
                {
                    var read = output.Read(frame, filled, frame.Length - filled);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled < frame.Length)
                {
                    break;
                }

                yield return frame;
            }

            if (!process.WaitForExit(15000) || process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg did not decode the video: {errors.Result.Trim()}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>Runs ffmpeg or ffprobe to its end, without a window, and returns what it printed.</summary>
    private static string RunTool(string name, params string[] arguments)
    {
        var start = new ProcessStartInfo(name) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{name} could not be started.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{name} did not finish.");
        }

        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"{name} failed: {errors.Result.Trim()}");
    }
}
