using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using TinyClips.App.Services.Files;
using TinyClips.Core.Automation;
using TinyClips.Core.Capture;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Services.ClipsLibrary;

namespace TinyClips.App;

/// <summary>
/// Runs agent commands from the automation contract against the app. Commands that touch the UI
/// or the capture pipeline run on the UI thread; file and settings checks happen here so the app
/// keeps the same rules as the macOS router (library files only, allowlisted settings).
/// </summary>
internal sealed partial class AppAutomationCommandHandler : IAutomationCommandHandler
{
    private static readonly string[] Supported =
    [
        "app.status",
        "app.open",
        "recording.status",
        "library.list",
        "settings.get",
        "capture.targets",
        "capture.screenshot",
        "capture.ocr",
        "capture.scrolling.start",
        "capture.scrolling.stop",
        "capture.scrolling.status",
        "capture.scrolling.cancel",
        "recording.startVideo",
        "recording.startGif",
        "recording.stop",
        "recording.togglePause",
        "recording.restart",
        "recording.discard",
        "editor.open",
        "studio.projects.list",
        "studio.project.open",
        "studio.project.openFolder",
        "studio.project.delete",
        "studio.project.get",
        "studio.project.saveCopy",
        "studio.project.edit",
        "studio.zoom.suggest",
        "studio.export.options",
        "studio.export",
        "studio.captions.check",
        "studio.captions.import",
        "studio.captions.fix",
        "studio.captions.export",
        "studio.captions.models",
        "studio.captions.generate",
        "editor.annotate",
        "editor.trim",
        "editor.saveFrame",
        "library.reveal",
        "library.copy",
        "settings.set",
        "library.delete",
        "library.upload",
    ];

    private readonly IAutomationAppActions _app;
    private readonly Func<DispatcherQueue?> _dispatcher;
    private readonly Func<bool> _isEnabled;

    public AppAutomationCommandHandler(IAutomationAppActions app, Func<DispatcherQueue?> dispatcher, Func<bool> isEnabled)
    {
        _app = app;
        _dispatcher = dispatcher;
        _isEnabled = isEnabled;
    }

    public string AppVersion => AppVersionInfo.GetCurrentVersionText();

    public string BuildFlavor => TinyClips.App.BuildFlavor.IsStoreBuild ? "store" : "direct";

    public IReadOnlyCollection<string> SupportedCommands => Supported;

    public async Task<JsonNode?> ExecuteAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return context.Command.Name switch
        {
            "app.status" => await OnUiAsync(AppStatus),
            "app.open" => await OnUiAsync(() => OpenWindow(context)),
            "recording.status" => await OnUiAsync(RecordingStatus),
            "library.list" => await ListLibraryAsync(context),
            "settings.get" => await OnUiAsync(() => GetSettings(context)),
            "settings.set" => await OnUiAsync(() => SetSetting(context)),
            "capture.targets" => await OnUiAsync(() => ListCaptureTargets(context)),
            "capture.screenshot" => await CaptureScreenshotAsync(context, cancellationToken),
            "capture.ocr" => await RecognizeTextAsync(context, cancellationToken),
            "recording.startVideo" => await OnUiAsync(() => StartVideoAsync(context)),
            "recording.startGif" => await OnUiAsync(() => StartGifAsync(context)),
            "capture.scrolling.start" => await OnUiAsync(async () => { await StartScrollingCaptureAsync(context, cancellationToken); return true; }),
            "capture.scrolling.stop" => await OnUiAsync(StopScrollingCaptureAsync),
            "capture.scrolling.status" => await OnUiAsync(ScrollingStatus),
            "capture.scrolling.cancel" => await OnUiAsync(CancelScrollingCapture, cancellationToken),
            "recording.stop" => await OnUiAsync(StopRecordingAsync),
            "recording.togglePause" => await OnUiAsync(TogglePauseAsync),
            "recording.restart" => await OnUiAsync(RestartRecordingAsync, cancellationToken),
            "recording.discard" => await OnUiAsync(DiscardRecordingAsync, cancellationToken),
            "editor.open" => await OpenEditorAsync(context, cancellationToken),
            "studio.projects.list" or "studio.project.open" or "studio.project.get" or "studio.project.saveCopy" or "studio.project.edit"
                or "studio.project.openFolder" or "studio.project.delete"
                or "studio.zoom.suggest" or "studio.export.options" or "studio.export"
                or "studio.captions.check" or "studio.captions.import" or "studio.captions.fix" or "studio.captions.export"
                or "studio.captions.models" or "studio.captions.generate"
                => await OnUiAsync(() => _app.ExecuteStudioAutomationCommandAsync(context.Command.Name, context.Params, cancellationToken)),
            "editor.annotate" => await AnnotateAsync(context, cancellationToken),
            "editor.trim" => await TrimAsync(context, cancellationToken),
            "editor.saveFrame" => await SaveFrameAsync(context, cancellationToken),
            "library.reveal" => await OnUiAsync(() => Reveal(context)),
            "library.copy" => await CopyAsync(context),
            "library.delete" => await DeleteAsync(context, cancellationToken),
            "library.upload" => await UploadAsync(context, cancellationToken),
            _ => throw new AutomationException(AutomationErrorCodes.NotSupported, $"{context.Command.Name} is not supported on Windows."),
        };
    }

    // -- App ---------------------------------------------------------------------------------

    private JsonNode AppStatus()
    {
        var snapshot = _app.GetRecordingSnapshot();
        return new JsonObject
        {
            ["app"] = AutomationHost.AppName,
            ["version"] = AppVersion,
            ["platform"] = AutomationHost.Platform,
            ["build"] = BuildFlavor,
            ["recording"] = snapshot.State,
            ["busy"] = snapshot.Busy,
            ["automationEnabled"] = _isEnabled(),
        };
    }

    private JsonNode OpenWindow(AutomationCommandContext context)
    {
        var window = context.GetString("window") ?? string.Empty;
        var topic = context.GetString("guideTopic");
        var selectedTopic = _app.OpenAutomationWindow(window, topic);
        var result = new JsonObject { ["opened"] = window };
        if (selectedTopic is not null)
        {
            result["guideTopic"] = selectedTopic;
        }
        return result;
    }

    private JsonNode RecordingStatus()
    {
        var snapshot = _app.GetRecordingSnapshot();
        var result = new JsonObject
        {
            ["state"] = snapshot.State,
            ["busy"] = snapshot.Busy,
            ["microphone"] = snapshot.Microphone,
            ["camera"] = snapshot.Camera,
        };
        if (snapshot.Kind is { } kind)
        {
            result["kind"] = KindName(kind);
        }

        return result;
    }

    // -- Settings ----------------------------------------------------------------------------

    private static AutomationSettingsCatalog SettingsCatalog()
    {
        var storage = App.Services.GetRequiredService<IClipStorageService>();
        return new AutomationSettingsCatalog(App.Services.GetRequiredService<ICaptureSettings>(), storage.OutputDirectory);
    }

    private static JsonNode GetSettings(AutomationCommandContext context)
    {
        var key = context.GetString("key");
        return string.IsNullOrEmpty(key) ? SettingsCatalog().GetAll() : SettingsCatalog().Get(key);
    }

    private JsonNode SetSetting(AutomationCommandContext context)
    {
        var key = context.GetString("key") ?? string.Empty;
        var value = context.Params["value"];
        return SettingsCatalog().Set(key, value?.DeepClone());
    }

    // -- Capture and recording ---------------------------------------------------------------

    private async Task<JsonNode> StartVideoAsync(AutomationCommandContext context)
    {
        var studio = context.GetBoolean("studio");
        var settings = App.Services.GetRequiredService<ICaptureSettings>();
        var (target, region, windowArea, monitor) = ResolveCaptureTarget(
            AutomationCaptureRequest.Parse(context.Params),
            allowWindowArea: true);
        var mediaPermissions = App.Services.GetRequiredService<IMediaDevicePermissionService>();
        var options = AutomationRecordingOptions.Resolve(context.Params, settings,
            mediaPermissions.HasMicrophoneAccess, mediaPermissions.HasCameraAccess);

        EnsureIdle();
        await _app.BeginTargetedAutomationRecordingAsync(CaptureType.Video, studio, target, region, options, windowArea, monitor);
        var snapshot = _app.GetRecordingSnapshot();
        var result = new JsonObject
        {
            ["started"] = true,
            ["studio"] = studio,
            ["microphone"] = snapshot.Microphone,
            ["camera"] = snapshot.Camera,
        };
        if (windowArea is { } area && monitor is { } display)
        {
            result["windowArea"] = CreateWindowAreaResult(area, display);
        }

        return result;
    }

    private async Task<JsonNode> StartGifAsync(AutomationCommandContext context)
    {
        var (target, region, windowArea, monitor) = ResolveCaptureTarget(
            AutomationCaptureRequest.Parse(context.Params),
            allowWindowArea: true);
        EnsureIdle();
        await _app.BeginTargetedAutomationRecordingAsync(CaptureType.Gif, false, target, region, null, windowArea, monitor);
        var result = new JsonObject { ["started"] = true };
        if (windowArea is { } area && monitor is { } display)
        {
            result["windowArea"] = CreateWindowAreaResult(area, display);
        }

        return result;
    }

    private async Task StartScrollingCaptureAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIdle();
        var (target, region, _, _) = ResolveCaptureTarget(AutomationCaptureRequest.Parse(context.Params));
        await _app.StartAutomationScrollingCaptureAsync(target, region);
    }

    private JsonNode ScrollingStatus()
    {
        var snapshot = _app.GetScrollingSnapshot();
        return new JsonObject
        {
            ["active"] = snapshot.Active,
            ["stopping"] = snapshot.Stopping,
            ["frameCount"] = snapshot.FrameCount,
        };
    }

    private async Task<JsonNode> StopScrollingCaptureAsync()
    {
        var result = await _app.StopAutomationScrollingCaptureAsync();
        return new JsonObject
        {
            ["path"] = result,
        };
    }

    private JsonNode CancelScrollingCapture()
    {
        _app.CancelAutomationScrollingCapture();
        return new JsonObject { ["cancelled"] = true };
    }

    private void EnsureIdle()
    {
        if (_app.GetRecordingSnapshot().Busy)
        {
            throw new AutomationException(AutomationErrorCodes.Busy, "Tiny Clips is already capturing or recording.");
        }
    }

    private AutomationRecordingSnapshot RequireRecording()
    {
        var snapshot = _app.GetRecordingSnapshot();
        if (snapshot.State is not ("recording" or "paused"))
        {
            throw new AutomationException(AutomationErrorCodes.Busy, "No recording is in progress.");
        }

        return snapshot;
    }

    private async Task<JsonNode> StopRecordingAsync()
    {
        RequireRecording();
        await _app.StopRecordingForAutomationAsync();
        return new JsonObject { ["stopped"] = true };
    }

    private async Task<JsonNode> TogglePauseAsync()
    {
        var snapshot = RequireRecording();
        if (snapshot.State == "paused")
        {
            await _app.ResumeRecordingForAutomationAsync();
        }
        else
        {
            await _app.PauseRecordingForAutomationAsync();
        }

        return new JsonObject { ["paused"] = _app.GetRecordingSnapshot().State == "paused" };
    }

    private async Task<JsonNode> RestartRecordingAsync()
    {
        RequireRecording();
        if (!await _app.RestartRecordingForAutomationAsync())
        {
            throw new AutomationException(AutomationErrorCodes.Busy, "This recording cannot be restarted.");
        }

        return new JsonObject { ["restarted"] = true };
    }

    private async Task<JsonNode> DiscardRecordingAsync()
    {
        RequireRecording();
        await _app.DiscardRecordingForAutomationAsync();
        return new JsonObject { ["discarded"] = true };
    }

    // -- Library -----------------------------------------------------------------------------

    private static async Task<JsonNode> ListLibraryAsync(AutomationCommandContext context)
    {
        var limit = context.GetInt32("limit", 20);
        if (limit is < 1 or > 500)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "limit must be between 1 and 500.");
        }

        CaptureType? kind = context.GetString("kind") switch
        {
            "screenshot" => CaptureType.Screenshot,
            "video" => CaptureType.Video,
            "gif" => CaptureType.Gif,
            _ => null,
        };

        var clips = await App.Services.GetRequiredService<IClipLibraryService>().GetClipsAsync().ConfigureAwait(false);
        var items = new JsonArray();
        foreach (var clip in clips.Where(c => kind is null || c.Type == kind).Take(limit))
        {
            items.Add(new JsonObject
            {
                ["path"] = clip.Path,
                ["name"] = clip.FileName,
                ["kind"] = KindName(clip.Type),
                ["createdAt"] = clip.CapturedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["sizeBytes"] = clip.FileSizeBytes,
            });
        }

        return new JsonObject { ["items"] = items };
    }

    private async Task<JsonNode> OpenEditorAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var raw = context.GetString("path") ?? string.Empty;
        JsonObject Result(string path, string editor)
        {
            var result = new JsonObject { ["opened"] = true, ["editor"] = editor };
            if (context.GetString("resultFormat") == "standard") result["path"] = path;
            return result;
        }
        var studio = context.GetBoolean("studio");
        if (studio && ActivatedFile.KindOf(raw) != ActivatedFileKind.StudioVideo)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "studio=true requires an MP4 or MOV video.");
        }

        if (ActivatedFile.KindOf(raw) == ActivatedFileKind.StudioProject)
        {
            var project = FullPathOrNotFound(raw);
            if (!File.Exists(project))
            {
                throw NotFound();
            }

            var editor = await OnUiAsync(() => _app.OpenEditorForAutomationAsync(project, null));
            return Result(project, editor);
        }

        var (path, type) = ResolveLibraryFile(raw);
        var opened = await OnUiAsync(() => _app.OpenEditorForAutomationAsync(path, type, studio, cancellationToken), cancellationToken);
        return Result(path, opened);
    }

    private JsonNode Reveal(AutomationCommandContext context)
    {
        var (path, _) = ResolveLibraryFile(context.GetString("path"));
        _app.RevealForAutomation(path);
        return new JsonObject { ["revealed"] = true };
    }

    private async Task<JsonNode> CopyAsync(AutomationCommandContext context)
    {
        var (path, type) = ResolveLibraryFile(context.GetString("path"));
        await OnUiAsync(async () =>
        {
            await ClipboardService.CopySavedClipAsync(path, type);
            return true;
        });
        return new JsonObject { ["copied"] = true };
    }

    private async Task<JsonNode> DeleteAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var (path, _) = ResolveLibraryFile(context.GetString("path"));
        cancellationToken.ThrowIfCancellationRequested();
        if (!ShellRecycleBin.MoveFile(path))
        {
            throw new AutomationException(AutomationErrorCodes.Internal, "The capture could not be moved to the Recycle Bin. It may be open in another app.");
        }

        await OnUiAsync(() =>
        {
            App.Services.GetRequiredService<IClipMetadataStore>().Remove(path);
            App.Services.GetRequiredService<IRecentCaptureService>().Remove(path);
            return true;
        });
        return new JsonObject { ["deleted"] = true, ["trashPath"] = null };
    }

    private async Task<JsonNode> UploadAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var (path, _) = ResolveLibraryFile(context.GetString("path"));
        var settings = App.Services.GetRequiredService<ICaptureSettings>();
        if (!settings.UploadcareEnabled || string.IsNullOrWhiteSpace(settings.UploadcarePublicKey))
        {
            throw new AutomationException(AutomationErrorCodes.NotSupported, "Uploads are not set up. Turn on Uploadcare in Settings → Uploadcare and enter a public key.");
        }

        string url;
        try
        {
            var result = await App.Services.GetRequiredService<IUploadcareUploadService>().UploadAsync(path, cancellationToken);
            url = result.DeliveryUri.AbsoluteUri;
        }
        catch (UploadcareUploadException ex)
        {
            throw new AutomationException(AutomationErrorCodes.Internal, ex.Message);
        }

        await OnUiAsync(async () =>
        {
            _app.RecordUploadLinkForAutomation(path, url);
            if (settings.UploadcareCopyUrl)
            {
                try
                {
                    await ClipboardService.CopyTextAsync(url);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Upload URL clipboard copy failed: {ex}");
                }
            }

            return true;
        });
        return new JsonObject { ["url"] = url, ["path"] = path };
    }

    /// <summary>
    /// Agents may only touch captures Tiny Clips knows about: files directly in a library folder
    /// or in the recent captures list, with a capture file type. Anything else is <c>not_found</c>.
    /// </summary>
    private static (string Path, CaptureType Type) ResolveLibraryFile(string? raw)
    {
        var path = FullPathOrNotFound(raw);
        var type = ClipLibraryService.CaptureTypeFor(path);
        if (!File.Exists(path) || type is null)
        {
            throw NotFound();
        }

        var directory = Path.GetDirectoryName(path);
        var library = App.Services.GetRequiredService<IClipLibraryService>();
        var inLibrary = directory is not null && library.GetLibraryDirectories()
            .Any(d => PathsEqual(d, directory));
        var inRecents = App.Services.GetRequiredService<IRecentCaptureService>().GetRecentCaptures()
            .Any(c => PathsEqual(c.Path, path));
        if (!inLibrary && !inRecents)
        {
            throw NotFound();
        }

        return (path, type.Value);
    }

    private static string FullPathOrNotFound(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "path is required.");
        }

        try
        {
            return Path.GetFullPath(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw NotFound();
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static AutomationException NotFound() =>
        new(AutomationErrorCodes.NotFound, "That file is not a capture in the Tiny Clips library.");

    internal static string KindName(CaptureType type) => type switch
    {
        CaptureType.Screenshot => "screenshot",
        CaptureType.Video => "video",
        CaptureType.Gif => "gif",
        _ => type.ToString().ToLowerInvariant(),
    };

    // -- Threading ---------------------------------------------------------------------------

    private Task<T> OnUiAsync<T>(Func<T> action, CancellationToken cancellationToken = default) =>
        OnUiAsync(() => Task.FromResult(action()), cancellationToken);

    private Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher() is not { } dispatcher)
        {
            throw new AutomationException(AutomationErrorCodes.Internal, "Tiny Clips is still starting.");
        }

        if (dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        if (!queued)
        {
            completion.TrySetException(new AutomationException(AutomationErrorCodes.Internal, "Tiny Clips is shutting down."));
        }

        return completion.Task;
    }
}
