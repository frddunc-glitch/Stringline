using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Planning;
using Stringline.Timetable.Editing;

namespace Stringline.Graph;

/// <summary>
/// What a board's drags edit (S44, <c>08-…md</c> §7): a session board's make plan commands for the engine, a draft
/// board's make timetable edits for the draft's editor. Both drag by <see cref="PlanDrag"/> over a
/// <see cref="PlanSnapshot"/> (a draft's is its booking with no live data, <see cref="DraftBoard.Plan"/>); the target
/// says how the drag snaps and stops, and what a finished drag becomes.
/// </summary>
/// <typeparam name="TEdit">The kind of edit the board makes.</typeparam>
public interface IDragTarget<TEdit>
    where TEdit : class
{
    /// <summary>The plan the drag reads: where the train runs now, its limits, and the preview of the edit.</summary>
    PlanSnapshot Snapshot { get; }

    /// <summary>The now line, which nothing is planned before; none on a draft board.</summary>
    SimTime? Now { get; }

    PlanDragRules Rules { get; }

    /// <summary>The edit a drag's preview makes, or null with the reason it cannot be made.</summary>
    TEdit? EditFor(PlanDragPreview preview, out string? refusal);
}

/// <summary>Starting drags and grips on a <see cref="IDragTarget{TEdit}"/>'s plan with its rules.</summary>
public static class DragTargets
{
    /// <inheritdoc cref="PlanDrag.Begin(GraphHit, PlanSnapshot, GraphViewport, ScreenPoint, SimTime?, bool, out string?, PlanDragRules?)"/>
    public static PlanDrag? Begin<TEdit>(this IDragTarget<TEdit> target, GraphHit hit, GraphViewport viewport, ScreenPoint at, bool fromVertex, out string? refusal)
        where TEdit : class
    {
        ArgumentNullException.ThrowIfNull(target);
        return PlanDrag.Begin(hit, target.Snapshot, viewport, at, target.Now, fromVertex, out refusal, target.Rules);
    }

    /// <inheritdoc cref="PlanDrag.Begin(GraphGrip, PlanSnapshot, GraphViewport, ScreenPoint, SimTime?, out string?, PlanDragRules?)"/>
    public static PlanDrag? Begin<TEdit>(this IDragTarget<TEdit> target, GraphGrip grip, GraphViewport viewport, ScreenPoint at, out string? refusal)
        where TEdit : class
    {
        ArgumentNullException.ThrowIfNull(target);
        return PlanDrag.Begin(grip, target.Snapshot, viewport, at, target.Now, out refusal, target.Rules);
    }

    /// <inheritdoc cref="PlanDrag.Start(PlanSnapshot, TrainId, PlanDragKind, int, SimTime?, out string?, PlanDragRules?)"/>
    public static PlanDrag? Start<TEdit>(this IDragTarget<TEdit> target, TrainId train, PlanDragKind kind, int index, out string? refusal)
        where TEdit : class
    {
        ArgumentNullException.ThrowIfNull(target);
        return PlanDrag.Start(target.Snapshot, train, kind, index, target.Now, out refusal, target.Rules);
    }

    /// <summary>The selected train's grips in a scene of the target's plan (<see cref="GraphGrips.For"/>).</summary>
    public static GraphGrips? GripsFor<TEdit>(this IDragTarget<TEdit> target, GraphScene scene, GraphSelection? selection)
        where TEdit : class
    {
        ArgumentNullException.ThrowIfNull(target);
        return GraphGrips.For(scene, target.Snapshot, selection, target.Rules);
    }
}

/// <summary>A session board (S12, S26): a drag is the plan command it previews, for the planning engine.</summary>
public sealed class SessionDragTarget(PlanSnapshot snapshot, SimTime? now) : IDragTarget<IPlanCommand>
{
    public PlanSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public SimTime? Now { get; } = now;

    public PlanDragRules Rules => PlanDragRules.Session;

    public IPlanCommand? EditFor(PlanDragPreview preview, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(preview);
        refusal = preview.Command is null ? "The drag makes no change." : null;
        return preview.Command;
    }
}

/// <summary>
/// A draft board (S44, <c>08-…md</c> §7): a drag becomes one timetable edit of the draft, setting the times of the
/// points it moved to the train as the preview runs it, rounded to half minutes, with the entry time moved with the
/// first point. Drags snap to half minutes and stop at <paramref name="minRun"/> (the sim's knowledge, then the
/// original timetable; Shift goes past with a warning).
/// </summary>
/// <param name="snapshot">The draft's plan (<see cref="DraftBoard.Plan"/>) made from <paramref name="import"/>'s timetable.</param>
/// <param name="import">The draft's import the plan was made from; the edit is refused once the draft has changed since.</param>
/// <param name="minRun">The least run between two points of a train, or null for the plan's own.</param>
public sealed class DraftDragTarget(PlanSnapshot snapshot, WttDocumentImport import, Func<TrainPlan, int, int, int?>? minRun = null) : IDragTarget<TimetableEdit>
{
    /// <summary>SimSig books times to the half minute.</summary>
    public const int RoundSeconds = 30;

    public PlanSnapshot Snapshot { get; } = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public WttDocumentImport Import { get; } = import ?? throw new ArgumentNullException(nameof(import));

    public SimTime? Now => null;

    public PlanDragRules Rules { get; } = minRun is null ? PlanDragRules.Draft : PlanDragRules.Draft with { MinRun = minRun };

    public TimetableEdit? EditFor(PlanDragPreview preview, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(preview);
        refusal = null;
        if (preview.Command is not { } command)
        {
            refusal = "The drag makes no change.";
            return null;
        }

        if (!Import.IsCurrent)
        {
            refusal = "The draft changed during the drag; drag again.";
            return null;
        }

        if (Import.Timetable.Find(preview.Train.Id) is not { } train)
        {
            refusal = $"{preview.Train.Headcode} is not in the draft.";
            return null;
        }

        var edits = Edits(train, preview.Train);
        if (edits.Count == 0)
        {
            refusal = "The drag makes no change.";
            return null;
        }

        return TimetableEdit.Composite(Describe(command), edits);
    }

    /// <summary>The edits that book <paramref name="train"/> as <paramref name="plan"/> runs it, times rounded to half minutes.</summary>
    private List<TimetableEdit> Edits(Train train, TrainPlan plan)
    {
        var edits = new List<TimetableEdit>();
        var count = Math.Min(train.Points.Count, plan.Points.Count);
        for (var k = 0; k < count; k++)
        {
            var booked = train.Points[k];
            var planned = plan.Points[k];
            var bookedArrive = booked.IsPass ? null : booked.Arrive;
            if (planned.Arrive == bookedArrive && planned.Depart == booked.Depart)
            {
                continue;
            }

            // Only the times the drag moved are rounded; a booking off the half minute elsewhere stays as it is.
            var arrive = planned.Arrive == bookedArrive ? bookedArrive : Round(planned.Arrive);
            var depart = planned.Depart == booked.Depart ? booked.Depart : Round(planned.Depart);
            if (arrive == bookedArrive && depart == booked.Depart)
            {
                continue;
            }

            var trip = Import.TripOf(train, k);
            edits.Add(TimetableEdit.Change(trip, t =>
            {
                if (arrive != bookedArrive)
                {
                    t.ArrTime = arrive;
                }

                if (depart != booked.Depart)
                {
                    t.DepPassTime = depart;
                }
            }));

            // The entry (or seeding) time moves with the first point.
            if (k == 0 && train.EntryTime is { } entry && (planned.FirstTime ?? booked.FirstTime) is { } first && booked.FirstTime is { } was && first != was)
            {
                var node = Import.NodeOf(train);
                var moved = Round(entry + (first - was))!.Value;
                edits.Add(TimetableEdit.Change(node, n => n.DepartTime = moved));
            }
        }

        return edits;
    }

    private string Describe(IPlanCommand command) => command switch
    {
        ShiftTrain shift => $"{Name(shift.Train)}: retime from {Where(shift.Train, shift.FromIndex)} by {PlanText.WithSign(Rounded(shift.DeltaSeconds))}",
        SetDwell dwell => $"{Name(dwell.Train)}: depart {Where(dwell.Train, dwell.Index)} at {Round(dwell.Depart)}",
        _ => command.Describe(Snapshot.Plan),
    };

    private string Name(TrainId id) => Snapshot.Plan.Find(id)?.Headcode.ToString() ?? id.ToString();

    private string Where(TrainId id, int index) =>
        Snapshot.Plan.Find(id) is { } plan && index >= 0 && index < plan.Points.Count ? plan.Points[index].Location.ToString() : "?";

    private static int Rounded(int seconds) => (int)Math.Round(seconds / (double)RoundSeconds, MidpointRounding.AwayFromZero) * RoundSeconds;

    private static SimTime? Round(SimTime? time) => time is { } t ? new SimTime(Rounded(t.Seconds)) : null;
}
