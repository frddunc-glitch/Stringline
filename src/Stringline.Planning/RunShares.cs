using Stringline.Core;

namespace Stringline.Planning;

/// <summary>
/// How far along a train's run from one timed point to the next a location is, in minimum run time (S36, S38): shared
/// by the forecaster's anchors and the places a train can be held at a signal (S33), so they agree. Pure.
/// </summary>
internal static class RunShares
{
    /// <summary>
    /// 0 at <paramref name="c"/>, 1 at <paramref name="n"/>, a plan point's share of the minimums (else of the planned
    /// times), or an unbooked timing location's share (<see cref="Unbooked"/>). Null if it is not on the way.
    /// </summary>
    public static double? Of(IReadOnlyList<PlannedPoint> points, MinRunTable mins, ForecastNetwork network, int c, int n, Tiploc location)
    {
        if (location == points[c].Location)
        {
            return 0;
        }

        for (var j = c + 1; j <= n; j++)
        {
            if (points[j].Location != location)
            {
                continue;
            }

            if (j == n)
            {
                return 1;
            }

            if (mins.Along(points, c, n) is { } whole && whole > 0 && mins.Along(points, c, j) is { } part)
            {
                return (double)part / whole;
            }

            return points[j].FirstTime is { } t && points[c].LastTime is { } d && points[n].FirstTime is { } a && a > d
                ? Math.Clamp((double)(t - d) / (a - d), 0, 1)
                : null;
        }

        return Unbooked(points, mins, network, c, n, location);
    }

    /// <summary>
    /// An unbooked timing location's share of the run (S36), if it lies on the way: between two of the plan's points
    /// from c to n on a corridor, or in the timetable's location graph. Its share comes from the corridor's section
    /// minimums, else the minimum runs either side of it; null if neither says where it is.
    /// </summary>
    public static double? Unbooked(IReadOnlyList<PlannedPoint> points, MinRunTable mins, ForecastNetwork network, int c, int n, Tiploc via)
    {
        for (var j = c; j < n; j++)
        {
            var (a, b) = (points[j].Location, points[j + 1].Location);
            var share = network.ShareAt(a, via, b);
            if (share is null && network.Links(a, via, b))
            {
                share = mins.Between(a, via) is { } x && mins.Between(via, b) is { } y && x + y > 0 ? (double)x / (x + y)
                    : mins.Between(via, b) is { } rest && mins.Between(a, b) is { } all && all > 0 ? Math.Clamp(1 - ((double)rest / all), 0, 1)
                    : null;
            }

            if (share is not { } s)
            {
                continue;
            }

            // As a share of the whole run from c to n: the steps before j, then its part of this one.
            double before = j == c ? 0 : mins.Along(points, c, j) ?? 0;
            double step = mins.Along(points, j, j + 1) ?? 0;
            double after = j + 1 < n ? mins.Along(points, j + 1, n) ?? 0 : 0;
            var whole = before + step + after;
            return Math.Clamp(whole > 0 ? (before + (s * step)) / whole : s, 0, 1);
        }

        return null;
    }
}
