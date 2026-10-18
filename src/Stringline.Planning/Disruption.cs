using System.Text.Json.Serialization;
using Stringline.Core;

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
/// needed. S13 adds the UI and drawing; the forecaster honours it from S11/S13.
/// </summary>
/// <param name="Id">Unique within the plan.</param>
/// <param name="Track">The track it applies to, or null for all.</param>
/// <param name="Start">From this sim time (inclusive).</param>
/// <param name="End">Until this sim time (exclusive).</param>
/// <param name="RunTimeFactor">Minimum run times are multiplied by this (1 to 100) during a speed restriction.</param>
/// <param name="AddedSeconds">Seconds added to each minimum run time during a speed restriction.</param>
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
    string Note = "")
{
    /// <summary>The reason this disruption cannot be used, or null if it is valid.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(Id) ? "A disruption needs an ID."
        : End <= Start ? "A disruption must end after it starts."
        : double.IsNaN(RunTimeFactor) || RunTimeFactor < 1 || RunTimeFactor > 100 ? "The running-time factor must be between 1 and 100."
        : AddedSeconds < 0 ? "Added seconds cannot be negative."
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
    public bool Covers(IReadOnlyList<PlannedPoint> points, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(points);
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
                return true;
            }

            if (points[k].Location == near)
            {
                return false;
            }
        }

        return false;
    }

    public override string ToString() => $"{Kind} {From}–{To} {Start}–{End}";
}
