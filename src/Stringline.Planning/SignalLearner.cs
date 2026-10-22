using System.Globalization;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Network.Profiles;

namespace Stringline.Planning;

/// <summary>How <see cref="SignalLearner"/> takes its samples.</summary>
public sealed record SignalLearnerOptions
{
    /// <summary>A run slower than this times the fastest run of its kind seen over the section was checked on the way, so is left out.</summary>
    public double MaxRunFactor { get; init; } = 1.5;

    /// <summary>A train is near a track circuit or berth if it was reported within this many sim seconds before.</summary>
    public int NearSeconds { get; init; } = 120;

    /// <summary>A track circuit's place before it is the one most trains near it were last at, by at least this share.</summary>
    public double PredecessorShare { get; init; } = 0.6;

    /// <summary>Occupations of a track circuit needed before the place before it is trusted.</summary>
    public int MinOccupations { get; init; } = 3;
}

/// <summary>How the track circuit occupations of a session were put to trains (S37's finding).</summary>
/// <param name="Occupations">Track circuits seen occupied, with a time.</param>
/// <param name="Attributed">Put to exactly one train.</param>
/// <param name="Ambiguous">More than one train was near, at the place before it.</param>
/// <param name="NoTrain">No train was near (a failure, a train not reporting, or the snapshot at connect).</param>
/// <param name="UnknownTrack">The track circuit had no settled place before it.</param>
/// <param name="Placed">Of the attributed, those inside a usable run between two timing locations.</param>
public sealed record TrackAttribution(int Occupations, int Attributed, int Ambiguous, int NoTrain, int UnknownTrack, int Placed)
{
    public static TrackAttribution None { get; } = new(0, 0, 0, 0, 0, 0);

    public double AttributedShare => Occupations == 0 ? 0 : (double)Attributed / Occupations;
}

/// <summary>What one session taught: its samples, and how many runs and steps they came from.</summary>
/// <param name="UpTo">The latest sim time seen.</param>
/// <param name="Runs">Runs between two timing locations.</param>
/// <param name="RunsUsed">Of those, runs a sample may come from (not stopped, not checked, timed both ends).</param>
/// <param name="Steps">TD berth steps seen, and how many were put to a train in a usable run.</param>
/// <param name="RunTimes">The usable runs' times between timing locations, for the axis (<see cref="LearnedRun"/>).</param>
public sealed record LearnedPlaces(IReadOnlyList<PlaceSample> Samples, SimTime? UpTo, int Runs, int RunsUsed, TrackAttribution Tracks, int Steps, int StepsPlaced, IReadOnlyList<RunSample>? RunTimes = null)
{
    public static LearnedPlaces None { get; } = new([], null, 0, 0, TrackAttribution.None, 0, 0);

    /// <summary>The usable runs' times (none if not given).</summary>
    public IReadOnlyList<RunSample> RunTimes { get; init; } = RunTimes ?? [];

    public int Count(SignallingKind kind) => Samples.Count(s => s.Kind == kind);
}

/// <summary>
/// Learns where signals, track circuits and TD berths are between timing locations (S37, #19), from a session's
/// gateway events. A signal pass at time t by a train running from timing location a (at t_a) to b (at t_b), not
/// stopped or checked on the way, is the sample (t − t_a) / (t_b − t_a) for that signal from a to b: its place in run
/// time, the measure of the derived corridor axis (<c>04-…md</c> §4.5), for that kind of run (<see cref="RunKind"/>:
/// starting or stopping at a or b, or running through). Track circuit occupations and berth steps are
/// placed the same way, once they are put to a train.
/// </summary>
/// <remarks>
/// <para><see cref="Observe"/> only keeps what it needs, under a lock, so a source can feed it from any thread without
/// slowing the planning engine; <see cref="Learn"/> works it out from what has been kept.</para>
/// <para>Every train counts, matched or not: a place belongs to the track, not to a timetable train. A run counts if
/// it is no slower than <see cref="SignalLearnerOptions.MaxRunFactor"/> times the fastest of its kind seen over the
/// section in the session: SimSig's trains rarely run as fast as the WTT's minimums, which would leave out half the
/// runs.</para>
/// <para>A track circuit is put to a train in two steps. First, for each one, the place most trains near it were last
/// reported at (the signal at the start of its block) is found. Then an occupation is put to the train last at that
/// place, if exactly one is near. A train standing after an arrival is not near.</para>
/// <para>A berth step is put to the train whose headcode the description is, if exactly one such train is near.</para>
/// </remarks>
public sealed class SignalLearner
{
    private readonly Lock _lock = new();
    private readonly List<Report> _reports = [];
    private readonly List<TrackChange> _tracks = [];
    private readonly List<Step> _steps = [];
    private SimTime? _latest;

    public SignalLearner(SignalLearnerOptions? options = null) => Options = options ?? new SignalLearnerOptions();

    public SignalLearnerOptions Options { get; }

    /// <summary>The latest sim time seen.</summary>
    public SimTime? UpTo
    {
        get
        {
            lock (_lock)
            {
                return _latest;
            }
        }
    }

    /// <summary>Keep what an event says about places. Thread-safe and cheap. A replay rewind starts again.</summary>
    public void Observe(GatewayEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        lock (_lock)
        {
            switch (e)
            {
                case TrainLocationEvent { Uid.Length: > 0 } location when !location.Location.IsEmpty:
                    _reports.Add(new Report(location.Uid, location.Headcode, location.Location, location.Action, location.Time, location.Platform, location.AspectApproach, _reports.Count, location.AspectPassed));
                    Seen(location.Time);
                    break;
                case SignallingStateEvent { ObjectType: SignallingObjectType.Track, Time: { } time } track when track.GetBoolean("clear") is { } clear && track.ObjectId.Trim().Length > 0:
                    _tracks.Add(new TrackChange(track.ObjectId.Trim(), time, !clear));
                    Seen(time);
                    break;
                case TdStepEvent { Kind: TdStepKind.Step, Time: { } time } step when step.To.Trim().Length > 0 && step.Description.Trim().Length > 0:
                    _steps.Add(new Step(step.To.Trim(), step.Description.Trim(), time));
                    Seen(time);
                    break;
                case ClockEvent clock when clock.Clock.Seconds > 0:
                    Seen(clock.Clock);
                    break;
                case ReplayRewoundEvent:
                    _reports.Clear();
                    _tracks.Clear();
                    _steps.Clear();
                    _latest = null;
                    break;
            }
        }
    }

    /// <summary>Works out the samples from everything observed so far, against a timetable (for its timing locations).</summary>
    public LearnedPlaces Learn(WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        List<Report> reports;
        List<TrackChange> tracks;
        List<Step> steps;
        SimTime? latest;
        lock (_lock)
        {
            reports = [.. _reports];
            tracks = [.. _tracks];
            steps = [.. _steps];
            latest = _latest;
        }

        return new Work(this, timetable, reports, tracks, steps, latest).Run();
    }

    private void Seen(SimTime time)
    {
        if (_latest is not { } latest || time > latest)
        {
            _latest = time;
        }
    }

    private sealed record Report(string Uid, Headcode Headcode, Tiploc Location, TrainLocationAction Action, SimTime Time, string Platform, int? AspectApproach, int Order, int? AspectPassed = null);

    private sealed record TrackChange(string Id, SimTime Time, bool Occupied);

    private sealed record Step(string Berth, string Description, SimTime Time);

    /// <summary>A train's run from one timing location to the next, with the reports in between.</summary>
    private sealed record Run(Report A, Report B, List<Report> Between, bool Usable)
    {
        public int Seconds => B.Time - A.Time;

        public double FractionAt(SimTime t) => Math.Clamp((double)(t - A.Time) / Seconds, 0, 1);

        public bool Covers(SimTime t) => t >= A.Time && t <= B.Time;

        public RunKind Kind => SignallingPlaces.RunOf(A.Action == TrainLocationAction.Depart, B.Action == TrainLocationAction.Arrive);
    }

    private sealed class Work(SignalLearner learner, WorkingTimetable timetable, List<Report> reports, List<TrackChange> tracks, List<Step> steps, SimTime? latest)
    {
        private readonly HashSet<Tiploc> _timing = timetable.Trains.SelectMany(t => t.Points).Select(p => p.Location).ToHashSet();
        private readonly SignalLearnerOptions _options = learner.Options;
        private readonly List<PlaceSample> _samples = [];
        private Dictionary<string, List<Report>> _byTrain = [];
        private Dictionary<string, List<Run>> _runs = [];
        private List<Report> _timeline = [];

        public LearnedPlaces Run()
        {
            _byTrain = reports.GroupBy(r => r.Uid, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Time).ThenBy(r => r.Order).ToList(), StringComparer.Ordinal);
            _timeline = [.. reports.OrderBy(r => r.Time).ThenBy(r => r.Order)];
            var (runs, used) = FindRuns();
            SignalSamples();
            var attribution = TrackSamples();
            var placed = StepSamples();
            var times = _runs.Values.SelectMany(r => r).Where(r => r.Usable).Select(r => new RunSample(r.A.Location, r.B.Location, r.Kind, r.Seconds, r.B.Time)).ToList();
            return new LearnedPlaces(_samples, latest, runs, used, attribution, steps.Count, placed, times);
        }

        private (int Runs, int Used) FindRuns()
        {
            var all = new List<Run>();
            foreach (var (uid, list) in _byTrain)
            {
                var runs = new List<Run>();
                Report? start = null;
                var between = new List<Report>();
                foreach (var r in list)
                {
                    if (!_timing.Contains(r.Location))
                    {
                        if (start is not null)
                        {
                            between.Add(r);
                        }

                        continue;
                    }

                    if (start is not null && r.Location != start.Location)
                    {
                        runs.Add(new Run(start, r, between, false));
                    }

                    start = r;
                    between = [];
                }

                _runs[uid] = runs;
                all.AddRange(runs);
            }

            var fastest = all.Where(Timed).GroupBy(r => (r.A.Location, r.B.Location, r.Kind)).ToDictionary(g => g.Key, g => g.Min(r => r.Seconds));
            var used = 0;
            foreach (var runs in _runs.Values)
            {
                for (var i = 0; i < runs.Count; i++)
                {
                    var run = runs[i];
                    if (!Timed(run) || run.Between.Any(s => s.AspectApproach == 0))
                    {
                        continue;
                    }

                    if (run.Seconds <= _options.MaxRunFactor * fastest[(run.A.Location, run.B.Location, run.Kind)])
                    {
                        runs[i] = run with { Usable = true };
                        used++;
                    }
                }
            }

            return (all.Count, used);
        }

        /// <summary>Left a (passing or departing) and reached b (passing or arriving), some time later.</summary>
        private static bool Timed(Run run) =>
            run.A.Action is TrainLocationAction.Pass or TrainLocationAction.Depart
            && run.B.Action is TrainLocationAction.Pass or TrainLocationAction.Arrive
            && run.Seconds > 0;

        private void SignalSamples()
        {
            foreach (var runs in _runs.Values)
            {
                for (var r = 0; r < runs.Count; r++)
                {
                    var run = runs[r];
                    if (!run.Usable)
                    {
                        continue;
                    }

                    // The signal ahead of each on this train's line (S38); for the last before b, the first the train
                    // passed after b (S39), so block headways can follow the signals across a timing location.
                    // The time to it is a block's running time (S39), unless the train stopped at b on the way.
                    var beyond = r + 1 < runs.Count && ReferenceEquals(runs[r + 1].A, run.B) && runs[r + 1].Between.Count > 0 ? runs[r + 1].Between[0] : null;
                    for (var i = 0; i < run.Between.Count; i++)
                    {
                        var s = run.Between[i];
                        var next = i + 1 < run.Between.Count ? run.Between[i + 1] : beyond;
                        int? seconds = next is null || (next == beyond && run.B.Action == TrainLocationAction.Arrive) || next.Time <= s.Time ? null : next.Time - s.Time;
                        _samples.Add(new PlaceSample(SignallingKind.Signal, s.Location.Code, run.A.Location, run.B.Location, run.FractionAt(s.Time), run.B.Time, Line: LineOf(run), Run: run.Kind, Next: next?.Location.Code, Aspect: s.AspectPassed, NextSeconds: seconds));
                    }
                }
            }
        }

        private TrackAttribution TrackSamples()
        {
            var occupied = tracks.Where(t => t.Occupied).OrderBy(t => t.Time).ToList();
            if (occupied.Count == 0)
            {
                return TrackAttribution.None;
            }

            // First: the place each track circuit's trains were last at.
            var counts = new Dictionary<string, Dictionary<Tiploc, int>>(StringComparer.Ordinal);
            var totals = new Dictionary<string, int>(StringComparer.Ordinal);
            Sweep(occupied, (track, near) =>
            {
                totals[track.Id] = totals.GetValueOrDefault(track.Id) + 1;
                var byPlace = counts.TryGetValue(track.Id, out var c) ? c : counts[track.Id] = [];
                foreach (var r in near)
                {
                    byPlace[r.Location] = byPlace.GetValueOrDefault(r.Location) + 1;
                }
            });
            var before = new Dictionary<string, Tiploc>(StringComparer.Ordinal);
            foreach (var (id, byPlace) in counts)
            {
                if (totals[id] >= _options.MinOccupations && byPlace.Count > 0 && byPlace.MaxBy(p => p.Value) is var best && best.Value >= _options.PredecessorShare * totals[id])
                {
                    before[id] = best.Key;
                }
            }

            // Then: each occupation to the one train near, last at that place.
            var clears = tracks.Where(t => !t.Occupied).GroupBy(t => t.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(t => t.Time).Order().ToList(), StringComparer.Ordinal);
            int attributed = 0, ambiguous = 0, none = 0, unknown = 0, placed = 0;
            Sweep(occupied, (track, near) =>
            {
                if (!before.TryGetValue(track.Id, out var place))
                {
                    unknown++;
                    return;
                }

                var at = near.Where(r => r.Location == place).ToList();
                switch (at.Count)
                {
                    case 0:
                        none++;
                        return;
                    case > 1:
                        ambiguous++;
                        return;
                }

                attributed++;
                if (RunAt(at[0].Uid, track.Time) is not { } run)
                {
                    return;
                }

                placed++;
                SimTime? cleared = clears.TryGetValue(track.Id, out var times) ? FirstFrom(times, track.Time) : null;
                var clearedInRun = cleared is { } c && c <= run.B.Time;
                double? end = clearedInRun ? run.FractionAt(cleared!.Value) : null;
                int? clearSeconds = clearedInRun && cleared!.Value > track.Time ? cleared.Value - track.Time : null;
                _samples.Add(new PlaceSample(SignallingKind.TrackCircuit, track.Id, run.A.Location, run.B.Location, run.FractionAt(track.Time), run.B.Time, end, LineOf(run), _timing.Contains(place) ? null : place.Code, run.Kind, ClearSeconds: clearSeconds));
            });
            return new TrackAttribution(occupied.Count, attributed, ambiguous, none, unknown, placed);
        }

        private int StepSamples()
        {
            var placed = 0;
            var signals = reports.Where(r => !_timing.Contains(r.Location)).Select(r => r.Location.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Sweep(steps.OrderBy(s => s.Time).ToList(), s => s.Time, (step, near) =>
            {
                var trains = near.Where(r => string.Equals(r.Headcode.ToString(), step.Description, StringComparison.OrdinalIgnoreCase)).ToList();
                if (trains.Count != 1 || RunAt(trains[0].Uid, step.Time) is not { } run)
                {
                    return;
                }

                placed++;
                var signal = SignalOf(step.Berth) is { } id && signals.Contains(id) ? id : null;
                _samples.Add(new PlaceSample(SignallingKind.Berth, step.Berth, run.A.Location, run.B.Location, run.FractionAt(step.Time), run.B.Time, Line: LineOf(run), Signal: signal, Run: run.Kind));
            });
            return placed;
        }

        private void Sweep(List<TrackChange> changes, Action<TrackChange, List<Report>> visit) => Sweep(changes, c => c.Time, visit);

        /// <summary>
        /// Visits each item in time order with the trains near it then: each train's latest report at or before the
        /// item's time, within <see cref="SignalLearnerOptions.NearSeconds"/>, unless it was an arrival (standing).
        /// </summary>
        private void Sweep<T>(List<T> items, Func<T, SimTime> timeOf, Action<T, List<Report>> visit)
        {
            var latest = new Dictionary<string, Report>(StringComparer.Ordinal);
            var next = 0;
            foreach (var item in items)
            {
                var t = timeOf(item);
                while (next < _timeline.Count && _timeline[next].Time <= t)
                {
                    latest[_timeline[next].Uid] = _timeline[next];
                    next++;
                }

                var near = new List<Report>();
                List<string>? stale = null;
                foreach (var (uid, r) in latest)
                {
                    if (t - r.Time > _options.NearSeconds)
                    {
                        (stale ??= []).Add(uid);
                    }
                    else if (r.Action != TrainLocationAction.Arrive)
                    {
                        near.Add(r);
                    }
                }

                foreach (var uid in stale ?? [])
                {
                    latest.Remove(uid);
                }

                visit(item, near);
            }
        }

        private Run? RunAt(string uid, SimTime t) =>
            _runs.TryGetValue(uid, out var runs) ? runs.FirstOrDefault(r => r.Usable && r.Covers(t)) : null;

        private static string? LineOf(Run run) => run.A.Platform.Trim() is { Length: > 0 } line ? line : null;

        private static SimTime? FirstFrom(List<SimTime> times, SimTime from)
        {
            var i = times.BinarySearch(from);
            i = i < 0 ? ~i : i;
            return i < times.Count ? times[i] : null;
        }
    }

    /// <summary>The signal a berth belongs to by its ID: berth <c>0457</c> is signal <c>S457</c>. Null for a berth not all digits.</summary>
    public static string? SignalOf(string berth)
    {
        ArgumentNullException.ThrowIfNull(berth);
        var digits = berth.Trim();
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var number = digits.TrimStart('0');
        return number.Length == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"S{number}");
    }
}
