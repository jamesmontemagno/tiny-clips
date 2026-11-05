using TinyClips.Core.Services.ClipsLibrary;
using Whisper.net.Ggml;

namespace TinyClips.Core.Studio.Transcription;

/// <summary>A Whisper model that captions can be generated with.</summary>
/// <param name="Id">What the settings and the agent commands call it.</param>
/// <param name="Name">What the editor calls it.</param>
/// <param name="SizeText">About how large its download is, for whoever is asked to agree to it.</param>
/// <param name="ApproximateBytes">About how large its file is, for the progress of its download.</param>
/// <param name="Guidance">What it is good for.</param>
/// <param name="Type">What Whisper.net calls it.</param>
/// <param name="FileName">The name of its file in the models folder.</param>
public sealed record StudioCaptionModel(
    string Id,
    string Name,
    string SizeText,
    long ApproximateBytes,
    string Guidance,
    GgmlType Type,
    string FileName);

/// <summary>The models on offer. The ones OutroKit offers, without its two older large ones.</summary>
public static class StudioCaptionModels
{
    private const long MiB = 1024 * 1024;

    public static IReadOnlyList<StudioCaptionModel> All { get; } =
    [
        new("Tiny", "Tiny", "75 MB", 75 * MiB, "Fastest, lowest accuracy", GgmlType.Tiny, "ggml-tiny.bin"),
        new("TinyEn", "Tiny English", "75 MB", 75 * MiB, "Fastest for English", GgmlType.TinyEn, "ggml-tiny.en.bin"),
        new("Base", "Base", "142 MB", 142 * MiB, "Balanced, for most computers", GgmlType.Base, "ggml-base.bin"),
        new("BaseEn", "Base English", "142 MB", 142 * MiB, "Balanced, for English", GgmlType.BaseEn, "ggml-base.en.bin"),
        new("Small", "Small", "466 MB", 466 * MiB, "More accurate, slower", GgmlType.Small, "ggml-small.bin"),
        new("SmallEn", "Small English", "466 MB", 466 * MiB, "More accurate for English, slower", GgmlType.SmallEn, "ggml-small.en.bin"),
        new("Medium", "Medium", "1.5 GB", 1536 * MiB, "High accuracy, uses a lot of memory", GgmlType.Medium, "ggml-medium.bin"),
        new("MediumEn", "Medium English", "1.5 GB", 1536 * MiB, "High accuracy for English, uses a lot of memory", GgmlType.MediumEn, "ggml-medium.en.bin"),
        new("LargeV3Turbo", "Large v3 Turbo", "1.5 GB", 1536 * MiB, "Fast for its high accuracy", GgmlType.LargeV3Turbo, "ggml-large-v3-turbo.bin"),
        new("LargeV3", "Large v3", "2.9 GB", 2970 * MiB, "Highest accuracy, slowest", GgmlType.LargeV3, "ggml-large-v3.bin"),
    ];

    public static StudioCaptionModel Default { get; } = All.First(static model => model.Id == "Base");

    /// <summary>The model with that id, or null. Case does not matter.</summary>
    public static StudioCaptionModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The Whisper models on this computer. A model is downloaded once, under a name of its own, and
/// gets its real name only when it is complete, so that a download that stops leaves nothing
/// that looks like a model.
/// </summary>
public sealed class StudioCaptionModelStore
{
    private const string PartialExtension = ".download";
    private const int BufferSize = 81920;

    private readonly Func<StudioCaptionModel, CancellationToken, Task<Stream>> _download;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public StudioCaptionModelStore(HttpClient httpClient)
        : this(DefaultDirectory(), DownloadFrom(new WhisperGgmlDownloader(httpClient)))
    {
    }

    /// <summary>Keeps the models in a folder of the caller's choosing.</summary>
    public StudioCaptionModelStore(string directory, HttpClient httpClient)
        : this(directory, DownloadFrom(new WhisperGgmlDownloader(httpClient)))
    {
    }

    /// <summary>For whoever has its own way of getting a model's bytes. The unit tests do.</summary>
    internal StudioCaptionModelStore(string directory, Func<StudioCaptionModel, CancellationToken, Task<Stream>> download)
    {
        ModelsDirectory = directory;
        _download = download;
    }

    public string ModelsDirectory { get; }

    public static string DefaultDirectory() =>
        Path.Combine(ClipsLibraryPaths.LocalDataDirectory(), "TinyClips", "Models");

    public string GetPath(StudioCaptionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Path.Combine(ModelsDirectory, model.FileName);
    }

    public bool IsInstalled(StudioCaptionModel model) => File.Exists(GetPath(model));

    /// <summary>The models on this computer, in the order of the catalog.</summary>
    public IReadOnlyList<StudioCaptionModel> GetInstalled() => StudioCaptionModels.All.Where(IsInstalled).ToArray();

    /// <summary>How much room the models on this computer take, in bytes.</summary>
    public long GetInstalledBytes() => GetInstalled().Sum(model => new FileInfo(GetPath(model)).Length);

    /// <summary>Deletes every model, and whatever a download that was stopped left behind.</summary>
    /// <returns>How many models were deleted.</returns>
    public int DeleteAll()
    {
        var installed = GetInstalled();
        foreach (var model in installed)
        {
            File.Delete(GetPath(model));
        }

        if (Directory.Exists(ModelsDirectory))
        {
            foreach (var partial in Directory.EnumerateFiles(ModelsDirectory, "*" + PartialExtension))
            {
                File.Delete(partial);
            }
        }

        return installed.Count;
    }

    /// <summary>The path of the model, after downloading it if it is not here yet.</summary>
    /// <param name="progress">How far the download is, from 0 to 1. Not called for a model that is here.</param>
    public async Task<string> EnsureAsync(StudioCaptionModel model, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var path = GetPath(model);
        if (File.Exists(path))
        {
            return path;
        }

        // Two editors that ask for one model wait for one download.
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                await DownloadAsync(model, path, progress, cancellationToken).ConfigureAwait(false);
            }

            return path;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static Func<StudioCaptionModel, CancellationToken, Task<Stream>> DownloadFrom(WhisperGgmlDownloader downloader) =>
        (model, cancellationToken) => downloader.GetGgmlModelAsync(model.Type, cancellationToken: cancellationToken);

    private async Task DownloadAsync(StudioCaptionModel model, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ModelsDirectory);
        var partial = path + "." + Guid.NewGuid().ToString("N") + PartialExtension;
        try
        {
            await using (var source = await _download(model, cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                long written = 0;
                var reported = -1;
                int read;
                progress?.Report(0);
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;

                    // The size is about right, so it stops short of done until the file is.
                    var percent = (int)Math.Min(99, written * 100 / Math.Max(1, model.ApproximateBytes));
                    if (percent != reported)
                    {
                        reported = percent;
                        progress?.Report(percent / 100.0);
                    }
                }
            }

            File.Move(partial, path, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // What is left is deleted with the models, and is never taken for one.
            }
        }
    }
}
