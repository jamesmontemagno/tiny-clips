namespace TinyClips.Core.Capture;

/// <summary>
/// Guardrails for a scrolling (panorama) capture. Mirrors the macOS <c>PanoramaCaptureLimits</c>.
/// </summary>
/// <param name="MaxFrames">Maximum number of stitched frames before the capture auto-stops.</param>
/// <param name="MaxOutputHeight">Maximum stitched height in pixels before the capture auto-stops.</param>
/// <param name="MaxMemoryBytes">
/// Peak memory budget: the output buffer plus the copy made for the final image, plus the
/// retained and incoming frames.
/// </param>
public sealed record PanoramaCaptureLimits(
    int MaxFrames,
    int MaxOutputHeight,
    long MaxMemoryBytes)
{
    /// <summary>
    /// Largest texture dimension Win2D / XAML can render; stitched images taller than this cannot
    /// be opened in the screenshot editor.
    /// </summary>
    public const int EditorMaxOutputHeight = 16_384;

    public static PanoramaCaptureLimits Default { get; } = new(
        MaxFrames: 600,
        MaxOutputHeight: 50_000,
        MaxMemoryBytes: 1_200_000_000);

    /// <summary>
    /// Limits for a capture whose result will be opened in the screenshot editor, which caps the
    /// output height at <see cref="EditorMaxOutputHeight"/>.
    /// </summary>
    public static PanoramaCaptureLimits ForEditor { get; } = Default with
    {
        MaxOutputHeight = EditorMaxOutputHeight,
    };
}
