using Stringline.Core;
using Stringline.Network.Profiles;

namespace Stringline.Planning;

/// <summary>The moves a train makes in the working plan, as the assist feature (S19) remembers routes by. Pure.</summary>
public static class AssistMoves
{
    /// <summary>
    /// The move at the plan point <paramref name="index"/>: its location, platform and line, and the nearest other
    /// locations before and after it on the train's running path (none past where it is terminated short).
    /// </summary>
    public static AssistMove Of(TrainPlan plan, int index)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var points = plan.RunningPoints;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, points.Count);
        var point = points[index];
        Tiploc? from = null;
        for (var k = index - 1; k >= 0 && from is null; k--)
        {
            from = points[k].Location != point.Location ? points[k].Location : null;
        }

        Tiploc? towards = null;
        for (var k = index + 1; k < points.Count && towards is null; k++)
        {
            towards = points[k].Location != point.Location ? points[k].Location : null;
        }

        return AssistMove.Of(point.Location, point.Platform, point.Line, from, towards);
    }
}
