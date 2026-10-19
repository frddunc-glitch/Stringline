using System.Text.Json.Serialization;
using Stringline.Core;
using Stringline.Network;

namespace Stringline.Planning;

[JsonConverter(typeof(JsonStringEnumConverter<DisruptionKind>))]
public enum DisruptionKind
{
    /// <summary>A block or possession: no train may run through (DIS-1). Raised as a conflict (S14), never clamped.</summary>
    Block,

    /// <summary>A speed restriction: running takes longer (DIS-2), by <see cref="Disruption.RunTimeFactor"/> and <see cref="Disruption.AddedSeconds"/>.</summary>
    SpeedRestriction,
}

/// <summary>
/// A disruption between two locations for a time range (<c>04-…md</c>). It covers a train's runs between its calls at
/// <see cref="From"/> and <see cref="To"/> (either way round, and every run in between), so no network topology is
/// needed. The forecaster and the plan's clamping honour it; S13 draws it and edits it on the graph.
/// </summary>
/// <param name="Id">Unique within the plan.</param>
/// <param name="Track">
/// The track it applies to, or null for all lines (Fred, S13). With a <paramref name="Direction"/> it names a corridor
/// track (such as "Down" or "Single") and applies to trains running that way; without one it is a WTT line code
/// (such as "UF") and applies to trains planned on that line out of the point they run from.
/// </param>
/// <param name="Start">From this sim time (inclusive).</param>
/// <param name="End">Until this sim time (exclusive).</param>
/// <param name="RunTimeFactor">Minimum run times are multiplied by this (1 to 100) during a speed restriction.</param>
/// <param name="AddedSeconds">Seconds added to each minimum run time during a speed restriction.</param>
/// <param name="Note">Free text, such as the reason.</param>
/// <param name="Direction">
/// The way the named <paramref name="Track"/> runs: <see cref="TrackDirection.Down"/> for trains running from
/// <see cref="From"/> towards <see cref="To"/>, <see cref="TrackDirection.Up"/> the other way, and
/// <see cref="TrackDirection.BiDirectional"/> for both (a single line). Null when the track is a WTT line code. A
/// disruption made on the graph has <see cref="From"/> at the corridor's Up end, so Down and Up are the corridor's.
/// </param>
public sealed record Disruption(
    string Id,
    DisruptionKind Kind,
    Tiploc From,
    Tiploc To,
    string? Track,
    SimTime Start,
    SimTime End,
    double RunTimeFactor = 1,
    int AddedSeconds = 0,
    string Note = "",
    TrackDirection? Direction = null)
{
    /// <summary>The reason this disruption cannot be used, or null if it is valid.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(Id) ? "A disruption needs an ID."
        : End <= Start ? "A disruption must end after it starts."
        : double.IsNaN(RunTimeFactor) || RunTimeFactor < 1 || RunTimeFactor > 100 ? "The running-time factor must be between 1 and 100."
        : AddedSeconds < 0 ? "Added seconds cannot be negative."
        : Track is not null && string.IsNullOrWhiteSpace(Track) ? "Give the track a name, or none for all lines."
        : Direction is not null && Track is null ? "A track direction needs the track's name."
        : null;

    /// <summary>True if a run departing at <paramref name="depart"/> falls in this disruption's time range.</summary>
    public bool IsActiveAt(SimTime depart) => depart >= Start && depart < End;

    /// <summary>A minimum run time as lengthened by this speed restriction (unchanged by a block).</summary>
    public int Lengthen(int minRunSeconds) => Kind == DisruptionKind.SpeedRestriction
        ? (int)Math.Ceiling(minRunSeconds * RunTimeFactor) + AddedSeconds
        : minRunSeconds;

    /// <summary>
    /// True if the run of <paramref name="points"/> from index <paramref name="from"/> to <paramref name="to"/> lies
    /// between a call at one end and the next call at the other. When <see cref="From"/> equals <see cref="To"/>, true
    /// if the run starts or ends there.
    /// </summary>
    public bool Covers(IReadOnlyList<PlannedPoint> points, int from, int to) => Covers(points, from, to, out _);

    /// <summary>
    /// True if this disruption covers the run (<see cref="Covers(IReadOnlyList{PlannedPoint}, int, int)"/>) and the
    /// train is on its track: any train if it has none, a train running the track's way if it has a
    /// <see cref="Direction"/>, else a train whose planned line out of point <paramref name="from"/> is the track. For a
    /// disruption at one location (<see cref="From"/> equals <see cref="To"/>), a direction applies to every train.
    /// </summary>
    public bool AppliesTo(IReadOnlyList<PlannedPoint> points, int from, int to)
    {
        if (!Covers(points, from, to, out var forward))
        {
            return false;
        }

        return Track is null || Direction switch
        {
            TrackDirection.Down => forward != false,
            TrackDirection.Up => forward != true,
            TrackDirection.BiDirectional => true,
            _ => string.Equals(Track, points[from].Line, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <param name="forward">True if the train runs from <see cref="From"/> to <see cref="To"/>, false the other way; null at one location.</param>
    private bool Covers(IReadOnlyList<PlannedPoint> points, int from, int to, out bool? forward)
    {
        ArgumentNullException.ThrowIfNull(points);
        forward = null;
        if (From == To)
        {
            return points[from].Location == From || points[to].Location == From;
        }

        var start = -1;
        for (var k = from; k >= 0; k--)
        {
            if (points[k].Location == From || points[k].Location == To)
            {
                start = k;
                break;
            }
        }

        if (start < 0)
        {
            return false;
        }

        var near = points[start].Location;
        var far = near == From ? To : From;
        for (var k = to; k < points.Count; k++)
        {
            if (points[k].Location == far)
            {
                forward = near == From;
                return true;
            }

            if (points[k].Location == near)
            {
                return false;
            }
        }

        return false;
    }

    public override string ToString() => $"{Kind} {From}–{To}{(Track is null ? string.Empty : " " + Track)} {Start}–{End}";
}
