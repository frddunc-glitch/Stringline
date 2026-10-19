using Stringline.Core;
using Stringline.Network;

namespace Stringline.Planning;

/// <summary>A track a disruption can be put on: all lines, a corridor track (with its direction) or a WTT line code.</summary>
/// <param name="Track">The disruption's <see cref="Disruption.Track"/>; null for all lines.</param>
/// <param name="Direction">The disruption's <see cref="Disruption.Direction"/>; null for all lines or a line code.</param>
/// <param name="Label">How the choice reads in a list, e.g. "Down (Down trains)" or "UF (WTT line)".</param>
public sealed record TrackChoice(string? Track, TrackDirection? Direction, string Label)
{
    public static TrackChoice AllLines { get; } = new(null, null, "All lines");

    /// <summary>The choice for a disruption's track, worded as <see cref="DisruptionTracks.For"/> words it.</summary>
    public static TrackChoice Of(string? track, TrackDirection? direction) =>
        track is null ? AllLines : new(track, direction, $"{track} ({Describe(direction)})");

    internal static string Describe(TrackDirection? direction) => direction switch
    {
        TrackDirection.Down => "Down trains",
        TrackDirection.Up => "Up trains",
        TrackDirection.BiDirectional => "both ways",
        _ => "WTT line",
    };

    public override string ToString() => Label;
}

/// <summary>
/// The tracks a block or speed restriction between two corridor locations can be put on (Fred, S13): all lines, the
/// tracks the profile gives the sections between them (Up and Down, or Single), and the WTT line codes the plan's
/// trains use between them, most used first.
/// </summary>
public static class DisruptionTracks
{
    public static IReadOnlyList<TrackChoice> For(Corridor corridor, Tiploc from, Tiploc to, WorkingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        ArgumentNullException.ThrowIfNull(plan);
        var choices = new List<TrackChoice> { TrackChoice.AllLines };
        int i = corridor.IndexOf(from), j = corridor.IndexOf(to);
        if (i < 0 || j < 0)
        {
            return choices;
        }

        var (lo, hi) = (Math.Min(i, j), Math.Max(i, j));
        for (var s = lo; s < hi; s++)
        {
            foreach (var track in corridor.Sections[s].Tracks)
            {
                if (!choices.Exists(c => c.Direction is not null && string.Equals(c.Track, track.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    choices.Add(TrackChoice.Of(track.Name, track.Direction));
                }
            }
        }

        var lines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var train in plan.Trains)
        {
            var points = train.Points;
            for (var k = 0; k + 1 < points.Count; k++)
            {
                if (points[k].Line is { Length: > 0 } line && Within(corridor, points[k].Location, lo, hi) &&
                    Within(corridor, points[k + 1].Location, lo, hi) && points[k].Location != points[k + 1].Location)
                {
                    lines[line] = lines.GetValueOrDefault(line) + 1;
                }
            }
        }

        foreach (var line in lines.OrderByDescending(l => l.Value).ThenBy(l => l.Key, StringComparer.Ordinal).Select(l => l.Key))
        {
            choices.Add(TrackChoice.Of(line, null));
        }

        return choices;
    }

    private static bool Within(Corridor corridor, Tiploc location, int lo, int hi) => corridor.IndexOf(location) is var k && k >= lo && k <= hi;
}
