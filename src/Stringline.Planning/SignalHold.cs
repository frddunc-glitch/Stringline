using System.Globalization;
using Stringline.Core;
using Stringline.Network.Profiles;

namespace Stringline.Planning;

/// <summary>
/// A train held at a signal between two of its timing points until a time (S33, Q12). SimSig can hold a train at any
/// signal, so a train already on its way to a junction can still be held outside it, which the WTT's timing points
/// cannot do. Part of the train's plan (<see cref="TrainPlan.SignalHolds"/>), made by <see cref="HoldAtSignal"/>.
/// </summary>
/// <param name="Index">The plan point the run with the signal starts from; the hold is on the run on to the next timed point.</param>
/// <param name="Signal">The signal's ID, as SimSig names it (<c>S457</c>).</param>
/// <param name="From">The timing location its learned place is measured from (<see cref="SignallingPlace.From"/>).</param>
/// <param name="To">The timing location its place is measured to.</param>
/// <param name="Fraction">Its place from <paramref name="From"/> to <paramref name="To"/>, in run time, for drawing.</param>
/// <param name="Share">
/// How far along the run from <paramref name="Index"/> to the next timed point it is, in minimum run time (0–1). Kept
/// with the hold, so the plan forecasts the same however the learned places change later.
/// </param>
/// <param name="Until">The train does not leave the signal before this.</param>
public sealed record SignalHold(int Index, string Signal, Tiploc From, Tiploc To, double Fraction, double Share, SimTime Until)
{
    public bool Is(int index, string signal) => Index == index && string.Equals(Signal, signal?.Trim(), StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"at {Signal} until {Until.ToString("H", CultureInfo.InvariantCulture)}";
}

/// <summary>A signal hold as the forecast runs it: when the train gets to the signal, and when it leaves.</summary>
/// <param name="ToIndex">The next timed point after the signal: the end of the run it is held on.</param>
/// <param name="Reaches">When the train is forecast to get to the signal.</param>
public sealed record SignalHoldForecast(SignalHold Hold, int ToIndex, SimTime Reaches)
{
    public string Signal => Hold.Signal;

    public SimTime Until => Hold.Until;

    /// <summary>How long it stands at the signal.</summary>
    public int Seconds => Until - Reaches;

    public override string ToString() => $"held at {Signal} {Reaches:T}–{Until:T}";
}

/// <summary>A placed signal a train could be held at on one of its runs, and how far along the run it is.</summary>
/// <param name="ToIndex">The next timed point after it.</param>
/// <param name="Share">How far along the run it is, in minimum run time (0–1).</param>
public sealed record SignalOnRun(SignallingPlace Place, int Index, int ToIndex, double Share)
{
    public string Signal => Place.Id;

    /// <summary>How much of the run lies between the two locations its place was learned between.</summary>
    internal double Span { get; init; } = 1;

    /// <summary>The hold at this signal until <paramref name="until"/>.</summary>
    public SignalHold Hold(SimTime until) => new(Index, Place.Id, Place.From, Place.To, Place.Fraction, Math.Round(Share, 4), until);
}

/// <summary>Where trains can be held at signals (S33), shared by the suggester, the optimiser and the app. Pure.</summary>
public static class SignalHolds
{
    /// <summary>A signal this close to either end of its run is not offered: holding there is holding at the timing point.</summary>
    public const double EndShare = 0.02;

    /// <summary>
    /// The usable (used or hand-set, S37) signals on the run from point <paramref name="index"/> to the next timed point,
    /// in order along it, one place for each: learned between two locations on the run (its points, or timing
    /// locations it passes that the plan does not book, S36), for the kind of run the train makes there if learned.
    /// Signals of every line are listed. Empty if the run has no next timed point.
    /// </summary>
    public static IReadOnlyList<SignalOnRun> OnRun(TrainPlan plan, int index, MinRunTable minRuns, ForecastNetwork network)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(minRuns);
        ArgumentNullException.ThrowIfNull(network);
        var points = plan.RunningPoints;
        var places = network.Places;
        if (places.IsEmpty || index < 0 || index >= points.Count - 1 || !points[index].IsTimed || NextTimed(points, index) is not { } n)
        {
            return [];
        }

        var shares = new Dictionary<Tiploc, double?>();
        double? ShareOf(Tiploc t) => shares.TryGetValue(t, out var s) ? s : shares[t] = RunShares.Of(points, minRuns, network, index, n, t);

        var found = new Dictionary<string, SignalOnRun>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in places.Signals.GroupBy(p => (Id: p.Id.ToUpperInvariant(), p.From, p.To)))
        {
            var (_, from, to) = group.Key;
            if (ShareOf(from) is not { } fromShare || ShareOf(to) is not { } toShare || toShare <= fromShare)
            {
                continue;
            }

            var departs = from == points[index].Location && !points[index].IsPass;
            var arrives = to == points[n].Location && !points[n].IsPass;
            if (UsablePlaces.Choose(group, SignallingPlaces.RunOf(departs, arrives)) is not { } place)
            {
                continue;
            }

            // Learned between other locations too (by trains that did not report one between): the narrowest pair says most.
            var share = fromShare + (place.Fraction * (toShare - fromShare));
            if (share > EndShare && share < 1 - EndShare &&
                (!found.TryGetValue(place.Id, out var known) || toShare - fromShare < known.Span))
            {
                found[place.Id] = new SignalOnRun(place, index, n, share) { Span = toShare - fromShare };
            }
        }

        return [.. found.Values.OrderBy(s => s.Share).ThenBy(s => s.Signal, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The signals on the run the train is on now (from its last actual point) that it has not yet passed, by its
    /// forecast: past where it was last known (<see cref="TrainForecast.Anchor"/>), or the one it is standing at (S38).
    /// From a signal it passed (or a track circuit's block signal, or a berth's own signal), they are its line's: the
    /// signals trains passed next after it, in turn (<see cref="SignallingPlace.Next"/>), not a parallel line's at a
    /// similar place. Empty if it is not running or not between two points.
    /// </summary>
    public static IReadOnlyList<SignalOnRun> Ahead(TrainPlan plan, TrainForecast forecast, MinRunTable minRuns, ForecastNetwork network)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(forecast);
        if (forecast.Status != TrainForecastStatus.Running || CurrentRun(forecast) is not { } c)
        {
            return [];
        }

        var run = OnRun(plan, c, minRuns, network);
        var anchor = forecast.Anchor is { } a && a.After == c ? a : null;
        var reached = anchor?.Share ?? 0;
        var start = forecast.Standing?.Signal ?? anchor?.Place switch
        {
            { Kind: SignallingKind.Signal } p => p.Id,
            { Block: { } block } => block,
            _ => null,
        };
        var line = start is null ? null : Line(run, start, network.Places.Signals.Any(p => p.Next is not null));
        var passed = anchor?.Place is { Kind: SignallingKind.Signal } p0 ? p0.Id : null;
        return [.. (line ?? run).Where(s => IsStandingAt(forecast, s.Signal) ||
            (s.Share > reached + 1e-6 && !string.Equals(s.Signal, passed, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// The plan point a running train has left and not yet reached the next timed point after: its last point with an
    /// actual departure or pass, followed by a forecast one. Null if it is standing at a point or not running.
    /// </summary>
    public static int? CurrentRun(TrainForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        var first = forecast.FirstForecast;
        if (first <= 0 || forecast.Points[first].Basis != ForecastBasis.Forecast)
        {
            return null;
        }

        for (var k = first - 1; k >= 0; k--)
        {
            if (forecast.Points[k].Basis == ForecastBasis.Actual && forecast.Points[k].LastTime is not null)
            {
                return k;
            }

            if (forecast.Points[k].Basis != ForecastBasis.None)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// When the train would pass the signal if not held: as far between where it was last known on the run (or the
    /// run's start) and its forecast at the next timed point as the signal's share, and not before
    /// <paramref name="now"/>; standing at the signal, when it is forecast to move. Null if it has passed it.
    /// </summary>
    public static SimTime? Reaches(TrainForecast forecast, SignalOnRun signal, SimTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        ArgumentNullException.ThrowIfNull(signal);
        if (forecast.At(signal.Index)?.LastTime is not { } departs || forecast.At(signal.ToIndex)?.FirstTime is not { } arrives)
        {
            return null;
        }

        if (IsStandingAt(forecast, signal.Signal))
        {
            return forecast.Standing!.Leaves;
        }

        var (from, share) = forecast.Anchor is { } a && a.After == signal.Index ? (forecast.Standing?.Leaves ?? a.Time, a.Share) : (departs, 0.0);
        if (signal.Share <= share + 1e-6 || share >= 1)
        {
            return null;
        }

        var seconds = (int)Math.Round(from.Seconds + ((arrives.Seconds - from.Seconds) * (signal.Share - share) / (1 - share)));
        return new SimTime(now is { } t ? Math.Max(seconds, t.Seconds) : seconds);
    }

    /// <summary>
    /// The signals of the run on <paramref name="start"/>'s line: it, then the one trains passed next most often after
    /// each, in turn. A signal with no next one on the run ends the line (the last before a terminus has none). Null if
    /// that is not known: <paramref name="start"/> is not placed on the run, or the profile has no next signals
    /// learned (places learned before S38).
    /// </summary>
    private static List<SignalOnRun>? Line(IReadOnlyList<SignalOnRun> run, string start, bool nextLearned)
    {
        var byId = run.ToDictionary(s => s.Signal, StringComparer.OrdinalIgnoreCase);
        if (!nextLearned || !byId.TryGetValue(start, out var current))
        {
            return null;
        }

        var line = new List<SignalOnRun> { current };
        while (current.Place.Next is { } next &&
            next.Where(x => byId.TryGetValue(x.Key, out var s) && s.Share > current.Share && !line.Contains(s))
                .OrderByDescending(x => x.Value).Select(x => byId[x.Key]).FirstOrDefault() is { } following)
        {
            line.Add(following);
            current = following;
        }

        return line;
    }

    private static bool IsStandingAt(TrainForecast forecast, string signal) =>
        forecast.Standing is { } standing && string.Equals(standing.Signal, signal, StringComparison.OrdinalIgnoreCase);

    private static int? NextTimed(IReadOnlyList<PlannedPoint> points, int k)
    {
        for (var n = k + 1; n < points.Count; n++)
        {
            if (points[n].IsTimed)
            {
                return n;
            }
        }

        return null;
    }
}
