using System.Text.Json;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.Core.Studio;

public interface IStudioProjectStore
{
    StudioProjectCreation CreateProjectForRecording(StudioProjectCreationRequest request);
    StudioProject BuildDefaultProject(string projectId, StudioProjectCreationRequest request, DateTimeOffset now);
    StudioProject Load(string projectId);
    void Save(StudioProject project);
    IReadOnlyList<StudioProjectSummary> ListSummaries();
    void Delete(string projectId);
    StudioProject MarkOpened(string projectId);
    StudioProject RecordExport(string projectId, string exportedPath);
    string? FindProjectIdByExportPath(string exportedPath);
    bool UpdateExportPath(string oldPath, string newPath);
    bool RemoveExportPath(string exportedPath);
    StudioProject GetOrCreateFlatProject(string videoPath, string appVersion);
    StudioStorageSummary GetStorageSummary();
    StudioEvents LoadEvents(string projectId);
    void SaveEvents(string projectId, StudioEvents events);
    StudioCleanupResult Cleanup(StudioCleanupOptions? options = null);
}

public sealed class StudioProjectStore : IStudioProjectStore
{
    public const string ProjectFileName = "project.json";
    public const string EventsFileName = "events.json";
    public const string ScreenFileName = "screen.mp4";
    public const string CameraFileName = "camera.mp4";

    private readonly string _rootDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, string> _exportIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _exportIndexLoaded;

    public StudioProjectStore(string? rootDirectory = null, TimeProvider? timeProvider = null)
    {
        _rootDirectory = rootDirectory ?? DefaultRootDirectory();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string DefaultRootDirectory() =>
        Path.Combine(ClipsLibraryPaths.LocalDataDirectory(), "TinyClips", "Projects");

    public StudioProjectCreation CreateProjectForRecording(StudioProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var projectId = Guid.NewGuid().ToString("N");
        var projectDirectory = ProjectDirectory(projectId);
        Directory.CreateDirectory(projectDirectory);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var project = BuildDefaultProject(projectId, request, now);
        SaveProjectFile(projectDirectory, project);

        return new StudioProjectCreation(
            project,
            new StudioProjectPaths(
                projectId,
                projectDirectory,
                Path.Combine(projectDirectory, ProjectFileName),
                Path.Combine(projectDirectory, ScreenFileName),
                request.Camera is null ? null : Path.Combine(projectDirectory, CameraFileName),
                Path.Combine(projectDirectory, EventsFileName)));
    }

    public StudioProject BuildDefaultProject(string projectId, StudioProjectCreationRequest request, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(request);

        var hasCamera = request.Camera is not null;
        return new StudioProject
        {
            Id = projectId,
            Name = request.Name ?? string.Empty,
            CreatedAt = now.ToUniversalTime(),
            ModifiedAt = now.ToUniversalTime(),
            LastOpenedAt = now.ToUniversalTime(),
            App = new StudioAppInfo { Platform = "windows", Version = request.AppVersion ?? string.Empty },
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource
                {
                    File = ScreenFileName,
                    Width = request.Screen.Width,
                    Height = request.Screen.Height,
                    Duration = request.Screen.Duration,
                    FrameRate = request.Screen.FrameRate,
                },
                Camera = request.Camera is null
                    ? null
                    : new StudioCameraSource
                    {
                        File = CameraFileName,
                        Width = request.Camera.Width,
                        Height = request.Camera.Height,
                        Duration = request.Camera.Duration,
                        StartOffset = request.Camera.StartOffset,
                    },
                Events = EventsFileName,
            },
            Scenes =
            [
                new StudioScene
                {
                    Layout = hasCamera ? StudioLayout.Bubble : StudioLayout.Screen,
                    Bubble = new StudioBubble { Anchor = request.BubbleAnchor },
                },
            ],
            Edits = new StudioEdits { TrimStart = hasCamera ? Math.Max(0, request.Camera!.StartOffset) : 0 },
            Overlays = new StudioOverlays { Clicks = request.ClickOverlay, Branding = request.Branding },
        };
    }

    public StudioProject Load(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        return LoadProjectFile(ProjectDirectory(projectId));
    }

    public void Save(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        SaveProjectFile(ProjectDirectory(project.Id), project with { ModifiedAt = now });
        _exportIndexLoaded = false;
    }

    public IReadOnlyList<StudioProjectSummary> ListSummaries()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        var summaries = new List<StudioProjectSummary>();
        foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
        {
            try
            {
                var project = LoadProjectFile(directory);
                var isFlat = project.Sources.Screen.External;
                var externalExists = !isFlat || File.Exists(project.Sources.Screen.File);
                summaries.Add(new StudioProjectSummary(
                    project.Id,
                    project.Name,
                    project.CreatedAt,
                    project.LastOpenedAt,
                    project.Exports.Length == 0,
                    isFlat,
                    project.KeepSources,
                    DirectorySize(directory),
                    externalExists));
            }
            catch (Exception ex) when (ex is StudioProjectInvalidException or StudioUnsupportedSchemaVersionException or IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return summaries.OrderBy(summary => summary.CreatedAt).ThenBy(summary => summary.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Delete(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var directory = ProjectDirectory(projectId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
            _exportIndexLoaded = false;
        }
    }

    public StudioProject MarkOpened(string projectId)
    {
        var project = Load(projectId);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var updated = project with { LastOpenedAt = now, ModifiedAt = now };
        SaveProjectFile(ProjectDirectory(projectId), updated);
        return updated;
    }

    public StudioProject RecordExport(string projectId, string exportedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportedPath);
        var project = Load(projectId);
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var updated = project with
        {
            ModifiedAt = now,
            Exports = [.. project.Exports, new StudioExport { Path = exportedPath, ExportedAt = now }],
        };
        SaveProjectFile(ProjectDirectory(projectId), updated);
        EnsureExportIndex();
        _exportIndex[NormalizePath(exportedPath)] = projectId;
        return updated;
    }

    public string? FindProjectIdByExportPath(string exportedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportedPath);
        EnsureExportIndex();
        return _exportIndex.TryGetValue(NormalizePath(exportedPath), out var projectId) ? projectId : null;
    }

    public bool UpdateExportPath(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        EnsureExportIndex();
        if (!_exportIndex.TryGetValue(NormalizePath(oldPath), out var projectId))
        {
            return false;
        }

        var project = Load(projectId);
        var changed = false;
        var exports = project.Exports.Select(export =>
        {
            if (!PathEquals(export.Path, oldPath))
            {
                return export;
            }

            changed = true;
            return export with { Path = newPath };
        }).ToArray();

        if (!changed)
        {
            return false;
        }

        Save(project with { Exports = exports });
        EnsureExportIndex(force: true);
        return true;
    }

    public bool RemoveExportPath(string exportedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportedPath);
        EnsureExportIndex();
        if (!_exportIndex.TryGetValue(NormalizePath(exportedPath), out var projectId))
        {
            return false;
        }

        var project = Load(projectId);
        var exports = project.Exports.Where(export => !PathEquals(export.Path, exportedPath)).ToArray();
        if (exports.Length == project.Exports.Length)
        {
            return false;
        }

        Save(project with { Exports = exports });
        EnsureExportIndex(force: true);
        return true;
    }

    public StudioProject GetOrCreateFlatProject(string videoPath, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var normalizedVideoPath = NormalizePath(videoPath);

        foreach (var summary in ListSummaries())
        {
            try
            {
                var flatProject = Load(summary.Id);
                if (flatProject.Sources.Screen.External && PathEquals(flatProject.Sources.Screen.File, normalizedVideoPath))
                {
                    return flatProject;
                }
            }
            catch (Exception ex) when (ex is StudioProjectInvalidException or StudioUnsupportedSchemaVersionException or IOException or UnauthorizedAccessException)
            {
            }
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var projectId = Guid.NewGuid().ToString("N");
        var project = new StudioProject
        {
            Id = projectId,
            Name = Path.GetFileNameWithoutExtension(normalizedVideoPath),
            CreatedAt = now,
            ModifiedAt = now,
            LastOpenedAt = now,
            App = new StudioAppInfo { Platform = "windows", Version = appVersion ?? string.Empty },
            Sources = new StudioSources
            {
                Screen = new StudioScreenSource
                {
                    File = normalizedVideoPath,
                    External = true,
                    Width = 1,
                    Height = 1,
                    Duration = 0,
                    FrameRate = 30,
                },
                Camera = null,
                Events = null,
            },
            Scenes = [new StudioScene { Layout = StudioLayout.Screen }],
        };

        var directory = ProjectDirectory(projectId);
        Directory.CreateDirectory(directory);
        SaveProjectFile(directory, project);
        return project;
    }

    public StudioStorageSummary GetStorageSummary()
    {
        var summaries = ListSummaries();
        return new StudioStorageSummary(summaries.Count, summaries.Sum(summary => summary.SizeBytes));
    }

    public StudioEvents LoadEvents(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var path = Path.Combine(ProjectDirectory(projectId), EventsFileName);
        if (!File.Exists(path))
        {
            return new StudioEvents();
        }

        return StudioProjectJson.ReadEvents(File.ReadAllText(path));
    }

    public void SaveEvents(string projectId, StudioEvents events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(events);
        var directory = ProjectDirectory(projectId);
        Directory.CreateDirectory(directory);
        AtomicWrite(Path.Combine(directory, EventsFileName), StudioProjectJson.WriteEvents(events));
    }

    public StudioCleanupResult Cleanup(StudioCleanupOptions? options = null)
    {
        var summaries = ListSummaries();
        var plan = StudioCleanupPolicy.Plan(summaries, _timeProvider.GetUtcNow().ToUniversalTime(), options);
        foreach (var projectId in plan.ProjectIdsToDelete)
        {
            Delete(projectId);
        }

        return new StudioCleanupResult(plan.ProjectIdsToDelete.Length);
    }

    private void EnsureExportIndex(bool force = false)
    {
        if (_exportIndexLoaded && !force)
        {
            return;
        }

        _exportIndex.Clear();
        foreach (var summary in ListSummaries())
        {
            try
            {
                var project = Load(summary.Id);
                foreach (var export in project.Exports.Where(export => !string.IsNullOrWhiteSpace(export.Path)))
                {
                    _exportIndex[NormalizePath(export.Path)] = project.Id;
                }
            }
            catch (Exception ex) when (ex is StudioProjectInvalidException or StudioUnsupportedSchemaVersionException or IOException or UnauthorizedAccessException)
            {
            }
        }

        _exportIndexLoaded = true;
    }

    private StudioProject LoadProjectFile(string projectDirectory) =>
        StudioProjectJson.ReadProject(File.ReadAllText(Path.Combine(projectDirectory, ProjectFileName)));

    private void SaveProjectFile(string projectDirectory, StudioProject project)
    {
        Directory.CreateDirectory(projectDirectory);
        AtomicWrite(Path.Combine(projectDirectory, ProjectFileName), StudioProjectJson.WriteProject(project));
    }

    private static void AtomicWrite(string path, string contents)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string ProjectDirectory(string projectId) => Path.Combine(_rootDirectory, projectId);

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

    private static bool PathEquals(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
}

public sealed record StudioRecordingSourceInfo(int Width, int Height, double Duration, double FrameRate = 30);

public sealed record StudioCameraSourceInfo(int Width, int Height, double Duration, double StartOffset = 0);

public sealed record StudioProjectCreationRequest(
    string Name,
    StudioRecordingSourceInfo Screen,
    StudioCameraSourceInfo? Camera,
    StudioAnchor BubbleAnchor,
    StudioClickOverlay ClickOverlay,
    bool Branding,
    string AppVersion);

public sealed record StudioProjectPaths(
    string ProjectId,
    string ProjectDirectory,
    string ProjectJsonPath,
    string ScreenPath,
    string? CameraPath,
    string EventsPath);

public sealed record StudioProjectCreation(StudioProject Project, StudioProjectPaths Paths);

public sealed record StudioProjectSummary(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastOpenedAt,
    bool IsDraft,
    bool IsFlat,
    bool KeepSources,
    long SizeBytes,
    bool ExternalVideoExists = true);

public sealed record StudioStorageSummary(int ProjectCount, long TotalBytes);

public sealed record StudioCleanupOptions(int RetentionDays = 30, long SizeCapBytes = 10L * 1024 * 1024 * 1024);

public sealed record StudioCleanupPlan(string[] ProjectIdsToDelete);

public sealed record StudioCleanupResult(int DeletedProjectCount);

public static class StudioCleanupPolicy
{
    public static StudioCleanupPlan Plan(
        IEnumerable<StudioProjectSummary> summaries,
        DateTimeOffset now,
        StudioCleanupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        options ??= new StudioCleanupOptions();

        var snapshot = summaries.ToArray();
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var flat in snapshot.Where(summary => summary.IsFlat && !summary.ExternalVideoExists))
        {
            selected.Add(flat.Id);
        }

        if (options.RetentionDays > 0)
        {
            var cutoff = now - TimeSpan.FromDays(options.RetentionDays);
            foreach (var summary in snapshot.Where(summary => IsEligible(summary) && summary.LastOpenedAt < cutoff))
            {
                selected.Add(summary.Id);
            }
        }

        if (options.SizeCapBytes > 0)
        {
            var total = snapshot.Sum(summary => summary.SizeBytes);
            foreach (var summary in snapshot.Where(IsEligible).OrderBy(summary => summary.LastOpenedAt).ThenBy(summary => summary.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (total <= options.SizeCapBytes)
                {
                    break;
                }

                if (selected.Add(summary.Id))
                {
                    total -= summary.SizeBytes;
                }
            }
        }

        return new StudioCleanupPlan(selected.ToArray());
    }

    private static bool IsEligible(StudioProjectSummary summary) =>
        !summary.IsDraft && !summary.KeepSources && !summary.IsFlat;
}
