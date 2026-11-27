using System.Text.Json;
using System.Runtime.InteropServices;
using TinyClips.Core.Studio.Rendering;

namespace TinyClips.Core.Studio;

public sealed partial class StudioProjectStore
{
    public Task<StudioPreparedTimelineEdit> StageTimelineAsync(
        StudioProject destination,
        IReadOnlyList<StudioTimelineImport> imports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(imports);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateProjectId(destination.Id);
        var snapshot = CloneProject(destination);
        var batch = imports.Select(item => item with
        {
            Recording = item.Recording is { } recording ? CloneProject(recording) : null,
        }).ToArray();
        return Task.Run(() => StageTimeline(snapshot, batch, cancellationToken), cancellationToken);
    }

    private StudioPreparedTimelineEdit StageTimeline(
        StudioProject destination, StudioTimelineImport[] imports, CancellationToken token)
    {
        var leases = new List<IDisposable>();
        var directory = ProjectDirectory(destination.Id);
        var staging = Path.Combine(directory, $".import-{Guid.NewGuid():N}");
        try
        {
            byte[] original;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                leases.Add(AcquireProjectUse(destination.Id));
                original = ReadTimelineManifest(destination.Id);
                using var reader = new StreamReader(new MemoryStream(original, writable: false));
                var onDisk = StudioProjectJson.ReadProject(reader.ReadToEnd()) with { Id = destination.Id };
                if (StudioProjectJson.WriteProject(onDisk) != StudioProjectJson.WriteProject(destination))
                    throw new StudioProjectStaleException();
                foreach (var recording in imports.Select(item => item.Recording).OfType<StudioProject>())
                {
                    if (recording.SchemaVersion != 1)
                        throw new StudioProjectInvalidException("Choose a single recording, not a multi-clip project.");
                    leases.Add(AcquireProjectUse(recording.Id));
                    _ = ReadTimelineManifest(recording.Id);
                }
                Directory.CreateDirectory(staging);
            }

            var basis = UpgradeTimelineBase(destination, staging, token);
            _ = StudioProjectMedia.Files(basis, directory, staging);
            var sources = basis.MediaSources!.ToList();
            var clips = basis.Timeline!.Clips.ToList();
            foreach (var item in imports)
            {
                token.ThrowIfCancellationRequested();
                var (source, clip) = StageTimelineSource(item, basis, staging, token);
                _ = new StudioTimelineMap(new() { Clips = [clip] }, [source]);
                sources.Add(source);
                clips.Add(clip);
            }
            var proposed = basis with { MediaSources = sources.ToArray(), Timeline = basis.Timeline with { Clips = clips.ToArray() } };
            proposed = CloneProject(proposed);
            _ = StudioProjectMedia.Files(proposed, directory, staging);
            var stagedIdentities = Directory.GetFiles(staging)
                .ToDictionary(file => Path.GetFileName(file), StudioMediaFileIdentity.Capture, StringComparer.OrdinalIgnoreCase);
            token.ThrowIfCancellationRequested();
            var revision = StudioPreparedTimelineEdit.Revision(original);
            return new(basis, proposed, revision,
                (proposal, cancellation) => CommitTimeline(destination.Id, proposal, proposed, staging, stagedIdentities, original, cancellation),
                () =>
                {
                    try { DeleteFolderQuietly(staging); }
                    finally { foreach (var lease in leases) lease.Dispose(); }
                });
        }
        catch
        {
            DeleteFolderQuietly(staging);
            foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }

    private StudioProject UpgradeTimelineBase(StudioProject destination, string staging, CancellationToken token)
    {
        var basis = StudioTimelineEditing.ToTimelineProject(destination);
        if (destination.SchemaVersion >= 2) return basis;
        var paths = GetPaths(destination);
        var sources = destination.Sources;
        var flat = StudioTimelineEditing.IsFlatRecording(destination);
        if (flat)
        {
            var path = sources.Screen.External
                ? StageMediaFile(paths.ScreenPath, staging, token)
                : RequireTimelineFile(paths.ScreenPath);
            sources = sources with
            {
                Screen = MeasureTimelineScreen(path, sources.Screen),
                Camera = null,
                Events = null,
                ExtensionData = new(sources.ExtensionData)
                {
                    ["mediaKind"] = JsonSerializer.SerializeToElement("flatVideo"),
                },
            };
        }
        else
        {
            sources = sources with { Events = TimelineEventsName(destination) };
        }
        var source = basis.MediaSources![0] with
        {
            Kind = flat ? StudioMediaSourceKind.FlatVideo : StudioMediaSourceKind.NativeRecording,
            Sources = sources,
        };
        return basis with
        {
            // The legacy display dimensions and frame rate anchor the project canvas.
            Sources = sources with
            {
                Screen = destination.Sources.Screen with { File = sources.Screen.File, External = false },
            },
            MediaSources = [source],
            Timeline = basis.Timeline! with
            {
                Clips = [basis.Timeline.Clips[0] with { SourceEnd = sources.Screen.Duration }],
            },
        };
    }

    private (StudioMediaSource, StudioTimelineClip) StageTimelineSource(
        StudioTimelineImport item, StudioProject destination, string staging, CancellationToken token)
    {
        var sourceId = Guid.NewGuid().ToString("D");
        var clipId = Guid.NewGuid().ToString("D");
        if (item.Recording is not { } recording)
        {
            var copied = StageMediaFile(item.VideoPath, staging, token, item.ExpectedVideoIdentity);
            var screen = MeasureTimelineScreen(copied, new());
            var source = new StudioMediaSource
            {
                Id = sourceId,
                Kind = StudioMediaSourceKind.FlatVideo,
                Sources = new() { Screen = screen },
            };
            return (source, new()
            {
                Id = clipId, SourceId = sourceId, Name = Path.GetFileNameWithoutExtension(item.VideoPath),
                SourceEnd = screen.Duration, Screen = destination.Screen with { Crop = null },
                Camera = new(), Scenes = [new() { Layout = StudioLayout.Screen }],
                Zooms = [], Audio = new(), Overlays = new() { Branding = destination.Overlays.Branding }, Captions = new(),
            });
        }

        // Keep the selected linked file stable too, even though its rendered bytes are not imported.
        using var selected = item.ExpectedVideoIdentity is null ? null : OpenExpectedMedia(item.VideoPath, item.ExpectedVideoIdentity);
        var paths = GetPaths(recording);
        if (StudioProjectFolder.IsLink(paths.ProjectDirectory))
            throw StudioProjectFolderException.FileOutsideFolder("recording");
        var flat = StudioTimelineEditing.IsFlatRecording(recording);
        var screenPath = recording.Sources.Screen.External ? paths.ScreenPath : RequireTimelineFile(paths.ScreenPath);
        var sources = recording.Sources with
        {
            Screen = MeasureTimelineScreen(StageMediaFile(screenPath, staging, token), recording.Sources.Screen, preserveFrameRate: !flat),
            Camera = null,
            Events = null,
        };
        if (!flat && recording.Sources.Camera is { } camera)
        {
            var copied = StageMediaFile(RequireTimelineFile(paths.CameraPath!), staging, token);
            var measured = ProbeTimelineVideo(copied);
            sources = sources with { Camera = camera with
            {
                File = Path.GetFileName(copied), Width = measured.Width, Height = measured.Height, Duration = measured.Duration,
            } };
        }
        if (!flat && TimelineEventsName(recording) is { } events)
        {
            var copied = StageMediaFile(RequireTimelineFile(Path.Combine(paths.ProjectDirectory, events)), staging, token);
            _ = StudioProjectJson.ReadEvents(File.ReadAllText(copied));
            sources = sources with { Events = Path.GetFileName(copied) };
        }
        var imported = new StudioMediaSource
        {
            Id = sourceId, Kind = flat ? StudioMediaSourceKind.FlatVideo : StudioMediaSourceKind.NativeRecording, Sources = sources,
        };
        if (recording.Canvas.Background.Image is { } image)
        {
            StudioProjectMedia.ValidateName(image, "canvas.background.image");
            var copied = StageMediaFile(RequireTimelineFile(Path.Combine(paths.ProjectDirectory, image)), staging, token);
            if (!StudioBackgroundImage.TryDecode(copied, 16, out _, out _, out _))
                throw new StudioProjectInvalidException("The recording background image cannot be decoded.");
            imported.ExtensionData[StudioProjectMedia.RecordingBackgroundImageKey] = JsonSerializer.SerializeToElement(Path.GetFileName(copied));
        }
        var converted = StudioTimelineEditing.ToTimelineProject(recording with { Sources = sources }).Timeline!.Clips[0];
        return (imported, converted with { Id = clipId, SourceId = sourceId });
    }

    private StudioProject CommitTimeline(string id, StudioProject proposal, StudioProject prepared,
        string staging, IReadOnlyDictionary<string, StudioMediaFileIdentity> stagedIdentities, byte[] original, CancellationToken token)
    {
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (proposal.Id != id || proposal.SchemaVersion != 2)
                throw new StudioProjectInvalidException("A prepared timeline edit belongs to its original project.");
            var updated = CloneProject(proposal) with { ModifiedAt = _timeProvider.GetUtcNow().ToUniversalTime() };
            // Only prepared media may be published; callers can edit/order clips but cannot smuggle
            // an unprobed source, replace source metadata, or drop registry entries needed by undo.
            var approved = prepared.MediaSources!.ToDictionary(source => source.Id, StringComparer.Ordinal);
            if (updated.MediaSources!.Length != approved.Count || updated.MediaSources.Any(source =>
                !approved.TryGetValue(source.Id, out var expected)
                || JsonSerializer.Serialize(source, StudioJsonContext.Default.StudioMediaSource)
                    != JsonSerializer.Serialize(expected, StudioJsonContext.Default.StudioMediaSource)))
            {
                throw new StudioProjectInvalidException("The proposal must retain the prepared media registry unchanged.");
            }
            var allowed = StudioProjectDestination.OwnedFiles(prepared);
            var directory = ProjectDirectory(id);
            var files = StudioProjectMedia.Files(updated, directory, staging);
            if (files.Any(file => !allowed.Contains(file.Name)))
                throw new StudioProjectInvalidException("The proposal names media not included in this transaction.");
            var json = StudioProjectJson.WriteProject(updated);
            if (!ReadTimelineManifest(id).SequenceEqual(original)) throw new StudioProjectStaleException();
            var installed = new List<string>();
            try
            {
                foreach (var file in files.Where(file => string.Equals(Path.GetDirectoryName(file.Source), staging, StringComparison.OrdinalIgnoreCase)))
                {
                    token.ThrowIfCancellationRequested();
                    if (!stagedIdentities.TryGetValue(file.Name, out var identity)
                        || StudioMediaFileIdentity.Capture(file.Source) != identity)
                        throw new StudioMediaFileChangedException();
                    var target = Path.Combine(directory, file.Name);
                    File.Move(file.Source, target);
                    installed.Add(target);
                }
                token.ThrowIfCancellationRequested();
                if (!ReadTimelineManifest(id).SequenceEqual(original)) throw new StudioProjectStaleException();
                AtomicWrite(Path.Combine(directory, ProjectFileName), json);
                _exportIndexLoaded = false;
                return updated;
            }
            catch (Exception failure)
            {
                try
                {
                    foreach (var path in installed.AsEnumerable().Reverse()) File.Delete(path);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("The timeline was not published, but its imported files could not all be removed.",
                        new AggregateException(failure, cleanup));
                }
                throw;
            }
        }
    }

    private byte[] ReadTimelineManifest(string id)
    {
        var directory = ProjectDirectory(id);
        if (StudioProjectFolder.IsLink(directory))
            throw StudioProjectFolderException.FileOutsideFolder("project directory");
        var path = RequireTimelineFile(Path.Combine(directory, ProjectFileName), validateName: false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > StudioProjectFolder.LargestProjectFileBytes)
            throw StudioProjectFolderException.NotAProjectFile();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private string? TimelineEventsName(StudioProject project)
    {
        var paths = GetPaths(project);
        if (File.Exists(paths.EventsPath)) return Path.GetFileName(RequireTimelineFile(paths.EventsPath));
        if (project.Sources.Events is { } events && events != EventsFileName)
            throw StudioProjectFolderException.MissingFile(events);
        return null;
    }

    private StudioScreenSource MeasureTimelineScreen(string path, StudioScreenSource metadata, bool preserveFrameRate = false)
    {
        var measured = ProbeTimelineVideo(path);
        return metadata with
        {
            File = Path.GetFileName(path), External = false, Width = measured.Width, Height = measured.Height,
            Duration = measured.Duration,
            FrameRate = preserveFrameRate && double.IsFinite(metadata.FrameRate) && metadata.FrameRate > 0
                ? metadata.FrameRate : measured.FrameRate,
            RotationDegrees = measured.RotationDegrees,
        };
    }

    private StudioRecordingSourceInfo ProbeTimelineVideo(string path)
    {
        StudioRecordingSourceInfo measured;
        try { measured = _probeTimelineMedia(path); }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or InvalidDataException)
        {
            throw new StudioProjectInvalidException("The imported video could not be read.", ex);
        }
        if (measured.Width <= 0 || measured.Height <= 0 || !double.IsFinite(measured.Duration) || measured.Duration <= 0
            || !double.IsFinite(measured.FrameRate) || measured.FrameRate <= 0 || !double.IsFinite(measured.RotationDegrees))
            throw new StudioProjectInvalidException("The imported video has no usable video stream or duration.");
        return measured;
    }

    private static FileStream OpenExpectedMedia(string path, StudioMediaFileIdentity expected)
    {
        try
        {
            var stream = StudioMediaFileIdentity.Open(path, out var actual);
            if (actual == expected) return stream;
            stream.Dispose();
            throw new StudioMediaFileChangedException();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new StudioMediaFileChangedException(ex);
        }
    }

    private static string StageMediaFile(string path, string staging, CancellationToken token, StudioMediaFileIdentity? expected = null)
    {
        token.ThrowIfCancellationRequested();
        using var input = expected is not null ? OpenExpectedMedia(path, expected) : StudioMediaFileIdentity.Open(path, out _);
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (extension.Length is 0 or > 12 || !extension.All(char.IsAsciiLetterOrDigit) || extension == "tinyclips") extension = "bin";
        var target = Path.Combine(staging, $"media-{Guid.NewGuid():N}.{extension}");
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(FolderCopyPieceBytes);
        try
        {
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        return target;
    }

    private static string RequireTimelineFile(string path, bool validateName = true)
    {
        if (validateName) StudioProjectMedia.ValidateName(Path.GetFileName(path), "source");
        if (StudioProjectFolder.IsLink(path)) throw StudioProjectFolderException.FileOutsideFolder("source");
        if (!File.Exists(path)) throw StudioProjectFolderException.MissingFile(Path.GetFileName(path));
        return path;
    }

    private static StudioProject CloneProject(StudioProject project) =>
        StudioProjectJson.ReadProject(StudioProjectJson.WriteProject(project));
}
