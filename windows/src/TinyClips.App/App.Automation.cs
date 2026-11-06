using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyClips.App.Settings;
using TinyClips.Core.Automation;
using TinyClips.Core.Capture;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using TinyClips.Core.Studio;
using TinyClips.Core.Studio.Transcription;

namespace TinyClips.App;

/// <summary>Agent control: the pipe server's lifetime, its tray indicator, and the commands it runs.</summary>
public partial class App : IAutomationAppActions
{
    private const string GlyphAgent = "\uE99A";
    private Button? _trayAgentIndicator;
    private bool _agentControlWired;

    internal AgentControlService AgentControl => Services.GetRequiredService<AgentControlService>();

    private AgentControlService CreateAgentControlService(IServiceProvider services)
    {
        AgentControlService? created = null;
        var handler = new AppAutomationCommandHandler(this, () => _dispatcher, () => created?.IsEnabled == true);
        created = new AgentControlService(
            services.GetRequiredService<ISettingsService>(),
            handler,
            new AutomationApprovalPrompt(() => _isExiting ? null : _dispatcher));
        return created;
    }

    /// <summary>Startup step: listens for agents when agent control was left on.</summary>
    private void StartAgentControl()
    {
        var service = AgentControl;
        if (!_agentControlWired)
        {
            _agentControlWired = true;
            service.StateChanged += OnAgentControlStateChanged;
        }

        _ = StartAgentControlAsync(service);
    }

    private static async Task StartAgentControlAsync(AgentControlService service)
    {
        try
        {
            await service.StartIfEnabledAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Agent control could not start: {ex}");
        }
    }

    private async Task StopAgentControlForExitAsync()
    {
        if (!_agentControlWired)
        {
            return;
        }

        try
        {
            AgentControl.StateChanged -= OnAgentControlStateChanged;
            await AgentControl.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Agent control did not stop cleanly: {ex}");
        }
    }

    private void OnAgentControlStateChanged(object? sender, EventArgs e) =>
        _dispatcher?.TryEnqueue(() =>
        {
            if (_isExiting)
            {
                return;
            }

            UpdateRecordingState();
            RefreshTrayAgentIndicator();
        });

    private static string TrayToolTipWithAgentState(string toolTip) =>
        Services.GetRequiredService<AgentControlService>().IsEnabled ? toolTip + " — Agent control on" : toolTip;

    /// <summary>The tray popup's title row, with a button that shows while agents may control the app.</summary>
    private Grid BuildTrayHeader(TextBlock title)
    {
        var header = new Grid { Margin = title.Margin, ColumnSpacing = 8 };
        title.Margin = new Thickness(0);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);

        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new FontIcon { Glyph = GlyphAgent, FontFamily = FluentIconFont, FontSize = 14 });
        label.Children.Add(new TextBlock { Text = "Agent control", Style = ResourceStyle("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center });

        var turnOff = new MenuFlyoutItem
        {
            Text = "Turn off agent control",
            Icon = new FontIcon { Glyph = "\uE711", FontFamily = FluentIconFont },
        };
        turnOff.Click += async (_, _) =>
        {
            _trayPopup?.Hide();
            await AgentControl.SetEnabledAsync(false);
        };

        var openSettings = new MenuFlyoutItem
        {
            Text = "Agent control settings",
            Icon = new FontIcon { Glyph = GlyphSettings, FontFamily = FluentIconFont },
        };
        openSettings.Click += (_, _) =>
        {
            _trayPopup?.Hide();
            OpenSettingsWindow(SettingsSectionKind.AgentControl);
        };

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuFlyoutItem { Text = "Agents can control Tiny Clips", IsEnabled = false });
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(turnOff);
        flyout.Items.Add(openSettings);

        var indicator = new Button
        {
            Content = label,
            Flyout = flyout,
            Style = ResourceStyle("SubtleButtonStyle"),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(indicator, "Agent control is on");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(indicator, "TrayAgentControlIndicator");
        ToolTipService.SetToolTip(indicator, "Agents can control Tiny Clips. Select to turn it off.");
        Grid.SetColumn(indicator, 1);
        header.Children.Add(indicator);

        _trayAgentIndicator = indicator;
        RefreshTrayAgentIndicator();
        return header;
    }

    private void RefreshTrayAgentIndicator()
    {
        if (_trayAgentIndicator is { } indicator)
        {
            indicator.Visibility = AgentControl.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // -- IAutomationAppActions (UI thread) ----------------------------------------------------

    AutomationRecordingSnapshot IAutomationAppActions.GetRecordingSnapshot()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();
        if (video.IsRecording)
        {
            return new(video.IsPaused ? "paused" : "recording", true, CaptureType.Video);
        }

        if (gif.IsRecording)
        {
            return new(gif.IsPaused ? "paused" : "recording", true, CaptureType.Gif);
        }

        if (_processingIndicator is not null)
        {
            return new("processing", true, _activeRecordingType);
        }

        var busy = _captureFlowCts is not null || _scrollingPanel is not null || _isExiting;
        return new("idle", busy, null);
    }

    void IAutomationAppActions.OpenAutomationWindow(string window)
    {
        switch (window)
        {
            case "settings":
                OpenSettingsWindow(section: null);
                break;
            case "library":
                OpenClipsManagerWindow();
                break;
            case "guide":
                OpenGuideWindow();
                break;
            default:
                throw new AutomationException(AutomationErrorCodes.InvalidParams, $"Unknown window {window}.");
        }
    }

    MonitorInfo? IAutomationAppActions.GetPreferredAutomationMonitor()
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        var monitors = Services.GetRequiredService<IMonitorService>();
        return ResolvePreferredMonitor(monitors, settings.MultiMonitorCaptureMode)
            ?? monitors.GetMonitorUnderCursor()
            ?? monitors.GetPrimaryMonitor();
    }

    async Task<(string Path, int Width, int Height)> IAutomationAppActions.CaptureForAutomationAsync(
        CaptureTarget target, PixelRect? region, CancellationToken cancellationToken)
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        WindowCaptureVisibility.Policy.BeginCapture(settings.IncludeTinyClipsInCapture, IsAnyRecordingActive());
        var frame = await Services.GetRequiredService<IScreenCaptureService>()
            .CaptureAsync(target, region, includeCursor: false, cancellationToken);
        if (settings.ShowBrandingOverlay)
        {
            new BrandingOverlayCompositor().Draw(frame.BgraPixels, frame.Width, frame.Height);
        }

        var path = await SaveScreenshotFrameAsync(
            Services.GetRequiredService<IScreenshotService>(), frame, alreadyBranded: true, cancellationToken);
        var (width, height) = await Task.Run(
            () => ScreenshotBackgroundDecoder.ReadDimensions(path, cancellationToken), cancellationToken);
        ShowSaveToast(path);
        return (path, width, height);
    }

    Task<string> IAutomationAppActions.RecognizeTextForAutomationAsync(
        CaptureTarget target, PixelRect? region, CancellationToken cancellationToken)
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        WindowCaptureVisibility.Policy.BeginCapture(settings.IncludeTinyClipsInCapture, IsAnyRecordingActive());
        return Services.GetRequiredService<IOcrService>().RecognizeAsync(target, region, cancellationToken);
    }

    void IAutomationAppActions.RecordAutomationScreenshot(string path)
    {
        Services.GetRequiredService<IRecentCaptureService>().Record(path, CaptureType.Screenshot);
        ShowSaveToast(path);
    }

    void IAutomationAppActions.RecordAutomationCapture(string path, CaptureType type)
    {
        Services.GetRequiredService<IRecentCaptureService>().Record(path, type);
        ShowSaveToast(path);
    }

    async Task IAutomationAppActions.BeginTargetedAutomationRecordingAsync(
        CaptureType type,
        bool studio,
        CaptureTarget target,
        PixelRect? region)
    {
        VideoRecordingCommand? videoCommand = type == CaptureType.Video
            ? studio ? VideoRecordingCommand.StudioRecording : VideoRecordingCommand.RecordVideo
            : null;
        var selection = new TargetSelection(target, region, ResolveMonitorForTarget(target));
        await BeginCaptureAsync(
            type,
            abortIfRecording: true,
            videoCommand: videoCommand,
            automationSelection: selection);
    }

    Task IAutomationAppActions.StartAutomationScrollingCaptureAsync(CaptureTarget target, PixelRect? region)
    {
        var settings = Services.GetRequiredService<ICaptureSettings>();
        WindowCaptureVisibility.Policy.BeginCapture(settings.IncludeTinyClipsInCapture, IsAnyRecordingActive());
        var selection = new TargetSelection(target, region, ResolveMonitorForTarget(target));
        return StartScrollingCaptureAsync(
            selection,
            settings,
            wasPickerInitiated: false,
            cancellationToken: CancellationToken.None,
            isAutomation: true);
    }

    AutomationScrollingSnapshot IAutomationAppActions.GetScrollingSnapshot() =>
        new(_scrollingPanel is not null, _scrollingStopping, _scrollingFrameCount);

    Task<string> IAutomationAppActions.StopAutomationScrollingCaptureAsync() =>
        StopAutomationScrollingCaptureAsync();

    void IAutomationAppActions.CancelAutomationScrollingCapture() => CancelAutomationScrollingCapture();

    private async Task<string> StopAutomationScrollingCaptureAsync()
    {
        if (!_scrollingWasAutomation || _scrollingPanel is not { } panel || _scrollingStopping)
        {
            throw new AutomationException(AutomationErrorCodes.Busy, "No automated scrolling capture is active.");
        }

        _scrollingStopping = true;
        panel.MarkFinishing();
        CaptureFlowTrace.Mark("scrolling: automation stop requested");
        var service = Services.GetRequiredService<IScrollingCaptureService>();
        try
        {
            var frame = await service.StopAsync();
            FinishScrollingCapture(null);
            var settings = Services.GetRequiredService<ICaptureSettings>();
            if (settings.ShowBrandingOverlay)
            {
                new BrandingOverlayCompositor().Draw(frame.BgraPixels, frame.Width, frame.Height);
            }

            var path = await SaveScreenshotFrameAsync(
                Services.GetRequiredService<IScreenshotService>(),
                frame,
                alreadyBranded: true);
            ShowSaveToast(path);
            return path;
        }
        catch (Exception ex)
        {
            if (_scrollingPanel is not null)
            {
                FinishScrollingCapture(ex);
            }
            else
            {
                ShowSaveFailureNotification("the scrolling capture");
            }

            throw new AutomationException(AutomationErrorCodes.Internal,
                $"Scrolling capture failed: {ex.Message}");
        }
    }

    private async Task StopAutomationScrollingCaptureFromUiAsync()
    {
        try
        {
            await StopAutomationScrollingCaptureAsync();
        }
        catch (AutomationException ex)
        {
            Debug.WriteLine($"Agent scrolling capture could not be saved: {ex.Message}");
        }
    }

    private void CancelAutomationScrollingCapture()
    {
        if (!_scrollingWasAutomation || _scrollingPanel is null)
        {
            throw new AutomationException(AutomationErrorCodes.Busy, "No automated scrolling capture is active.");
        }

        Services.GetRequiredService<IScrollingCaptureService>().Cancel();
        FinishScrollingCapture(new PanoramaCaptureException(PanoramaCaptureError.Cancelled));
    }

    Task IAutomationAppActions.StopRecordingForAutomationAsync() => StopActiveRecordingAsync(throwOnFailure: true);

    Task IAutomationAppActions.PauseRecordingForAutomationAsync() => PauseActiveRecordingAsync();

    Task IAutomationAppActions.ResumeRecordingForAutomationAsync() => ResumeActiveRecordingAsync();

    async Task<bool> IAutomationAppActions.RestartRecordingForAutomationAsync()
    {
        if (_activeRecordingSelection is null || _activeRecordingType is null)
        {
            return false;
        }

        await RestartActiveRecordingAsync();
        return true;
    }

    Task IAutomationAppActions.DiscardRecordingForAutomationAsync() => DiscardActiveRecordingAsync();

    async Task<string> IAutomationAppActions.OpenEditorForAutomationAsync(string path, CaptureType? type)
    {
        switch (type)
        {
            case null:
                if (await OpenStudioProjectFileAsync(path) is { } failure)
                {
                    var code = Services.GetRequiredService<ICaptureSettings>().StudioPreviewEnabled
                        ? AutomationErrorCodes.Internal
                        : AutomationErrorCodes.NotSupported;
                    throw new AutomationException(code, failure);
                }

                return "studio";
            case CaptureType.Screenshot:
                OpenScreenshotEditor(path, reopenPickerAfterClose: false);
                return "screenshot";
            case CaptureType.Video when TryOpenStudioProjectForVideo(path):
                return "studio";
            case CaptureType.Video:
                OpenTrimmer(path, CaptureType.Video, isRecentCapture: true);
                return "videoTrimmer";
            default:
                OpenTrimmer(path, CaptureType.Gif, isRecentCapture: true);
                return "gifTrimmer";
        }
    }

    Task<JsonNode> IAutomationAppActions.ExecuteStudioAutomationCommandAsync(
        string commandName,
        JsonObject parameters,
        CancellationToken cancellationToken) =>
        ExecuteStudioAutomationCommandAsync(commandName, parameters, cancellationToken);

    private async Task<JsonNode> ExecuteStudioAutomationCommandAsync(
        string commandName,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = Services.GetRequiredService<ICaptureSettings>();
        if (!settings.StudioPreviewEnabled)
        {
            throw new AutomationException(AutomationErrorCodes.NotSupported,
                "Studio is turned off. Turn it on in Settings → Studio.");
        }

        var store = Services.GetRequiredService<IStudioProjectStore>();
        switch (commandName)
        {
            case "studio.projects.list":
            {
                var limit = parameters["limit"]?.GetValue<int>() ?? 20;
                if (limit is < 1 or > 100)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams, "limit must be from 1 to 100.");
                }

                var projects = new JsonArray();
                foreach (var summary in store.ListSummaries().OrderByDescending(static item => item.LastUsedAt).Take(limit))
                {
                    projects.Add(new JsonObject
                    {
                        ["projectId"] = summary.Id,
                        ["name"] = summary.Name,
                        ["createdAt"] = FormatStudioTimestamp(summary.CreatedAt),
                        ["lastOpenedAt"] = FormatStudioTimestamp(summary.LastOpenedAt),
                        ["isDraft"] = summary.IsDraft,
                        ["isFlat"] = summary.IsFlat,
                        ["keepSources"] = summary.KeepSources,
                        ["sourceExists"] = summary.SourceExists,
                    });
                }

                return new JsonObject { ["projects"] = projects };
            }
            case "studio.project.get":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var project = ReadAutomationProject(projectId, store);
                return new JsonObject
                {
                    ["project"] = ProjectNode(project),
                    ["revision"] = ProjectRevision(project),
                };
            }
            case "studio.project.edit":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var expectedModifiedAt = RequiredStudioString(parameters, "expectedModifiedAt");
                var expectedRevision = RequiredStudioString(parameters, "expectedRevision");
                if (parameters["patch"] is not JsonObject patch || patch.Count == 0)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams, "patch must be a non-empty object.");
                }

                var allowed = new HashSet<string>(StringComparer.Ordinal)
                {
                    "canvas", "screen", "camera", "scenes", "zooms", "edits", "audio", "overlays", "captions",
                };
                if (patch.Any(item => !allowed.Contains(item.Key)))
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "patch may contain editable Studio properties only.");
                }

                var current = ReadAutomationProject(projectId, store);
                var currentNode = ProjectNode(current).AsObject();
                if (!string.Equals(currentNode["modifiedAt"]?.GetValue<string>(), expectedModifiedAt, StringComparison.Ordinal)
                    || !string.Equals(ProjectRevision(current), expectedRevision, StringComparison.Ordinal))
                {
                    throw new AutomationException(AutomationErrorCodes.Conflict,
                        "The Studio project changed since it was read. Get it again before editing.");
                }

                MergeStudioJson(patch, currentNode);
                StudioProject updated;
                try
                {
                    updated = StudioProjectJson.ReadProject(currentNode.ToJsonString());
                }
                catch (StudioProjectInvalidException ex)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        $"The Studio patch is invalid: {ex.Message}");
                }
                StudioProject saved = ApplyAutomationEdit(projectId, current, updated, store);

                return new JsonObject
                {
                    ["project"] = ProjectNode(saved),
                    ["revision"] = ProjectRevision(saved),
                };
            }
            case "studio.captions.check":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var project = ReadAutomationProject(projectId, store);
                var findings = StudioCaptionQuality.Check(project.Captions.Cues, project.Sources.Screen.Duration);
                var result = CaptionFindingsNode(findings);
                result["projectId"] = projectId;
                result["cueCount"] = project.Captions.Cues.Length;
                return result;
            }
            case "studio.captions.import":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var timing = AutomationCaptionTiming.Parse(parameters["timing"]);
                var current = ReadCheckedProject(parameters, projectId, store);
                var contents = StudioCaptionFiles.Read(
                    RequiredStudioString(parameters, "text"), timing, StudioTimeMap.FromProject(current));
                if (contents.Cues.Length == 0)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "No captions could be read from that text.");
                }

                var saved = ApplyAutomationEdit(projectId, current, current with
                {
                    Captions = current.Captions with { Language = null, Cues = StudioCaptionQuality.InOrder(contents.Cues) },
                }, store);
                return new JsonObject
                {
                    ["project"] = ProjectNode(saved),
                    ["revision"] = ProjectRevision(saved),
                    ["cueCount"] = contents.Cues.Length,
                    ["skipped"] = contents.SkippedBlocks,
                };
            }
            case "studio.captions.fix":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var current = ReadCheckedProject(parameters, projectId, store);
                var duration = current.Sources.Screen.Duration;
                var fixes = StudioCaptionQuality.Fix(current.Captions.Cues, duration);
                var saved = fixes.Applied == 0
                    ? current
                    : ApplyAutomationEdit(projectId, current, current with
                    {
                        Captions = current.Captions with { Cues = fixes.Cues },
                    }, store);
                var result = CaptionFindingsNode(StudioCaptionQuality.Check(saved.Captions.Cues, duration));
                result["project"] = ProjectNode(saved);
                result["revision"] = ProjectRevision(saved);
                result["applied"] = fixes.Applied;
                return result;
            }
            case "studio.captions.models":
            {
                var models = Services.GetRequiredService<StudioCaptionModelStore>();
                var items = new JsonArray();
                foreach (var model in StudioCaptionModels.All)
                {
                    items.Add(new JsonObject
                    {
                        ["id"] = model.Id,
                        ["name"] = model.Name,
                        ["size"] = model.SizeText,
                        ["guidance"] = model.Guidance,
                        ["installed"] = models.IsInstalled(model),
                    });
                }

                return new JsonObject { ["models"] = items, ["selected"] = settings.StudioCaptionModelId };
            }
            case "studio.captions.generate":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var current = ReadCheckedProject(parameters, projectId, store);
                var model = parameters["model"]?.GetValue<string>() is { } modelId
                    ? StudioCaptionModels.Find(modelId)
                        ?? throw new AutomationException(AutomationErrorCodes.InvalidParams, $"There is no speech model called {modelId}.")
                    : StudioCaptionModels.Find(settings.StudioCaptionModelId) ?? StudioCaptionModels.Default;
                var generator = Services.GetRequiredService<IStudioCaptionGenerator>();
                if (!generator.IsModelInstalled(model) && parameters["allowModelDownload"]?.GetValue<bool>() != true)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        $"The {model.Name} model ({model.SizeText}) is not on this computer. Set allowModelDownload to true to download it.");
                }

                StudioGeneratedCaptions heard;
                try
                {
                    heard = await generator.GenerateAsync(current, store.GetPaths(current), model, progress: null, cancellationToken);
                }
                catch (StudioCaptionException ex)
                {
                    throw new AutomationException(AutomationErrorCodes.Internal, ex.Message);
                }

                // Nothing heard leaves the captions as they were, as in the editor.
                if (heard.Cues.Length == 0)
                {
                    throw new AutomationException(AutomationErrorCodes.Internal, StudioGeneratedCaptions.NoSpeechMessage);
                }

                // It took a while. The cues are in source time, so an edit made meanwhile is kept: the
                // open editor's, with what is being typed in it, or else the saved project.
                StudioWindows().FindOpenViewModel(projectId)?.CommitCaptionText();
                var latest = ReadAutomationProject(projectId, store);
                var saved = ApplyAutomationEdit(projectId, latest, latest with
                {
                    Captions = latest.Captions with { Language = heard.Language, Cues = heard.Cues },
                }, store);
                return new JsonObject
                {
                    ["project"] = ProjectNode(saved),
                    ["revision"] = ProjectRevision(saved),
                    ["cueCount"] = heard.Cues.Length,
                    ["language"] = heard.Language,
                    ["model"] = model.Id,
                };
            }
            case "studio.captions.export":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var outputPath = Path.GetFullPath(RequiredStudioString(parameters, "outputPath"));
                var outputDirectory = Path.GetFullPath(
                    Services.GetRequiredService<IClipStorageService>().OutputDirectory(CaptureType.Video));
                if (StudioCaptionFiles.GetFormat(outputPath) is not { } format
                    || !string.Equals(Path.GetDirectoryName(outputPath), outputDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "outputPath must be an .srt or .vtt file directly inside the configured video folder.");
                }

                if (!Directory.Exists(outputDirectory))
                {
                    throw new AutomationException(AutomationErrorCodes.Internal,
                        "The configured video-save folder does not exist.");
                }

                StudioWindows().FindOpenViewModel(projectId)?.CommitCaptionText();
                var captions = StudioCaptionResolver.Resolve(ReadAutomationProject(projectId, store));
                if (captions.Length == 0)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "This video has no captions to export.");
                }

                try
                {
                    StudioCaptionFiles.WriteNewFile(outputPath, StudioCaptionFiles.Write(format, captions));
                }
                catch (IOException) when (File.Exists(outputPath))
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "The export destination already exists; choose a new filename.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new AutomationException(AutomationErrorCodes.Internal, $"Captions could not be written: {ex.Message}");
                }

                return new JsonObject
                {
                    ["projectId"] = projectId,
                    ["path"] = outputPath,
                    ["captionCount"] = captions.Length,
                };
            }
            case "studio.zoom.suggest":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var project = ReadAutomationProject(projectId, store);
                var suggestions = StudioZoomSuggestions.Suggest(project, store.LoadEvents(projectId));
                var result = ProjectNode(project with { Zooms = suggestions }).AsObject();
                return new JsonObject
                {
                    ["projectId"] = projectId,
                    ["suggestions"] = result["zooms"]?.DeepClone(),
                    ["count"] = suggestions.Length,
                };
            }
            case "studio.export":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var outputPath = Path.GetFullPath(RequiredStudioString(parameters, "outputPath"));
                var outputDirectory = Path.GetFullPath(
                    Services.GetRequiredService<IClipStorageService>().OutputDirectory(CaptureType.Video));
                if (!string.Equals(Path.GetExtension(outputPath), ".mp4", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetDirectoryName(outputPath), outputDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "outputPath must be an MP4 directly inside the configured video folder.");
                }

                if (File.Exists(outputPath))
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "The export destination already exists; choose a new filename.");
                }
                if (!Directory.Exists(outputDirectory))
                {
                    throw new AutomationException(AutomationErrorCodes.Internal,
                        "The configured video-save folder does not exist.");
                }

                var project = ReadAutomationProject(projectId, store);
                var openEditor = StudioWindows().FindOpenViewModel(projectId);
                openEditor?.CommitCaptionText();
                if (openEditor is not null)
                {
                    if (!openEditor.SaveForAutomationExport() || openEditor.Project is not { } current)
                    {
                        throw new AutomationException(AutomationErrorCodes.Busy,
                            "The Studio project is not ready to export.");
                    }

                    project = current;
                }

                var paths = store.GetPaths(project);
                var target = StudioExportTarget.Capture(outputPath);
                try
                {
                    target.Validate(project, paths, savedProjectFile: null);
                }
                catch (IOException ex)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams, ex.Message);
                }

                var temporaryPath = Path.Combine(outputDirectory,
                    $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.export.mp4");
                try
                {
                    await Services.GetRequiredService<IStudioExportService>().ExportAsync(
                        project,
                        store.LoadEvents(projectId),
                        paths,
                        temporaryPath,
                        settings.VideoCodec,
                        progress: null,
                        cancellationToken: cancellationToken);
                    try
                    {
                        File.Move(temporaryPath, outputPath, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(outputPath))
                    {
                        throw new AutomationException(AutomationErrorCodes.InvalidParams,
                            "The export destination was created by another process; choose a new filename.");
                    }

                    _ = store.RecordExport(projectId, outputPath);
                    Services.GetRequiredService<IRecentCaptureService>().Record(outputPath, CaptureType.Video);
                    ShowSaveToast(outputPath);
                    return new JsonObject { ["projectId"] = projectId, ["path"] = outputPath };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AutomationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new AutomationException(AutomationErrorCodes.Internal, $"Studio export failed: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Debug.WriteLine($"Studio automation export could not remove its temporary file: {ex.Message}");
                    }
                }
            }
            default:
                throw new AutomationException(AutomationErrorCodes.NotSupported, $"Studio command {commandName} is not supported.");
        }
    }

    // The project an edit is to change, when it is still the one the agent read.
    private StudioProject ReadCheckedProject(JsonObject parameters, string projectId, IStudioProjectStore store)
    {
        var expectedModifiedAt = RequiredStudioString(parameters, "expectedModifiedAt");
        var expectedRevision = RequiredStudioString(parameters, "expectedRevision");
        var current = ReadAutomationProject(projectId, store);
        var currentNode = ProjectNode(current).AsObject();
        if (!string.Equals(currentNode["modifiedAt"]?.GetValue<string>(), expectedModifiedAt, StringComparison.Ordinal)
            || !string.Equals(ProjectRevision(current), expectedRevision, StringComparison.Ordinal))
        {
            throw new AutomationException(AutomationErrorCodes.Conflict,
                "The Studio project changed since it was read. Get it again before editing.");
        }

        return current;
    }

    // Saves an edit: through the undo history of an open editor, and directly otherwise.
    private StudioProject ApplyAutomationEdit(string projectId, StudioProject current, StudioProject updated, IStudioProjectStore store)
    {
        if (StudioWindows().FindOpenViewModel(projectId) is not { } openEditor)
        {
            return store.Save(updated);
        }

        if (!openEditor.ApplyAutomationProject(updated, current.ModifiedAt))
        {
            throw new AutomationException(AutomationErrorCodes.Conflict,
                "The Studio project changed or became unavailable while applying the edit.");
        }

        return store.Load(projectId);
    }

    private static JsonObject CaptionFindingsNode(StudioCaptionFinding[] findings)
    {
        var items = new JsonArray();
        foreach (var finding in findings)
        {
            items.Add(new JsonObject
            {
                ["cue"] = finding.Cue,
                ["kind"] = StudioCaptionQuality.GetKindName(finding.Kind),
                ["canFix"] = finding.CanFix,
            });
        }

        return new JsonObject { ["findings"] = items, ["summary"] = StudioCaptionQuality.GetSummary(findings) };
    }

    private StudioProject ReadAutomationProject(string projectId, IStudioProjectStore store)
    {
        if (StudioWindows().FindOpenViewModel(projectId) is { } viewModel)
        {
            return viewModel.Project
                ?? throw new AutomationException(AutomationErrorCodes.Busy, "The Studio project is not ready for agent control.");
        }

        if (!store.Exists(projectId))
        {
            throw new AutomationException(AutomationErrorCodes.NotFound, $"No Studio project with ID {projectId}.");
        }

        try
        {
            return store.Load(projectId);
        }
        catch (DirectoryNotFoundException)
        {
            throw new AutomationException(AutomationErrorCodes.NotFound, $"No Studio project with ID {projectId}.");
        }
    }

    private static JsonNode ProjectNode(StudioProject project) =>
        JsonNode.Parse(StudioProjectJson.WriteProject(project))
        ?? throw new InvalidOperationException("Studio project serialization returned no JSON.");

    private static string ProjectRevision(StudioProject project) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StudioProjectJson.WriteProject(project))))
            .ToLowerInvariant();

    private static string RequiredStudioString(JsonObject parameters, string key) =>
        parameters[key]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} is required.");

    private static string FormatStudioTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void MergeStudioJson(JsonObject patch, JsonObject target)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject patchObject && target[key] is JsonObject targetObject)
            {
                MergeStudioJson(patchObject, targetObject);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    void IAutomationAppActions.RevealForAutomation(string path) => RevealInExplorer(path);

    void IAutomationAppActions.RecordUploadLinkForAutomation(string path, string url) => RecordUploadLink(path, url);

    void IAutomationAppActions.OnAutomationSettingChanged(string key)
    {
        if (key == "studioPreviewEnabled")
        {
            RefreshStudioPreviewSetting();
        }
    }

    /// <summary>Brings an agent approval prompt to the front, the way the app's other windows open.</summary>
    internal void ActivateAutomationWindow(Window window) => ActivateWindowToForeground(window);
}
