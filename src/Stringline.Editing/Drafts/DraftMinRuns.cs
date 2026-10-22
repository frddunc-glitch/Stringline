using Stringline.Core.Timetables;
using Stringline.Editing.Knowledge;
using Stringline.Planning;

namespace Stringline.Editing.Drafts;

/// <summary>
/// The minimum run a drag on the draft board stops at (S44, Fred): the least run the sim is known to take for the
/// train's speed band and freight flag (<see cref="SimKnowledge.LeastRun"/>, from its other timetables and play), else
/// the least the original timetable books between the points (<see cref="MinRunTable"/>). Shift lets a drag past it.
/// </summary>
public static class DraftMinRuns
{
    /// <summary>The least run of a train of <paramref name="draft"/> between two of its points, or null if none is known.</summary>
    /// <param name="original">The timetable as the draft was opened (its <see cref="MinRunTable"/>).</param>
    public static Func<TrainPlan, int, int, int?> For(SimKnowledge? knowledge, MinRunTable original, WorkingTimetable draft)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(draft);
        return (plan, from, to) =>
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (from < 0 || to >= plan.Points.Count || from >= to)
            {
                return null;
            }

            if (knowledge is not null && draft.Find(plan.Id) is { } train)
            {
                var along = plan.Points.Skip(from).Take(to - from + 1).Select(p => p.Location).ToList();
                if (knowledge.LeastRun(along, RunBand.Of(train)) is { } known)
                {
                    return known;
                }
            }

            return original.Along(plan.Points, from, to);
        };
    }
}
