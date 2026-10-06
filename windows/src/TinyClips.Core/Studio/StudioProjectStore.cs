using System.Text.Json;
using System.Text.RegularExpressions;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.Core.Studio;

public interface IStudioProjectStore
{
    string RootDirectory { get; }
    StudioProjectPaths BeginRecording();
    StudioProject CompleteRecording(string projectId, StudioProjectCreationRequest request);
    StudioProjectPaths GetPaths(string projectId);
    StudioProjectPaths GetPaths(StudioProject project);
    bool Exists(string projectId);
    StudioProject Load(string projectId);
    StudioProject Save(StudioProject project);
    void Delete(string projectId);
    StudioProject MarkOpened(string projectId);

    /// <summary>
    /// Pins a project against automatic cleanup, or lets go of it again. It is written into the
    /// project on disk at once and is not one of the editor's edits: nothing undoes it.
    /// </summary>
    StudioProject SetKeepSources(string projectId, bool keepSources);

    IReadOnlyList<StudioProjectSummary> ListSummaries();

    /// <summary>
    /// The project folders whose <c>project.json</c> is there and cannot be read: damaged, or
    /// written by a newer version. <see cref="ListSummaries"/> leaves them out and cleanup leaves
    /// them alone, so this list is the only place they show up.
    /// </summary>
    IReadOnlyList<StudioUnreadableProject> ListUnreadableProjects();

    /// <summary>
    /// The full path of the screen recording a project keeps in its own folder, or null when it
    /// has none there: the project is built around a video kept elsewhere, the file is gone, or
    /// there is no such project. A project whose <c>project.json</c> cannot be read is looked
    /// for under the name every recording gets.
    /// </summary>
    string? FindScreenRecording(string projectId);

    StudioStorageSummary GetStorageSummary();
    StudioProject RecordExport(string projectId, string exportedPath);
    string? FindProjectIdByExportPath(string exportedPath);
    bool UpdateExportPath(string oldPath, string newPath);
    bool RemoveExportPath(string exportedPath);
    StudioProject GetOrCreateFlatProject(string videoPath, StudioRecordingSourceInfo video, string appVersion);
    StudioEvents LoadEvents(string projectId);
    void SaveEvents(string projectId, StudioEvents events);

    /// <summary>
    /// Deletes what the cleanup rules select: old projects whose video was exported, and
    /// recordings that were never finished.
    /// </summary>
    /// <param name="options">The rules. Null means the defaults.</param>
    /// <param name="inUseProjectIds">
    /// Projects that are open in an editor or being recorded into. They are left alone, and the
    /// storage limit is worked out without counting on their going.
    /// </param>
    /// <param name="isInUse">
    /// Asked again for each project just before it is deleted, and true leaves it. The list
    /// above is made before the cleanup starts, and reading every project takes a moment: a
    /// project that is opened in that moment is not in the list, and would lose its folder from
    /// under its editor. It is called while the store is locked, on the thread the cleanup runs
    /// on, so it has to answer at once and must not wait for another thread.
    /// </param>
    StudioCleanupResult Cleanup(
        StudioCleanupOptions? options = null,
        IReadOnlyCollection<string>? inUseProjectIds = null,
        Func<string, bool>? isInUse = null);
}

public sealed class StudioProjectStore : IStudioProjectStore
{
    public const string ProjectFileName = "project.json";
    public const string EventsFileName = "events.json";
    public const string ScreenFileName = "screen.mp4";
    public const string CameraFileName = "camera.mp4";
    public const string PosterFileName = "poster.jpg";

    private static readonly Regex ProjectIdRegex = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _rootDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _exportIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _exportIndexLoaded;

    public StudioProjectStore(string? rootDirectory = null, TimeProvider? timeProvider = null)
    {
        _rootDirectory = rootDirectory ?? DefaultRootDirectory();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string RootDirectory => _rootDirectory;

    public static string DefaultRootDirectory() =>
        Path.Combine(ClipsLibraryPaths.LocalDataDirectory(), "TinyClips", "Projects");

    public StudioProjectPaths BeginRecording()
    {
        lock (_sync)
        {
            var projectId = Guid.NewGuid().ToString("D");
            var paths = BuildPaths(projectId, null);
            Directory.CreateDirectory(paths.ProjectDirectory);
            return paths;
        }
    }

    public StudioProject CompleteRecording(string projectId, StudioProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProjectId(projectId);

        lock (_sync)
        {
            var project = BuildDefaultProject(projectId, request, _timeProvider.GetUtcNow().ToUniversalTime());
            SaveProjectFile(ProjectDirectory(projectId), project);
            _exportIndexLoaded = false;
            return project;
        }
    }

    public StudioProjectPaths GetPaths(string projectId)
    {
        ValidateProjectId(projectId);
        return BuildPaths(projectId, null);
    }

    public StudioProjectPaths GetPaths(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateProjectId(project.Id);
        return BuildPaths(project.Id, project);
    }

    public bool Exists(string projectId)
    {
        ValidateProjectId(projectId);
        lock (_sync)
        {
            return File.Exists(Path.Combine(ProjectDirectory(projectId), ProjectFileName));
        }
    }

    public StudioProject Load(string projectId)
    {
        ValidateProjectId(projectId);
        lock (_sync)
        {
            return LoadProjectFile(ProjectDirectory(projectId), projectId);
        }
    }

    public StudioProject Save(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ValidateProjectId(project.Id);

        lock (_sync)
        {
            var updated = project with { ModifiedAt = _timeProvider.GetUtcNow().ToUniversalTime() };
            SaveProjectFile(ProjectDirectory(updated.Id), updated);
            _exportIndexLoaded = false;
            return updated;
        }
    }

    public void Delete(string projectId)
    {
        ValidateProjectId(projectId);
        lock (_sync)
        {
            var directory = ProjectDirectory(projectId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
                _exportIndexLoaded = false;
            }
        }
    }

    public StudioProject MarkOpened(string projectId)
    {
        ValidateProjectId(projectId);
        lock (_sync)
        {
            var project = LoadProjectFile(ProjectDirectory(projectId), projectId);
            var updated = project with { LastOpenedAt = _timeProvider.GetUtcNow().ToUniversalTime() };
            SaveProjectFile(ProjectDirectory(projectId), updated);
            return updated;
        }
    }

    public StudioProject SetKeepSources(string projectId, bool keepSources)
    {
        ValidateProjectId(projectId);
        lock (_sync)
        {
            var directory = ProjectDirectory(projectId);
            var project = LoadProjectFile(directory, projectId);
            if (project.KeepSources == keepSources)
            {
                return project;
            }

            var updated = project with { KeepSources = keepSources };
            SaveProjectFile(directory, updated);
            return updated;
        }
    }

    public IReadOnlyList<StudioProjectSummary> ListSummaries()
    {
        lock (_sync)
        {
            return EnumerateProjectFolders()
                .Select(folder => TryLoadSummary(folder))
                .Where(static summary => summary is not null)
                .Select(static summary => summary!)
                .OrderBy(summary => summary.CreatedAt)
                .ThenBy(summary => summary.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<StudioUnreadableProject> ListUnreadableProjects()
    {
        lock (_sync)
        {
            var unreadable = new List<StudioUnreadableProject>();
            foreach (var folder in EnumerateProjectFolders())
            {
                // Without the file it is a recording that never finished, which cleanup removes
                // after a day.
                if (!File.Exists(Path.Combine(folder.Directory, ProjectFileName)))
                {
                    continue;
                }

                try
                {
                    LoadProjectFile(folder.Directory, folder.Id);
                }
                catch (Exception ex) when (CanSkipProjectRead(ex))
                {
                    unreadable.Add(new StudioUnreadableProject(
                        folder.Id,
                        FolderCreationTime(folder.Directory),
                        DirectorySize(folder.Directory),
                        File.Exists(Path.Combine(folder.Directory, ScreenFileName))));
                }
            }

            return unreadable
                .OrderBy(project => project.CreatedAt)
                .ThenBy(project => project.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public string? FindScreenRecording(string projectId)
    {
        ValidateProjectId(projectId);
        lock (_sync)
        {
            var directory = ProjectDirectory(projectId);
            string path;
            try
            {
                var project = LoadProjectFile(directory, projectId);
                if (project.Sources.Screen.External)
                {
                    return null;
                }

                path = Path.Combine(directory, RequirePlainFileName(project.Sources.Screen.File, "sources.screen.file"));
            }
            catch (Exception ex) when (CanSkipProjectRead(ex))
            {
                // What cannot be read does not say where its recording is. Every recording is
                // written under the same name, so that is where it is looked for.
                path = Path.Combine(directory, ScreenFileName);
            }

            return File.Exists(path) ? path : null;
        }
    }

    public StudioStorageSummary GetStorageSummary()
    {
        var summaries = ListSummaries();
        return new StudioStorageSummary(summaries.Count, summaries.Sum(summary => summary.SizeBytes));
    }

    public StudioProject RecordExport(string projectId, string exportedPath)
    {
        ValidateProjectId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportedPath);

        lock (_sync)
        {
            var normalizedExportPath = NormalizePath(exportedPath);
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            StudioProject? updatedProject = null;

            foreach (var folder in EnumerateProjectFolders())
            {
                StudioProject project;
                try
                {
                    project = LoadProjectFile(folder.Directory, folder.Id);
                }
                catch (Exception ex) when (CanSkipProjectRead(ex))
                {
                    continue;
                }

                var exports = project.Exports
                    .Where(export => !PathEquals(export.Path, normalizedExportPath))
                    .ToArray();

                if (project.Id == projectId)
                {
                    exports = [.. exports, new StudioExport { Path = normalizedExportPath, ExportedAt = now }];
                    updatedProject = project with { ModifiedAt = now, Exports = exports };
                    SaveProjectFile(folder.Directory, updatedProject);
                }
                else if (exports.Length != project.Exports.Length)
                {
                    SaveProjectFile(folder.Directory, project with { ModifiedAt = now, Exports = exports });
                }
            }

            if (updatedProject is null)
            {
                throw new DirectoryNotFoundException($"Studio project '{projectId}' does not exist.");
            }

            EnsureExportIndex(force: true);
            return updatedProject;
        }
    }

    public string? FindProjectIdByExportPath(string exportedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportedPath);
        lock (_sync)
        {
            EnsureExportIndex();
            return _exportIndex.TryGetValue(NormalizePath(exportedPath), out var projectId) ? projectId : null;
        }
    }

    public bool UpdateExportPath(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        lock (_sync)
        {
            EnsureExportIndex();
            if (!_exportIndex.TryGetValue(NormalizePath(oldPath), out var projectId))
            {
                return false;
            }

            var normalizedNewPath = NormalizePath(newPath);
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var directory = ProjectDirectory(projectId);
            var project = LoadProjectFile(directory, projectId);
            var moved = project.Exports.LastOrDefault(export => PathEquals(export.Path, oldPath));
            if (moved is null)
            {
                return false;
            }

            // The file at the new path is now this export, so no other entry may keep pointing at it.
            var exports = project.Exports
                .Where(export => !PathEquals(export.Path, oldPath) && !PathEquals(export.Path, normalizedNewPath))
                .Append(moved with { Path = normalizedNewPath })
                .ToArray();
            SaveProjectFile(directory, project with { ModifiedAt = now, Exports = exports });

            foreach (var folder in EnumerateProjectFolders().Where(folder => folder.Id != projectId))
            {
                try
                {
                    var other = LoadProjectFile(folder.Directory, folder.Id);
                    var remaining = other.Exports.Where(export => !PathEquals(export.Path, normalizedNewPath)).ToArray();
                    if (remaining.Length != other.Exports.Length)
                    {
                        SaveProjectFile(folder.Directory, other with { ModifiedAt = now, Exports = remaining });
                    }
                }
                catch (Exception ex) when (CanSkipProjectRead(ex))
                {
                }
            }

            EnsureExportIndex(force: true);
            return true;
        }
    }

    public bool RemoveExportPath(string exportedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportedPath);

        lock (_sync)
        {
            EnsureExportIndex();
            if (!_exportIndex.TryGetValue(NormalizePath(exportedPath), out var projectId))
            {
                return false;
            }

            var directory = ProjectDirectory(projectId);
            var project = LoadProjectFile(directory, projectId);
            var exports = project.Exports.Where(export => !PathEquals(export.Path, exportedPath)).ToArray();
            if (exports.Length == project.Exports.Length)
            {
                return false;
            }

            SaveProjectFile(directory, project with { ModifiedAt = _timeProvider.GetUtcNow().ToUniversalTime(), Exports = exports });
            EnsureExportIndex(force: true);
            return true;
        }
    }

    public StudioProject GetOrCreateFlatProject(string videoPath, StudioRecordingSourceInfo video, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var normalizedVideoPath = NormalizePath(videoPath);

        lock (_sync)
        {
            foreach (var folder in EnumerateProjectFolders())
            {
                try
                {
                    var flatProject = LoadProjectFile(folder.Directory, folder.Id);
                    if (flatProject.Sources.Screen.External && PathEquals(flatProject.Sources.Screen.File, normalizedVideoPath))
                    {
                        return flatProject;
                    }
                }
                catch (Exception ex) when (CanSkipProjectRead(ex))
                {
                }
            }

            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var projectId = Guid.NewGuid().ToString("D");
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
                        Width = video.Width,
                        Height = video.Height,
                        Duration = video.Duration,
                        FrameRate = video.FrameRate,
                    },
                    Camera = null,
                    Events = null,
                },
                Scenes = [new StudioScene { Layout = StudioLayout.Screen }],
            };

            SaveProjectFile(ProjectDirectory(projectId), project);
            return project;
        }
    }

    public StudioEvents LoadEvents(string projectId)
    {
        ValidateProjectId(projectId);
        lock (_sync)
        {
            var path = Path.Combine(ProjectDirectory(projectId), EventsFileName);
            return File.Exists(path) ? StudioProjectJson.ReadEvents(File.ReadAllText(path)) : new StudioEvents();
        }
    }

    public void SaveEvents(string projectId, StudioEvents events)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(events);

        lock (_sync)
        {
            var directory = ProjectDirectory(projectId);
            Directory.CreateDirectory(directory);
            AtomicWrite(Path.Combine(directory, EventsFileName), StudioProjectJson.WriteEvents(events));
        }
    }

    public StudioCleanupResult Cleanup(
        StudioCleanupOptions? options = null,
        IReadOnlyCollection<string>? inUseProjectIds = null,
        Func<string, bool>? isInUse = null)
    {
        if (inUseProjectIds is not null)
        {
            foreach (var id in inUseProjectIds)
            {
                ValidateProjectId(id);
            }
        }

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var inUse = new HashSet<string>(inUseProjectIds ?? [], StringComparer.Ordinal);
            var summaries = new List<StudioProjectSummary>();
            var unfinishedRecordings = new List<string>();

            foreach (var folder in EnumerateProjectFolders())
            {
                if (!File.Exists(Path.Combine(folder.Directory, ProjectFileName)))
                {
                    if (!inUse.Contains(folder.Id) && IsOlderThanUnfinishedRecordingLimit(folder.Directory, now))
                    {
                        unfinishedRecordings.Add(folder.Id);
                    }

                    continue;
                }

                var summary = TryLoadSummary(folder);
                if (summary is not null)
                {
                    summaries.Add(summary);
                }
            }

            var plan = StudioCleanupPolicy.Plan(summaries, now, options, inUse);
            var candidates = unfinishedRecordings.Concat(plan.ProjectIdsToDelete).Distinct(StringComparer.Ordinal).ToArray();
            var deleted = new List<string>();

            foreach (var projectId in candidates)
            {
                // Asked now, with the store locked: an editor reads its project through the
                // store before it opens any of its files, so a project that is not in use at
                // this moment is gone before an editor can have anything of it open.
                if (isInUse?.Invoke(projectId) == true)
                {
                    continue;
                }

                try
                {
                    var directory = ProjectDirectory(projectId);
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                        deleted.Add(projectId);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (deleted.Count > 0)
            {
                _exportIndexLoaded = false;
            }

            return new StudioCleanupResult(deleted.Count, deleted.ToArray());
        }
    }

    internal static StudioProject BuildDefaultProjectForRecording(string projectId, StudioProjectCreationRequest request, DateTimeOffset now)
    {
        ValidateProjectId(projectId);
        ArgumentNullException.ThrowIfNull(request);

        var hasCamera = request.Camera is not null;
        var look = request.Look;
        var screen = (look?.Screen ?? new StudioScreenStyle()) with { Crop = null };
        var camera = (look?.Camera ?? new StudioCameraStyle()) with { Crop = null };
        var firstScene = new StudioScene
        {
            Layout = hasCamera ? StudioLayout.Bubble : StudioLayout.Screen,
            Bubble = new StudioBubble { Anchor = request.BubbleAnchor },
        };

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
            Canvas = look?.Canvas ?? new StudioCanvas(),
            Screen = screen,
            Camera = camera,
            // With a camera, what was changed while recording becomes scenes (section 9.1).
            Scenes = hasCamera
                ? StudioRecordingBuilder.BuildScenes(firstScene, request.CameraCorners, request.LayoutMarkers, request.Screen.Duration)
                : [firstScene],
            Edits = new StudioEdits { TrimStart = hasCamera ? Math.Max(0, request.Camera!.StartOffset) : 0 },
            Overlays = new StudioOverlays { Clicks = request.ClickOverlay, Branding = request.Branding },
        };
    }

    private StudioProject BuildDefaultProject(string projectId, StudioProjectCreationRequest request, DateTimeOffset now) =>
        BuildDefaultProjectForRecording(projectId, request, now);

    private StudioProjectPaths BuildPaths(string projectId, StudioProject? project)
    {
        ValidateProjectId(projectId);
        var projectDirectory = ProjectDirectory(projectId);
        var screenPath = project?.Sources.Screen.External == true
            ? NormalizePath(project.Sources.Screen.File)
            : Path.Combine(projectDirectory, RequirePlainFileName(project?.Sources.Screen.File ?? ScreenFileName, "sources.screen.file"));
        var cameraPath = project is null
            ? Path.Combine(projectDirectory, CameraFileName)
            : project.Sources.Camera is null ? null : Path.Combine(projectDirectory, RequirePlainFileName(project.Sources.Camera.File, "sources.camera.file"));
        var eventsPath = Path.Combine(projectDirectory, RequirePlainFileName(project?.Sources.Events ?? EventsFileName, "sources.events"));

        return new StudioProjectPaths(
            projectId,
            projectDirectory,
            Path.Combine(projectDirectory, ProjectFileName),
            screenPath,
            cameraPath,
            eventsPath,
            Path.Combine(projectDirectory, PosterFileName));
    }

    private void EnsureExportIndex(bool force = false)
    {
        if (_exportIndexLoaded && !force)
        {
            return;
        }

        _exportIndex.Clear();
        foreach (var folder in EnumerateProjectFolders())
        {
            try
            {
                var project = LoadProjectFile(folder.Directory, folder.Id);
                foreach (var export in project.Exports.Where(export => !string.IsNullOrWhiteSpace(export.Path)))
                {
                    _exportIndex[NormalizePath(export.Path)] = project.Id;
                }
            }
            catch (Exception ex) when (CanSkipProjectRead(ex))
            {
            }
        }

        _exportIndexLoaded = true;
    }

    private StudioProjectSummary? TryLoadSummary(ProjectFolder folder)
    {
        try
        {
            var project = LoadProjectFile(folder.Directory, folder.Id);
            var isFlat = project.Sources.Screen.External;
            var externalExists = !isFlat || File.Exists(project.Sources.Screen.File);

            // Looked at where each video was saved. One on a drive that is not connected counts
            // as not there, which keeps the project: the safe side of not knowing.
            var exportMissing = project.Exports.Length > 0
                && !project.Exports.Any(static export => !string.IsNullOrWhiteSpace(export.Path) && File.Exists(export.Path));
            return new StudioProjectSummary(
                folder.Id,
                project.Name,
                project.CreatedAt,
                project.LastOpenedAt,
                project.Exports.Length == 0,
                isFlat,
                project.KeepSources,
                DirectorySize(folder.Directory),
                externalExists,
                exportMissing);
        }
        catch (Exception ex) when (CanSkipProjectRead(ex))
        {
            return null;
        }
    }

    private IEnumerable<ProjectFolder> EnumerateProjectFolders()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(_rootDirectory)
            .Select(directory => new ProjectFolder(Path.GetFileName(directory), directory))
            .Where(folder => IsValidProjectId(folder.Id))
            .ToArray();
    }

    private StudioProject LoadProjectFile(string projectDirectory, string projectId)
    {
        var project = StudioProjectJson.ReadProject(File.ReadAllText(Path.Combine(projectDirectory, ProjectFileName)));
        return project with { Id = projectId };
    }

    private void SaveProjectFile(string projectDirectory, StudioProject project)
    {
        ValidateProjectId(project.Id);
        Directory.CreateDirectory(projectDirectory);
        AtomicWrite(Path.Combine(projectDirectory, ProjectFileName), StudioProjectJson.WriteProject(project));
    }

    private static void AtomicWrite(string path, string contents)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 2 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private string ProjectDirectory(string projectId)
    {
        ValidateProjectId(projectId);
        return Path.Combine(_rootDirectory, projectId);
    }

    private static bool IsOlderThanUnfinishedRecordingLimit(string directory, DateTimeOffset now)
    {
        var creationTime = new DirectoryInfo(directory).CreationTimeUtc;
        return creationTime <= (now - TimeSpan.FromHours(24)).UtcDateTime;
    }

    private static DateTimeOffset FolderCreationTime(string directory)
    {
        try
        {
            return new DateTimeOffset(new DirectoryInfo(directory).CreationTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));

    private static bool PathEquals(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool CanSkipProjectRead(Exception ex) =>
        ex is StudioProjectInvalidException or StudioUnsupportedSchemaVersionException or IOException or UnauthorizedAccessException or JsonException;

    private static bool IsValidProjectId(string? projectId) =>
        projectId is not null && ProjectIdRegex.IsMatch(projectId);

    // The reader already rejects these, but a project built in memory has not been through it.
    private static string RequirePlainFileName(string fileName, string property) =>
        StudioProjectJson.IsPlainFileName(fileName)
            ? fileName
            : throw new StudioProjectInvalidException($"Property {property} must be a file name.");

    private static void ValidateProjectId(string? projectId)
    {
        if (!IsValidProjectId(projectId))
        {
            throw new ArgumentException("Project id must be a lowercase hyphenated GUID.", nameof(projectId));
        }
    }

    private readonly record struct ProjectFolder(string Id, string Directory);
}

public sealed record StudioRecordingSourceInfo(int Width, int Height, double Duration, double FrameRate = 30);

public sealed record StudioCameraSourceInfo(int Width, int Height, double Duration, double StartOffset = 0);

/// <param name="CameraCorners">
/// The corners the camera was in while recording, with the time it got to each. With
/// <paramref name="LayoutMarkers"/> they become the project's scenes (section 9.1 of the format).
/// </param>
/// <param name="LayoutMarkers">The layouts chosen while recording, with the time of each.</param>
public sealed record StudioProjectCreationRequest(
    string Name,
    StudioRecordingSourceInfo Screen,
    StudioCameraSourceInfo? Camera,
    StudioAnchor BubbleAnchor,
    StudioClickOverlay ClickOverlay,
    bool Branding,
    string AppVersion,
    StudioLook? Look = null,
    IReadOnlyList<StudioCameraCornerEvent>? CameraCorners = null,
    IReadOnlyList<StudioLayoutMarker>? LayoutMarkers = null);

public sealed record StudioProjectPaths(
    string ProjectId,
    string ProjectDirectory,
    string ProjectJsonPath,
    string ScreenPath,
    string? CameraPath,
    string EventsPath,
    string PosterPath);

/// <param name="IsDraft">No video was ever exported from the project.</param>
/// <param name="ExportMissing">
/// Videos were exported and none of them is where it was saved any more. The project is then the
/// only copy of the recording, as a draft is, and is treated as one: listed with the drafts, and
/// never removed by cleanup.
/// </param>
public sealed record StudioProjectSummary(
    string Id,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastOpenedAt,
    bool IsDraft,
    bool IsFlat,
    bool KeepSources,
    long SizeBytes,
    bool ExternalVideoExists = true,
    bool ExportMissing = false)
{
    /// <summary>
    /// Whether cleanup may remove the project: a video exported from it is still where it was
    /// saved, the project is not pinned, and it is not built around a video kept elsewhere.
    /// </summary>
    public bool IsRemovableByCleanup => !IsDraft && !ExportMissing && !KeepSources && !IsFlat;
}

/// <summary>A project folder whose <c>project.json</c> cannot be read.</summary>
/// <param name="CreatedAt">When the folder was made, which is when the recording started.</param>
/// <param name="HasScreenRecording">Whether a screen recording is in the folder to be saved out of it.</param>
public sealed record StudioUnreadableProject(string Id, DateTimeOffset CreatedAt, long SizeBytes, bool HasScreenRecording);

public sealed record StudioStorageSummary(int ProjectCount, long TotalBytes);

public sealed record StudioCleanupOptions(int RetentionDays = 30, long SizeCapBytes = 10L * 1024 * 1024 * 1024);

public sealed record StudioCleanupPlan(string[] ProjectIdsToDelete);

public sealed record StudioCleanupResult(int DeletedProjectCount, string[] ProjectIdsDeleted);

public static class StudioCleanupPolicy
{
    public static StudioCleanupPlan Plan(
        IEnumerable<StudioProjectSummary> summaries,
        DateTimeOffset now,
        StudioCleanupOptions? options = null,
        IReadOnlyCollection<string>? inUseProjectIds = null)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        options ??= new StudioCleanupOptions();

        var inUse = new HashSet<string>(inUseProjectIds ?? [], StringComparer.Ordinal);
        var snapshot = summaries.ToArray();
        var remaining = snapshot.ToDictionary(summary => summary.Id, StringComparer.Ordinal);
        var selected = new List<string>();

        foreach (var flat in snapshot.Where(summary => !inUse.Contains(summary.Id) && summary.IsFlat && !summary.ExternalVideoExists))
        {
            if (remaining.Remove(flat.Id))
            {
                selected.Add(flat.Id);
            }
        }

        if (options.RetentionDays > 0)
        {
            var cutoff = now - TimeSpan.FromDays(options.RetentionDays);
            foreach (var summary in snapshot.Where(summary => !inUse.Contains(summary.Id) && summary.IsRemovableByCleanup && summary.LastOpenedAt < cutoff))
            {
                if (remaining.Remove(summary.Id))
                {
                    selected.Add(summary.Id);
                }
            }
        }

        if (options.SizeCapBytes > 0)
        {
            // The limit is on what cleanup may remove. Drafts, pinned projects and the like are
            // not counted: they are never removed, and counted they would use the room up, so
            // that every exported project went the moment it was exported.
            var removable = remaining.Values.Where(static summary => summary.IsRemovableByCleanup).ToArray();
            var total = removable.Sum(summary => summary.SizeBytes);
            foreach (var summary in removable
                .Where(summary => !inUse.Contains(summary.Id))
                .OrderBy(summary => summary.LastOpenedAt)
                .ThenBy(summary => summary.Id, StringComparer.Ordinal))
            {
                if (total <= options.SizeCapBytes)
                {
                    break;
                }

                selected.Add(summary.Id);
                total -= summary.SizeBytes;
            }
        }

        return new StudioCleanupPlan(selected.ToArray());
    }
}
