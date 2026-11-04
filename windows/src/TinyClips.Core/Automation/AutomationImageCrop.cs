using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;
using TinyClips.Core.Editing;

namespace TinyClips.Core.Automation;

public static partial class AutomationImageCrop
{
    public static async Task<(string Path, int Width, int Height)> SaveAsync(string source, JsonNode? region,
        string output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rect = AutomationCaptureRequest.ParseRegion(region);
        using var decoded = ReadLibraryPixels(source, cancellationToken);
        // Addition in the incoming integer type could wrap a far edge back inside the image.
        if ((long)rect.X + rect.Width > decoded.Width || (long)rect.Y + rect.Height > decoded.Height)
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                $"region must fit inside the source image ({decoded.Width} × {decoded.Height} pixels).");

        using var cropped = new SKBitmap();
        if (!decoded.ExtractSubset(cropped, SKRectI.Create(rect.X, rect.Y, rect.Width, rect.Height)))
            throw new AutomationException(AutomationErrorCodes.Internal, "Could not crop the screenshot.");
        var path = await ScreenshotExportFile.WriteNewAsync(output, (stage, token) =>
        {
            token.ThrowIfCancellationRequested();
            using var png = cropped.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new AutomationException(AutomationErrorCodes.Internal, "Could not encode the cropped PNG.");
            using var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            png.SaveTo(file);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return (path, cropped.Width, cropped.Height);
    }

    private static SKBitmap ReadLibraryPixels(string source, CancellationToken cancellationToken)
    {
        // ResolveLibraryFile authorized the named entry, not the target of an alias or NTFS stream.
        var entry = new FileInfo(source);
        if (entry.Name.Contains(':') || entry.LinkTarget is not null) throw NotFound();
        for (var directory = entry.Directory; directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw NotFound();
        using var stream = entry.OpenRead();
        if (!IsAuthorizedHandle(stream.SafeFileHandle, source)) throw NotFound();
        try
        {
            return ScreenshotBackgroundDecoder.DecodeSourcePixels(stream, int.MaxValue, cancellationToken, out _);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new AutomationException(AutomationErrorCodes.InvalidParams,
                "The screenshot must be a readable still PNG, JPEG, or WebP image of 32 megapixels or less.");
        }
    }

    // Check the actual opened handle (including ancestor junctions), and decode that very stream.
    private static unsafe bool IsAuthorizedHandle(SafeFileHandle file, string source)
    {
        var expected = Path.GetFullPath(source);
        if (!expected.StartsWith(@"\\?\", StringComparison.Ordinal))
            expected = expected.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + expected[2..] : @"\\?\" + expected;
        var buffer = new char[expected.Length + 1];
        fixed (char* start = buffer)
        {
            var length = GetFinalPathNameByHandle(file, start, (uint)buffer.Length, 0);
            return length == expected.Length && expected.AsSpan().Equals(buffer.AsSpan(0, (int)length), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static AutomationException NotFound() => new(AutomationErrorCodes.NotFound, "That file is not a library screenshot.");

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW")]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint length, uint flags);
}
