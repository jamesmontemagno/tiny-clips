using System.Collections.Immutable;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TinyClips.App.ScreenshotEditor;
using TinyClips.Core.Automation;
using TinyClips.Core.Capture;
using TinyClips.Core.Editing;
using TinyClips.Core.Models;
using TinyClips.Core.Services;

namespace TinyClips.App;

/// <summary>
/// Unattended capture targeting and annotation: agents choose displays, regions, and windows
/// with IDs from <c>capture.targets</c>, and annotate screenshots with structured objects. No
/// picker or editor window is shown, and missing targets are <c>invalid_params</c>.
/// </summary>
internal sealed partial class AppAutomationCommandHandler
{
    // Static so IDs stay valid for the app session even when agent control is toggled.
    private static readonly AutomationCaptureTargetRegistry CaptureTargets = new();

    // -- capture.targets ---------------------------------------------------------------------

    private static JsonNode ListCaptureTargets(AutomationCommandContext context)
    {
        var kind = context.GetString("kind") ?? "all";
        var displays = new JsonArray();
        var windows = new JsonArray();

        if (kind is "all" or "displays")
        {
            var monitors = App.Services.GetRequiredService<IMonitorService>().GetMonitors();
            for (var i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                displays.Add(new JsonObject
                {
                    ["id"] = CaptureTargets.GetDisplayId(monitor.HMonitor),
                    ["name"] = monitor.IsPrimary ? $"Display {i + 1} (primary)" : $"Display {i + 1}",
                    ["width"] = monitor.Width,
                    ["height"] = monitor.Height,
                    ["scaleFactor"] = monitor.ScaleFactor,
                });
            }
        }

        if (kind is "all" or "windows")
        {
            foreach (var window in WindowEnumerator.GetWindows().Where(w => w.IsSelectable))
            {
                windows.Add(new JsonObject
                {
                    ["id"] = CaptureTargets.GetWindowId(window.Hwnd),
                    ["title"] = window.Title,
                    ["app"] = window.AppName,
                });
            }
        }

        return new JsonObject { ["displays"] = displays, ["windows"] = windows };
    }

    // -- capture.screenshot / capture.ocr ----------------------------------------------------

    private async Task<JsonNode> CaptureScreenshotAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var request = AutomationCaptureRequest.Parse(context.Params);
        var (path, width, height) = await OnUiAsync(() => CaptureOnUiAsync(request, cancellationToken));
        return new JsonObject
        {
            ["path"] = path,
            ["mode"] = ModeName(request.Mode),
            ["width"] = width,
            ["height"] = height,
        };
    }

    private Task<(string Path, int Width, int Height)> CaptureOnUiAsync(AutomationCaptureRequest request, CancellationToken cancellationToken)
    {
        EnsureIdle();
        var (target, region) = ResolveCaptureTarget(request);
        return _app.CaptureForAutomationAsync(target, region, cancellationToken);
    }

    private async Task<JsonNode> RecognizeTextAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var request = AutomationCaptureRequest.Parse(context.Params);
        string text;
        try
        {
            text = await OnUiAsync(() => RecognizeOnUiAsync(request, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            throw new AutomationException(AutomationErrorCodes.NotSupported,
                $"Text recognition is unavailable: {ex.Message} Add an OCR language in Windows Settings → Time & language → Language & region.");
        }

        return new JsonObject { ["text"] = text };
    }

    private Task<string> RecognizeOnUiAsync(AutomationCaptureRequest request, CancellationToken cancellationToken)
    {
        var (target, region) = ResolveCaptureTarget(request);
        return _app.RecognizeTextForAutomationAsync(target, region, cancellationToken);
    }

    /// <summary>Re-validates opaque IDs against the current displays and windows.</summary>
    private (CaptureTarget Target, PixelRect? Region) ResolveCaptureTarget(AutomationCaptureRequest request)
    {
        switch (request.Mode)
        {
            case AutomationCaptureMode.Window:
                if (!CaptureTargets.TryResolveWindow(request.WindowId!, out var hwnd)
                    || !WindowEnumerator.GetWindows().Any(w => w.IsSelectable && w.Hwnd == hwnd))
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "windowId does not match an open window. Call capture.targets for current IDs.");
                }

                return (CaptureTarget.Window(hwnd), null);

            case AutomationCaptureMode.Region:
            {
                var monitor = ResolveDisplay(request.DisplayId!);
                var region = request.Region!.Value;
                AutomationCaptureRequest.EnsureRegionFits(region, monitor.Width, monitor.Height);
                return (CaptureTarget.Monitor(monitor.HMonitor), region);
            }

            default:
            {
                var monitor = request.DisplayId is { } displayId
                    ? ResolveDisplay(displayId)
                    : _app.GetPreferredAutomationMonitor()
                        ?? throw new AutomationException(AutomationErrorCodes.Internal, "No display was found to capture.");
                return (CaptureTarget.Monitor(monitor.HMonitor), null);
            }
        }
    }

    private static MonitorInfo ResolveDisplay(string displayId)
    {
        if (CaptureTargets.TryResolveDisplay(displayId, out var handle)
            && App.Services.GetRequiredService<IMonitorService>().GetMonitors().FirstOrDefault(m => m.HMonitor == handle) is { } monitor)
        {
            return monitor;
        }

        throw new AutomationException(AutomationErrorCodes.InvalidParams,
            "displayId does not match a connected display. Call capture.targets for current IDs.");
    }

    private static string ModeName(AutomationCaptureMode mode) => mode switch
    {
        AutomationCaptureMode.Region => "region",
        AutomationCaptureMode.Window => "window",
        _ => "screen",
    };

    private async Task<JsonNode> CropAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var (source, type) = ResolveLibraryFile(context.GetString("path"));
        if (type != CaptureType.Screenshot)
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "editor.crop works on screenshots only.");

        var output = App.Services.GetRequiredService<IClipStorageService>()
            .GenerateFilePath(CaptureType.Screenshot, ".png", "(cropped)");
        var crop = await Task.Run(() => AutomationImageCrop.SaveAsync(source, context.Params["region"], output, cancellationToken), cancellationToken);
        await OnUiAsync(() =>
        {
            App.Services.GetRequiredService<IRecentCaptureService>().Record(crop.Path, CaptureType.Screenshot);
            return true;
        });
        return new JsonObject { ["path"] = crop.Path, ["sourcePath"] = source, ["width"] = crop.Width, ["height"] = crop.Height };
    }

    // -- editor.annotate ---------------------------------------------------------------------

    private async Task<JsonNode> AnnotateAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var annotationsNode = context.Params["annotations"];
        AutomationAnnotationParser.ValidateStructure(annotationsNode);

        var (source, type) = ResolveLibraryFile(context.GetString("path"));
        if (type != CaptureType.Screenshot)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "editor.annotate works on screenshots only.");
        }

        ScreenshotBackgroundPixels pixels;
        try
        {
            pixels = await Task.Run(() => ScreenshotBackgroundDecoder.Decode(source, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                "The screenshot could not be read as a still PNG, JPEG, or WebP image of 32 megapixels or less.");
        }

        var annotations = AutomationAnnotationParser.Parse(annotationsNode, pixels.Width, pixels.Height);
        var snapshots = annotations.Select(a => a.CaptureSnapshot()).ToImmutableArray();

        var extension = Path.GetExtension(source).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ".jpg",
            ".webp" => ".webp",
            _ => ".png",
        };
        var output = App.Services.GetRequiredService<IClipStorageService>()
            .GenerateFilePath(CaptureType.Screenshot, extension, "(annotated)");
        if (PathsEqual(output, source))
        {
            throw new AutomationException(AutomationErrorCodes.Internal, "Tiny Clips could not choose a new file name for the annotated copy.");
        }

        var quality = App.Services.GetRequiredService<ICaptureSettings>().JpegQuality;
        using (var rendered = await EditorController.RenderAnnotationsAsync(
            pixels.BgraPixels, pixels.Width, pixels.Height, snapshots, cancellationToken))
        {
            output = await Task.Run(() => ScreenshotExportFile.WriteNewAsync(output,
                (temporaryPath, token) => ScreenshotEditorWindow.EncodeBitmapToFileAsync(output, temporaryPath, rendered, quality, token),
                cancellationToken), cancellationToken);
        }

        await OnUiAsync(() =>
        {
            _app.RecordAutomationScreenshot(output);
            return true;
        });

        return new JsonObject
        {
            ["path"] = output,
            ["sourcePath"] = source,
            ["annotationsApplied"] = annotations.Count,
        };
    }
}
