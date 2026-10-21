using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network.Profiles;

namespace Stringline.Planning;

/// <summary>What to forecast: the working plan, the live trains and the clock.</summary>
/// <param name="Plan">The working plan; its disruptions and minimum run times are used.</param>
public sealed record ForecastRequest(WorkingPlan Plan)
{
    /// <summary>Live trains; their matched workings give the actuals.</summary>
    public IEnumerable<LiveTrain> LiveTrains { get; init; } = [];

    /// <summary>The sim time now; no forecast time is earlier. Null forecasts as if nothing had run yet.</summary>
    public SimTime? Now { get; init; }

    /// <summary>When the session started; trains not seen whose plan ends before it are left out.</summary>
    public SimTime? SessionStart { get; init; }

    /// <summary>The sim's expected entry delay by headcode (<c>train_delay</c>), for trains not yet entered.</summary>
    public IReadOnlyDictionary<Headcode, int>? EntryDelays { get; init; }

    public ForecastNetwork Network { get; init; } = ForecastNetwork.Empty;

    public ForecastOptions Options { get; init; } = new();

    /// <summary>The signals, track circuits and berths as last reported (S38), for progress between timing points and held trains.</summary>
    public LineSideState LineSide { get; init; } = LineSideState.Empty;

    /// <summary>A request for the plan, live trains and last reported clock of a snapshot.</summary>
    public static ForecastRequest For(PlanSnapshot snapshot, ForecastNetwork? network = null, ForecastOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ForecastRequest(snapshot.Plan)
        {
            LiveTrains = snapshot.Trains,
            Now = snapshot.Clock.HasTime ? snapshot.Clock.LastReported : null,
            SessionStart = snapshot.SessionStart,
            EntryDelays = snapshot.EntryDelays,
            Network = network ?? ForecastNetwork.Empty,
            Options = options ?? new ForecastOptions(),
            LineSide = snapshot.LineSide,
        };
    }
}

/// <summary>
/// Projects every train along its working plan from its latest actual (<c>06-…md</c> §2). Pure, deterministic and
/// thread-safe.
/// </summary>
/// <remarks>
/// <para>One discrete-event pass: each train's next departure goes on a queue ordered by time, then category priority,
/// then plan order. Taking a departure works out the run to the next timed point:</para>
/// <list type="bullet">
/// <item>The run takes the planned time, or less if the train is late, but never less than the minimum run (lengthened
/// by a speed restriction in force), and never gets it there before its planned time: a late train uses up its
/// allowances until it is back on time, and an early one stays early only where it passes.</item>
/// <item>At a stop it leaves after the shortest dwell, and not before its planned departure.</item>
/// <item>A block in force over the run holds the train until the block ends.</item>
/// <item>With the interaction pass on, the run is checked against the trains already through each section on it (see
/// <see cref="ForecastNetwork"/>), and the train is held until it can go: behind a train the same way by the headway
/// at both ends, on another track if there is one free; on a single line, after an opposing train has cleared. No
/// pair is held further apart than the WTT books them, so the WTT itself never makes knock-on delay.</item>
/// <item>No time is before now: a train that has not reported reaching its next point is forecast to reach it now
/// at the earliest, and is <see cref="TrainForecast.PossiblyStopped"/> when that is long overdue.</item>
/// </list>
/// <para>A train formed from another (next working, divide, detach) starts no earlier than the other arrives at the
/// point it forms there, plus the turnaround.</para>
/// </remarks>
public static class Forecaster
{
    /// <summary>Most re-checks of one run before it is accepted as it stands (a guard; real runs settle in two or three).</summary>
    private const int MaxSettle = 64;

    /// <summary>A train's pace in a run is measured once it has covered this share of it (S38)…</summary>
    private const double MinPaceShare = 0.15;

    /// <summary>…and this many seconds of minimum run.</summary>
    private const int MinPaceSeconds = 20;

    /// <summary>A train last reported this long ago may still be near where it was (as the learner has it).</summary>
    private const int NearSeconds = 120;

    public static Forecast Run(ForecastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var starting = StartingTrains.Work(
            request.Plan.Timetable,
            request.SessionStart,
            new HashSet<TrainId>([.. StartingTrains.Matched(request.LiveTrains), .. request.Plan.RunAnyway]),
            request.Now);
        var independent = new Pass(request, starting, interaction: false).Run();
        if (!request.Options.Interaction || request.Network.Sections.Count == 0)
        {
            return new Forecast(request.Now, false, independent) { Starting = starting };
        }

        var interacted = new Pass(request, starting, interaction: true).Run();
        var result = new TrainForecast[interacted.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var with = interacted[i];
            var without = independent[i];
            var knockOn = with.Points.LastOrDefault(p => p.LastTime is not null)?.LastTime is { } a &&
                without.Points.LastOrDefault(p => p.LastTime is not null)?.LastTime is { } b ? Math.Max(0, a - b) : 0;
            result[i] = knockOn == 0 ? with : with with { KnockOnSeconds = knockOn };
        }

        return new Forecast(request.Now, true, result, new Forecast(request.Now, false, independent) { Starting = starting }) { Starting = starting };
    }

    /// <summary>The live train and working matched to each WTT train.</summary>
    private static Dictionary<TrainId, (LiveTrain Train, int Working)> MatchedWorkings(IEnumerable<LiveTrain> trains)
    {
        var map = new Dictionary<TrainId, (LiveTrain, int)>();
        foreach (var live in trains)
        {
            for (var w = 0; w < live.Workings.Count; w++)
            {
                if (live.Workings[w] is { IsMatched: true, Train: { } id })
                {
                    map[id] = (live, w);
                }
            }
        }

        return map;
    }

    /// <summary>One forecast run over every train.</summary>
    private sealed class Pass
    {
        private readonly ForecastRequest _request;
        private readonly StartingTrains _starting;
        private readonly bool _interaction;
        private readonly WorkingPlan _plan;
        private readonly ForecastOptions _options;
        private readonly int? _now;
        private readonly List<Runner> _runners = [];
        private readonly PriorityQueue<Runner, (int Time, int Priority, int Order)> _queue = new();
        private readonly Dictionary<int, SectionOccupancy<Runner>> _occupancy = [];
        private readonly Dictionary<Headcode, Runner> _firstNotStarted = [];
        private Dictionary<Tiploc, int>? _near;
        private Dictionary<Headcode, int>? _headcodes;
        private Dictionary<string, List<(string Berth, SimTime Since)>>? _berths;

        public Pass(ForecastRequest request, StartingTrains starting, bool interaction)
        {
            _request = request;
            _starting = starting;
            _interaction = interaction;
            _plan = request.Plan;
            _options = request.Options;
            _now = request.Now?.Seconds;
        }

        public List<TrainForecast> Run()
        {
            var live = MatchedWorkings(_request.LiveTrains);
            var byId = new Dictionary<TrainId, Runner>();
            foreach (var train in _plan.Trains)
            {
                if (train.Cancelled)
                {
                    continue;
                }

                var wtt = _plan.Timetable.Find(train.Source);
                var runner = new Runner(train, wtt, _runners.Count, _request.Network.PriorityOf(train.CategoryId));
                if (live.TryGetValue(train.Id, out var found))
                {
                    runner.LiveUid = found.Train.SimUid;
                    Safely(runner, () => StartRunning(runner, found.Train, found.Working));
                }
                else if (_request.SessionStart is { } start && _now is not null && train.RunningPoints.LastOrDefault(p => p.LastTime is not null)?.LastTime < start)
                {
                    continue; // over before the sim started
                }
                else if (train.Origin == PlanOrigin.Wtt && _starting.ReasonFor(train.Source) is { } reason)
                {
                    runner.Status = TrainForecastStatus.NotRunning;
                    runner.NotRunning = reason;
                }
                else if (train.Origin == PlanOrigin.Wtt && _starting.IsNotSeen(train.Source))
                {
                    runner.NotSeen = true;
                }

                _runners.Add(runner);
                byId[train.Id] = runner;
            }

            foreach (var runner in _runners)
            {
                if (runner.Status == TrainForecastStatus.NotStarted)
                {
                    _firstNotStarted.TryAdd(runner.Train.Headcode, runner);
                }
            }

            // Trains formed from another wait for it, unless they are running already.
            foreach (var runner in _runners)
            {
                if (runner.Status == TrainForecastStatus.NotStarted)
                {
                    Safely(runner, () => Prepare(runner, byId));
                }
            }

            while (_queue.TryDequeue(out var runner, out _))
            {
                Safely(runner, () => Step(runner));
            }

            return _runners.Select(r => r.ToForecast()).ToList();
        }

        private void Safely(Runner runner, Action action)
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // One train's failure must not stop the others (02-…md §6): it is marked unavailable.
            catch (Exception)
#pragma warning restore CA1031
            {
                runner.Failed = true;
                runner.ReleaseAll(this);
            }
        }

        // ---- Start ----

        /// <summary>A train with actuals: actual times up to its latest report, then the first departure to forecast.</summary>
        private void StartRunning(Runner r, LiveTrain live, int working)
        {
            var points = r.Points;
            var cursor = 0;
            var last = -1;
            var lastWasArrival = false;
            var unbooked = new List<ActualEvent>();
            ActualEvent? lastTiming = null;
            var sinceTiming = new List<ActualEvent>(); // signal passes since the last timing location (S38)
            foreach (var actual in live.ActualsOf(working))
            {
                if (!actual.AtTimingPoint)
                {
                    sinceTiming.Add(actual);
                    continue;
                }

                lastTiming = actual;
                sinceTiming.Clear();
                var k = -1;
                for (var j = cursor; j < points.Count; j++)
                {
                    if (points[j].Location == actual.Location)
                    {
                        k = j;
                        break;
                    }
                }

                if (k < 0)
                {
                    // A timing location the plan does not book (SimSig reports those too): a progress anchor (S36) if it
                    // comes after a booked report and the plan does not go there at all.
                    if (last >= 0 && !points.Any(p => p.Location == actual.Location))
                    {
                        unbooked.Add(actual);
                    }

                    continue;
                }

                unbooked.Clear();

                if (actual.Action == TrainLocationAction.Arrive)
                {
                    r.ActualArrive[k] ??= actual.Time.Seconds;
                    cursor = k;
                    lastWasArrival = true;
                }
                else
                {
                    r.ActualDepart[k] = actual.Time.Seconds;
                    cursor = k + 1;
                    lastWasArrival = false;
                }

                last = k;
            }

            r.Status = TrainForecastStatus.Running;
            r.LastSeen = live.LastSeen.Seconds;
            for (var k = 0; k <= last && k < points.Count; k++)
            {
                if (r.ActualArrive[k] is not null || r.ActualDepart[k] is not null)
                {
                    // A pass reported as a pass keeps its one time; a train that stopped there has two.
                    var arrive = r.ActualArrive[k];
                    r.Set(k, arrive, r.ActualDepart[k], arrive is null && points[k].IsPass, ForecastBasis.Actual);
                }
            }

            if (working < live.CurrentIndex)
            {
                r.Status = TrainForecastStatus.Finished; // it has become its next working
                return;
            }

            if (last < 0)
            {
                // Seen (at signals) but not yet at a point of its plan.
                Begin(r, null);
                return;
            }

            var lastPoint = last == points.Count - 1;
            if (_options.UnbookedAnchors && !lastPoint && unbooked.Count > 0 && NextTimed(points, last) is { } next)
            {
                for (var i = unbooked.Count - 1; i >= 0 && r.Anchor is null; i--)
                {
                    r.Anchor = AnchorAt(r, last, next, unbooked[i]);
                }
            }

            if (_options.SignalPlaces && !lastPoint && lastTiming is not null && NextTimed(points, last) is { } then)
            {
                // From the timing location last reported: the booked point, or an unbooked one on the way (S36).
                double? fromShare = lastTiming.Location == points[last].Location ? 0
                    : r.Anchor is { } a && a.Location == lastTiming.Location && a.Time == lastTiming.Time ? a.Share : null;
                if (fromShare is { } s0)
                {
                    PlaceProgress(r, live, last, then, lastTiming, s0, sinceTiming);
                }
            }

            if (lastWasArrival)
            {
                if (lastPoint)
                {
                    r.Status = TrainForecastStatus.Finished;
                    return;
                }

                if (r.Anchor is { } gone)
                {
                    // It has left without reporting its departure: it is at the anchor now.
                    r.Set(last, r.ActualArrive[last], null, false, ForecastBasis.Actual);
                    Queue(r, last, gone.Time.Seconds, fixedDeparture: true);
                    return;
                }

                var arrived = r.ActualArrive[last]!.Value;
                var depart = Floor(DepartAfterArriving(r, last, arrived));
                r.Set(last, arrived, depart, false, ForecastBasis.Arrived);
                Queue(r, last, depart, fixedDeparture: false);
            }
            else if (lastPoint)
            {
                r.Status = TrainForecastStatus.Finished;
            }
            else
            {
                Queue(r, last, r.ActualDepart[last]!.Value, fixedDeparture: true);
            }
        }

        /// <summary>The next point after <paramref name="k"/> with a time, if any.</summary>
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

        /// <summary>
        /// A report at an unbooked timing location as an anchor for the run from <paramref name="c"/> to
        /// <paramref name="n"/> (S36), if it lies on the way: between two of the plan's points from c to n on a corridor,
        /// or in the timetable's location graph. Its share of the run comes from the corridor's section minimums, else
        /// the minimum runs either side of it; null if neither says where it is.
        /// </summary>
        private ProgressAnchor? AnchorAt(Runner r, int c, int n, ActualEvent report) =>
            UnbookedShare(r, c, n, report.Location) is { } share ? new ProgressAnchor(report.Location, report.Time, c, n, share) : null;

        /// <summary>An unbooked timing location's share of the run from <paramref name="c"/> to <paramref name="n"/>, as <see cref="AnchorAt"/> finds it; null if not on the way.</summary>
        private double? UnbookedShare(Runner r, int c, int n, Tiploc via)
        {
            var points = r.Points;
            var mins = _plan.MinRuns;
            var network = _request.Network;
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

        /// <summary>
        /// The run from <paramref name="c"/> to <paramref name="n"/> of a train already past an unbooked anchor (S36),
        /// as the time from its departure <paramref name="d"/>: the rest from the anchor takes the minimum from there (from
        /// the WTT, else the run's minimum in proportion), or the planned rest if the train is not late, as
        /// <see cref="RunTime"/> does for a whole run.
        /// </summary>
        private int AnchoredRun(Runner r, ProgressAnchor anchor, int c, int n, int d, int plannedRun, int plannedArrive)
        {
            if (anchor.Place is { } place)
            {
                return PlacedRun(r, anchor, place, c, n, d, plannedRun, plannedArrive);
            }

            var from = anchor.Time.Seconds;
            var full = _plan.MinRuns.Along(r.Points, c, n, anchor.Time, _plan.Disruptions, plannedRun) ?? plannedRun;
            var plain = _plan.MinRuns.Along(r.Points, c, n);
            var restMin = _plan.MinRuns.Between(anchor.Location, r.Points[n].Location) is { } direct
                ? (plain is > 0 ? direct * (double)full / plain.Value : direct) // a speed restriction lengthens it as it does the whole run
                : full * (1 - anchor.Share);
            var plannedRest = plannedRun * (1 - anchor.Share);
            var rest = (int)Math.Round(Math.Max(restMin, Math.Min(plannedRest, plannedArrive - from)));
            return Math.Max(0, from + rest - d);
        }

        // ---- Progress between timing points at learned places (S38) ----

        /// <summary>
        /// Moves a running train's anchor on to the furthest learned place it is known to have reached since its last
        /// timing location: the last signal it passed, the track circuit it last entered, or the berth its description
        /// last stepped into, whichever came last. Then, if the signal ahead of there shows red and the train is overdue
        /// at it, it is standing there (<see cref="TrainForecast.Standing"/>).
        /// </summary>
        private void PlaceProgress(Runner r, LiveTrain live, int c, int n, ActualEvent from, double fromShare, List<ActualEvent> passes)
        {
            var places = _request.Network.Places;
            if (places.IsEmpty)
            {
                return;
            }

            var departed = from.Action != TrainLocationAction.Pass;
            var best = (Time: int.MinValue, Share: -1.0, Anchor: (ProgressAnchor?)null);
            void Offer(SignallingKind kind, string id, IEnumerable<SignallingPlace> candidates, SimTime time, int? aspect = null)
            {
                foreach (var group in candidates.Where(p => p.From == from.Location).GroupBy(p => p.To).OrderBy(g => LocationShare(r, c, n, g.Key) ?? 2))
                {
                    if (LocationShare(r, c, n, group.Key) is not { } toShare || toShare <= fromShare)
                    {
                        continue;
                    }

                    var arrives = group.Key == r.Points[n].Location && !r.Points[n].IsPass;
                    var place = UsablePlaces.Choose(group, SignallingPlaces.RunOf(departed, arrives))!;
                    var share = fromShare + (place.Fraction * (toShare - fromShare));
                    if (time.Seconds > best.Time || (time.Seconds == best.Time && share > best.Share))
                    {
                        var at = new AnchorPlace(kind, place.Id, from.Location, group.Key, place.Fraction, place.Run, from.Time, fromShare, aspect) { Block = kind == SignallingKind.Signal ? null : place.Signal };
                        best = (time.Seconds, share, new ProgressAnchor(Tiploc.TryCreate(id, out var tiploc) ? tiploc : from.Location, time, c, n, Math.Clamp(share, 0, 1)) { Place = at });
                    }

                    return;
                }
            }

            foreach (var pass in passes)
            {
                Offer(SignallingKind.Signal, pass.Location.Code, places.SignalFrom(pass.Location.Code, from.Location), pass.Time, pass.AspectPassed);
            }

            // A track circuit occupied since the train's last report, in the block it was last reported entering, if no
            // other train could have done it (as the learner puts them to trains).
            var lastReport = live.Last;
            if (lastReport.Action != TrainLocationAction.Arrive && NearAt(lastReport.Location) <= 1)
            {
                foreach (var track in places.TracksAfter(lastReport.Location.Code).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
                {
                    if (_request.LineSide.Track(track.Key) is { LastOccupied: { } occupied } && occupied >= lastReport.Time && (_now is not { } now || occupied.Seconds <= now))
                    {
                        Offer(SignallingKind.TrackCircuit, track.Key, track, occupied);
                    }
                }
            }

            // A berth its description stepped into since the last timing location, if no other train has its headcode.
            if (HeadcodeCount(live.Headcode) == 1)
            {
                foreach (var (berth, since) in BerthsHolding(live.Headcode))
                {
                    if (since >= from.Time)
                    {
                        Offer(SignallingKind.Berth, berth, places.Berth(berth), since);
                    }
                }
            }

            if (best.Anchor is { } anchor)
            {
                r.Anchor = anchor;
            }

            Standing(r, c, n, from, fromShare, best.Anchor);
        }

        /// <summary>
        /// Marks the train standing at the signal ahead of where it was last known (<paramref name="at"/>, or its last
        /// timing location), if that signal is red and the train is overdue there: later than
        /// <see cref="ForecastOptions.StandingFactor"/> times its minimum run to the signal, plus <see cref="ForecastOptions.StandingGraceSeconds"/>. Red is as the sim reported it; without the TD topic, a single yellow passed at
        /// the signal before it says so. The anchor moves to the signal, at the time it is reckoned to have got there.
        /// </summary>
        private void Standing(Runner r, int c, int n, ActualEvent from, double fromShare, ProgressAnchor? at)
        {
            if (_now is not { } now)
            {
                return;
            }

            var places = _request.Network.Places;
            var share = at?.Share ?? fromShare;
            var time = at?.Time ?? from.Time;
            var to = at?.Place?.To ?? places.Signals.Where(p => p.From == from.Location).Select(p => p.To).Distinct()
                .Select(t => (To: t, Share: LocationShare(r, c, n, t))).Where(t => t.Share > fromShare).OrderBy(t => t.Share).Select(t => (Tiploc?)t.To).FirstOrDefault();
            if (to is not { } next || LocationShare(r, c, n, next) is not { } toShare)
            {
                return;
            }

            var departed = from.Action != TrainLocationAction.Pass;
            var arrives = next == r.Points[n].Location && !r.Points[n].IsPass;
            var wanted = SignallingPlaces.RunOf(departed, arrives);
            var between = places.SignalsBetween(from.Location, next).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => UsablePlaces.Choose(g, wanted)!, StringComparer.OrdinalIgnoreCase);

            // The signal ahead on the train's own line: a berth's own signal; else the one trains passed next after the
            // last signal it passed (or the one starting the track circuit's block); else, from the timing location,
            // the first signal of trains that took the same line there. Parallel lines have signals at similar places.
            var aheadId = at?.Place switch
            {
                { Kind: SignallingKind.Berth, Block: { } own } => own,
                { Kind: SignallingKind.Signal } p => NextSignal(between, p.Id),
                { Kind: SignallingKind.TrackCircuit, Block: { } block } => NextSignal(between, block),
                _ => from.Platform.Length == 0 ? null
                    : between.Values.Where(p => p.Line == from.Platform).MinBy(p => p.Fraction)?.Id,
            };
            if (aheadId is null || !between.TryGetValue(aheadId, out var signal))
            {
                return;
            }

            var ahead = (Place: signal, Share: fromShare + (signal.Fraction * (toShare - fromShare)));
            if (ahead.Share <= share)
            {
                return;
            }

            var reported = _request.LineSide.Signal(signal.Id);
            var red = reported is { } state ? state.IsRed && state.Since.Seconds <= now
                : at?.Place is { Kind: SignallingKind.Signal, Aspect: SignalAspects.SingleYellow }; // a single yellow says the next was at red; no TD to say it has cleared
            if (!red)
            {
                return;
            }

            var plannedRun = Math.Max(0, r.Points[n].FirstTime!.Value.Seconds - r.Points[c].LastTime!.Value.Seconds);
            var full = _plan.MinRuns.Along(r.Points, c, n, time, _plan.Disruptions, plannedRun) ?? plannedRun;
            var expected = (int)Math.Round(full * (ahead.Share - share));
            if (now < time.Seconds + (_options.StandingFactor * expected) + _options.StandingGraceSeconds)
            {
                return;
            }

            var since = new SimTime(Math.Min(now, time.Seconds + expected));
            var place = new AnchorPlace(SignallingKind.Signal, signal.Id, from.Location, next, signal.Fraction, signal.Run, from.Time, fromShare, SignalAspects.Red);
            r.Anchor = new ProgressAnchor(Tiploc.TryCreate(signal.Id, out var tiploc) ? tiploc : from.Location, since, c, n, Math.Clamp(ahead.Share, 0, 1)) { Place = place };
            r.Standing = new StandingAtSignal(signal.Id, since, from.Location, next, signal.Fraction, new SimTime(Math.Max(now, since.Seconds)), reported is not null);
        }

        /// <summary>The signal trains passed next most often after <paramref name="signal"/>, among those placed in this run; null if none is known.</summary>
        private static string? NextSignal(Dictionary<string, SignallingPlace> between, string signal) =>
            between.TryGetValue(signal, out var place) && place.Next is { } next
                ? next.Where(n => between.ContainsKey(n.Key)).OrderByDescending(n => n.Value).Select(n => n.Key).FirstOrDefault()
                : null;

        /// <summary>
        /// How far along the run from <paramref name="c"/> to <paramref name="n"/> a location is, in minimum run time:
        /// 0 at c, a plan point's share of the minimums (else of the planned times), or an unbooked location's share as
        /// an anchor would have it. Null if it is not on the way.
        /// </summary>
        private double? LocationShare(Runner r, int c, int n, Tiploc location)
        {
            var points = r.Points;
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

                if (_plan.MinRuns.Along(points, c, n) is { } whole && whole > 0 && _plan.MinRuns.Along(points, c, j) is { } part)
                {
                    return (double)part / whole;
                }

                return points[j].FirstTime is { } t && points[c].LastTime is { } d && points[n].FirstTime is { } a && a > d
                    ? Math.Clamp((double)(t - d) / (a - d), 0, 1)
                    : null;
            }

            return UnbookedShare(r, c, n, location);
        }

        /// <summary>
        /// The run from <paramref name="c"/> to <paramref name="n"/> of a train past a learned place (S38), as the time
        /// from its departure <paramref name="d"/>: the share of the run left, at the minimum scaled by how the train
        /// has run so far in it (no slower than <see cref="ForecastOptions.MaxPace"/>), or the planned rest if it is not
        /// late. A train standing at a signal runs the rest from when it can move, at the minimum.
        /// </summary>
        private int PlacedRun(Runner r, ProgressAnchor anchor, AnchorPlace place, int c, int n, int d, int plannedRun, int plannedArrive)
        {
            var from = r.Standing is { } standing ? standing.Leaves.Seconds : anchor.Time.Seconds;
            var full = _plan.MinRuns.Along(r.Points, c, n, anchor.Time, _plan.Disruptions, plannedRun) ?? plannedRun;
            var pace = 1.0;
            var covered = anchor.Share - place.FromShare;
            var expected = full * covered;
            if (r.Standing is null && covered >= MinPaceShare && expected >= MinPaceSeconds && r.ActualArrive[c] is null)
            {
                pace = Math.Clamp((anchor.Time - place.FromTime) / expected, 1, _options.MaxPace);
            }

            var restMin = full * (1 - anchor.Share) * pace;
            var plannedRest = plannedRun * (1 - anchor.Share);
            var rest = (int)Math.Round(Math.Max(restMin, Math.Min(plannedRest, plannedArrive - from)));
            return Math.Max(0, from + rest - d);
        }

        /// <summary>Running trains whose last report was at this place and who may still be near it (for putting a track circuit to one).</summary>
        private int NearAt(Tiploc place)
        {
            if (_near is null)
            {
                _near = [];
                foreach (var t in _request.LiveTrains)
                {
                    var last = t.Last;
                    if (last.Action != TrainLocationAction.Arrive && (_now is not { } now || last.Time.Seconds >= now - NearSeconds))
                    {
                        _near[last.Location] = _near.GetValueOrDefault(last.Location) + 1;
                    }
                }
            }

            return _near.GetValueOrDefault(place);
        }

        private int HeadcodeCount(Headcode headcode)
        {
            _headcodes ??= _request.LiveTrains.GroupBy(t => t.Headcode).ToDictionary(g => g.Key, g => g.Count());
            return _headcodes.GetValueOrDefault(headcode);
        }

        /// <summary>The berths holding this headcode now, and since when.</summary>
        private List<(string Berth, SimTime Since)> BerthsHolding(Headcode headcode)
        {
            _berths ??= _request.LineSide.Berths.GroupBy(b => b.Value.Description, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(b => (b.Key, b.Value.Since)).ToList(), StringComparer.OrdinalIgnoreCase);
            return _berths.TryGetValue(headcode.Value, out var list) ? list : [];
        }

        /// <summary>A train not seen yet: if another train forms it, wait for that one; otherwise start now.</summary>
        private void Prepare(Runner r, Dictionary<TrainId, Runner> byId)
        {
            if (r.Wtt?.FormedFrom is { } parentId && r.Train.Origin == PlanOrigin.Wtt && byId.TryGetValue(parentId, out var parent) &&
                FormingPoint(parent, r.Train.Id) is { } k && k <= parent.Train.LastIndex)
            {
                var turnaround = Turnaround(parent, k, r.Train.Id);
                if (parent.Result[k]?.FirstTime is { } arrived)
                {
                    Begin(r, arrived.Seconds + turnaround);
                    return;
                }

                if (parent.Status is TrainForecastStatus.Running or TrainForecastStatus.NotStarted && !parent.Failed && !parent.Done)
                {
                    parent.Waiting.Add((k, r, turnaround));
                    return;
                }
            }

            Begin(r, null);
        }

        /// <summary>Start a train at its first timed point, no earlier than <paramref name="ready"/> (its forming train's arrival plus turnaround) or now.</summary>
        private void Begin(Runner r, int? ready)
        {
            var points = r.Points;
            var first = -1;
            for (var k = 0; k < points.Count; k++)
            {
                if (points[k].IsTimed)
                {
                    first = k;
                    break;
                }
            }

            if (first < 0)
            {
                r.Status = TrainForecastStatus.Finished;
                r.Next = 0;
                r.ReleaseAll(this);
                return;
            }

            var hint = r.Status == TrainForecastStatus.NotStarted && _request.EntryDelays is { } delays && delays.TryGetValue(r.Train.Headcode, out var d) && IsFirstWithHeadcode(r) ? d : 0;
            var floor = Math.Max(ready ?? int.MinValue, _now ?? int.MinValue);
            var p = points[first];
            var planned = p.FirstTime!.Value.Seconds;
            var at = Math.Max(planned + hint, floor);
            r.Next = first;
            r.Release(first, at, this);
            if (first == points.Count - 1 && !p.IsPass)
            {
                r.Set(first, at, null, false, ForecastBasis.Forecast);
                Finish(r);
                return;
            }

            if (p.IsPass || p.Arrive is null)
            {
                var depart = p.IsPass ? at : Math.Max(at, (p.Depart ?? p.Arrive!.Value).Seconds);
                r.Set(first, null, depart, p.IsPass, ForecastBasis.Forecast);
                Queue(r, first, depart, fixedDeparture: false);
            }
            else
            {
                var depart = DepartAfterArriving(r, first, at);
                r.Set(first, at, depart, false, ForecastBasis.Forecast);
                Queue(r, first, depart, fixedDeparture: false);
            }
        }

        /// <summary>The sim gives entry delays by headcode: they apply to the first train with it not yet started.</summary>
        private bool IsFirstWithHeadcode(Runner r) => _firstNotStarted.GetValueOrDefault(r.Train.Headcode) == r;

        // ---- Running ----

        private void Queue(Runner r, int from, int depart, bool fixedDeparture)
        {
            r.Current = from;
            r.Departure = depart;
            r.FixedDeparture = fixedDeparture;
            if (r.Next < 0)
            {
                r.Next = from;
            }

            _queue.Enqueue(r, (depart, -r.Priority, r.Order));
        }

        /// <summary>Take a train's departure from its current point: forecast the run to its next timed point.</summary>
        private void Step(Runner r)
        {
            var points = r.Points;
            var c = r.Current;
            var n = c + 1;
            while (n < points.Count && !points[n].IsTimed)
            {
                n++;
            }

            if (n >= points.Count)
            {
                Finish(r);
                return;
            }

            var plannedDepart = points[c].LastTime!.Value.Seconds;
            var plannedArrive = points[n].FirstTime!.Value.Seconds;
            var plannedRun = Math.Max(0, plannedArrive - plannedDepart);
            var route = _interaction ? _request.Network.Route(points[c].Location, points[n].Location) : null;
            var booked = route is null ? null : Booked(r, c, n);

            var d = r.Departure;
            int run = plannedRun;
            for (var settle = 0; settle < MaxSettle; settle++)
            {
                run = r.FixedDeparture && r.Anchor is { } anchor && anchor.After == c
                    ? AnchoredRun(r, anchor, c, n, d, plannedRun, plannedArrive)
                    : RunTime(r, c, n, d, plannedRun, plannedArrive);
                if (r.FixedDeparture)
                {
                    break;
                }

                var moved = AfterBlocks(points, c, n, d, run, out var block);
                if (block is not null)
                {
                    r.BlockHolds.Add(new BlockHold(c, n, block.Id, new SimTime(d), new SimTime(moved)));
                }

                if (moved == d && route is not null)
                {
                    moved = d + Hold(route, r, booked, d, run);
                }

                if (moved == d)
                {
                    break;
                }

                d = moved;
            }

            var arrive = d + run;
            if (_now is { } now && arrive < now)
            {
                if (r.Status == TrainForecastStatus.Running && now - Math.Max(arrive, r.LastSeen) > _options.PossiblyStoppedSeconds)
                {
                    r.PossiblyStopped = true;
                }

                arrive = now;
            }

            if (route is not null)
            {
                Reserve(route, r, booked, d, arrive - d);
            }

            if (d != r.Departure)
            {
                HoldAt(r, c, d);
            }

            ArriveAt(r, n, arrive);
        }

        /// <summary>The run time from <paramref name="c"/> to <paramref name="n"/> leaving at <paramref name="d"/> (see the class remarks).</summary>
        private int RunTime(Runner r, int c, int n, int d, int plannedRun, int plannedArrive)
        {
            var min = _plan.MinRuns.Along(r.Points, c, n, new SimTime(d), _plan.Disruptions, plannedRun) ?? plannedRun;
            return Math.Max(min, Math.Min(plannedRun, plannedArrive - d));
        }

        /// <summary>The departure, moved to the end of any block in force over the run (<paramref name="block"/>).</summary>
        private int AfterBlocks(IReadOnlyList<PlannedPoint> points, int c, int n, int d, int run, out Disruption? block)
        {
            foreach (var disruption in _plan.Disruptions)
            {
                if (disruption.Kind == DisruptionKind.Block && d < disruption.End.Seconds && d + run > disruption.Start.Seconds && disruption.AppliesTo(points, c, n))
                {
                    block = disruption;
                    return disruption.End.Seconds;
                }
            }

            block = null;
            return d;
        }

        /// <summary>The train is held at its current point until <paramref name="d"/>: a pass there becomes a stop.</summary>
        private static void HoldAt(Runner r, int c, int d)
        {
            var current = r.Result[c]!;
            if (current.IsPass)
            {
                r.Set(c, current.Depart?.Seconds, d, false, current.Basis);
            }
            else
            {
                r.Set(c, current.Arrive?.Seconds, d, false, current.Basis);
            }

            r.Departure = d;
        }

        private void ArriveAt(Runner r, int n, int arrive)
        {
            var points = r.Points;
            var p = points[n];
            var last = n == points.Count - 1;
            r.Release(n, arrive, this);
            if (p.IsPass && !last)
            {
                r.Set(n, null, arrive, true, ForecastBasis.Forecast);
                Queue(r, n, arrive, fixedDeparture: false);
                return;
            }

            if (last || p.Depart is null)
            {
                r.Set(n, arrive, null, false, ForecastBasis.Forecast);
                Finish(r);
                return;
            }

            var depart = DepartAfterArriving(r, n, arrive);
            r.Set(n, arrive, depart, false, ForecastBasis.Forecast);
            Queue(r, n, depart, fixedDeparture: false);
        }

        private void Finish(Runner r)
        {
            r.Done = true;
            r.ReleaseAll(this);
        }

        /// <summary>A train formed from another can start, at <paramref name="ready"/> at the earliest (null: as planned).</summary>
        public void Released(Runner child, int? ready)
        {
            if (child.Status == TrainForecastStatus.NotStarted && child.Next < 0 && !child.Failed)
            {
                Safely(child, () => Begin(child, ready));
            }
        }

        private int DepartAfterArriving(Runner r, int k, int arrived)
        {
            var p = r.Points[k];
            var planned = p.Depart?.Seconds ?? arrived;
            return Math.Max(arrived + Dwell(r, k), planned);
        }

        private int Dwell(Runner r, int k)
        {
            if (r.Points[k].IsPass)
            {
                return 0;
            }

            var wttPoint = r.Wtt is { } wtt && k < wtt.Points.Count ? wtt.Points[k] : null;
            return wttPoint?.DwellSeconds ?? r.Wtt?.Dwell.StationForward ?? _options.DefaultDwellSeconds;
        }

        private int Floor(int time) => _now is { } now ? Math.Max(time, now) : time;

        private static int? FormingPoint(Runner parent, TrainId child)
        {
            if (parent.Wtt is not { } wtt)
            {
                return null;
            }

            for (var k = 0; k < wtt.Points.Count; k++)
            {
                if (wtt.Points[k].Activities.Any(a => a.FormsAssociatedTrain && a.Train == child))
                {
                    return k;
                }
            }

            return null;
        }

        private int Turnaround(Runner parent, int k, TrainId child)
        {
            var activity = parent.Wtt!.Points[k].Activities.First(a => a.FormsAssociatedTrain && a.Train == child);
            var dwell = parent.Wtt.Dwell;
            return (activity.Kind == ActivityKind.Next ? dwell.TerminateReverse ?? dwell.TerminateForward
                : activity.Kind is ActivityKind.DivideFront or ActivityKind.DivideRear ? dwell.Divide
                : null) ?? _options.DefaultTurnaroundSeconds;
        }

        // ---- Interaction ----

        /// <summary>The train's booked departure and arrival for the run in the WTT, if it is a WTT train timed at both ends.</summary>
        private static (int Depart, int Arrive)? Booked(Runner r, int c, int n) =>
            r.Train.Origin == PlanOrigin.Wtt && r.Wtt is { } wtt && n < wtt.Points.Count &&
            wtt.Points[c].LastTime is { } d && wtt.Points[n].FirstTime is { } a && a >= d
                ? (d.Seconds, a.Seconds)
                : null;

        private SectionOccupancy<Runner> OccupancyOf(ForecastSection section)
        {
            if (!_occupancy.TryGetValue(section.Id, out var o))
            {
                _occupancy[section.Id] = o = new SectionOccupancy<Runner>(section);
            }

            return o;
        }

        /// <summary>How much later than <paramref name="d"/> the run must leave to clear every train already through its sections.</summary>
        private int Hold(RouteStep[] route, Runner r, (int Depart, int Arrive)? booked, int d, int run)
        {
            var total = 0;
            for (var settle = 0; settle < MaxSettle; settle++)
            {
                var shift = 0;
                foreach (var step in route)
                {
                    var x = SectionWindow.Of(step, d + total, run, booked);
                    shift = Math.Max(shift, OccupancyOf(step.Section).Shift(x, r, out _));
                }

                if (shift == 0)
                {
                    break;
                }

                total += shift;
            }

            return total;
        }

        private void Reserve(RouteStep[] route, Runner r, (int Depart, int Arrive)? booked, int d, int run)
        {
            foreach (var step in route)
            {
                var x = SectionWindow.Of(step, d, run, booked);
                var occupancy = OccupancyOf(step.Section);
                occupancy.Shift(x, r, out var track);
                occupancy.Add(new SectionReservation<Runner>(r, x, track));
            }
        }
    }

    /// <summary>One train's state during a pass.</summary>
    private sealed class Runner
    {
        public Runner(TrainPlan train, Train? wtt, int order, int priority)
        {
            Train = train;
            Wtt = wtt;
            Order = order;
            Priority = priority;
            Points = train.RunningPoints;
            Result = new ForecastPoint?[Points.Count];
            ActualArrive = new int?[Points.Count];
            ActualDepart = new int?[Points.Count];
        }

        public TrainPlan Train { get; }

        public Train? Wtt { get; }

        public int Order { get; }

        public int Priority { get; }

        public IReadOnlyList<PlannedPoint> Points { get; }

        public ForecastPoint?[] Result { get; }

        public int?[] ActualArrive { get; }

        public int?[] ActualDepart { get; }

        public TrainForecastStatus Status { get; set; } = TrainForecastStatus.NotStarted;

        public string? LiveUid { get; set; }

        public int LastSeen { get; set; } = int.MinValue;

        /// <summary>The first point forecast, or -1 before the train has started.</summary>
        public int Next { get; set; } = -1;

        public int Current { get; set; }

        public int Departure { get; set; }

        /// <summary>The departure from <see cref="Current"/> is actual: the train is already on its way.</summary>
        public bool FixedDeparture { get; set; }

        public bool PossiblyStopped { get; set; }

        /// <summary>The unbooked timing location the run on from its last booked report is forecast from (S36).</summary>
        public ProgressAnchor? Anchor { get; set; }

        /// <summary>Standing at a red signal (S38).</summary>
        public StandingAtSignal? Standing { get; set; }

        public NotRunningReason? NotRunning { get; set; }

        public bool NotSeen { get; set; }

        public bool Failed { get; set; }

        public bool Done { get; set; }

        /// <summary>Where a block held the train (<see cref="TrainForecast.BlockHolds"/>).</summary>
        public List<BlockHold> BlockHolds { get; } = [];

        /// <summary>Trains formed from this one, waiting for it to reach the point they form at.</summary>
        public List<(int Point, Runner Child, int Turnaround)> Waiting { get; } = [];

        public void Set(int k, int? arrive, int? depart, bool isPass, ForecastBasis basis)
        {
            var p = Points[k];
            var last = depart ?? arrive;
            var planned = p.LastTime?.Seconds;
            Result[k] = new ForecastPoint(k, p.Location, ToTime(arrive), ToTime(depart), isPass, basis,
                last is { } t && planned is { } pl ? t - pl : null);
        }

        /// <summary>Starts the trains waiting for this one to reach point <paramref name="k"/>.</summary>
        public void Release(int k, int arrive, Pass pass)
        {
            for (var i = Waiting.Count - 1; i >= 0; i--)
            {
                if (Waiting[i].Point == k)
                {
                    var (_, child, turnaround) = Waiting[i];
                    Waiting.RemoveAt(i);
                    pass.Released(child, arrive + turnaround);
                }
            }
        }

        /// <summary>Lets go of every train still waiting (this one will not get where they form): they start as planned.</summary>
        public void ReleaseAll(Pass pass)
        {
            var waiting = Waiting.ToList();
            Waiting.Clear();
            foreach (var (_, child, _) in waiting)
            {
                pass.Released(child, null);
            }
        }

        public TrainForecast ToForecast()
        {
            if (Failed)
            {
                return new TrainForecast(Train.Id, Train.Headcode, TrainForecastStatus.Unavailable, [], LiveUid);
            }

            if (Status == TrainForecastStatus.NotRunning)
            {
                return new TrainForecast(Train.Id, Train.Headcode, Status, []) { NotRunning = NotRunning };
            }

            var points = new ForecastPoint[Points.Count];
            for (var k = 0; k < points.Length; k++)
            {
                var p = Points[k];
                points[k] = Result[k] ?? new ForecastPoint(k, p.Location, null, null, p.IsPass, ForecastBasis.None, null);
            }

            return new TrainForecast(Train.Id, Train.Headcode, Status, points, LiveUid, PossiblyStopped)
            {
                BlockHolds = BlockHolds.Count == 0 ? [] : [.. BlockHolds],
                NotSeen = NotSeen && Status == TrainForecastStatus.NotStarted,
                Anchor = Status == TrainForecastStatus.Running ? Anchor : null,
                Standing = Status == TrainForecastStatus.Running ? Standing : null,
            };
        }

        private static SimTime? ToTime(int? seconds) => seconds is { } s ? new SimTime(s) : null;
    }
}
