using System.Globalization;
using Stringline.Core;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>The kinds of grip on the selected train (S26, <c>05-…md</c> §3).</summary>
public enum GraphGripKind
{
    /// <summary>A square at a timing point: drag to retime from there on.</summary>
    Point,

    /// <summary>A bar at a stop's departure end: drag to change the dwell.</summary>
    Dwell,

    /// <summary>A round handle on the line's body: drag to retime the rest of the run.</summary>
    Body,
}

/// <summary>
/// One grip on the selected train's WTT or plan line (S26). A grip that cannot move (a point passed, the latest
/// report, a cancelled or finished train) is locked, and says why.
/// </summary>
/// <param name="Index">The plan point the grip is at; for <see cref="GraphGripKind.Body"/>, the first point the drag moves.</param>
/// <param name="LocationName">The location's name as the graph shows it.</param>
/// <param name="At">Where the grip is drawn, in world coordinates.</param>
public sealed record GraphGrip(GraphGripKind Kind, TrainId Train, Headcode Headcode, int Index, Tiploc Location, string LocationName, GraphVertex At)
{
    /// <summary>Why the grip cannot move, or null if it can.</summary>
    public string? LockReason { get; init; }

    public bool IsLocked => LockReason is not null;

    /// <summary>The drag the grip makes.</summary>
    public PlanDragKind DragKind => Kind switch
    {
        GraphGripKind.Point => PlanDragKind.ShiftFrom,
        GraphGripKind.Dwell => PlanDragKind.Dwell,
        _ => PlanDragKind.Shift,
    };

    /// <summary>What dragging the grip does, for the status bar and tooltip.</summary>
    public string Action => Kind switch
    {
        GraphGripKind.Point => $"Drag to retime {Headcode} from {LocationName}",
        GraphGripKind.Dwell => $"Drag to change {Headcode}'s departure from {LocationName}",
        _ => $"Drag to retime the rest of {Headcode}'s run, from {LocationName}",
    };

    /// <summary>True if <paramref name="other"/> is the same grip, wherever it is drawn now (after an edit or a pan).</summary>
    public bool SameAs(GraphGrip? other) => other is not null && other.Train == Train && other.Kind == Kind && other.Index == Index;
}

/// <summary>A grip being dragged or nudged: where it has got to, and the time label drawn beside it.</summary>
/// <param name="Limited">Held at a limit: the grip and label are drawn in the clamp colour.</param>
public sealed record GraphGripDrag(GraphGrip Grip, GraphVertex At, string Label, bool Limited);

/// <summary>
/// The grips on the selected train (S26, TRIMS style; <c>05-…md</c> §3): a square at each timing point, a bar at each
/// stop's departure end and a handle on the body of the run. They are drawn only on the selected train, on its plan
/// line if it has been re-planned, else on its WTT line, and they are hit before anything else, within
/// <see cref="HitPixels"/>. Immutable: <see cref="Hot"/>, <see cref="Selected"/> and <see cref="Drag"/> are set with <c>with</c>.
/// </summary>
public sealed class GraphGrips
{
    /// <summary>How near a grip, in pixels, a press or the mouse must be to take it.</summary>
    public const double HitPixels = 7;

    /// <summary>How near the selected train's line, in pixels, a press drags its body.</summary>
    public const double LinePixels = 6;

    /// <summary>The body handle goes only on a stretch of line at least this long on screen.</summary>
    internal const double MinBodyPixels = 30;

    private readonly IReadOnlyList<(GraphVertex A, GraphVertex B)> _body;
    private readonly GraphGrip? _bodyGrip;

    private GraphGrips(TrainId train, Colour colour, IReadOnlyList<GraphPath> paths, IReadOnlyList<GraphGrip> grips, GraphGrip? body, IReadOnlyList<(GraphVertex, GraphVertex)> bodySegments)
    {
        Train = train;
        Colour = colour;
        Paths = paths;
        Grips = grips;
        _bodyGrip = body;
        _body = bodySegments;
    }

    public TrainId Train { get; }

    /// <summary>The line's colour, which movable grips are filled with.</summary>
    public Colour Colour { get; }

    /// <summary>The lines the grips are on: the train's plan paths if it has any, else its WTT paths.</summary>
    public IReadOnlyList<GraphPath> Paths { get; }

    /// <summary>The point and dwell grips, in the line's order. The body handle is placed per view (<see cref="InView"/>).</summary>
    public IReadOnlyList<GraphGrip> Grips { get; }

    /// <summary>The grip under the mouse, drawn larger.</summary>
    public GraphGrip? Hot { get; private init; }

    /// <summary>The grip picked with a click, which the arrow keys nudge; drawn with a ring.</summary>
    public GraphGrip? Selected { get; private init; }

    /// <summary>The grip being dragged; while set, only it is drawn, where it has got to.</summary>
    public GraphGripDrag? Drag { get; private init; }

    /// <summary>This set with <paramref name="hot"/> under the mouse; the same set if that grip is already hot.</summary>
    public GraphGrips WithHot(GraphGrip? hot) => (hot is null ? Hot is null : hot.SameAs(Hot)) ? this : Copy(hot, Selected, Drag);

    public GraphGrips WithSelected(GraphGrip? selected) => Copy(Hot, selected is null ? null : Find(selected) ?? selected, Drag);

    public GraphGrips WithDrag(GraphGripDrag? drag) => Copy(Hot, Selected, drag);

    /// <summary>
    /// The grips for the selected train in <paramref name="scene"/>, or null if no train is selected or none of its
    /// WTT or plan line is on this corridor.
    /// </summary>
    /// <param name="rules">The board's drag rules (a draft board's, S44); null for a session board's.</param>
    public static GraphGrips? For(GraphScene scene, PlanSnapshot snapshot, GraphSelection? selection, PlanDragRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (selection?.Train is not { } id || snapshot.Plan.Find(id) is not { } plan)
        {
            return null;
        }

        var paths = Editable(scene, id, GraphLayer.Plan);
        if (paths.Count == 0)
        {
            paths = Editable(scene, id, GraphLayer.Wtt);
        }

        if (paths.Count == 0)
        {
            return null;
        }

        var names = new Dictionary<Tiploc, string>();
        foreach (var location in scene.Locations)
        {
            names.TryAdd(location.Tiploc, location.Name);
        }

        string NameOf(Tiploc t) => names.TryGetValue(t, out var n) ? n : t.ToString();

        GraphGrip Grip(GraphGripKind kind, int index, GraphVertex at)
        {
            var location = plan.Points[index].Location;
            var grip = new GraphGrip(kind, id, plan.Headcode, index, location, NameOf(location), at);
            return PlanDrag.Start(snapshot, id, grip.DragKind, index, null, out var refusal, rules) is null
                ? grip with { LockReason = refusal ?? "It cannot be moved." }
                : grip;
        }

        var grips = new List<GraphGrip>();
        foreach (var path in paths)
        {
            for (var v = 0; v < path.Vertices.Count; v++)
            {
                if (path.PointAt(v) is not { } index || index >= plan.Points.Count)
                {
                    continue;
                }

                if (path.IsDwellEnd(v))
                {
                    // No bar where the run ends, though the WTT may time a departure there (the train forms its next working).
                    if (index < plan.LastIndex)
                    {
                        grips.Add(Grip(GraphGripKind.Dwell, index, path.Vertices[v]));
                    }
                }
                else if (v == 0 || path.PointAt(v - 1) != index)
                {
                    grips.Add(Grip(GraphGripKind.Point, index, path.Vertices[v]));
                }
            }
        }

        // The body: the sloping stretches of the run still to go, from the first point a body drag moves.
        var drag = PlanDrag.Start(snapshot, id, PlanDragKind.Shift, -1, null, out var bodyRefusal, rules);
        var from = drag?.Index ?? 0;
        var segments = new List<(GraphVertex, GraphVertex)>();
        foreach (var path in paths)
        {
            for (var i = 0; i + 1 < path.Vertices.Count; i++)
            {
                if (path.PointAt(i) is { } p && p >= from && path.Vertices[i].Axis != path.Vertices[i + 1].Axis)
                {
                    segments.Add((path.Vertices[i], path.Vertices[i + 1]));
                }
            }
        }

        GraphGrip? body = null;
        if (segments.Count > 0)
        {
            var index = Math.Min(from, plan.Points.Count - 1);
            var location = plan.Points[index].Location;
            body = new GraphGrip(GraphGripKind.Body, id, plan.Headcode, index, location, NameOf(location), segments[0].Item1)
            {
                LockReason = drag is null ? bodyRefusal ?? "It cannot be moved." : null,
            };
        }

        return new GraphGrips(id, paths[0].Colour, paths, grips, body, segments);
    }

    /// <summary>
    /// The grips to draw and hit in <paramref name="viewport"/>: the point and dwell grips in the plot, and the body
    /// handle at the middle of the first stretch of the run still to go that is long enough on screen.
    /// </summary>
    public IReadOnlyList<GraphGrip> InView(GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var list = new List<GraphGrip>(Grips.Count + 1);
        foreach (var grip in Grips)
        {
            if (viewport.IsInPlot(viewport.ToScreen(grip.At.Time, grip.At.Axis)))
            {
                list.Add(grip);
            }
        }

        if (BodyIn(viewport) is { } body)
        {
            list.Add(body);
        }

        return list;
    }

    /// <summary>The grip at <paramref name="point"/>: the nearest within <see cref="HitPixels"/>, a point or dwell grip before the body handle.</summary>
    public GraphGrip? HitTest(GraphViewport viewport, ScreenPoint point)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (!viewport.IsInPlot(point))
        {
            return null;
        }

        GraphGrip? best = null;
        var bestScore = double.PositiveInfinity;
        foreach (var grip in InView(viewport))
        {
            var at = viewport.ToScreen(grip.At.Time, grip.At.Axis);
            var distance = Math.Sqrt(((at.X - point.X) * (at.X - point.X)) + ((at.Y - point.Y) * (at.Y - point.Y)));
            var score = distance + (grip.Kind == GraphGripKind.Body ? 1 : 0);
            if (distance <= HitPixels && score < bestScore)
            {
                (best, bestScore) = (grip, score);
            }
        }

        return best;
    }

    /// <summary>
    /// The selected train's own line at <paramref name="point"/>, within <see cref="LinePixels"/>: a press there drags its
    /// body even where its forecast or actual line is drawn over it (S26).
    /// </summary>
    public GraphHit? LineAt(GraphViewport viewport, ScreenPoint point)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return viewport.IsInPlot(point) ? GraphHitTester.HitTest(Paths, viewport, point, LinePixels) : null;
    }

    /// <summary>
    /// The tooltip over a grip: what it is, its time, and what dragging it does and the keys that nudge it, or why it is
    /// locked. The same in both orientations, but for the arrow keys named.
    /// </summary>
    /// <param name="nudgeSeconds">How far an arrow key nudges it (30 s on a draft board, S44).</param>
    public static HoverInfo Describe(GraphGrip grip, bool vertical, int nudgeSeconds = PlanDrag.SnapSeconds)
    {
        ArgumentNullException.ThrowIfNull(grip);
        var lines = new List<HoverLine>
        {
            new("Grip", grip.Kind switch
            {
                GraphGripKind.Point => "Timing point",
                GraphGripKind.Dwell => "Departure (the dwell)",
                _ => "The rest of the run",
            }),
        };
        if (grip.Kind != GraphGripKind.Body)
        {
            lines.Add(new HoverLine("Time", new SimTime((int)Math.Round(grip.At.Time)).ToString()));
        }

        if (grip.LockReason is { } reason)
        {
            lines.Add(new HoverLine("Locked", reason));
        }
        else
        {
            lines.Add(new HoverLine("Drag", grip.Action["Drag to ".Length..]));
            lines.Add(new HoverLine("Keys", string.Create(CultureInfo.InvariantCulture, $"Click it, then {(vertical ? "↑/↓" : "←/→")} {nudgeSeconds} s (Shift: 1 min)")));
        }

        var heading = grip.Kind == GraphGripKind.Body ? $"{grip.Headcode}: rest of the run" : $"{grip.Headcode} at {grip.LocationName}";
        return new HoverInfo(heading, lines);
    }

    /// <summary>This set's copy of <paramref name="grip"/> (the same kind at the same point), if it has one.</summary>
    public GraphGrip? Find(GraphGrip grip)
    {
        ArgumentNullException.ThrowIfNull(grip);
        // The body handle moves along the line as the train runs, so it matches by kind alone.
        return grip.Kind == GraphGripKind.Body
            ? grip.Train == Train ? _bodyGrip : null
            : Grips.FirstOrDefault(g => g.SameAs(grip));
    }

    /// <summary>The body handle placed in <paramref name="viewport"/>, if a stretch of the run to go is long enough on screen.</summary>
    internal GraphGrip? BodyIn(GraphViewport viewport)
    {
        if (_bodyGrip is not { } body)
        {
            return null;
        }

        var area = new SkiaSharp.SKRect((float)viewport.PlotLeft, (float)viewport.PlotTop, (float)viewport.PlotRight, (float)viewport.PlotBottom);
        foreach (var (a, b) in _body)
        {
            if (SkiaGraphRenderer.ClipToRect(viewport.ToScreen(a.Time, a.Axis), viewport.ToScreen(b.Time, b.Axis), area) is not var (c, d))
            {
                continue;
            }

            var length = Math.Sqrt(((d.X - c.X) * (d.X - c.X)) + ((d.Y - c.Y) * (d.Y - c.Y)));
            if (length >= MinBodyPixels)
            {
                var mid = new ScreenPoint((c.X + d.X) / 2, (c.Y + d.Y) / 2);
                var (time, axis) = viewport.ToWorld(mid);
                return body with { At = new GraphVertex(time, axis) };
            }
        }

        return null;
    }

    private static List<GraphPath> Editable(GraphScene scene, TrainId id, GraphLayer layer) =>
        scene.Paths.Where(p => p.Train == id && p.Layer == layer && p.Points is not null && p.Vertices.Count > 0).ToList();

    private GraphGrips Copy(GraphGrip? hot, GraphGrip? selected, GraphGripDrag? drag) =>
        new(Train, Colour, Paths, Grips, _bodyGrip, _body) { Hot = hot, Selected = selected, Drag = drag };
}
