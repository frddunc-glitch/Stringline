using System.Text.Json.Serialization;
using Stringline.Core;

namespace Stringline.Network.Profiles;

/// <summary>What a learned place is (S37, #19).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SignallingKind>))]
public enum SignallingKind
{
    Signal,
    TrackCircuit,
    Berth,
}

/// <summary>
/// How the runs a place was learned from began and ended (S37). A train that starts or stops at a timing location
/// passes a signal near it at a different share of its run time than one running through, so each kind of run has a
/// place of its own (Waterloo: S110 between Queenstown Road and Vauxhall is at 0.69 running through, 0.60 stopping).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunKind>))]
public enum RunKind
{
    /// <summary>Passed both timing locations.</summary>
    Through,

    /// <summary>Departed from the first, passed the second.</summary>
    Starting,

    /// <summary>Passed the first, arrived at the second.</summary>
    Stopping,

    /// <summary>Departed from the first and arrived at the second.</summary>
    StartingAndStopping,
}

/// <summary>Whether a place is good enough to use.</summary>
public enum PlaceState
{
    /// <summary>Too few samples, or they spread too far: kept, and learning goes on.</summary>
    Learning,

    /// <summary>Enough samples, close together.</summary>
    Used,

    /// <summary>Set by the user: used, and learning never moves it.</summary>
    HandSet,
}

/// <summary>
/// Where a signal, track circuit or TD berth is between two timing locations (S37), learned from play: the share of
/// the run time from <see cref="From"/> to <see cref="To"/> at which trains pass it, in that direction of running.
/// </summary>
/// <remarks>
/// A place is keyed by its kind, ID, <see cref="From"/>, <see cref="To"/> and <see cref="Run"/>: a signal reached
/// from two directions or by two routes, or by trains that stop and trains that do not, has a place for each. The samples are kept (the latest <see cref="SignallingPlaces.MaxKeptSamples"/>,
/// to four places) so another session's samples can be pooled with them.
/// </remarks>
public sealed record SignallingPlace
{
    /// <summary>The signal (<c>S457</c>), track circuit (<c>T810</c>) or berth (<c>0457</c>) ID, as SimSig names it.</summary>
    public required string Id { get; init; }

    /// <summary>The timing location trains pass it after.</summary>
    public required Tiploc From { get; init; }

    /// <summary>The timing location trains reach next.</summary>
    public required Tiploc To { get; init; }

    /// <summary>How the runs it was learned from began and ended.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RunKind Run { get; init; }

    /// <summary>The direction of running from <see cref="From"/> to <see cref="To"/> on a corridor with both, if any.</summary>
    public TrackDirection? Direction { get; init; }

    /// <summary>
    /// The place, 0 at <see cref="From"/> and 1 at <see cref="To"/>, in run time: the median of the samples, or as set
    /// by hand. For a track circuit, where a train's front enters it; for a berth, where the description steps into it.
    /// </summary>
    public double Fraction { get; init; }

    /// <summary>A track circuit's end: where it clears behind the train, if it cleared before the train reached <see cref="To"/>.</summary>
    public double? EndFraction { get; init; }

    /// <summary>The line or platform the trains passing it reported at <see cref="From"/>, when they all reported the same one.</summary>
    public string? Line { get; init; }

    /// <summary>Trains passing it reported different lines at <see cref="From"/>, so it has no <see cref="Line"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MixedLines { get; init; }

    /// <summary>
    /// A track circuit: the signal trains last passed before entering it (the start of its block). A berth: the
    /// signal its ID matches (berth <c>0457</c>, signal <c>S457</c>).
    /// </summary>
    public string? Signal { get; init; }

    /// <summary>
    /// A signal: the signals trains passed next after it, with how many (S38), so the signal ahead of a train is the one
    /// on its line, not a parallel one at a similar place. For the last signal before <see cref="To"/>, the first after
    /// it (S39, so block headways follow the line across a timing location). Null if none was seen (a place learned
    /// before S38).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? Next { get; init; }

    /// <summary>
    /// A signal: the aspects trains passed it at (<c>aspPass</c>: 0 red, 2 single yellow, 4 double yellow, 6 green), in
    /// order (S39). A double yellow says the line is four-aspect (<see cref="BlockHeadways"/>). Null if none was reported
    /// (a track circuit or berth, or a signal learned before S39).
    /// </summary>
    public IReadOnlyList<int>? Aspects { get; init; }

    /// <summary>
    /// A signal: the fastest seconds trains took from passing it to passing each signal in <see cref="Next"/> (S39), the
    /// block's running time unchecked, from runs not checked on the way. Null if none was timed.
    /// </summary>
    public IReadOnlyDictionary<string, int>? NextSeconds { get; init; }

    /// <summary>A track circuit: the fastest seconds from its occupation to its clearing behind the train (S39); null if none was timed.</summary>
    public int? ClearSeconds { get; init; }

    /// <summary>The latest samples (0–1), oldest first.</summary>
    public IReadOnlyList<double> Samples { get; init; } = [];

    /// <summary>A track circuit's latest end samples, oldest first.</summary>
    public IReadOnlyList<double> EndSamples { get; init; } = [];

    /// <summary>Every sample ever taken, including those no longer kept in <see cref="Samples"/>.</summary>
    public int SampleCount { get; init; }

    /// <summary>The sim time (with its day) of the last run learned from.</summary>
    public SimTime? LastLearned { get; init; }

    /// <summary>The session the last run learned from came from (<see cref="LearnedSession.Id"/>).</summary>
    public string? LastSession { get; init; }

    /// <summary>The place was set or added by the user: learning adds samples but never moves it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HandSet { get; init; }

    /// <summary>The interquartile range of <see cref="Samples"/> (0 with fewer than two).</summary>
    [JsonIgnore]
    public double Spread => SignallingPlaces.InterquartileRange(Samples);

    public PlaceState StateFor(int minSamples) =>
        HandSet ? PlaceState.HandSet
        : SampleCount >= minSamples && Spread <= SignallingPlaces.MaxSpread ? PlaceState.Used
        : PlaceState.Learning;

    /// <summary>Used or hand-set: good enough to draw and forecast with (S38).</summary>
    public bool IsUsable(int minSamples) => StateFor(minSamples) != PlaceState.Learning;

    public bool Is(string id, Tiploc from, Tiploc to, RunKind run) =>
        string.Equals(Id, id.Trim(), StringComparison.OrdinalIgnoreCase) && From == from && To == to && Run == run;

    public override string ToString() => FormattableString.Invariant($"{Id} {From}→{To} {Run} {Fraction:0.000} ({SampleCount})");
}

/// <summary>A session (or recording) learned from, and how far, so the same run is never counted twice.</summary>
/// <param name="Id">For a live session its file's name; for a replay its recording's (<c>SignalLearning</c> in Planning).</param>
/// <param name="UpTo">The latest sim time learned up to: runs ending later are new.</param>
public sealed record LearnedSession(string Id, SimTime UpTo);

/// <summary>One sample from the learner, before it is pooled into a <see cref="SignallingPlace"/>.</summary>
/// <param name="At">When the run it came from reached <paramref name="To"/>.</param>
/// <param name="End">A track circuit's end, if it cleared before the train reached <paramref name="To"/>.</param>
/// <param name="Line">The line or platform reported at <paramref name="From"/>, if any.</param>
/// <param name="Next">
/// A signal: the signal the train passed next (S38), before reaching <paramref name="To"/>, or for the last signal
/// before it, the first after it (S39); null if none.
/// </param>
/// <param name="Aspect">A signal: the aspect it was passed at (<c>aspPass</c>), if reported (S39).</param>
/// <param name="NextSeconds">A signal: the seconds from passing it to passing <paramref name="Next"/>, a block's running time (S39).</param>
/// <param name="ClearSeconds">A track circuit: the seconds from its occupation to its clearing behind the train (S39).</param>
public sealed record PlaceSample(SignallingKind Kind, string Id, Tiploc From, Tiploc To, double Fraction, SimTime At, double? End = null, string? Line = null, string? Signal = null, RunKind Run = RunKind.Through, string? Next = null, int? Aspect = null, int? NextSeconds = null, int? ClearSeconds = null);

/// <summary>One timed run between two timing locations from the learner, before it is pooled into a <see cref="LearnedRun"/>.</summary>
/// <param name="At">When the run reached <paramref name="To"/>.</param>
public sealed record RunSample(Tiploc From, Tiploc To, RunKind Run, int Seconds, SimTime At);

/// <summary>
/// How long trains took between two timing locations they reported one after the other, learned from play: runs not
/// checked at a signal on the way and no slower than the learner's limit (S37). The derived axis is spaced by them
/// where enough runs are known (<see cref="SignallingPlaces.LearnedRunSeconds"/>), so trains draw as they really run.
/// </summary>
public sealed record LearnedRun
{
    public required Tiploc From { get; init; }

    public required Tiploc To { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RunKind Run { get; init; }

    /// <summary>The latest run times in seconds, oldest first.</summary>
    public IReadOnlyList<int> Seconds { get; init; } = [];

    /// <summary>Every run ever timed, including those no longer kept in <see cref="Seconds"/>.</summary>
    public int SampleCount { get; init; }

    /// <summary>The fastest run ever timed.</summary>
    public int FastestSeconds { get; init; }

    /// <summary>The sim time (with its day) of the last run learned from.</summary>
    public SimTime? LastLearned { get; init; }

    /// <summary>
    /// The run time of a fast train here: the lower quartile of <see cref="Seconds"/>, so one run timed short by a
    /// missed report does not set it (0 with none).
    /// </summary>
    [JsonIgnore]
    public int FastSeconds => Seconds.Count == 0 ? 0 : Seconds.Order().ElementAt((Seconds.Count - 1) / 4);

    public bool Is(Tiploc from, Tiploc to, RunKind run) => From == from && To == to && Run == run;

    public override string ToString() => FormattableString.Invariant($"{From}→{To} {Run} {FastSeconds}s ({SampleCount})");
}

/// <summary>
/// The signals, track circuits and TD berths learned for a sim (S37, #19), kept in its profile so they can be shared.
/// Immutable: every change returns a new instance.
/// </summary>
public sealed record SignallingPlaces
{
    /// <summary>Samples a place needs before it is used, unless changed in Tools ▸ Options.</summary>
    public const int DefaultMinSamples = 3;

    /// <summary>The widest spread (interquartile range, as a share of the section) a used place may have.</summary>
    public const double MaxSpread = 0.10;

    /// <summary>Samples kept per place; older ones still count in <see cref="SignallingPlace.SampleCount"/>.</summary>
    public const int MaxKeptSamples = 64;

    /// <summary>Sessions remembered; the oldest are forgotten after this many.</summary>
    public const int MaxSessions = 500;

    public static SignallingPlaces Empty { get; } = new();

    public IReadOnlyList<SignallingPlace> Signals { get; init; } = [];

    public IReadOnlyList<SignallingPlace> TrackCircuits { get; init; } = [];

    public IReadOnlyList<SignallingPlace> Berths { get; init; } = [];

    /// <summary>Run times between timing locations, by kind of run, in order of their locations.</summary>
    public IReadOnlyList<LearnedRun> Runs { get; init; } = [];

    /// <summary>The sessions learned from, oldest first.</summary>
    public IReadOnlyList<LearnedSession> Sessions { get; init; } = [];

    [JsonIgnore]
    public bool IsEmpty => Signals.Count == 0 && TrackCircuits.Count == 0 && Berths.Count == 0 && Runs.Count == 0 && Sessions.Count == 0;

    [JsonIgnore]
    public int Count => Signals.Count + TrackCircuits.Count + Berths.Count;

    public IReadOnlyList<SignallingPlace> Of(SignallingKind kind) => kind switch
    {
        SignallingKind.Signal => Signals,
        SignallingKind.TrackCircuit => TrackCircuits,
        _ => Berths,
    };

    /// <summary>Every place with its kind.</summary>
    public IEnumerable<(SignallingKind Kind, SignallingPlace Place)> All =>
        Signals.Select(p => (SignallingKind.Signal, p))
            .Concat(TrackCircuits.Select(p => (SignallingKind.TrackCircuit, p)))
            .Concat(Berths.Select(p => (SignallingKind.Berth, p)));

    public SignallingPlace? Find(SignallingKind kind, string id, Tiploc from, Tiploc to, RunKind run = RunKind.Through) =>
        Of(kind).FirstOrDefault(p => p.Is(id, from, to, run));

    /// <summary>The places of one kind between two locations, in either direction, in order along the run.</summary>
    public IEnumerable<SignallingPlace> Between(SignallingKind kind, Tiploc a, Tiploc b) =>
        Of(kind).Where(p => (p.From == a && p.To == b) || (p.From == b && p.To == a)).OrderBy(p => p.From != a).ThenBy(p => p.Fraction);

    /// <summary>
    /// The usual time of a fast train running through from <paramref name="a"/> to <paramref name="b"/> in either
    /// direction (the faster of the two), from runs passing both, if at least <paramref name="minSamples"/> are known
    /// in all.
    /// </summary>
    public int? LearnedRunSeconds(Tiploc a, Tiploc b, int minSamples)
    {
        var runs = Runs.Where(r => r.Run == RunKind.Through && r.Seconds.Count > 0 && ((r.From == a && r.To == b) || (r.From == b && r.To == a))).ToList();
        return runs.Count == 0 || runs.Sum(r => r.SampleCount) < minSamples ? null : runs.Min(r => r.FastSeconds);
    }

    /// <summary>The session's entry, if it has been learned from.</summary>
    public LearnedSession? FindSession(string id) => Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Pools a session's samples in (S37). Only runs that ended after the session was last learned up to are taken,
    /// so learning again from a resumed, reviewed or replayed session counts nothing twice. A hand-set place gets the
    /// samples but keeps its fraction.
    /// </summary>
    /// <param name="sessionId">The session (or recording) the samples come from.</param>
    /// <param name="upTo">The latest sim time the session has reached.</param>
    /// <param name="corridors">The profile's corridors, for each place's direction.</param>
    /// <param name="added">How many samples were new.</param>
    public SignallingPlaces Learn(IEnumerable<PlaceSample> samples, string sessionId, SimTime upTo, IEnumerable<Corridor> corridors, out int added) =>
        Learn(samples, [], sessionId, upTo, corridors, out added);

    /// <inheritdoc cref="Learn(IEnumerable{PlaceSample}, string, SimTime, IEnumerable{Corridor}, out int)"/>
    /// <param name="runs">The session's timed runs between timing locations, pooled into <see cref="Runs"/> the same way (not counted in <paramref name="added"/>).</param>
    public SignallingPlaces Learn(IEnumerable<PlaceSample> samples, IEnumerable<RunSample> runs, string sessionId, SimTime upTo, IEnumerable<Corridor> corridors, out int added)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(corridors);
        var before = FindSession(sessionId)?.UpTo;
        var fresh = samples.Where(s => before is not { } b || s.At > b).ToList();
        added = fresh.Count;
        var corridorList = corridors.ToList();
        var result = this;
        foreach (var kind in Enum.GetValues<SignallingKind>())
        {
            var places = Of(kind).ToList();
            foreach (var group in fresh.Where(s => s.Kind == kind).GroupBy(s => (Id: s.Id.Trim().ToUpperInvariant(), s.From, s.To, s.Run)))
            {
                var index = places.FindIndex(p => p.Is(group.Key.Id, group.Key.From, group.Key.To, group.Key.Run));
                var place = index >= 0 ? places[index] : new SignallingPlace { Id = group.First().Id.Trim(), From = group.Key.From, To = group.Key.To, Run = group.Key.Run, Direction = DirectionOf(corridorList, group.Key.From, group.Key.To) };
                place = Add(place, group.OrderBy(s => s.At).ToList(), sessionId);
                if (index >= 0)
                {
                    places[index] = place;
                }
                else
                {
                    places.Add(place);
                }
            }

            result = result.With(kind, places);
        }

        var learnedRuns = Runs.ToList();
        foreach (var group in runs.Where(r => (before is not { } b || r.At > b) && r.Seconds > 0 && r.From != r.To).GroupBy(r => (r.From, r.To, r.Run)))
        {
            var index = learnedRuns.FindIndex(r => r.Is(group.Key.From, group.Key.To, group.Key.Run));
            var run = index >= 0 ? learnedRuns[index] : new LearnedRun { From = group.Key.From, To = group.Key.To, Run = group.Key.Run };
            var ordered = group.OrderBy(r => r.At).ToList();
            run = Pooled(run, ordered.Select(r => r.Seconds).ToList(), ordered.Count, ordered[^1].At);
            if (index >= 0)
            {
                learnedRuns[index] = run;
            }
            else
            {
                learnedRuns.Add(run);
            }
        }

        result = result.WithRuns(learnedRuns);
        var latest = before is { } previous && previous > upTo ? previous : upTo;
        return result with { Sessions = Sessions.Where(s => s.Id != sessionId).Append(new LearnedSession(sessionId, latest)).TakeLast(MaxSessions).ToList() };
    }

    /// <summary>
    /// Another profile's places pooled with these (Import profile ▸ Merge): samples are added together, a hand-set
    /// place keeps its fraction (this profile's first, if both are), and the sessions learned from are combined.
    /// </summary>
    public SignallingPlaces Pool(SignallingPlaces other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var result = this;
        foreach (var kind in Enum.GetValues<SignallingKind>())
        {
            var places = Of(kind).ToList();
            foreach (var theirs in other.Of(kind))
            {
                var index = places.FindIndex(p => p.Is(theirs.Id, theirs.From, theirs.To, theirs.Run));
                if (index < 0)
                {
                    places.Add(theirs);
                    continue;
                }

                var mine = places[index];
                var merged = mine with
                {
                    Samples = Keep(mine.Samples.Concat(theirs.Samples)),
                    EndSamples = Keep(mine.EndSamples.Concat(theirs.EndSamples)),
                    SampleCount = mine.SampleCount + theirs.SampleCount,
                    Direction = mine.Direction ?? theirs.Direction,
                    Signal = mine.Signal ?? theirs.Signal,
                    Next = Counted(mine.Next, theirs.Next?.Select(n => (n.Key, n.Value)) ?? []),
                    Aspects = AspectsOf(mine.Aspects, theirs.Aspects ?? []),
                    NextSeconds = Fastest(mine.NextSeconds, theirs.NextSeconds?.Select(n => (n.Key, n.Value)) ?? []),
                    ClearSeconds = Fastest(mine.ClearSeconds, theirs.ClearSeconds),
                    Line = mine.Line == theirs.Line || theirs.Line is null ? mine.Line : mine.Line is null && !mine.MixedLines ? theirs.Line : null,
                    MixedLines = mine.MixedLines || theirs.MixedLines || (mine.Line is not null && theirs.Line is not null && mine.Line != theirs.Line),
                    LastLearned = Later(mine.LastLearned, theirs.LastLearned),
                    LastSession = Later(mine.LastLearned, theirs.LastLearned) == mine.LastLearned ? mine.LastSession : theirs.LastSession,
                    HandSet = mine.HandSet || theirs.HandSet,
                };
                places[index] = mine.HandSet ? merged
                    : theirs.HandSet ? merged with { Fraction = theirs.Fraction, EndFraction = theirs.EndFraction }
                    : Settled(merged);
            }

            result = result.With(kind, places);
        }

        var runs = result.Runs.ToList();
        foreach (var theirs in other.Runs)
        {
            var index = runs.FindIndex(r => r.Is(theirs.From, theirs.To, theirs.Run));
            if (index < 0)
            {
                runs.Add(theirs);
            }
            else
            {
                var mine = runs[index];
                runs[index] = Pooled(mine, theirs.Seconds, theirs.SampleCount, theirs.LastLearned) with
                {
                    FastestSeconds = mine.SampleCount == 0 ? theirs.FastestSeconds : theirs.SampleCount == 0 ? mine.FastestSeconds : Math.Min(mine.FastestSeconds, theirs.FastestSeconds),
                };
            }
        }

        result = result.WithRuns(runs);
        var sessions = Sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var s in other.Sessions)
        {
            sessions[s.Id] = sessions.TryGetValue(s.Id, out var mine) && mine.UpTo > s.UpTo ? mine : s;
        }

        return result with { Sessions = [.. Sessions.Select(s => sessions[s.Id]), .. other.Sessions.Where(s => FindSession(s.Id) is null)] };
    }

    /// <summary>A place set by hand at <paramref name="fraction"/>: an existing one is moved and marked hand-set, or a new one added.</summary>
    public SignallingPlaces SetByHand(SignallingKind kind, string id, Tiploc from, Tiploc to, double fraction, RunKind run = RunKind.Through, IEnumerable<Corridor>? corridors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (from == to)
        {
            throw new ArgumentException("A place needs two different timing locations.", nameof(to));
        }

        fraction = Math.Clamp(fraction, 0, 1);
        var places = Of(kind).ToList();
        var index = places.FindIndex(p => p.Is(id, from, to, run));
        if (index >= 0)
        {
            places[index] = places[index] with { Fraction = fraction, HandSet = true };
        }
        else
        {
            places.Add(new SignallingPlace { Id = id.Trim(), From = from, To = to, Run = run, Fraction = fraction, HandSet = true, Direction = DirectionOf(corridors?.ToList() ?? [], from, to) });
        }

        return With(kind, places);
    }

    /// <summary>The places without this one.</summary>
    public SignallingPlaces Remove(SignallingKind kind, string id, Tiploc from, Tiploc to, RunKind run = RunKind.Through) =>
        With(kind, Of(kind).Where(p => !p.Is(id, from, to, run)).ToList());

    /// <summary>
    /// Forgets the learned places between two locations (either direction), keeping hand-set ones. The sessions are
    /// remembered, so the same runs are not learned again.
    /// </summary>
    public SignallingPlaces Reset(Tiploc a, Tiploc b)
    {
        var result = this;
        foreach (var kind in Enum.GetValues<SignallingKind>())
        {
            result = result.With(kind, result.Of(kind).Where(p => p.HandSet || !((p.From == a && p.To == b) || (p.From == b && p.To == a))).ToList());
        }

        return result.WithRuns(result.Runs.Where(r => !((r.From == a && r.To == b) || (r.From == b && r.To == a))).ToList());
    }

    /// <summary>
    /// Forgets every learned place, keeping hand-set ones. The sessions are remembered, as for <see cref="Reset"/>, so
    /// the session running now does not bring its earlier runs straight back.
    /// </summary>
    public SignallingPlaces ResetAll() => this with
    {
        Signals = Signals.Where(p => p.HandSet).ToList(),
        TrackCircuits = TrackCircuits.Where(p => p.HandSet).ToList(),
        Berths = Berths.Where(p => p.HandSet).ToList(),
        Runs = [],
    };

    /// <summary>The places with these learned runs, in order of their locations.</summary>
    public SignallingPlaces WithRuns(IReadOnlyList<LearnedRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return this with { Runs = runs.OrderBy(r => r.From.Code, StringComparer.Ordinal).ThenBy(r => r.To.Code, StringComparer.Ordinal).ThenBy(r => r.Run).ToList() };
    }

    public SignallingPlaces With(SignallingKind kind, IReadOnlyList<SignallingPlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        var ordered = places.OrderBy(p => p.From.Code, StringComparer.Ordinal).ThenBy(p => p.To.Code, StringComparer.Ordinal)
            .ThenBy(p => p.Run).ThenBy(p => p.Fraction).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        return kind switch
        {
            SignallingKind.Signal => this with { Signals = ordered },
            SignallingKind.TrackCircuit => this with { TrackCircuits = ordered },
            _ => this with { Berths = ordered },
        };
    }

    /// <summary>The first rule the places break, or null: each needs an ID, two different locations and fractions within 0–1.</summary>
    public string? FindProblem()
    {
        foreach (var (kind, place) in All)
        {
            if (string.IsNullOrWhiteSpace(place.Id) || place.From.IsEmpty || place.To.IsEmpty || place.From == place.To)
            {
                return $"A learned {Name(kind)} '{place.Id}' ({place.From}–{place.To}) needs an ID and two different locations.";
            }

            if (place.Fraction is < 0 or > 1 || place.EndFraction is < 0 or > 1 || double.IsNaN(place.Fraction))
            {
                return $"The learned {Name(kind)} {place.Id} ({place.From}–{place.To}) has a place outside 0–1.";
            }
        }

        foreach (var run in Runs)
        {
            if (run.From.IsEmpty || run.To.IsEmpty || run.From == run.To || run.Seconds.Any(t => t <= 0))
            {
                return $"A learned run {run.From}–{run.To} needs two different locations and times above 0.";
            }
        }

        return null;
    }

    public static string Name(RunKind run) => run switch
    {
        RunKind.Through => "running through",
        RunKind.Starting => "starting",
        RunKind.Stopping => "stopping",
        _ => "starting and stopping",
    };

    /// <summary>The kind of a run that left its first location this way and reached its second this way.</summary>
    public static RunKind RunOf(bool departed, bool arrived) => (departed, arrived) switch
    {
        (false, false) => RunKind.Through,
        (true, false) => RunKind.Starting,
        (false, true) => RunKind.Stopping,
        _ => RunKind.StartingAndStopping,
    };

    public static string Name(SignallingKind kind) => kind switch
    {
        SignallingKind.Signal => "signal",
        SignallingKind.TrackCircuit => "track circuit",
        _ => "berth",
    };

    /// <summary>The median of some values (0 for none).</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The interquartile range (linear between ranks), 0 with fewer than two values.</summary>
    public static double InterquartileRange(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        return Quantile(sorted, 0.75) - Quantile(sorted, 0.25);
    }

    private static double Quantile(List<double> sorted, double q)
    {
        var rank = q * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = Math.Min(low + 1, sorted.Count - 1);
        return sorted[low] + ((rank - low) * (sorted[high] - sorted[low]));
    }

    private static SignallingPlace Add(SignallingPlace place, List<PlaceSample> samples, string sessionId)
    {
        var line = place.Line;
        var mixed = place.MixedLines;
        foreach (var s in samples)
        {
            var reported = string.IsNullOrWhiteSpace(s.Line) ? null : s.Line.Trim();
            if (mixed || reported is null)
            {
                continue;
            }

            if (line is null)
            {
                line = reported;
            }
            else if (line != reported)
            {
                (line, mixed) = (null, true);
            }
        }

        var updated = place with
        {
            Samples = Keep(place.Samples.Concat(samples.Select(s => s.Fraction))),
            EndSamples = Keep(place.EndSamples.Concat(samples.Where(s => s.End is not null).Select(s => s.End!.Value))),
            SampleCount = place.SampleCount + samples.Count,
            LastLearned = Later(place.LastLearned, samples[^1].At),
            LastSession = sessionId,
            Signal = samples.LastOrDefault(s => s.Signal is not null)?.Signal ?? place.Signal,
            Next = Counted(place.Next, samples.Where(s => s.Next is not null).Select(s => (s.Next!.Trim(), 1))),
            Aspects = AspectsOf(place.Aspects, samples.Where(s => s.Aspect is not null).Select(s => s.Aspect!.Value)),
            NextSeconds = Fastest(place.NextSeconds, samples.Where(s => s.Next is not null && s.NextSeconds is > 0).Select(s => (s.Next!.Trim(), s.NextSeconds!.Value))),
            ClearSeconds = samples.Where(s => s.ClearSeconds is > 0).Aggregate(place.ClearSeconds, (a, s) => Fastest(a, s.ClearSeconds)),
            Line = line,
            MixedLines = mixed,
        };
        return place.HandSet ? updated : Settled(updated);
    }

    /// <summary>A learned run with more times added (oldest first): the latest kept, the count and fastest updated.</summary>
    private static LearnedRun Pooled(LearnedRun run, IReadOnlyList<int> seconds, int count, SimTime? at) => run with
    {
        Seconds = run.Seconds.Concat(seconds).TakeLast(MaxKeptSamples).ToList(),
        SampleCount = run.SampleCount + count,
        FastestSeconds = seconds.Count == 0 ? run.FastestSeconds : run.SampleCount == 0 ? seconds.Min() : Math.Min(run.FastestSeconds, seconds.Min()),
        LastLearned = Later(run.LastLearned, at),
    };

    private static SignallingPlace Settled(SignallingPlace place) => place with
    {
        Fraction = Math.Round(Median(place.Samples), 4),
        EndFraction = place.EndSamples.Count == 0 ? null : Math.Round(Median(place.EndSamples), 4),
    };

    /// <summary>Counts of next signals with more added; null if there are none.</summary>
    private static Dictionary<string, int>? Counted(IReadOnlyDictionary<string, int>? counts, IEnumerable<(string Id, int Count)> more)
    {
        var result = counts is null ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, int>(counts, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, count) in more)
        {
            result[id] = result.GetValueOrDefault(id) + count;
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>The fastest time for each signal; null if there are none.</summary>
    private static Dictionary<string, int>? Fastest(IReadOnlyDictionary<string, int>? times, IEnumerable<(string Id, int Seconds)> more)
    {
        var result = times is null ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, int>(times, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, seconds) in more)
        {
            result[id] = result.TryGetValue(id, out var known) ? Math.Min(known, seconds) : seconds;
        }

        return result.Count == 0 ? null : result;
    }

    private static int? Fastest(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static List<int>? AspectsOf(IEnumerable<int>? a, IEnumerable<int> b)
    {
        var all = (a ?? []).Concat(b).Where(x => x >= 0).Distinct().Order().ToList();
        return all.Count == 0 ? null : all;
    }

    private static List<double> Keep(IEnumerable<double> samples) => samples.Select(s => Math.Round(Math.Clamp(s, 0, 1), 4)).TakeLast(MaxKeptSamples).ToList();

    private static SimTime? Later(SimTime? a, SimTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    private static TrackDirection? DirectionOf(List<Corridor> corridors, Tiploc from, Tiploc to) =>
        corridors.Where(c => c.Contains(from) && c.Contains(to)).Select(c => c.DirectionOf([from, to])).FirstOrDefault(d => d is not null);
}
