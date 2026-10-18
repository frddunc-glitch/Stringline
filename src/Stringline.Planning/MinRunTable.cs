using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network;

namespace Stringline.Planning;

/// <summary>
/// The fastest booked run between each pair of locations a WTT train runs between directly (Fred, S10): from the
/// departure or pass at one to the arrival or pass at the next, less the allowances booked in between. Either
/// direction counts. Re-plans are clamped to these (<see cref="WorkingPlanBuilder"/>).
/// </summary>
public sealed class MinRunTable
{
    private readonly Dictionary<LocationPair, int> _minimums;

    private MinRunTable(Dictionary<LocationPair, int> minimums) => _minimums = minimums;

    public static MinRunTable Empty { get; } = new([]);

    /// <summary>The number of location pairs with a minimum.</summary>
    public int Count => _minimums.Count;

    public static MinRunTable Build(WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        var minimums = new Dictionary<LocationPair, int>();
        foreach (var train in timetable.Trains)
        {
            for (var i = 1; i < train.Points.Count; i++)
            {
                var from = train.Points[i - 1];
                var to = train.Points[i];
                if (from.Location == to.Location || from.LastTime is not { } depart || to.FirstTime is not { } arrive)
                {
                    continue;
                }

                var run = arrive - depart - from.AllowanceSeconds;
                if (run <= 0)
                {
                    continue;
                }

                var pair = new LocationPair(from.Location, to.Location);
                if (!minimums.TryGetValue(pair, out var best) || run < best)
                {
                    minimums[pair] = run;
                }
            }
        }

        return new MinRunTable(minimums);
    }

    /// <summary>The fastest run between two locations next to each other in some train's booking, or null if none is known.</summary>
    public int? Between(Tiploc a, Tiploc b) =>
        a == b ? 0 : _minimums.TryGetValue(new LocationPair(a, b), out var seconds) ? seconds : null;

    /// <summary>
    /// The minimum run of a train between two of its points: the sum over each step in between (untimed points too),
    /// or, if a step is unknown, the direct pair's minimum. Null if neither is known.
    /// </summary>
    public int? Along(IReadOnlyList<PlannedPoint> points, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(points);
        var total = 0;
        for (var k = from + 1; k <= to; k++)
        {
            if (Between(points[k - 1].Location, points[k].Location) is not { } step)
            {
                return Between(points[from].Location, points[to].Location);
            }

            total += step;
        }

        return total;
    }
}
