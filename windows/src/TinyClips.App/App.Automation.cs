using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
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
    private TrayAgentIndicator? _trayAgentIndicator;
    private bool _agentControlWired;
    private AutomationException? _timelineRecordingError;

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

    /// <summary>The tray popup's title row, with a button that shows agent control state and toggles it.</summary>
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
        var labelText = new TextBlock { Style = ResourceStyle("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(labelText);

        var status = new MenuFlyoutItem { IsEnabled = false };
        var toggle = new MenuFlyoutItem();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(toggle, "TrayAgentControlToggleItem");
        toggle.Click += async (_, _) =>
        {
            _trayPopup?.Hide();
            var isFirstEnable = await AgentControl.SetEnabledAsync(!AgentControl.IsEnabled);
            if (isFirstEnable && AgentControl.IsEnabled)
            {
                OpenSettingsWindow(SettingsSectionKind.AgentControl);
            }
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
        flyout.Items.Add(status);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(toggle);
        flyout.Items.Add(openSettings);

        var indicator = new Button
        {
            Content = label,
            Flyout = flyout,
            Style = ResourceStyle("SubtleButtonStyle"),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(indicator, "TrayAgentControlIndicator");
        Grid.SetColumn(indicator, 1);
        header.Children.Add(indicator);

        _trayAgentIndicator = new TrayAgentIndicator(indicator, label, labelText, status, toggle);
        RefreshTrayAgentIndicator();
        return header;
    }

    private void RefreshTrayAgentIndicator()
    {
        if (_trayAgentIndicator is not { } parts)
        {
            return;
        }

        var enabled = AgentControl.IsEnabled;
        parts.Label.Text = enabled ? "Agent control on" : "Agent control off";
        parts.Content.Opacity = enabled ? 1.0 : 0.6;
        parts.Status.Text = enabled ? "Agents can control Tiny Clips" : "Agents can't control Tiny Clips";
        parts.Toggle.Text = enabled ? "Turn off agent control" : "Turn on agent control";
        parts.Toggle.Icon = new FontIcon { Glyph = enabled ? "\uE711" : GlyphAgent, FontFamily = FluentIconFont };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            parts.Button, enabled ? "Agent control is on" : "Agent control is off");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
            parts.Toggle,
            enabled
                ? "Stops AI agents from controlling Tiny Clips."
                : "Allows paired agents to control Tiny Clips and opens settings the first time.");
        ToolTipService.SetToolTip(
            parts.Button,
            enabled ? "Agents can control Tiny Clips. Select to turn it off." : "Agent control is off. Select to turn it on.");
    }

    private sealed record TrayAgentIndicator(
        Button Button,
        StackPanel Content,
        TextBlock Label,
        MenuFlyoutItem Status,
        MenuFlyoutItem Toggle);

    // -- IAutomationAppActions (UI thread) ----------------------------------------------------

    AutomationRecordingSnapshot IAutomationAppActions.GetRecordingSnapshot()
    {
        var video = Services.GetRequiredService<IVideoRecordingService>();
        var gif = Services.GetRequiredService<IGifRecordingService>();
        if (video.IsRecording)
        {
            return new(video.IsPaused ? "paused" : "recording", true, CaptureType.Video,
                video.IsMicrophoneCapturing,
                Services.GetRequiredService<IWebcamCaptureService>().IsRunning
                    && _activeVideoRecordingOptions.WebcamEnabled != false);
        }

        if (gif.IsRecording)
        {
            return new(gif.IsPaused ? "paused" : "recording", true, CaptureType.Gif);
        }

        if (_processingIndicator is not null || HasPendingStudioDraftCompletion)
        {
            return new("processing", true, _activeRecordingType);
        }

        var busy = _captureFlowCts is not null || _scrollingPanel is not null || _isExiting;
        return new("idle", busy, null, Error: _timelineRecordingError);
    }

    string? IAutomationAppActions.OpenAutomationWindow(string window, string? guideTopic)
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
                var guide = _guideWindow
                    ?? throw new AutomationException(AutomationErrorCodes.Busy, "The Guide could not be opened.");
                if (guideTopic is not null)
                {
                    guide.SelectTopic(guideTopic);
                }
                return guide.SelectedTopic;
            default:
                throw new AutomationException(AutomationErrorCodes.InvalidParams, $"Unknown window {window}.");
        }
        return null;
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

        var path = await SavePersistentScreenshotFrameAsync(
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
        PixelRect? region,
        VideoRecordingOptions? options,
        WindowAreaSelection? windowArea,
        MonitorInfo? monitor,
        Func<string, Task>? studioDraftCompleted,
        Action? validateStudioStart)
    {
        VideoRecordingCommand? videoCommand = type == CaptureType.Video
            ? studio ? VideoRecordingCommand.StudioRecording : VideoRecordingCommand.RecordVideo
            : null;
        // A window area keeps the display snapshot it was resolved against, so a display
        // change before the start is rejected instead of recording a shifted region.
        var selection = windowArea is not null && monitor is not null
            ? new TargetSelection(target, region, monitor, WindowArea: windowArea)
            : new TargetSelection(target, region, ResolveMonitorForTarget(target));
        await BeginCaptureAsync(
            type,
            abortIfRecording: true,
            videoCommand: videoCommand,
            automationSelection: selection,
            automationOptions: options,
            studioDraftCompleted: studioDraftCompleted,
            validateStudioStart: validateStudioStart);
    }

    private static void EnsureAutomationDevicePermissions(VideoRecordingOptions options)
    {
        var permissions = Services.GetRequiredService<IMediaDevicePermissionService>();
        AutomationRecordingOptions.EnsureAuthorized(options, Services.GetRequiredService<ICaptureSettings>(),
            permissions.HasMicrophoneAccess, permissions.HasCameraAccess);
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

            var path = await SavePersistentScreenshotFrameAsync(
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

        await RestartActiveRecordingAsync(throwOnFailure: true);
        return true;
    }

    Task IAutomationAppActions.DiscardRecordingForAutomationAsync() => DiscardActiveRecordingAsync();

    async Task<string> IAutomationAppActions.OpenEditorForAutomationAsync(string path, CaptureType? type, bool studio, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case null:
                if (await OpenStudioProjectFileAsync(path) is { } failure)
                {
                    throw new AutomationException(AutomationErrorCodes.Internal, failure);
                }

                return "studio";
            case CaptureType.Screenshot:
                OpenScreenshotEditor(path, reopenPickerAfterClose: false);
                return "screenshot";
            case CaptureType.Video when studio:
                var result = await StudioWindows().OpenVideoAsync(path, cancellationToken);
                if (result.Window is null)
                {
                    throw new AutomationException(AutomationErrorCodes.Internal, result.Failure ?? "This video could not be opened in Studio.");
                }
                return "studio";
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

    async Task<JsonNode> IAutomationAppActions.ExecuteStudioAutomationCommandAsync(
        AutomationCommandContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = Services.GetRequiredService<ICaptureSettings>();
        var store = Services.GetRequiredService<IStudioProjectStore>();
        var commandName = context.Command.Name;
        var parameters = context.Params;
        switch (commandName)
        {
            case "studio.projects.list":
            {
                var limit = parameters["limit"]?.GetValue<int>() ?? 20;
                if (limit is < 1 or > 100)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams, "limit must be from 1 to 100.");
                }

                return StudioProjectAutomationListing.Create(store.ListLibraryProjects(), limit);
            }
            case "studio.project.get":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var project = ReadAutomationProject(projectId, store, allowTimeline: true);
                return new JsonObject
                {
                    ["project"] = ProjectNode(project),
                    ["revision"] = ProjectRevision(project),
                };
            }
            case "studio.project.saveCopy":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var folderName = RequiredStudioString(parameters, "folderName");
                using var projectUse = store.AcquireProjectUse(projectId);
                _ = ReadAutomationProject(projectId, store, allowTimeline: true);
                if (StudioWindows().FindOpenViewModel(projectId) is { } openEditor
                    && !openEditor.PrepareAutomationProjectCopy())
                {
                    throw new AutomationException(AutomationErrorCodes.Busy, "The Studio project is not ready to save.");
                }

                return await AutomationStudioProjectCopy.SaveAsync(
                    store, projectId, folderName,
                    Services.GetRequiredService<IClipStorageService>().OutputDirectory(CaptureType.Video),
                    cancellationToken);
            }
            case "studio.project.open":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                if (StudioWindows().FindOpenViewModel(projectId) is null)
                {
                    _ = ReadAutomationProject(projectId, store, allowTimeline: true);
                }

                if (OpenStudioWindow(projectId) is null)
                {
                    throw new AutomationException(AutomationErrorCodes.Busy, "The Studio project could not be opened.");
                }

                return new JsonObject { ["projectId"] = projectId, ["opened"] = true };
            }
            case "studio.project.openFolder":
            case "studio.project.delete":
            {
                var id = RequiredStudioString(parameters, "projectId");
                try
                {
                    if (commandName == "studio.project.openFolder")
                    {
                        await StudioWindows().OpenLibraryFolderAsync(id);
                        return new JsonObject { ["projectId"] = id, ["opened"] = true };
                    }
                    if (!await StudioWindows().TryDeleteLibraryProjectAsync(id))
                        throw new AutomationException(AutomationErrorCodes.Busy, "The Studio project is in use.");
                    return new JsonObject { ["projectId"] = id, ["deleted"] = true };
                }
                catch (DirectoryNotFoundException ex)
                {
                    throw new AutomationException(AutomationErrorCodes.NotFound, ex.Message);
                }
                catch (ArgumentException ex)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams, ex.Message);
                }
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

                var current = ReadAutomationProject(projectId, store, allowTimeline: true);
                if (current.SchemaVersion >= 2)
                {
                    if (patch.Count != 1 || patch["canvas"] is not JsonObject)
                        throw new AutomationException(AutomationErrorCodes.NotSupported,
                            "Only the canvas can be patched globally; other timeline properties belong to individual clips.");
                    return await CreateTimelineAutomation().ExecuteAsync(TimelineRequest.From(context), null, cancellationToken);
                }
                var currentNode = ProjectNode(current).AsObject();
                if (!string.Equals(currentNode["modifiedAt"]?.GetValue<string>(), expectedModifiedAt, StringComparison.Ordinal)
                    || !string.Equals(ProjectRevision(current), expectedRevision, StringComparison.Ordinal))
                {
                    throw new AutomationException(AutomationErrorCodes.Conflict,
                        "The Studio project changed since it was read. Get it again before editing.");
                }

                StudioTimelineAutomation.MergeEditableFields(patch, currentNode);
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
            case "studio.export.options":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var project = ReadAutomationProject(projectId, store, allowTimeline: true);
                var choice = ReadStudioExportChoice(parameters, settings);
                var natural = StudioCanvasMath.NaturalSize(project);
                var options = StudioExportPresets.CreateOptions(project, settings.VideoCodec, choice);
                var resolutions = new JsonArray();
                foreach (var option in options.Resolutions)
                {
                    resolutions.Add(new JsonObject
                    {
                        ["id"] = option.Id,
                        ["label"] = option.Label,
                        ["width"] = (int)option.Size.Width,
                        ["height"] = (int)option.Size.Height,
                    });
                }

                var qualities = new JsonArray();
                foreach (var option in options.Qualities)
                {
                    qualities.Add(new JsonObject
                    {
                        ["id"] = option.Id,
                        ["label"] = option.Label,
                        ["videoBitrate"] = option.VideoBitrate,
                        ["estimatedBytes"] = option.EstimatedBytes,
                    });
                }

                return new JsonObject
                {
                    ["projectId"] = projectId,
                    ["resolution"] = StudioExportPresets.Id(options.Choice.Resolution),
                    ["quality"] = StudioExportPresets.Id(options.Choice.Quality),
                    ["width"] = (int)options.Size.Width,
                    ["height"] = (int)options.Size.Height,
                    ["canvasWidth"] = (int)natural.Width,
                    ["canvasHeight"] = (int)natural.Height,
                    ["codec"] = settings.VideoCodec == VideoCodec.Hevc ? "hevc" : "h264",
                    ["resolutions"] = resolutions,
                    ["qualities"] = qualities,
                };
            }
            case "studio.export":
            {
                var projectId = RequiredStudioString(parameters, "projectId");
                var choice = ReadStudioExportChoice(parameters, settings);
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

                using var projectUse = store.AcquireProjectUse(projectId);
                var project = ReadAutomationProject(projectId, store, allowTimeline: true);
                var openEditor = StudioWindows().FindOpenViewModel(projectId);
                openEditor?.CommitCaptionText();
                if (openEditor is not null)
                {
                    if (!openEditor.SaveForAutomationExport() || openEditor.Manifest is not { } current)
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
                var codec = settings.VideoCodec;
                try
                {
                    await Services.GetRequiredService<IStudioExportService>().ExportAsync(
                        project,
                        project.SchemaVersion >= 2 ? new StudioEvents() : store.LoadEvents(projectId),
                        paths,
                        temporaryPath,
                        codec,
                        choice,
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

                    var saved = store.RecordExport(projectId, outputPath);
                    StudioWindows().FindOpenViewModel(projectId)?.RefreshExportBookkeeping(saved);
                    Services.GetRequiredService<IRecentCaptureService>().Record(outputPath, CaptureType.Video);
                    ShowSaveToast(outputPath);
                    var natural = StudioCanvasMath.NaturalSize(project);
                    var exported = StudioExportPresets.GetSize(natural, choice.Resolution);
                    return new JsonObject
                    {
                        ["projectId"] = projectId,
                        ["path"] = outputPath,
                        ["resolution"] = StudioExportPresets.Id(StudioExportPresets.Effective(natural, choice.Resolution)),
                        ["quality"] = StudioExportPresets.Id(choice.Quality),
                        ["width"] = (int)exported.Width,
                        ["height"] = (int)exported.Height,
                        ["codec"] = codec == VideoCodec.Hevc ? "hevc" : "h264",
                    };
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

    private StudioProject ReadAutomationProject(string projectId, IStudioProjectStore store, bool allowTimeline = false)
    {
        return CheckAutomationTimeline(StudioTimelineAutomation.ReadProject(projectId, store,
            StudioWindows().FindOpenViewModel(projectId)), allowTimeline);
    }

    private static StudioProject CheckAutomationTimeline(StudioProject project, bool allowTimeline)
    {
        if (!allowTimeline && project.SchemaVersion >= 2)
        {
            throw new AutomationException(AutomationErrorCodes.NotSupported,
                "This command works on a single recording, not on a multi-clip timeline.");
        }

        return project;
    }

    private static JsonNode ProjectNode(StudioProject project) =>
        JsonNode.Parse(StudioProjectJson.WriteProject(project))
        ?? throw new InvalidOperationException("Studio project serialization returned no JSON.");

    private static string ProjectRevision(StudioProject project) => AutomationStudioTimeline.Revision(project);

    // What was asked for, and for what was not (left out or null), the choice last made in Export's flyout.
    private static StudioExportChoice ReadStudioExportChoice(JsonObject parameters, ICaptureSettings settings)
    {
        var resolution = settings.StudioExportResolution;
        if (parameters["resolution"] is { } askedResolution
            && !(askedResolution.GetValueKind() == JsonValueKind.String && StudioExportPresets.TryParseResolution(askedResolution.GetValue<string>(), out resolution)))
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                $"resolution must be one of: {string.Join(", ", StudioExportPresets.ResolutionIds)}.");
        }

        var quality = settings.StudioExportQuality;
        if (parameters["quality"] is { } askedQuality
            && !(askedQuality.GetValueKind() == JsonValueKind.String && StudioExportPresets.TryParseQuality(askedQuality.GetValue<string>(), out quality)))
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                $"quality must be one of: {string.Join(", ", StudioExportPresets.QualityIds)}.");
        }

        return new StudioExportChoice(resolution, quality);
    }

    private static string RequiredStudioString(JsonObject parameters, string key) =>
        parameters[key]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{key} is required.");

    private static string FormatStudioTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private StudioTimelineAutomation CreateTimelineAutomation() => new(
        Services.GetRequiredService<IStudioProjectStore>(), StudioWindows().FindOpenViewModel,
        path => AutomationStudioTimeline.ResolveImportPath(path,
            Services.GetRequiredService<IClipLibraryService>().GetLibraryDirectories(),
            Services.GetRequiredService<IRecentCaptureService>().GetRecentCaptures().Select(item => item.Path)),
        request => request.RequireAccess(!_isExiting && AgentControl.IsEnabled, AgentControl.Clients));

    Task<JsonNode> IAutomationAppActions.ExecuteTimelineCommandAsync(AutomationCommandContext context, CancellationToken cancellationToken) =>
        CreateTimelineAutomation().ExecuteAsync(TimelineRequest.From(context), null, cancellationToken);

    (Action ValidateStart, Func<string, Task> Complete) IAutomationAppActions.PrepareTimelineRecording(
        AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var request = TimelineRequest.From(context);
        var timeline = CreateTimelineAutomation();
        void ValidateStart() => timeline.ValidateRecordingStart(request, cancellationToken);
        ValidateStart();
        return (ValidateStart, id => CompleteTimelineRecordingAsync(timeline, request, id));
    }

    private async Task CompleteTimelineRecordingAsync(StudioTimelineAutomation timeline, TimelineRequest request, string recordingId)
    {
        string? error = null;
        try
        {
            await timeline.ExecuteAsync(request, recordingId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            error = ex is AutomationException failure ? failure.Code : AutomationErrorCodes.Internal;
            _timelineRecordingError = new AutomationException(error,
                $"{ex.Message} The finished take was kept as Studio draft {recordingId}.");
            throw _timelineRecordingError;
        }
        finally
        {
            AgentControl.AuditLog.Write(new AutomationAuditEntry
            {
                Timestamp = DateTimeOffset.UtcNow, Command = request.Command, ClientId = request.ClientId,
                ClientName = request.ClientName, ErrorCode = error, Outcome = error is null ? "ok" : "error",
            });
        }
    }

    void IAutomationAppActions.RevealForAutomation(string path) => RevealInExplorer(path);

    void IAutomationAppActions.RecordUploadLinkForAutomation(string path, string url) => RecordUploadLink(path, url);

    /// <summary>Brings an agent approval prompt to the front, the way the app's other windows open.</summary>
    internal void ActivateAutomationWindow(Window window) => ActivateWindowToForeground(window);
}
