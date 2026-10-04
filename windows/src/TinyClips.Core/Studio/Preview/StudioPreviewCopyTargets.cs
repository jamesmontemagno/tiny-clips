namespace TinyClips.Core.Studio.Preview;

/// <summary>The size of the texture a clip's player copies its frames into.</summary>
/// <param name="Scale">The fraction of the source's size this stands for.</param>
internal readonly record struct StudioPreviewCopyTargetSize(int Width, int Height, double Scale);

/// <summary>
/// Chooses how large the texture is that a player copies its frames into. A copy costs time in
/// proportion to the target, so the target is no larger than the clip appears on the surface and
/// never larger than the source. Sizes come from a ladder of steps a factor of √2 apart, and a
/// target is kept while it is large enough and less than twice too large, so that dragging a size
/// slider or a window edge does not recreate the texture on every change.
/// </summary>
internal static class StudioPreviewCopyTargets
{
    /// <summary>No target is smaller than this on its long side (unless the source is).</summary>
    public const int MinimumLongSide = 64;

    private const double Slack = 1e-6;

    /// <summary>
    /// The fraction of the source's size at which the clip is drawn without being enlarged, from
    /// the rectangle it fills on the surface and the part of the source that is shown there. 0 when
    /// the clip is not drawn; never more than 1.
    /// </summary>
    public static double NeededScale(int sourceWidth, int sourceHeight, StudioFrameRect shown, StudioFrameRect crop)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return 0;
        }

        var horizontal = shown.Width / (crop.Width * sourceWidth);
        var vertical = shown.Height / (crop.Height * sourceHeight);
        var scale = Math.Max(
            double.IsFinite(horizontal) ? horizontal : 0,
            double.IsFinite(vertical) ? vertical : 0);
        return Math.Clamp(scale, 0, 1);
    }

    /// <summary>The smallest scale ever used for a source of this size.</summary>
    public static double MinimumScale(int sourceWidth, int sourceHeight) =>
        Math.Min(1, MinimumLongSide / (double)Math.Max(1, Math.Max(sourceWidth, sourceHeight)));

    /// <summary>
    /// The target for a clip that needs <paramref name="neededScale"/>. Returns
    /// <paramref name="current"/> unchanged while it still fits.
    /// </summary>
    public static StudioPreviewCopyTargetSize Choose(int sourceWidth, int sourceHeight, double neededScale, StudioPreviewCopyTargetSize? current)
    {
        sourceWidth = Math.Max(1, sourceWidth);
        sourceHeight = Math.Max(1, sourceHeight);
        var minimum = MinimumScale(sourceWidth, sourceHeight);
        var needed = double.IsFinite(neededScale) ? Math.Clamp(neededScale, minimum, 1) : 1;

        if (current is { } kept &&
            kept.Width <= sourceWidth &&
            kept.Height <= sourceHeight &&
            kept.Scale >= needed - Slack &&
            kept.Scale <= (needed * 2) + Slack)
        {
            return kept;
        }

        var scale = LadderScale(needed);
        return new StudioPreviewCopyTargetSize(Dimension(sourceWidth, scale), Dimension(sourceHeight, scale), scale);
    }

    /// <summary>The smallest ladder step, 2^(-k/2), that is not below <paramref name="needed"/>.</summary>
    internal static double LadderScale(double needed)
    {
        if (needed >= 1)
        {
            return 1;
        }

        var step = (int)Math.Floor((-2 * Math.Log2(needed)) + Slack);
        return Math.Pow(2, -step / 2.0);
    }

    // Even, because video processors prefer it, and never beyond the source.
    private static int Dimension(int source, double scale)
    {
        var size = (int)Math.Ceiling((source * scale) - Slack);
        size += size & 1;
        return Math.Clamp(size, Math.Min(2, source), source);
    }
}
