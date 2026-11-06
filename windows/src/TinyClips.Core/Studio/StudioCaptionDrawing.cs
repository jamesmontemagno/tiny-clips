using System.Runtime.CompilerServices;

namespace TinyClips.Core.Studio;

/// <summary>A colour of a drawn caption. An opacity of 0 means the part it paints is not drawn.</summary>
public readonly record struct StudioCaptionPaint(byte Red, byte Green, byte Blue, double Opacity);

/// <summary>
/// How a caption looks on a canvas of a given size, in that canvas's pixels: section 15.4 of the
/// project format. How the text is laid out is the renderer's business.
/// </summary>
/// <param name="TextHeight">The size of the font.</param>
/// <param name="Margin">How far the text stays from the edge of the canvas it is placed at.</param>
/// <param name="LongestLine">The width at which a line wraps.</param>
/// <param name="IsBold">Whether the text is bold.</param>
/// <param name="Text">The colour of the letters.</param>
/// <param name="Outline">The colour of the outline.</param>
/// <param name="OutlineWidth">How far the outline reaches outside the letters. 0 for none.</param>
/// <param name="Shadow">The colour of the shadow.</param>
/// <param name="ShadowOffset">How far the shadow lies down and to the right. 0 for none.</param>
/// <param name="Panel">The colour of the panel behind the lines.</param>
/// <param name="PanelReach">How far the panel reaches past the lines. 0 for none.</param>
public readonly record struct StudioCaptionStyle(
    double TextHeight,
    double Margin,
    double LongestLine,
    bool IsBold,
    StudioCaptionPaint Text,
    StudioCaptionPaint Outline,
    double OutlineWidth,
    StudioCaptionPaint Shadow,
    double ShadowOffset,
    StudioCaptionPaint Panel,
    double PanelReach)
{
    /// <summary>The canvas's short side is this many units.</summary>
    public const double UnitsInShortSide = 288;

    private const double MarginUnits = 12;
    private const double LongestLineShare = 0.9;

    private static readonly StudioCaptionPaint White = new(0xFF, 0xFF, 0xFF, 1);
    private static readonly StudioCaptionPaint Black = new(0, 0, 0, 1);
    private static readonly StudioCaptionPaint HalfBlack = new(0, 0, 0, 0.5);
    private static readonly StudioCaptionPaint Gold = new(0xFF, 0xD7, 0x00, 1);
    private static readonly StudioCaptionPaint OffWhite = new(0xF5, 0xF5, 0xF5, 1);

    /// <summary>How far anything is drawn outside the text's own box.</summary>
    public double Reach => Math.Max(PanelReach, OutlineWidth + ShadowOffset);

    /// <summary>The look of <paramref name="overlay"/> on a canvas of this size.</summary>
    public static StudioCaptionStyle For(StudioCaptionOverlay overlay, double canvasWidth, double canvasHeight)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var unit = Math.Min(canvasWidth, canvasHeight) / UnitsInShortSide;
        var height = overlay.Size switch
        {
            StudioCaptionSize.Small => 14,
            StudioCaptionSize.Large => 24,
            _ => 18,
        } * unit;
        var margin = MarginUnits * unit;
        var longest = canvasWidth * LongestLineShare;
        return overlay.Appearance switch
        {
            StudioCaptionAppearance.ContrastPanel => new(height, margin, longest, false, White, default, 0, default, 0, new(0, 0, 0, 0.6), 4 * unit),
            StudioCaptionAppearance.BoldImpact => new(height, margin, longest, true, Gold, Black, 2.4 * unit, Black, 1.2 * unit, default, 0),
            StudioCaptionAppearance.CleanShadow => new(height, margin, longest, false, OffWhite, HalfBlack, 0.6 * unit, HalfBlack, 1.5 * unit, default, 0),
            _ => new(height, margin, longest, false, White, Black, 1.2 * unit, default, 0, default, 0),
        };
    }

    /// <summary>
    /// Where the top of the first line goes, for text <paramref name="textHeight"/> tall. Text too
    /// tall for the canvas keeps its top on the canvas, because a caption starts at its first line.
    /// </summary>
    public double TopOfText(StudioCaptionPosition position, double textHeight, double canvasHeight) => position == StudioCaptionPosition.Top
        ? Margin
        : Math.Max(0, canvasHeight - Margin - textHeight);
}

/// <summary>
/// The captions of one project, worked out once. A project is never changed, only replaced, and a
/// renderer asks for the caption of every frame it draws, so it is kept with the project.
/// </summary>
public sealed class StudioCaptionTrack
{
    private static readonly ConditionalWeakTable<StudioProject, StudioCaptionTrack> Tracks = [];

    private readonly StudioResolvedCaption[] _captions;
    private readonly StudioTimeMap _map;

    private StudioCaptionTrack(StudioProject project)
    {
        _map = StudioTimeMap.FromProject(project);
        _captions = StudioCaptionResolver.Resolve(project.Captions, _map);
    }

    public static StudioCaptionTrack For(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Tracks.GetValue(project, static key => new StudioCaptionTrack(key));
    }

    /// <summary>
    /// What is drawn into the frame at <paramref name="sourceTime"/>: nothing when the project does
    /// not draw its captions, or no caption is showing then. A renderer works in source time, and
    /// a frame of the video has one, so this agrees with the frame's place in the video.
    /// </summary>
    public static string? DrawnTextAt(StudioProject project, double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Overlays.Captions.Enabled ? For(project).TextAtSourceTime(sourceTime) : null;
    }

    /// <summary>The text showing at <paramref name="sourceTime"/>, or null.</summary>
    public string? TextAtSourceTime(double sourceTime)
    {
        if (_captions.Length == 0)
        {
            return null;
        }

        var index = StudioCaptionResolver.IndexAt(_captions, _map.SourceToOutput(sourceTime));
        return index < 0 ? null : _captions[index].Text;
    }
}
