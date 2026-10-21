using Stringline.Core;

namespace Stringline.Network.Profiles;

/// <summary>
/// The learned places good enough to draw and forecast with (S38): used or hand-set ones, indexed for the lookups the
/// graph and the forecaster make on every train. Immutable and thread-safe; build once per profile.
/// </summary>
/// <remarks>
/// A signal can have a place for each kind of run (<see cref="RunKind"/>). Fred chose (S38, question 34) to use the
/// place for the kind of run the train makes there, and else any usable place for the signal over the same two timing
/// locations: a hand-set one first, then the one with the most samples (<see cref="Choose"/>).
/// </remarks>
public sealed class UsablePlaces
{
    private readonly Dictionary<(string Id, Tiploc From), List<SignallingPlace>> _signals;
    private readonly Dictionary<(Tiploc From, Tiploc To), List<SignallingPlace>> _signalsBetween;
    private readonly Dictionary<string, List<SignallingPlace>> _tracksAfter;
    private readonly Dictionary<string, List<SignallingPlace>> _berths;

    private UsablePlaces(SignallingPlaces places, int minSamples)
    {
        MinSamples = minSamples;
        var signals = places.Signals.Where(p => p.IsUsable(minSamples)).ToList();
        _signals = signals.GroupBy(p => (Id: p.Id.ToUpperInvariant(), p.From)).ToDictionary(g => g.Key, g => g.ToList());
        _signalsBetween = signals.GroupBy(p => (p.From, p.To)).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Fraction).ToList());
        _tracksAfter = places.TrackCircuits.Where(p => p.IsUsable(minSamples))
            .GroupBy(p => (p.Signal ?? p.From.Code).ToUpperInvariant(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _berths = places.Berths.Where(p => p.IsUsable(minSamples))
            .GroupBy(p => p.Id.ToUpperInvariant(), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        Signals = signals;
    }

    public static UsablePlaces Empty { get; } = new(SignallingPlaces.Empty, SignallingPlaces.DefaultMinSamples);

    /// <summary>The samples a learned place needs to be used.</summary>
    public int MinSamples { get; }

    /// <summary>Every usable signal place.</summary>
    public IReadOnlyList<SignallingPlace> Signals { get; }

    public bool IsEmpty => _signals.Count == 0 && _tracksAfter.Count == 0 && _berths.Count == 0;

    public static UsablePlaces Of(SignallingPlaces places, int minSamples = SignallingPlaces.DefaultMinSamples)
    {
        ArgumentNullException.ThrowIfNull(places);
        return places.Count == 0 && minSamples == SignallingPlaces.DefaultMinSamples ? Empty : new UsablePlaces(places, minSamples);
    }

    /// <summary>The usable places of signal <paramref name="id"/> learned from runs leaving <paramref name="from"/> (any next location, any kind).</summary>
    public IReadOnlyList<SignallingPlace> SignalFrom(string id, Tiploc from) =>
        _signals.TryGetValue((id.Trim().ToUpperInvariant(), from), out var list) ? list : [];

    /// <summary>The usable signal places from <paramref name="from"/> to <paramref name="to"/>, any kind, in order along the run.</summary>
    public IReadOnlyList<SignallingPlace> SignalsBetween(Tiploc from, Tiploc to) =>
        _signalsBetween.TryGetValue((from, to), out var list) ? list : [];

    /// <summary>
    /// The usable track circuit places whose block starts at <paramref name="place"/>: the signal trains last passed
    /// before entering them, or the timing location when no signal came between.
    /// </summary>
    public IReadOnlyList<SignallingPlace> TracksAfter(string place) =>
        _tracksAfter.TryGetValue(place.Trim().ToUpperInvariant(), out var list) ? list : [];

    /// <summary>The usable places of a berth, any locations and kind.</summary>
    public IReadOnlyList<SignallingPlace> Berth(string id) =>
        _berths.TryGetValue(id.Trim().ToUpperInvariant(), out var list) ? list : [];

    /// <summary>
    /// The place to use of several for one ID between the same two locations: the one learned for
    /// <paramref name="run"/>, else (any kind, when <paramref name="run"/> is null or not learned) a hand-set one, then
    /// the one with the most samples. Null if there are none.
    /// </summary>
    public static SignallingPlace? Choose(IEnumerable<SignallingPlace> candidates, RunKind? run)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        SignallingPlace? best = null;
        foreach (var p in candidates)
        {
            if (run is { } wanted && p.Run == wanted)
            {
                return p;
            }

            if (best is null || (p.HandSet && !best.HandSet) || (p.HandSet == best.HandSet && p.SampleCount > best.SampleCount))
            {
                best = p;
            }
        }

        return best;
    }

    /// <summary>The kinds of run that start the way <paramref name="departed"/> says (the end not known yet).</summary>
    public static IEnumerable<RunKind> KindsStarting(bool departed) =>
        departed ? [RunKind.Starting, RunKind.StartingAndStopping] : [RunKind.Through, RunKind.Stopping];

    /// <summary>
    /// The place's axis position on a corridor with both its locations: as far between them as its fraction, since the
    /// derived axis is measured in run time too. Null if either location is not on the corridor.
    /// </summary>
    public static double? PositionOn(Corridor corridor, Tiploc from, Tiploc to, double fraction)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        return corridor.PositionOf(from) is { } a && corridor.PositionOf(to) is { } b && a != b ? a + (Math.Clamp(fraction, 0, 1) * (b - a)) : null;
    }
}
