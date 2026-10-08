namespace TinyClips.Core.Studio;

/// <summary>
/// Zooms worked out from the clicks of a recording, as section 8 of the project format defines
/// them. A pure function of the project and its events: the editor offers the result, and the
/// user keeps, changes or deletes it.
/// </summary>
public static class StudioZoomSuggestions
{
    private const double Scale = 2;

    /// <summary>How long before a click its zoom starts.</summary>
    private const double Lead = 0.6;

    /// <summary>How long after the last click a zoom stays.</summary>
    private const double Hold = 1.5;

    /// <summary>Clicks no further apart than this belong together.</summary>
    private const double Join = 4;

    /// <summary>The part of a zoom's window, from each edge, in which a click counts as somewhere else.</summary>
    private const double Inset = 0.15;

    private const double Shortest = 0.3;
    private const double Ease = 0.5;

    /// <summary>
    /// The suggestions for <paramref name="project"/>, in time order, each with
    /// <see cref="StudioZoomOrigin.Auto"/>. A suggestion that overlaps a zoom the user made is left
    /// out. Empty when the recording has no clicks.
    /// </summary>
    public static StudioZoom[] Suggest(StudioProject project, StudioEvents? events)
    {
        ArgumentNullException.ThrowIfNull(project);
        var crop = StudioCanvasMath.ValidCropOrNull(project.Screen.Crop) ?? new StudioRect(0, 0, 1, 1);
        var baseRect = new StudioFrameRect(crop.X, crop.Y, crop.Width, crop.Height);
        var duration = Math.Max(0, project.Sources.Screen.Duration);

        // OrderBy keeps clicks with the same time in the order they are stored.
        var clicks = (events?.Clicks ?? [])
            .Where(static click => click is not null)
            .OrderBy(static click => click.T);

        var groups = new List<Group>();
        foreach (var click in clicks)
        {
            if (click.T < 0 || click.T > duration || !Contains(baseRect, click.X, click.Y))
            {
                continue;
            }

            if (groups.Count > 0 && click.T - groups[^1].Last <= Join)
            {
                var current = groups[^1];
                var window = StudioZoomMath.HeldWindow(baseRect, Scale, current.X, current.Y);
                var inner = new StudioFrameRect(
                    window.X + (Inset * window.Width),
                    window.Y + (Inset * window.Height),
                    (1 - (2 * Inset)) * window.Width,
                    (1 - (2 * Inset)) * window.Height);
                if (Contains(inner, click.X, click.Y))
                {
                    // The same place: the zoom stays.
                    groups[^1] = current with { Last = click.T };
                }
                else
                {
                    // Another place, soon after. The next zoom starts on the very number this one
                    // ends on, which is what chains the two.
                    var end = Math.Max(click.T - Lead, (current.Last + click.T) / 2);
                    groups[^1] = current with { End = end };
                    groups.Add(new Group(end, click.X, click.Y, click.T, null));
                }
            }
            else
            {
                groups.Add(new Group(Math.Max(0, click.T - Lead), click.X, click.Y, click.T, null));
            }
        }

        var suggestions = new List<StudioZoom>(groups.Count);
        foreach (var group in groups)
        {
            var end = group.End ?? Math.Min(duration, group.Last + Hold);
            if (end - group.Start >= Shortest)
            {
                suggestions.Add(new StudioZoom
                {
                    Start = group.Start,
                    End = end,
                    Scale = Scale,
                    Focus = new StudioZoomFocus { Mode = StudioZoomFocusMode.Point, X = group.X, Y = group.Y },
                    EaseIn = Ease,
                    EaseOut = Ease,
                    Origin = StudioZoomOrigin.Auto,
                });
            }
        }

        var manual = StudioLayoutResolver.NormalizeZooms(
            (project.Zooms ?? []).Where(static zoom => zoom is not null && zoom.Origin != StudioZoomOrigin.Auto).ToArray());
        if (manual.Length == 0)
        {
            return [.. suggestions];
        }

        return suggestions
            .Where(suggestion => !manual.Any(zoom => suggestion.Start < zoom.End && zoom.Start < suggestion.End))
            .ToArray();
    }

    private static bool Contains(StudioFrameRect rect, double x, double y) =>
        x >= rect.X && x <= rect.X + rect.Width && y >= rect.Y && y <= rect.Y + rect.Height;

    private readonly record struct Group(double Start, double X, double Y, double Last, double? End);
}