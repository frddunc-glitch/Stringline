namespace Stringline.Network;

/// <summary>Computes axis positions (0 at the first location, 1 at the last) for a corridor.</summary>
public static class CorridorAxis
{
    /// <summary>
    /// The axis position of each location. <see cref="AxisMode.Derived"/> spaces them by the cumulative
    /// <see cref="Section.MinRunSeconds"/>. <see cref="AxisMode.Distance"/> uses the configured
    /// <see cref="CorridorLocation.DistanceKm"/>: between two known distances, positions are interpolated by run time,
    /// and beyond the outer ones they are extrapolated at the average km per second of the known stretch. With fewer
    /// than two distinct known distances, the derived axis is used.
    /// </summary>
    public static IReadOnlyList<double> Positions(Corridor corridor)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        return Positions(corridor.Locations.Select(l => l.DistanceKm).ToList(), corridor.Sections.Select(s => s.MinRunSeconds).ToList(), corridor.Axis);
    }

    /// <summary>The corridor with every <see cref="CorridorLocation.AxisPosition"/> recomputed.</summary>
    public static Corridor Apply(Corridor corridor)
    {
        var positions = Positions(corridor);
        return corridor with { Locations = corridor.Locations.Select((l, i) => l with { AxisPosition = positions[i] }).ToList() };
    }

    /// <param name="distancesKm">One per location.</param>
    /// <param name="runSeconds">One per section (locations − 1).</param>
    public static IReadOnlyList<double> Positions(IReadOnlyList<double?> distancesKm, IReadOnlyList<int> runSeconds, AxisMode mode)
    {
        ArgumentNullException.ThrowIfNull(distancesKm);
        ArgumentNullException.ThrowIfNull(runSeconds);
        var n = distancesKm.Count;
        if (n > 0 && runSeconds.Count != n - 1)
        {
            throw new ArgumentException($"Expected {n - 1} section run times for {n} locations, got {runSeconds.Count}.", nameof(runSeconds));
        }

        if (n == 0)
        {
            return [];
        }

        if (n == 1)
        {
            return [0.0];
        }

        // Cumulative run time; a section of 0 s or less still gets a sliver so locations never coincide.
        var time = new double[n];
        for (var i = 1; i < n; i++)
        {
            time[i] = time[i - 1] + Math.Max(1, runSeconds[i - 1]);
        }

        var km = mode == AxisMode.Distance ? Interpolate(distancesKm, time) : null;
        var axis = km ?? time;
        var span = axis[^1] - axis[0];
        return axis.Select(x => (x - axis[0]) / span).ToArray();
    }

    /// <summary>Kilometres at every location from the known ones, or null if fewer than two distinct ones are known.</summary>
    private static double[]? Interpolate(IReadOnlyList<double?> known, double[] time)
    {
        var indices = Enumerable.Range(0, known.Count).Where(i => known[i] is { } d && double.IsFinite(d)).ToList();
        if (indices.Count < 2 || known[indices[0]] == known[indices[^1]])
        {
            return null;
        }

        var km = new double[known.Count];
        foreach (var i in indices)
        {
            km[i] = known[i]!.Value;
        }

        for (var k = 1; k < indices.Count; k++)
        {
            int a = indices[k - 1], b = indices[k];
            for (var i = a + 1; i < b; i++)
            {
                km[i] = km[a] + ((km[b] - km[a]) * (time[i] - time[a]) / (time[b] - time[a]));
            }
        }

        int first = indices[0], last = indices[^1];
        var rate = (km[last] - km[first]) / (time[last] - time[first]);
        for (var i = 0; i < first; i++)
        {
            km[i] = km[first] - (rate * (time[first] - time[i]));
        }

        for (var i = last + 1; i < km.Length; i++)
        {
            km[i] = km[last] + (rate * (time[i] - time[last]));
        }

        return km;
    }
}
