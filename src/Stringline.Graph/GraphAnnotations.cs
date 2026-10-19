using System.Globalization;
using SkiaSharp;
using Stringline.Core;
using Stringline.Network;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>
/// A block or speed restriction on the graph (<c>05-…md</c> §2): its time range across the corridor between its two
/// locations. A disruption at one location draws as a narrow band along it.
/// </summary>
/// <param name="AxisFrom">The axis position of the end nearer the corridor's Up end.</param>
/// <param name="AxisTo">The axis position of the other end (equal to <paramref name="AxisFrom"/> at one location).</param>
/// <param name="Label">The short text drawn in its corner, e.g. "Block · Down" or "SR ×1.5 +30 s".</param>
/// <param name="IsDraft">Being drawn with B+drag: not yet in the plan.</param>
public sealed record GraphDisruption(Disruption Disruption, double AxisFrom, double AxisTo, string Label, bool IsDraft = false)
{
    public string Id => Disruption.Id;

    public DisruptionKind Kind => Disruption.Kind;

    public double TimeStart => Disruption.Start.Seconds;

    public double TimeEnd => Disruption.End.Seconds;

    /// <summary>A disruption on the graph, worded for its corner label.</summary>
    public static GraphDisruption For(Disruption disruption, double axisFrom, double axisTo, bool isDraft = false)
    {
        ArgumentNullException.ThrowIfNull(disruption);
        return new(disruption, Math.Min(axisFrom, axisTo), Math.Max(axisFrom, axisTo), LabelOf(disruption), isDraft);
    }

    /// <summary>"Block", "SR ×1.5 +30 s", with " · {track}" when it is on one track.</summary>
    public static string LabelOf(Disruption disruption)
    {
        ArgumentNullException.ThrowIfNull(disruption);
        var text = disruption.Kind == DisruptionKind.Block ? "Block" : "SR" + RunningText(disruption);
        return disruption.Track is { } track ? $"{text} · {track}" : text;
    }

    /// <summary>" ×1.5 +30 s": how a speed restriction lengthens the minimum run (empty if it does not).</summary>
    internal static string RunningText(Disruption d) =>
        (d.RunTimeFactor > 1 ? string.Create(CultureInfo.InvariantCulture, $" ×{d.RunTimeFactor:0.##}") : string.Empty) +
        (d.AddedSeconds > 0 ? " +" + PlanText.Duration(d.AddedSeconds) : string.Empty);
}

/// <summary>A note pinned to the graph at a time and location (DIS-3).</summary>
/// <param name="Label">The first line of the text, shortened, as drawn.</param>
public sealed record GraphNote(PlanNote Note, double Axis, string Label)
{
    public string Id => Note.Id;

    public double Time => Note.Time.Seconds;
}

/// <summary>A rectangle in screen pixels.</summary>
public readonly record struct ScreenRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;

    public bool Contains(ScreenPoint p, double slop = 0) =>
        p.X >= Left - slop && p.X <= Right + slop && p.Y >= Top - slop && p.Y <= Bottom + slop;
}

/// <summary>Corridor locations and a time range picked on the graph: for a new block or restriction (B+drag or right-click).</summary>
/// <param name="From">The location nearer the corridor's Up end.</param>
public readonly record struct GraphSpan(GraphLocation From, GraphLocation To, SimTime Start, SimTime End);

/// <summary>
/// Disruptions and notes on the graph (S13): building them for a scene, where they draw, what is under the mouse, and
/// the span a B+drag or right-click picks. Pure, so the shell only adapts the mouse.
/// </summary>
public static class GraphAnnotations
{
    /// <summary>The narrowest a disruption at one location draws across the corridor, in pixels.</summary>
    public const double MinThickness = 8;

    /// <summary>How close to a location line, in pixels, a point counts as on it.</summary>
    public const double LocationSlop = 4;

    /// <summary>The radius of a note's marker, in pixels.</summary>
    public const float NoteMarkerRadius = 4;

    /// <summary>The font size of note and disruption labels.</summary>
    public const float LabelFontSize = 10;

    /// <summary>The longest note label drawn on the graph; the tooltip has the whole text.</summary>
    public const int NoteLabelLength = 32;

    /// <summary>The plan's disruptions and notes whose locations are on the corridor.</summary>
    public static (IReadOnlyList<GraphDisruption> Disruptions, IReadOnlyList<GraphNote> Notes) For(Corridor corridor, WorkingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        ArgumentNullException.ThrowIfNull(plan);
        var disruptions = new List<GraphDisruption>();
        foreach (var d in plan.Disruptions)
        {
            if (corridor.PositionOf(d.From) is { } from && corridor.PositionOf(d.To) is { } to)
            {
                disruptions.Add(GraphDisruption.For(d, from, to));
            }
        }

        var notes = new List<GraphNote>();
        foreach (var n in plan.Notes)
        {
            if (corridor.PositionOf(n.Location) is { } axis)
            {
                notes.Add(new GraphNote(n, axis, n.Summary(NoteLabelLength)));
            }
        }

        return (disruptions, notes);
    }

    /// <summary>Where a disruption draws: at least <see cref="MinThickness"/> across the corridor.</summary>
    public static ScreenRect RectOf(GraphDisruption disruption, GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(disruption);
        ArgumentNullException.ThrowIfNull(viewport);
        double t0 = viewport.TimeToPixel(disruption.TimeStart), t1 = viewport.TimeToPixel(disruption.TimeEnd);
        double a0 = viewport.AxisToPixel(disruption.AxisFrom), a1 = viewport.AxisToPixel(disruption.AxisTo);
        if (a1 - a0 < MinThickness)
        {
            var middle = (a0 + a1) / 2;
            (a0, a1) = (middle - (MinThickness / 2), middle + (MinThickness / 2));
        }

        return viewport.IsVertical ? new ScreenRect(a0, t0, a1, t1) : new ScreenRect(t0, a0, t1, a1);
    }

    /// <summary>The marker of a note: the point it is pinned to.</summary>
    public static ScreenPoint MarkerOf(GraphNote note, GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(viewport);
        return viewport.ToScreen(note.Time, note.Axis);
    }

    /// <summary>The box of a note's label, just above and right of its marker.</summary>
    public static ScreenRect LabelOf(GraphNote note, GraphViewport viewport)
    {
        var marker = MarkerOf(note, viewport);
        using var font = GraphFonts.Create(GraphFonts.Regular, LabelFontSize);
        var width = font.MeasureText(note.Label) + 8;
        var left = marker.X + NoteMarkerRadius + 2;
        var bottom = marker.Y - 2;
        return new ScreenRect(left, bottom - LabelFontSize - 6, left + width, bottom);
    }

    /// <summary>The note under a point (its marker or label), the last drawn first; null if none.</summary>
    public static GraphNote? NoteAt(GraphScene scene, GraphViewport viewport, ScreenPoint point)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        for (var i = scene.Notes.Count - 1; i >= 0; i--)
        {
            var note = scene.Notes[i];
            var marker = MarkerOf(note, viewport);
            if (Math.Abs(marker.X - point.X) <= NoteMarkerRadius + 3 && Math.Abs(marker.Y - point.Y) <= NoteMarkerRadius + 3)
            {
                return note;
            }

            if (viewport.Overlaps(note.Time, note.Time, note.Axis, note.Axis) && LabelOf(note, viewport).Contains(point))
            {
                return note;
            }
        }

        return null;
    }

    /// <summary>The disruption under a point, the last drawn first; null if none.</summary>
    public static GraphDisruption? DisruptionAt(GraphScene scene, GraphViewport viewport, ScreenPoint point)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        for (var i = scene.Disruptions.Count - 1; i >= 0; i--)
        {
            if (RectOf(scene.Disruptions[i], viewport).Contains(point))
            {
                return scene.Disruptions[i];
            }
        }

        return null;
    }

    /// <summary>The corridor location whose line is nearest a point.</summary>
    public static GraphLocation? NearestLocation(GraphScene scene, GraphViewport viewport, ScreenPoint point)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        var at = viewport.AxisPixelOf(point);
        return scene.Locations.MinBy(l => Math.Abs(viewport.AxisToPixel(l.Axis) - at));
    }

    /// <summary>
    /// The span between two points (a B+drag's corners, or one point for a right-click): the locations either side,
    /// widened out to the location lines around it (a point within <see cref="LocationSlop"/> of a line is on it, so
    /// a span along one line is that location alone), and the times snapped outwards to <paramref name="snapSeconds"/>,
    /// at least one step long. Null for a corridor with no locations.
    /// </summary>
    public static GraphSpan? SpanBetween(GraphScene scene, GraphViewport viewport, ScreenPoint a, ScreenPoint b, int snapSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapSeconds, 1);
        if (scene.Locations.Count == 0)
        {
            return null;
        }

        var locations = scene.Locations.OrderBy(l => l.Axis).ToList();
        double pa = viewport.AxisPixelOf(a), pb = viewport.AxisPixelOf(b);
        // Pixels grow with the axis in both orientations, so the lower pixel is the Up end.
        double lo = Math.Min(pa, pb), hi = Math.Max(pa, pb);
        var from = locations.LastOrDefault(l => viewport.AxisToPixel(l.Axis) <= lo + LocationSlop) ?? locations[0];
        var to = locations.FirstOrDefault(l => viewport.AxisToPixel(l.Axis) >= hi - LocationSlop) ?? locations[^1];
        if (to.Axis < from.Axis)
        {
            to = from;
        }

        double ta = viewport.PixelToTime(viewport.TimePixelOf(a)), tb = viewport.PixelToTime(viewport.TimePixelOf(b));
        var start = Math.Max(0, (int)Math.Floor(Math.Min(ta, tb) / snapSeconds) * snapSeconds);
        var end = (int)Math.Ceiling(Math.Max(ta, tb) / snapSeconds) * snapSeconds;
        if (end <= start)
        {
            end = start + snapSeconds;
        }

        return new GraphSpan(from, to, new SimTime(start), new SimTime(end));
    }

    /// <summary>
    /// The tooltip for a disruption (#12): its kind, where, when, track, its effect (held, or the slower running), its
    /// note, and that a right-click edits it.
    /// </summary>
    public static HoverInfo Describe(GraphDisruption disruption, GraphScene scene)
    {
        ArgumentNullException.ThrowIfNull(disruption);
        ArgumentNullException.ThrowIfNull(scene);
        var d = disruption.Disruption;
        var lines = new List<HoverLine>
        {
            new("Where", d.From == d.To ? NameOf(scene, d.From) : $"{NameOf(scene, d.From)} – {NameOf(scene, d.To)}"),
            new("When", $"{d.Start} – {d.End}"),
            new("Track", TrackChoice.Of(d.Track, d.Direction).Label),
        };
        lines.Add(d.Kind == DisruptionKind.SpeedRestriction
            ? new HoverLine("Running", GraphDisruption.RunningText(d).Trim() is { Length: > 0 } running ? running : "no slower")
            : new HoverLine("Effect", "trains running into it are held until it ends"));

        if (!string.IsNullOrWhiteSpace(d.Note))
        {
            lines.Add(new HoverLine("Note", d.Note.Trim()));
        }

        lines.Add(new HoverLine("Tip", "Right-click to edit or remove it"));
        return new HoverInfo($"{(d.Kind == DisruptionKind.Block ? "Block" : "Speed restriction")} {d.Id}", lines);
    }

    /// <summary>The tooltip for a note: where it is pinned and its whole text.</summary>
    public static HoverInfo Describe(GraphNote note, GraphScene scene)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(scene);
        return new HoverInfo($"Note {note.Id}", [new("At", $"{NameOf(scene, note.Note.Location)} {note.Note.Time}"), new("Note", note.Note.Text.Trim())]);
    }

    private static string NameOf(GraphScene scene, Tiploc tiploc) => scene.Locations.FirstOrDefault(l => l.Tiploc == tiploc)?.Name ?? tiploc.Code;

    internal static SKRect ToSk(this ScreenRect r) => new((float)r.Left, (float)r.Top, (float)r.Right, (float)r.Bottom);
}
