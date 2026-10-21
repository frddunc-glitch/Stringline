using System.Text.Json.Serialization;

namespace Stringline.Network;

/// <summary>Where a section's headway comes from (<c>06-…md</c> §3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HeadwaySource>))]
public enum HeadwaySource
{
    /// <summary>The profile's default headway, or the global one in Tools ▸ Options: the WTT gave no usable figure.</summary>
    Default,

    /// <summary>Derived from the spacing the WTT books trains at (<see cref="BookedHeadway"/>), being less than the default; re-derived on every refresh.</summary>
    Wtt,

    /// <summary>Set by hand in the profile editor; kept when the corridor is refreshed or regenerated.</summary>
    Hand,

    /// <summary>
    /// Measured from the signal blocks learned from play (S39, #19; <c>BlockHeadways</c>): above or below the default,
    /// and used ahead of the WTT's.
    /// </summary>
    Blocks,
}

/// <summary>Wording for <see cref="HeadwaySource"/>.</summary>
public static class HeadwaySources
{
    /// <summary>"set by hand", "from the WTT", "from signal blocks", "default": for "Headway: 2 min (from the WTT)".</summary>
    public static string Describe(HeadwaySource source) => source switch
    {
        HeadwaySource.Hand => "set by hand",
        HeadwaySource.Wtt => "from the WTT",
        HeadwaySource.Blocks => "from signal blocks",
        _ => "default",
    };

    /// <summary>"Hand", "WTT", "Blocks", "Default": for a column.</summary>
    public static string Name(HeadwaySource source) => source switch
    {
        HeadwaySource.Hand => "Hand",
        HeadwaySource.Wtt => "WTT",
        HeadwaySource.Blocks => "Blocks",
        _ => "Default",
    };
}

/// <summary>
/// The headway a WTT books trains to in one section (<c>06-…md</c> §3): the least booked spacing between trains the same
/// way on the same track, taken as a low percentile so that one tight pair does not set it. It is used only where it
/// is less than the default (<see cref="Section.ResolveHeadway"/>): trains booked far apart on a quiet line say nothing
/// about how close the signalling lets them run.
/// </summary>
/// <param name="Seconds">The figure, or null if the WTT gives none (fewer than <see cref="MinPairs"/> pairs).</param>
/// <param name="Pairs">Booked pairs one behind the other on a track that the figure comes from.</param>
/// <param name="SpacingSeconds">The <see cref="Percentile"/>th percentile of their spacing before the floor, or null if there are no pairs.</param>
/// <param name="ClosestSeconds">The closest pair's spacing, or null if there are no pairs.</param>
public sealed record BookedHeadway(int? Seconds, int Pairs, int? SpacingSeconds, int? ClosestSeconds = null)
{
    /// <summary>The percentile of booked spacings taken as the headway.</summary>
    public const double Percentile = 5;

    /// <summary>The least headway derived: a closer spacing is taken as this.</summary>
    public const int FloorSeconds = 60;

    /// <summary>The fewest pairs a figure is derived from.</summary>
    public const int MinPairs = 10;

    /// <summary>Pairs booked further apart than this are not following each other, so they are not counted.</summary>
    public const int MaxSpacingSeconds = 30 * 60;

    public static BookedHeadway None { get; } = new(null, 0, null);

    /// <summary>
    /// The headway from booked windows (enter and leave, in seconds) through one section, each list one direction.
    /// Each direction's runs are put on its tracks as the forecaster sees them (<c>ForecastNetwork</c>): in entry
    /// order, on the track that has been free longest, with as many tracks as the WTT needs at once. A pair's spacing is
    /// the lesser of its spacing at the two ends.
    /// </summary>
    public static BookedHeadway Derive(params IEnumerable<(int Enter, int Exit)>[] directions)
    {
        ArgumentNullException.ThrowIfNull(directions);
        var spacings = new List<int>();
        foreach (var runs in directions)
        {
            AddSpacings(runs.ToList(), spacings);
        }

        if (spacings.Count == 0)
        {
            return None;
        }

        spacings.Sort();
        var rank = (int)Math.Ceiling(Percentile / 100 * spacings.Count) - 1;
        var spacing = spacings[Math.Clamp(rank, 0, spacings.Count - 1)];
        int? seconds = spacings.Count >= MinPairs ? Math.Max(FloorSeconds, spacing) : null;
        return new BookedHeadway(seconds, spacings.Count, spacing, spacings[0]);
    }

    /// <summary>
    /// How many tracks one direction's booked runs need side by side: put on tracks in entry order, a run sharing a
    /// track with one entering or leaving within <paramref name="parallelSeconds"/> of it (or overtaking it) needs another.
    /// </summary>
    public static int TracksNeeded(List<(int Enter, int Exit)> runs, int parallelSeconds = ParallelSeconds)
    {
        ArgumentNullException.ThrowIfNull(runs);
        runs.Sort();
        var last = new List<(int Enter, int Exit)>();
        foreach (var run in runs)
        {
            var track = last.FindIndex(t => run.Enter >= t.Enter + parallelSeconds && run.Exit >= t.Exit + parallelSeconds);
            if (track < 0)
            {
                last.Add(run);
            }
            else
            {
                last[track] = run;
            }
        }

        return last.Count;
    }

    /// <summary>Two booked runs the same way entering or leaving a section closer together than this are on different tracks.</summary>
    public const int ParallelSeconds = 60;

    private static void AddSpacings(List<(int Enter, int Exit)> runs, List<int> spacings)
    {
        var tracks = TracksNeeded(runs);
        if (tracks == 0)
        {
            return;
        }

        // Each run follows the run last on the track free longest that it fits behind.
        var last = new List<(int Enter, int Exit)>(tracks);
        foreach (var run in runs)
        {
            var best = -1;
            for (var t = 0; t < last.Count; t++)
            {
                if (run.Enter >= last[t].Enter + ParallelSeconds && run.Exit >= last[t].Exit + ParallelSeconds && (best < 0 || last[t].Enter < last[best].Enter))
                {
                    best = t;
                }
            }

            if (best < 0)
            {
                last.Add(run);
                continue;
            }

            var spacing = Math.Min(run.Enter - last[best].Enter, run.Exit - last[best].Exit);
            if (spacing <= MaxSpacingSeconds)
            {
                spacings.Add(spacing);
            }

            last[best] = run;
        }
    }
}
