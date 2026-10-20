using System.Globalization;
using Stringline.Core;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>What a drag on a train's line changes (<c>05-…md</c> §3).</summary>
public enum PlanDragKind
{
    /// <summary>The body of the line: retime the rest of the run, from the next point the train has not reached (RP-2).</summary>
    Shift,

    /// <summary>A timing point's grip, or Alt+drag from a vertex: retime from that timing point on (RP-2).</summary>
    ShiftFrom,

    /// <summary>The right-hand end of a stop: change the departure, so the dwell (RP-3).</summary>
    Dwell,
}

/// <summary>Where a drag has got to: the command it would make, the train as it would then run, and what to tell the user.</summary>
/// <param name="Command">The edit, or null while the drag makes no change.</param>
/// <param name="Train">The train's plan with <paramref name="Command"/> applied (its plan now if there is none).</param>
/// <param name="DeltaSeconds">How far the drag moves the train (or the departure), after snapping and limits.</param>
/// <param name="Status">For the status bar: what the edit does, and why it stopped at a limit.</param>
/// <param name="Limited">True if the drag was held at a limit (now, the latest report, a minimum run or the arrival).</param>
/// <param name="Warnings">Clamps the plan would make to keep the rest of the run realistic.</param>
public sealed record PlanDragPreview(IPlanCommand? Command, TrainPlan Train, int DeltaSeconds, string Status, bool Limited, IReadOnlyList<PlanWarning> Warnings)
{
    /// <summary>The new time of the point the drag moves: the first point retimed, or the departure (S26).</summary>
    public SimTime? Time { get; init; }

    /// <summary>
    /// The short text drawn by the grip while dragging (S26): the new time and the change, and "stopped: …" with the
    /// limit's short name when held at one, such as "07:05:15 (−2 min 45 s) · stopped: now".
    /// </summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>
/// A drag edit of one train on the graph (<c>05-…md</c> §3): its body retimes the rest of the run, a timing point's grip
/// (or Alt+drag from a vertex) retimes from that point, and the right end of a stop changes the dwell. Time snaps to
/// 15 s (1 s fine). A drag never plans the train before the now line or its latest report (Fred, S12), and a retime
/// earlier stops where the run into the first point moved would be faster than the minimum. Each move gives a
/// <see cref="PlanDragPreview"/> made by applying the command to the snapshot's plan off the engine, so the ghost shows
/// exactly what the edit gives. The same rules nudge a grip from the keyboard (<see cref="Nudge"/>, S26).
/// </summary>
public sealed class PlanDrag
{
    /// <summary>Times snap to this many seconds while dragging.</summary>
    public const int SnapSeconds = 15;

    /// <summary>How near the end of a stop, in pixels, a press must be to drag the dwell rather than the body.</summary>
    public const double DwellGripPixels = 6;

    private readonly PlanSnapshot _snapshot;
    private readonly TrainPlan _plan;
    private readonly double _pressTime;
    private readonly int _minDelta;
    private readonly string? _minReason;
    private readonly string? _minShort;
    private readonly SimTime _original;
    private readonly SimTime? _minDepart;

    private PlanDrag(PlanSnapshot snapshot, TrainPlan plan, PlanDragKind kind, int index, double pressTime, SimTime original, int minDelta, (string? Reason, string? Short) limit, SimTime? minDepart)
    {
        _snapshot = snapshot;
        _plan = plan;
        Kind = kind;
        Index = index;
        _pressTime = pressTime;
        _original = original;
        _minDelta = minDelta;
        (_minReason, _minShort) = limit;
        _minDepart = minDepart;
        Preview = new PlanDragPreview(null, plan, 0, Idle(), false, []) { Time = original, Label = original.ToString() };
    }

    public PlanDragKind Kind { get; }

    public TrainId Train => _plan.Id;

    /// <summary>The point the drag retimes from, or whose departure it moves.</summary>
    public int Index { get; }

    public Tiploc Location => _plan.Points[Index].Location;

    /// <summary>The time the drag moves from: the first timed point retimed, or the departure.</summary>
    public SimTime Original => _original;

    /// <summary>The latest preview (before any move: no change).</summary>
    public PlanDragPreview Preview { get; private set; }

    /// <summary>
    /// Starts a drag of a press on a train's WTT or plan line, or returns null: with no <paramref name="refusal"/> if
    /// the press is not on a line that can be edited (the graph pans instead), or with the reason the train cannot be
    /// edited here (shown to the user).
    /// </summary>
    /// <param name="now">The now line; nothing is planned before it.</param>
    /// <param name="fromVertex">Alt is held: retime from the vertex nearest the press.</param>
    public static PlanDrag? Begin(GraphHit hit, PlanSnapshot snapshot, GraphViewport viewport, ScreenPoint at, SimTime? now, bool fromVertex, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(viewport);
        refusal = null;
        var path = hit.Path;
        if (path.Layer is not (GraphLayer.Wtt or GraphLayer.Plan) || path.Train is not { } id || path.Points is null || snapshot.Plan.Find(id) is null)
        {
            return null;
        }

        var vertex = hit.NearestVertex;
        var (kind, index) = (PlanDragKind.Shift, -1);
        if (path.IsDwellEnd(vertex) && Distance(viewport.ToScreen(path.Vertices[vertex].Time, path.Vertices[vertex].Axis), at) <= DwellGripPixels)
        {
            (kind, index) = (PlanDragKind.Dwell, path.PointAt(vertex)!.Value);
        }
        else if (fromVertex && path.PointAt(vertex) is { } vertexIndex)
        {
            (kind, index) = (PlanDragKind.ShiftFrom, vertexIndex);
        }

        return Start(snapshot, id, kind, index, PressTime(viewport, at), now, out refusal);
    }

    /// <summary>Starts a drag of a grip on the selected train (S26), or returns null with the reason it is locked.</summary>
    public static PlanDrag? Begin(GraphGrip grip, PlanSnapshot snapshot, GraphViewport viewport, ScreenPoint at, SimTime? now, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(grip);
        ArgumentNullException.ThrowIfNull(viewport);
        return Start(snapshot, grip.Train, grip.DragKind, grip.Index, PressTime(viewport, at), now, out refusal);
    }

    /// <summary>
    /// Starts an edit of <paramref name="train"/> of a kind at a plan point, away from the mouse: a keyboard nudge, or
    /// to ask whether a grip can move. Returns null with the reason when it cannot.
    /// </summary>
    /// <param name="index">The point retimed from (<see cref="PlanDragKind.ShiftFrom"/>) or whose departure moves
    /// (<see cref="PlanDragKind.Dwell"/>); ignored for <see cref="PlanDragKind.Shift"/>, which starts at the next point not reached.</param>
    public static PlanDrag? Start(PlanSnapshot snapshot, TrainId train, PlanDragKind kind, int index, SimTime? now, out string? refusal) =>
        Start(snapshot, train, kind, index, 0, now, out refusal);

    private static PlanDrag? Start(PlanSnapshot snapshot, TrainId id, PlanDragKind kind, int index, double pressTime, SimTime? now, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        refusal = null;
        if (snapshot.Plan.Find(id) is not { } plan)
        {
            refusal = $"{id} is not in the plan.";
            return null;
        }

        var name = plan.Headcode.ToString();
        if (plan.Cancelled)
        {
            refusal = $"{name} is cancelled; reinstate it to retime it.";
            return null;
        }

        var forecast = snapshot.Forecast.Find(id);
        if (forecast?.Status == TrainForecastStatus.Finished)
        {
            refusal = $"{name} has finished its run.";
            return null;
        }

        if (forecast?.Status == TrainForecastStatus.NotRunning)
        {
            refusal = $"{name} is not running ({forecast.NotRunning}); choose Run anyway to retime it.";
            return null;
        }

        // The first point not yet reached, and the latest report.
        var next = 0;
        SimTime? latest = null;
        if (forecast is { Status: TrainForecastStatus.Running })
        {
            next = -1;
            for (var k = 0; k < forecast.Points.Count; k++)
            {
                var p = forecast.Points[k];
                if (p.Basis == ForecastBasis.Forecast && next < 0)
                {
                    next = k;
                }

                var reported = p.Basis switch
                {
                    ForecastBasis.Actual => p.LastTime,
                    ForecastBasis.Arrived => p.Arrive,
                    _ => null,
                };
                if (reported is { } r && (latest is null || r > latest))
                {
                    latest = r;
                }
            }

            if (next < 0 && kind != PlanDragKind.Dwell)
            {
                refusal = $"{name} has no more points to retime.";
                return null;
            }
        }

        var limit = Later(now, latest);
        (string, string)? limitReason = limit is null ? null : limit == latest && latest != now
            ? ($"{name} was last reported at {latest}", "last report")
            : ($"nothing can be planned before now ({limit})", "now");

        // The end of a stop: the dwell.
        if (kind == PlanDragKind.Dwell)
        {
            var point = index >= 0 && index < plan.Points.Count ? plan.Points[index] : null;
            if (point is not { IsCall: true, Arrive: { } arrive, Depart: { } depart } || index >= plan.LastIndex)
            {
                refusal = point is null || index > plan.LastIndex
                    ? $"{name} no longer runs there."
                    : index == plan.LastIndex
                        ? $"{name} ends its run at {point.Location}."
                        : $"{name} does not stop at {point.Location} in the plan.";
                return null;
            }

            if (forecast?.At(index) is { Basis: ForecastBasis.Actual })
            {
                refusal = $"{name} has already left {point.Location}.";
                return null;
            }

            // The departure may not be before the arrival, nor before now.
            var minDepart = arrive;
            (string, string) reason = ($"{name} cannot leave {point.Location} before it arrives ({arrive})", "arrival");
            if (now is { } n && n > minDepart)
            {
                (minDepart, reason) = (n, ($"nothing can be planned before now ({n})", "now"));
            }

            return new PlanDrag(snapshot, plan, PlanDragKind.Dwell, index, pressTime, depart, int.MinValue, reason, minDepart);
        }

        var from = next;
        if (kind == PlanDragKind.ShiftFrom)
        {
            if (index < 0 || index >= plan.Points.Count)
            {
                refusal = $"{name} has no such point.";
                return null;
            }

            if (index < next)
            {
                refusal = forecast?.At(index) is { Basis: ForecastBasis.Arrived }
                    ? $"{name} has arrived at {plan.Points[index].Location}; drag its departure instead."
                    : $"{name} has already passed {plan.Points[index].Location}.";
                return null;
            }

            from = index;
        }

        if (from > plan.LastIndex)
        {
            refusal = $"{name} no longer runs to {plan.Points[from].Location}.";
            return null;
        }

        // The first timed point moved, and the timed point before it.
        var first = -1;
        for (var k = from; k <= plan.LastIndex && first < 0; k++)
        {
            if (plan.Points[k].IsTimed)
            {
                first = k;
            }
        }

        if (first < 0)
        {
            refusal = $"{name} has no times to retime from {plan.Points[from].Location}.";
            return null;
        }

        var firstTime = plan.Points[first].FirstTime!.Value;
        var minDelta = int.MinValue;
        (string?, string?) minReason = (null, null);
        if (limit is { } l)
        {
            (minDelta, minReason) = (Math.Min(0, l - firstTime), limitReason!.Value);
        }

        var before = -1;
        for (var k = first - 1; k >= 0 && before < 0; k--)
        {
            if (plan.Points[k].IsTimed)
            {
                before = k;
            }
        }

        if (before >= 0 && plan.Points[before].LastTime is { } leave &&
            snapshot.Plan.MinRuns.Along(plan.Points, before, first, leave, snapshot.Plan.Disruptions) is { } minRun)
        {
            var run = firstTime - leave;
            var runDelta = Math.Min(0, minRun - run);
            if (runDelta > minDelta)
            {
                minDelta = runDelta;
                minReason = (string.Create(CultureInfo.InvariantCulture,
                    $"{name} needs at least {PlanText.Duration(minRun)} from {plan.Points[before].Location} to {plan.Points[first].Location}"), "minimum run");
            }
        }

        return new PlanDrag(snapshot, plan, kind, from, pressTime, firstTime, minDelta, minReason, null);
    }

    /// <summary>The drag has moved to <paramref name="at"/> (in the current viewport, which may have panned since it began).</summary>
    /// <param name="fine">Snap to 1 s rather than 15 s (Shift held).</param>
    public PlanDragPreview Update(GraphViewport viewport, ScreenPoint at, bool fine)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return Move(PressTime(viewport, at) - _pressTime, fine ? 1 : SnapSeconds);
    }

    /// <summary>
    /// Moves the point by <paramref name="seconds"/> from where it started (S26: a nudge from the arrow keys), held at the
    /// same limits as a drag, which are rounded to the 15 s grid.
    /// </summary>
    public PlanDragPreview Nudge(int seconds) => Move(seconds, SnapSeconds);

    private PlanDragPreview Move(double raw, int step)
    {
        var delta = (int)Math.Round(raw / step, MidpointRounding.AwayFromZero) * step;
        var limited = false;
        IPlanCommand? command;
        if (Kind == PlanDragKind.Dwell)
        {
            var depart = _original + delta;
            if (_minDepart is { } min && depart < min)
            {
                // Held at the limit, on the snapping grid.
                depart = _original + CeilingTo(min - _original, step);
                limited = true;
            }

            delta = depart - _original;
            command = delta == 0 ? null : new SetDwell(Train, Index, depart);
        }
        else
        {
            if (delta < _minDelta)
            {
                delta = CeilingTo(_minDelta, step);
                limited = true;
            }

            command = delta == 0 ? null : new ShiftTrain(Train, Index, delta);
        }

        var train = _plan;
        IReadOnlyList<PlanWarning> warnings = [];
        string status;
        if (command is null)
        {
            status = Idle();
        }
        else
        {
            var (after, result) = _snapshot.Plan.Apply(command);
            if (!result.Applied)
            {
                status = result.Error ?? "This edit cannot be made.";
                command = null;
            }
            else
            {
                train = after.Get(Train);
                warnings = result.Warnings;
                status = Describe(command, delta);
                if (warnings.Count > 0)
                {
                    status += " — " + warnings[0].Message + (warnings.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" (and {warnings.Count - 1} more)") : string.Empty);
                }
            }
        }

        if (limited && _minReason is { } reason)
        {
            status += $" — stopped: {reason}.";
        }

        var time = _original + delta;
        var label = delta == 0 ? time.ToString() : $"{time} ({PlanText.WithSign(delta)})";
        if (limited && _minShort is { } limit)
        {
            label += $" · stopped: {limit}";
        }

        Preview = new PlanDragPreview(command, train, delta, status, limited, warnings) { Time = time, Label = label };
        return Preview;
    }

    private string Describe(IPlanCommand command, int delta) => command is SetDwell
        ? $"{command.Describe(_snapshot.Plan)} ({PlanText.WithSign(delta)})"
        : command.Describe(_snapshot.Plan);

    private string Idle() => Kind == PlanDragKind.Dwell
        ? $"Drag to change {_plan.Headcode}'s departure from {Location}."
        : $"Drag to retime {_plan.Headcode} from {Location}.";

    private static double PressTime(GraphViewport viewport, ScreenPoint at) => viewport.PixelToTime(viewport.TimePixelOf(at));

    /// <summary>The smallest multiple of <paramref name="step"/> at least <paramref name="seconds"/>.</summary>
    private static int CeilingTo(int seconds, int step)
    {
        if (seconds == int.MinValue)
        {
            return seconds;
        }

        var q = Math.DivRem(seconds, step, out var r);
        return (r > 0 ? q + 1 : q) * step;
    }

    private static SimTime? Later(SimTime? a, SimTime? b) => a is null ? b : b is null ? a : SimTime.Max(a.Value, b.Value);

    private static double Distance(ScreenPoint a, ScreenPoint b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
