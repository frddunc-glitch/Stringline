using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>
/// How a drag on a board snaps and where it stops (S44): a session board's drags snap to 15 s (1 s with Shift) and
/// stop at the plan's minimum run; a draft board's snap to half minutes, as SimSig times are, and stop at the least
/// run the sim is known to take, which Shift lets them past.
/// </summary>
public sealed record PlanDragRules
{
    /// <summary>A session board's drags (S12, S26).</summary>
    public static PlanDragRules Session { get; } = new();

    /// <summary>A draft board's drags (S44, Fred): half minutes, and Shift goes past the minimum run with a warning.</summary>
    public static PlanDragRules Draft { get; } = new()
    {
        SnapSeconds = 30,
        FineSnapSeconds = 30,
        NudgeSeconds = 30,
        FinePassesMinRun = true,
        EditsTimetable = true,
    };

    /// <summary>Times snap to this many seconds while dragging.</summary>
    public int SnapSeconds { get; init; } = PlanDrag.SnapSeconds;

    /// <summary>Times snap to this many seconds with Shift held.</summary>
    public int FineSnapSeconds { get; init; } = 1;

    /// <summary>An arrow key nudges a picked grip this far (Shift: a minute).</summary>
    public int NudgeSeconds { get; init; } = PlanDrag.SnapSeconds;

    /// <summary>Shift lets a drag go past the minimum run (with a warning) rather than snapping finer.</summary>
    public bool FinePassesMinRun { get; init; }

    /// <summary>
    /// The drag edits the booked times (a draft), not a running plan: a train SimSig would not run by its start rules can
    /// still be moved.
    /// </summary>
    public bool EditsTimetable { get; init; }

    /// <summary>
    /// The least time a train may take between two of its points (from, to), or null if none is known: the minimum run
    /// a drag stops at. Null takes the plan's own (<see cref="WorkingPlan.MinRuns"/>); a draft board asks the sim's
    /// knowledge first (S44).
    /// </summary>
    public Func<TrainPlan, int, int, int?>? MinRun { get; init; }
}
