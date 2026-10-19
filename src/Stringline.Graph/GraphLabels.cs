using System.Globalization;
using SkiaSharp;

namespace Stringline.Graph;

/// <summary>
/// The location labels beside the plot (#3): how much room they need, how a name too long for it is shortened, and the
/// hover over a label, which gives its full name and TIPLOC. Horizontal graphs put the labels in the left margin,
/// vertical ones (rotated) in the top margin; both use the same room.
/// </summary>
public static class GraphLabels
{
    /// <summary>The least room for labels, in pixels, so a corridor of short names still has a tidy margin.</summary>
    public const double MinRoom = 90;

    /// <summary>The most room for labels; a longer name is shortened with an ellipsis.</summary>
    public const double MaxRoom = 260;

    /// <summary>Pixels between a label and the plot (the tick mark is in this gap).</summary>
    public const double Gap = 6;

    /// <summary>Pixels kept clear at the outer edge, beyond the longest label.</summary>
    public const double Edge = 6;

    /// <summary>Height of the title band at the top of the surface; vertical labels stop under it.</summary>
    public const double TitleBand = 24;

    private const string Ellipsis = "…";

    /// <summary>Pixels either side of a label's line within which the hover finds it.</summary>
    private const double HoverReach = 9;

    /// <summary>
    /// The room the widest label needs: its name in the station (bold) or location font, or its TIPLOC, whichever is
    /// longer, held between <see cref="MinRoom"/> and <see cref="MaxRoom"/>.
    /// </summary>
    public static double RoomFor(IEnumerable<GraphLocation> locations, GraphTheme? theme = null)
    {
        ArgumentNullException.ThrowIfNull(locations);
        theme ??= GraphTheme.Light;
        using var regular = GraphFonts.Create(GraphFonts.Regular, theme.LocationFontSize);
        using var bold = GraphFonts.Create(GraphFonts.Bold, theme.LocationFontSize);
        using var tiploc = GraphFonts.Create(GraphFonts.Regular, theme.TiplocFontSize);
        var widest = 0f;
        foreach (var location in locations)
        {
            widest = Math.Max(widest, (location.IsStation ? bold : regular).MeasureText(location.Name));
            widest = Math.Max(widest, tiploc.MeasureText(location.Tiploc.Code));
        }

        return Math.Clamp(Math.Ceiling(widest), MinRoom, MaxRoom);
    }

    /// <summary>The margins for labels needing <paramref name="room"/> pixels, in either orientation.</summary>
    public static GraphMargins MarginsFor(GraphOrientation orientation, double room) =>
        orientation == GraphOrientation.Vertical
            ? GraphMargins.DefaultVertical with { Top = TitleBand + Edge + room + Gap }
            : GraphMargins.Default with { Left = Edge + room + Gap };

    /// <summary>The margins that fit these labels, with the <see cref="RoomFor"/> them.</summary>
    public static GraphMargins MarginsFor(GraphOrientation orientation, IEnumerable<GraphLocation> locations, GraphTheme? theme = null) =>
        MarginsFor(orientation, RoomFor(locations, theme));

    /// <summary>The room a viewport's margin leaves for labels.</summary>
    public static double RoomIn(GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return Math.Max(0, viewport.IsVertical ? viewport.PlotTop - Gap - TitleBand - Edge : viewport.PlotLeft - Gap - Edge);
    }

    /// <summary>The text, shortened with an ellipsis to fit <paramref name="width"/> pixels if it is longer.</summary>
    public static string Fit(string text, SKFont font, float width)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (font.MeasureText(text) <= width)
        {
            return text;
        }

        // The longest start (whole text elements, so no split surrogate pairs) that fits with the ellipsis.
        var starts = StringInfo.ParseCombiningCharacters(text);
        int low = 0, high = starts.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (font.MeasureText(string.Concat(text.AsSpan(0, starts[mid]).TrimEnd(), Ellipsis)) <= width)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low == 0 ? Ellipsis : string.Concat(text.AsSpan(0, starts[low]).TrimEnd(), Ellipsis);
    }

    /// <summary>The location whose label is under a point in the label margin, if any.</summary>
    public static GraphLocation? LocationAt(GraphScene scene, GraphViewport viewport, ScreenPoint at)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        var inMargin = viewport.IsVertical
            ? at.Y >= TitleBand && at.Y < viewport.PlotTop && at.X >= viewport.PlotLeft - HoverReach && at.X <= viewport.PlotRight + HoverReach
            : at.X >= 0 && at.X < viewport.PlotLeft && at.Y >= viewport.PlotTop - HoverReach && at.Y <= viewport.PlotBottom + HoverReach;
        if (!inMargin)
        {
            return null;
        }

        // A label's name sits just before its line along the axis and its TIPLOC just after, so look a little after.
        var along = viewport.AxisPixelOf(at);
        GraphLocation? best = null;
        var bestDistance = HoverReach;
        foreach (var location in scene.Locations)
        {
            var distance = Math.Abs(along - (viewport.AxisToPixel(location.Axis) + 3));
            if (distance <= bestDistance)
            {
                (best, bestDistance) = (location, distance);
            }
        }

        return best;
    }

    /// <summary>
    /// The hover over a label: the full name, the TIPLOC and platforms, and a hint that the profile can shorten the name
    /// for good.
    /// </summary>
    public static HoverInfo Describe(GraphLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        var lines = new List<HoverLine> { new("TIPLOC", location.Tiploc.Code) };
        if (location.Platforms > 0)
        {
            lines.Add(new HoverLine("Platforms", location.Platforms.ToString(CultureInfo.InvariantCulture)));
        }

        lines.Add(new HoverLine("Tip", "Tools ▸ Edit profile can give it a shorter name"));
        return new HoverInfo(location.Name, lines);
    }
}
