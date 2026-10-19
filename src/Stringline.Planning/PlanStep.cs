using Stringline.Core;

namespace Stringline.Planning;

public enum PlanStepKind
{
    /// <summary>A command was applied (it clears the redo list).</summary>
    Execute,

    /// <summary>The last applied command was undone.</summary>
    Undo,

    /// <summary>The last undone command was applied again.</summary>
    Redo,

    /// <summary>The history was dropped: a new session, or a different timetable.</summary>
    Reset,
}

/// <summary>
/// One change to the plan's edit history, as the session store keeps it (S16): <see cref="PlanningEngine.PlanStepped"/>
/// raises one for every applied command, undo, redo and reset, and <see cref="PlanningEngine.RestorePlan"/> rebuilds the
/// history from a list of them.
/// </summary>
/// <param name="Command">The command, for <see cref="PlanStepKind.Execute"/>.</param>
/// <param name="Description">The history's text for the command, as it was described when applied.</param>
/// <param name="SimTime">The sim clock when it happened (the latest reported), or null before the clock is known.</param>
/// <param name="At">The wall time when it happened.</param>
public sealed record PlanStep(PlanStepKind Kind, IPlanCommand? Command = null, string? Description = null, SimTime? SimTime = null, DateTimeOffset At = default)
{
    public static PlanStep Execute(IPlanCommand command, string? description = null, SimTime? simTime = null, DateTimeOffset at = default) =>
        new(PlanStepKind.Execute, command ?? throw new ArgumentNullException(nameof(command)), description, simTime, at);
}

/// <summary>Pure helpers over a session's plan steps.</summary>
public static class PlanLog
{
    /// <summary>
    /// The history the steps leave: the commands applied (oldest first) and those that can be redone (next first),
    /// each with its description. Only the steps after the last reset count.
    /// </summary>
    public static (IReadOnlyList<PlanStep> Done, IReadOnlyList<PlanStep> Undone) Fold(IEnumerable<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var done = new List<PlanStep>();
        var undone = new List<PlanStep>(); // next redo last
        foreach (var step in steps)
        {
            switch (step.Kind)
            {
                case PlanStepKind.Execute when step.Command is not null:
                    done.Add(step);
                    undone.Clear();
                    break;
                case PlanStepKind.Undo when done.Count > 0:
                    undone.Add(done[^1]);
                    done.RemoveAt(done.Count - 1);
                    break;
                case PlanStepKind.Redo when undone.Count > 0:
                    done.Add(undone[^1]);
                    undone.RemoveAt(undone.Count - 1);
                    break;
                case PlanStepKind.Reset:
                    done.Clear();
                    undone.Clear();
                    break;
            }
        }

        undone.Reverse();
        return (done, undone);
    }

    /// <summary>
    /// The steps that had happened by sim time <paramref name="now"/> (post-run review, SES-3): those made at or before
    /// it, in order, and those made before the clock was known. With no time, only the latter.
    /// </summary>
    public static IReadOnlyList<PlanStep> By(IEnumerable<PlanStep> steps, SimTime? now)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return [.. steps.Where(s => s.SimTime is not { } t || (now is { } n && t <= n))];
    }
}
