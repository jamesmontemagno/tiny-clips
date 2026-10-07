using System.Diagnostics;
using System.Globalization;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Rendering;
using Vortice.MediaFoundation;

namespace TinyClips.Tools.StudioRenderCheck;

/// <summary>
/// Exports a project that somebody made, with the app's exporter, and says what came of it.
/// It is not a check and judges nothing. It is for a project from elsewhere: one saved as a
/// folder on a Mac, for instance, whose export on the Mac can then be held against this one.
/// </summary>
/// <remarks>
/// The project is read by the reader every project is read by. Its recordings and its events
/// are taken from beside the project file, under the plain file names the file gives; a name
/// that is a path is refused. Nothing is written but the output, and nothing in the project's
/// folder is changed.
/// </remarks>
internal static class ProjectExport
{
    private const string SavedProjectExtension = ".tinyclips";

    public static async Task<int> Run(string projectPath, string outputPath)
    {
        try
        {
            var file = FindProjectFile(Path.GetFullPath(projectPath));
            var folder = Path.GetDirectoryName(file)!;
            var project = StudioProjectJson.ReadProject(File.ReadAllText(file));
            var screen = Beside(folder, project.Sources.Screen.File, "the screen recording");
            var camera = project.Sources.Camera is { } source ? Beside(folder, source.File, "the camera recording") : null;
            var eventsPath = project.Sources.Events is { Length: > 0 } name ? Beside(folder, name, "the events") : null;
            var events = eventsPath is not null ? StudioProjectJson.ReadEvents(File.ReadAllText(eventsPath)) : new StudioEvents();
            var output = Path.GetFullPath(outputPath);
            var size = StudioExportLimits.GetExportSize(project);

            Console.WriteLine($"Project: {project.Name} ({project.App.Platform} {project.App.Version}), {file}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  screen {project.Sources.Screen.Width}×{project.Sources.Screen.Height}, {project.Sources.Screen.Duration:0.###} s at {project.Sources.Screen.FrameRate:0.##} fps; camera {(project.Sources.Camera is { } c ? $"{c.Width}×{c.Height}, {c.Duration:0.###} s, starting {c.StartOffset:0.###} s in" : "none")}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {project.Scenes.Length} scene(s), {project.Zooms.Length} zoom(s), {project.Edits.Cuts.Length} cut(s), {project.Edits.Speed.Length} speed change(s), trimmed to {project.Edits.TrimStart:0.###} s .. {(project.Edits.TrimEnd is { } end ? end.ToString("0.###", CultureInfo.InvariantCulture) : "the end")} s; {events.Clicks.Length} click(s), {events.Cursor.Length} cursor sample(s)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  exports at {size.Width:0}×{size.Height:0}"));

            MediaFactory.MFStartup(true).CheckError();
            try
            {
                using (var graphics = StudioGraphicsDevice.CreateHardware())
                {
                    Console.WriteLine($"Graphics: {graphics.AdapterName}{(graphics.IsSoftware ? " (software)" : string.Empty)}");
                }

                var paths = new StudioProjectPaths(
                    project.Id,
                    folder,
                    file,
                    screen,
                    camera,
                    eventsPath ?? Path.Combine(folder, "events.json"),
                    Path.Combine(folder, "poster.jpg"));
                var watch = Stopwatch.StartNew();
                var result = await new StudioExporter().ExportAsync(project, events, paths, output).ConfigureAwait(false);
                var seconds = watch.Elapsed.TotalSeconds;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Exported: {result.OutputPath}"));
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {result.Width}×{result.Height}, {result.FrameCount} frames, sound {(result.HasAudio ? "yes" : "no")}; {result.EncoderDescription}{(result.SoftwareRendering ? ", drawn by WARP" : string.Empty)}; {seconds:0.00} s ({result.FrameCount / seconds:0} fps); {new FileInfo(output).Length:N0} bytes"));
                return 0;
            }
            finally
            {
                MediaFactory.MFShutdown();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Not exported: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            {
                Console.WriteLine($"  because: {inner.GetType().Name}: {inner.Message}");
            }

            return 1;
        }
    }

    // A project file as it is in the app's storage or in a saved folder, or the folder that holds one.
    private static string FindProjectFile(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException("There is no such file or folder.", path);
        }

        var saved = Directory.GetFiles(path, "*" + SavedProjectExtension);
        if (saved.Length == 1)
        {
            return saved[0];
        }

        var stored = Path.Combine(path, "project.json");
        return saved.Length == 0 && File.Exists(stored)
            ? stored
            : throw new InvalidOperationException($"The folder holds {saved.Length} {SavedProjectExtension} files, and a project folder holds one.");
    }

    private static string Beside(string folder, string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name is "." or "..")
        {
            throw new InvalidOperationException($"The project names {what} as '{name}', which is not the name of a file beside it.");
        }

        var path = Path.Combine(folder, name);
        return File.Exists(path) ? path : throw new FileNotFoundException($"The project names {what} as '{name}', and it is not beside the project file.", path);
    }
}