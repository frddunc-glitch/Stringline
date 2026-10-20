using System.Globalization;
using Stringline.Core;
using Stringline.Network;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>
/// A conflict on the graph (<c>05-…md</c> §2): a red ring where the train that must give way meets it, and a red
/// highlight over the section (or platform's location) for the time the clash covers.
/// </summary>
/// <param name="Axis">The axis position of <see cref="Conflict.Where"/>, where the ring is.</param>
/// <param name="AxisFrom">The highlight's end nearer the corridor's Up end.</param>
/// <param name="AxisTo">The highlight's other end (equal to <paramref name="AxisFrom"/> at one location).</param>
public sealed record GraphConflict(Conflict Conflict, double Axis, double AxisFrom, double AxisTo)
{
    public string Id => Conflict.Id;

    public double Time => Conflict.When.Seconds;

    public double TimeStart => Conflict.Start.Seconds;

    public double TimeEnd => Math.Max(Conflict.End.Seconds, Conflict.Start.Seconds);
}

/// <summary>Conflicts on the graph (S14): building them for a scene, where they draw, what is under the mouse, the tooltip.</summary>
public static class GraphConflicts
{
    /// <summary>The radius of a conflict's ring, in pixels.</summary>
    public const float RingRadius = 7;

    /// <summary>The narrowest a highlight draws across the corridor, in pixels.</summary>
    public const double MinThickness = 8;

    /// <summary>
    /// The conflicts that meet this corridor at their <see cref="Conflict.Where"/>; the highlight runs to
    /// <see cref="Conflict.To"/> if that is on the corridor too, else it is at the one location.
    /// </summary>
    public static IReadOnlyList<GraphConflict> For(Corridor corridor, ConflictSet conflicts)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        ArgumentNullException.ThrowIfNull(conflicts);
        var list = new List<GraphConflict>();
        foreach (var c in conflicts.All)
        {
            if (corridor.PositionOf(c.Where) is not { } at)
            {
                continue;
            }

            var to = c.To is { } far && corridor.PositionOf(far) is { } other ? other : at;
            list.Add(new GraphConflict(c, at, Math.Min(at, to), Math.Max(at, to)));
        }

        return list;
    }

    /// <summary>True if the corridor shows the conflict (its <see cref="Conflict.Where"/> is on it).</summary>
    public static bool Shows(Corridor corridor, Conflict conflict)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        ArgumentNullException.ThrowIfNull(conflict);
        return corridor.PositionOf(conflict.Where) is not null;
    }

    /// <summary>The ring's centre.</summary>
    public static ScreenPoint RingOf(GraphConflict conflict, GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(viewport);
        return viewport.ToScreen(conflict.Time, conflict.Axis);
    }

    /// <summary>Where the highlight draws: at least <see cref="MinThickness"/> across the corridor and 2 px in time.</summary>
    public static ScreenRect RectOf(GraphConflict conflict, GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(viewport);
        double t0 = viewport.TimeToPixel(conflict.TimeStart), t1 = Math.Max(t0 + 2, viewport.TimeToPixel(conflict.TimeEnd));
        double a0 = viewport.AxisToPixel(conflict.AxisFrom), a1 = viewport.AxisToPixel(conflict.AxisTo);
        if (a1 - a0 < MinThickness)
        {
            var middle = (a0 + a1) / 2;
            (a0, a1) = (middle - (MinThickness / 2), middle + (MinThickness / 2));
        }

        return viewport.IsVertical ? new ScreenRect(a0, t0, a1, t1) : new ScreenRect(t0, a0, t1, a1);
    }

    /// <summary>The conflict whose ring is under a point, the nearest first; null if none.</summary>
    public static GraphConflict? ConflictAt(GraphScene scene, GraphViewport viewport, ScreenPoint point)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        GraphConflict? best = null;
        var bestDistance = double.MaxValue;
        foreach (var c in scene.Conflicts)
        {
            var ring = RingOf(c, viewport);
            var distance = Math.Sqrt(((ring.X - point.X) * (ring.X - point.X)) + ((ring.Y - point.Y) * (ring.Y - point.Y)));
            if (distance <= RingRadius + 3 && distance < bestDistance)
            {
                (best, bestDistance) = (c, distance);
            }
        }

        return best;
    }

    /// <summary>
    /// The conflict whose highlight is under a point (#12), the smallest first so a short clash inside a long one can
    /// still be pointed at; of equal ones the last drawn. Null if none.
    /// </summary>
    public static GraphConflict? HighlightAt(GraphScene scene, GraphViewport viewport, ScreenPoint point)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        GraphConflict? best = null;
        var bestArea = double.MaxValue;
        foreach (var c in scene.Conflicts)
        {
            var rect = RectOf(c, viewport);
            var area = rect.Width * rect.Height;
            if (rect.Contains(point) && area <= bestArea)
            {
                (best, bestArea) = (c, area);
            }
        }

        return best;
    }

    /// <summary>
    /// The tooltip (#12): the kind and what clashes, the trains, where and when, the section's headway and where it comes
    /// from (#7), the delay to resolve it, the knock-on,
    /// and the best suggestion if one has been worked out, else how to get one.
    /// </summary>
    /// <param name="suggestion">The top suggestion for this conflict, worded, if the suggestions panel has one.</param>
    public static HoverInfo Describe(GraphConflict conflict, GraphScene scene, string? suggestion = null)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(scene);
        var c = conflict.Conflict;
        var lines = new List<HoverLine>
        {
            new("Trains", TrainsOf(c)),
            new("Where", c.To is { } to && to != c.Where ? $"{NameOf(scene, c.Where)} – {NameOf(scene, to)}" : NameOf(scene, c.Where)),
            new("When", c.End > c.Start ? $"{c.When} (clash {c.Start.ToString("H", CultureInfo.InvariantCulture)}–{c.End.ToString("H", CultureInfo.InvariantCulture)})" : c.When.ToString()),
        };
        if (c.HeadwaySeconds is { } headway)
        {
            // #7: the headway the clash was judged by, and where it came from.
            lines.Add(new HoverLine("Headway", $"{PlanText.Duration(headway)} ({HeadwaySources.Describe(c.HeadwaySource ?? HeadwaySource.Default)})"));
        }

        lines.Add(new HoverLine("Resolve", $"delay {c.HeadcodeA} {PlanText.Duration(c.SeveritySeconds)}"));
        if (c.KnockOnSeconds >= SimTime.SecondsPerMinute)
        {
            lines.Add(new HoverLine("Knock-on", $"{c.HeadcodeA} {PlanText.Duration(c.KnockOnSeconds)} late by the forecast"));
        }

        lines.Add(suggestion is { Length: > 0 } best ? new HoverLine("Suggest", best) : new HoverLine("Tip", "Click to focus it and see suggestions"));
        return new HoverInfo($"{KindName(c.Kind)}: {c.Summary}", lines);
    }

    /// <summary>"2B02 gives way to 1A01", "1A01 held by block B1", or the one train.</summary>
    private static string TrainsOf(Conflict c) => c.Kind switch
    {
        ConflictKind.Disruption => $"{c.HeadcodeA} held by block {c.DisruptionId}",
        _ when c.HeadcodeB is not null || c.B is not null => $"{c.HeadcodeA} gives way to {c.HeadcodeB?.Value ?? c.B?.Value}",
        _ => c.HeadcodeA.Value,
    };

    /// <summary>"Headway", "Single line", "Platform"…</summary>
    public static string KindName(ConflictKind kind) => kind switch
    {
        ConflictKind.Headway => "Headway",
        ConflictKind.Overtake => "Overtake",
        ConflictKind.SingleLine => "Single line",
        ConflictKind.Platform => "Platform",
        ConflictKind.Disruption => "Block",
        ConflictKind.Unrealistic => "Run time",
        _ => kind.ToString(),
    };

    private static string NameOf(GraphScene scene, Tiploc tiploc) => scene.Locations.FirstOrDefault(l => l.Tiploc == tiploc)?.Name ?? tiploc.Code;
}
