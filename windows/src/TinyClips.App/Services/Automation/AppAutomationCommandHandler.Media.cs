using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TinyClips.Core.Automation;
using TinyClips.Core.Models;
using TinyClips.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace TinyClips.App;

/// <summary>
/// Unattended trim and frame export. Results are new library files; the source is never
/// modified or deleted, and no trimmer window opens.
/// </summary>
internal sealed partial class AppAutomationCommandHandler
{
    private async Task<JsonNode> TrimAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var (source, type) = ResolveLibraryFile(context.GetString("path"));
        var start = RequiredNumber(context, "start");
        var end = RequiredNumber(context, "end");
        var removeAudio = context.GetBoolean("removeAudio");

        JsonObject result;
        switch (type)
        {
            case CaptureType.Video:
                result = await TrimVideoAsync(source, start, end, removeAudio, cancellationToken);
                break;
            case CaptureType.Gif:
                if (removeAudio)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams, "removeAudio applies to videos only.");
                }

                result = await TrimGifAsync(source, start, end, cancellationToken);
                break;
            default:
                throw new AutomationException(AutomationErrorCodes.InvalidParams, "editor.trim works on videos and GIFs only.");
        }

        var output = result["path"]!.GetValue<string>();
        await OnUiAsync(() =>
        {
            _app.RecordAutomationCapture(output, type);
            return true;
        });
        result["sourcePath"] = source;
        return result;
    }

    private async Task<JsonNode> SaveFrameAsync(AutomationCommandContext context, CancellationToken cancellationToken)
    {
        var (source, type) = ResolveLibraryFile(context.GetString("path"));
        double? time = context.Params["time"] is null ? null : RequiredNumber(context, "time");
        int? frame = context.Params["frame"] is null ? null : context.GetInt32("frame", -1);
        if (time is null == frame is null)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, "Pass exactly one of time or frame.");
        }

        JsonObject result;
        switch (type)
        {
            case CaptureType.Video:
                if (time is not { } videoTime)
                {
                    throw new AutomationException(AutomationErrorCodes.InvalidParams,
                        "Videos take time in seconds; frame applies to GIFs only.");
                }

                result = await SaveVideoFrameAsync(source, videoTime, cancellationToken);
                break;
            case CaptureType.Gif:
                result = await SaveGifFrameAsync(source, time, frame, cancellationToken);
                break;
            default:
                throw new AutomationException(AutomationErrorCodes.InvalidParams, "editor.saveFrame works on videos and GIFs only.");
        }

        var output = result["path"]!.GetValue<string>();
        await OnUiAsync(() =>
        {
            _app.RecordAutomationScreenshot(output);
            return true;
        });
        result["sourcePath"] = source;
        return result;
    }

    private static double RequiredNumber(AutomationCommandContext context, string name)
    {
        if (context.Params[name] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number))
        {
            return number;
        }

        throw new AutomationException(AutomationErrorCodes.InvalidParams, $"{name} must be a number of seconds.");
    }

    private static async Task<JsonObject> TrimVideoAsync(
        string source, double start, double end, bool removeAudio, CancellationToken cancellationToken)
    {
        var clip = await OpenClipAsync(source);
        var range = AutomationMediaRange.Clamp(start, end, clip.OriginalDuration.TotalSeconds);
        clip.TrimTimeFromStart = TimeSpan.FromSeconds(range.Start);
        clip.TrimTimeFromEnd = TimeSpan.FromSeconds(Math.Max(0, clip.OriginalDuration.TotalSeconds - range.End));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        var outFile = await CreateNewOutputAsync(
            CaptureType.Video, ".mp4", removeAudio ? " (trimmed, no audio)" : " (trimmed)", source);
        try
        {
            if (removeAudio)
            {
                var profile = composition.CreateDefaultEncodingProfile();
                profile.Audio = null;
                await composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise, profile).AsTask(cancellationToken);
            }
            else
            {
                await composition.RenderToFileAsync(outFile, MediaTrimmingPreference.Precise).AsTask(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DeleteQuietlyAsync(outFile);
            throw new AutomationException(AutomationErrorCodes.Internal, $"Could not export the trimmed video: {ex.Message}");
        }
        catch
        {
            await DeleteQuietlyAsync(outFile);
            throw;
        }

        return new JsonObject
        {
            ["path"] = outFile.Path,
            ["start"] = range.Start,
            ["end"] = range.End,
        };
    }

    private static async Task<JsonObject> TrimGifAsync(string source, double start, double end, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(source);
        using var input = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await OpenGifDecoderAsync(input);
        var delays = await ReadGifDelaysAsync(decoder);
        var seconds = delays.Select(d => d / 100.0).ToArray();
        var range = AutomationMediaRange.Clamp(start, end, AutomationMediaRange.Duration(seconds));
        var (first, last) = AutomationMediaRange.GifFrames(range.Start, range.End, seconds);

        var outFile = await CreateNewOutputAsync(CaptureType.Gif, ".gif", " (trimmed)", source);
        try
        {
            using var output = await outFile.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.GifEncoderId, output);
            await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
            {
                { "/appext/application", new BitmapTypedValue("NETSCAPE2.0"u8.ToArray(), Windows.Foundation.PropertyType.UInt8Array) },
                { "/appext/data", new BitmapTypedValue(new byte[] { 3, 1, 0, 0 }, Windows.Foundation.PropertyType.UInt8Array) },
            });

            for (var i = first; i <= last; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = await DecodeGifFrameAsync(decoder, i);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet
                {
                    { "/grctlext/Delay", new BitmapTypedValue(delays[i], Windows.Foundation.PropertyType.UInt16) },
                    { "/grctlext/Disposal", new BitmapTypedValue((byte)1, Windows.Foundation.PropertyType.UInt8) },
                });
                if (i < last)
                {
                    await encoder.GoToNextFrameAsync();
                }
            }

            await encoder.FlushAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DeleteQuietlyAsync(outFile);
            throw new AutomationException(AutomationErrorCodes.Internal, $"Could not write the trimmed GIF: {ex.Message}");
        }
        catch
        {
            await DeleteQuietlyAsync(outFile);
            throw;
        }

        return new JsonObject
        {
            ["path"] = outFile.Path,
            ["start"] = AutomationMediaRange.StartTime(first, seconds),
            ["end"] = AutomationMediaRange.StartTime(last + 1, seconds),
            ["frameCount"] = last - first + 1,
        };
    }

    private static async Task<JsonObject> SaveVideoFrameAsync(string source, double time, CancellationToken cancellationToken)
    {
        var clip = await OpenClipAsync(source);
        var duration = clip.OriginalDuration.TotalSeconds;
        if (!double.IsFinite(time) || time < 0 || time >= duration)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                $"time must be within the video (0–{duration:0.00} s).");
        }

        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        SoftwareBitmap bitmap;
        try
        {
            // 0,0 keeps the source resolution, matching the trimmer's Export frame.
            using var thumb = await composition.GetThumbnailAsync(
                TimeSpan.FromSeconds(time), 0, 0, VideoFramePrecision.NearestFrame).AsTask(cancellationToken);
            var decoder = await BitmapDecoder.CreateAsync(thumb);
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AutomationException(AutomationErrorCodes.Internal, $"Could not read the video frame: {ex.Message}");
        }

        using (bitmap)
        {
            var path = await SaveFramePngAsync(bitmap, source, cancellationToken);
            return new JsonObject { ["path"] = path, ["time"] = time };
        }
    }

    private static async Task<JsonObject> SaveGifFrameAsync(string source, double? time, int? frame, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(source);
        using var input = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await OpenGifDecoderAsync(input);
        var seconds = (await ReadGifDelaysAsync(decoder)).Select(d => d / 100.0).ToArray();
        int index;
        if (frame is { } requested)
        {
            if (requested < 0 || requested >= seconds.Length)
            {
                throw new AutomationException(AutomationErrorCodes.InvalidParams,
                    $"frame must be between 0 and {seconds.Length - 1}.");
            }

            index = requested;
        }
        else
        {
            index = AutomationMediaRange.GifFrameAt(time ?? 0, seconds);
        }

        using var bitmap = await DecodeGifFrameAsync(decoder, index);
        var path = await SaveFramePngAsync(bitmap, source, cancellationToken);
        return new JsonObject
        {
            ["path"] = path,
            ["time"] = AutomationMediaRange.StartTime(index, seconds),
            ["frame"] = index,
        };
    }

    private static async Task<MediaClip> OpenClipAsync(string source)
    {
        try
        {
            return await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(source));
        }
        catch (Exception ex)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, $"The video could not be read: {ex.Message}");
        }
    }

    private static async Task<BitmapDecoder> OpenGifDecoderAsync(IRandomAccessStream input)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, input);
            if (decoder.FrameCount == 0)
            {
                throw new InvalidDataException("The GIF contains no frames.");
            }

            return decoder;
        }
        catch (Exception ex)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams, $"The GIF could not be read: {ex.Message}");
        }
    }

    /// <summary>Frame delays in centiseconds, read the same way the GIF trimmer reads them.</summary>
    private static async Task<ushort[]> ReadGifDelaysAsync(BitmapDecoder decoder)
    {
        var delays = new ushort[decoder.FrameCount];
        for (var i = 0; i < delays.Length; i++)
        {
            delays[i] = 10;
            try
            {
                var frame = await decoder.GetFrameAsync((uint)i);
                var props = await frame.BitmapProperties.GetPropertiesAsync(new[] { "/grctlext/Delay" });
                if (props.TryGetValue("/grctlext/Delay", out var value) && value.Value is ushort delay)
                {
                    delays[i] = Math.Max((ushort)2, delay);
                }
            }
            catch
            {
                // Frames without the extension keep the default delay.
            }
        }

        return delays;
    }

    private static async Task<SoftwareBitmap> DecodeGifFrameAsync(BitmapDecoder decoder, int index)
    {
        var frame = await decoder.GetFrameAsync((uint)index);
        return await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private static async Task<string> SaveFramePngAsync(SoftwareBitmap bitmap, string source, CancellationToken cancellationToken)
    {
        var outFile = await CreateNewOutputAsync(CaptureType.Screenshot, ".png", "(frame)", source);
        try
        {
            using var output = await outFile.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DeleteQuietlyAsync(outFile);
            throw new AutomationException(AutomationErrorCodes.Internal, $"Could not save the frame: {ex.Message}");
        }
        catch
        {
            await DeleteQuietlyAsync(outFile);
            throw;
        }

        return outFile.Path;
    }

    /// <summary>Creates a new, uniquely named library file; never an existing file or the source.</summary>
    private static async Task<StorageFile> CreateNewOutputAsync(CaptureType type, string extension, string suffix, string source)
    {
        var path = App.Services.GetRequiredService<IClipStorageService>().GenerateFilePath(type, extension, suffix);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.GenerateUniqueName);
        if (PathsEqual(file.Path, source))
        {
            throw new AutomationException(AutomationErrorCodes.Internal, "Tiny Clips could not choose a new file name.");
        }

        return file;
    }

    private static async Task DeleteQuietlyAsync(StorageFile file)
    {
        try
        {
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch
        {
            // A partial output that cannot be removed is left for the user.
        }
    }
}
