using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

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

    public static Forecast Run(ForecastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var independent = new Pass(request, interaction: false).Run();
        if (!request.Options.Interaction || request.Network.Sections.Count == 0)
        {
            return new Forecast(request.Now, false, independent);
        }

        var interacted = new Pass(request, interaction: true).Run();
        var result = new TrainForecast[interacted.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var with = interacted[i];
            var without = independent[i];
            var knockOn = with.Points.LastOrDefault(p => p.LastTime is not null)?.LastTime is { } a &&
                without.Points.LastOrDefault(p => p.LastTime is not null)?.LastTime is { } b ? Math.Max(0, a - b) : 0;
            result[i] = knockOn == 0 ? with : with with { KnockOnSeconds = knockOn };
        }

        return new Forecast(request.Now, true, result);
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
        private readonly bool _interaction;
        private readonly WorkingPlan _plan;
        private readonly ForecastOptions _options;
        private readonly int? _now;
        private readonly List<Runner> _runners = [];
        private readonly PriorityQueue<Runner, (int Time, int Priority, int Order)> _queue = new();
        private readonly Dictionary<int, Occupancy> _occupancy = [];
        private readonly Dictionary<Headcode, Runner> _firstNotStarted = [];

        public Pass(ForecastRequest request, bool interaction)
        {
            _request = request;
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
            foreach (var actual in live.ActualsOf(working))
            {
                if (!actual.AtTimingPoint)
                {
                    continue;
                }

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
                    continue; // a timing location the plan does not book (SimSig reports those too)
                }

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
            if (lastWasArrival)
            {
                if (lastPoint)
                {
                    r.Status = TrainForecastStatus.Finished;
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
                run = RunTime(r, c, n, d, plannedRun, plannedArrive);
                if (r.FixedDeparture)
                {
                    break;
                }

                var moved = AfterBlocks(points, c, n, d, run);
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

        /// <summary>The departure, moved to the end of any block in force over the run.</summary>
        private int AfterBlocks(IReadOnlyList<PlannedPoint> points, int c, int n, int d, int run)
        {
            foreach (var block in _plan.Disruptions)
            {
                if (block.Kind == DisruptionKind.Block && d < block.End.Seconds && d + run > block.Start.Seconds && block.AppliesTo(points, c, n))
                {
                    return block.End.Seconds;
                }
            }

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

        private Occupancy OccupancyOf(ForecastSection section)
        {
            if (!_occupancy.TryGetValue(section.Id, out var o))
            {
                _occupancy[section.Id] = o = new Occupancy(section);
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
                    var x = Window.Of(step, d + total, run, booked);
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
                var x = Window.Of(step, d, run, booked);
                var occupancy = OccupancyOf(step.Section);
                occupancy.Shift(x, r, out var track);
                occupancy.Add(new Reservation(r, x, track));
            }
        }
    }

    /// <summary>One train's time in one section, as forecast and as booked.</summary>
    private readonly record struct Window(bool Down, int Enter, int Exit, int? BookedEnter, int? BookedExit)
    {
        public static Window Of(RouteStep step, int depart, int run, (int Depart, int Arrive)? booked)
        {
            int? bookedEnter = null, bookedExit = null;
            if (booked is { } b)
            {
                var bookedRun = b.Arrive - b.Depart;
                bookedEnter = b.Depart + (int)Math.Round(bookedRun * step.EnterShare);
                bookedExit = b.Depart + (int)Math.Round(bookedRun * step.ExitShare);
            }

            return new Window(step.Down, depart + (int)Math.Round(run * step.EnterShare), depart + (int)Math.Round(run * step.ExitShare), bookedEnter, bookedExit);
        }

        public Window Shifted(int seconds) => this with { Enter = Enter + seconds, Exit = Exit + seconds };
    }

    private sealed record Reservation(Runner Runner, Window Window, int Track);

    /// <summary>The trains through one section so far, in entry order, and the rules for one more.</summary>
    private sealed class Occupancy(ForecastSection section)
    {
        private readonly List<Reservation> _reservations = [];
        private int _longest;

        public void Add(Reservation reservation)
        {
            var at = _reservations.FindLastIndex(r => r.Window.Enter <= reservation.Window.Enter) + 1;
            _reservations.Insert(at, reservation);
            _longest = Math.Max(_longest, reservation.Window.Exit - reservation.Window.Enter);
        }

        /// <summary>The least delay that lets <paramref name="x"/> through, and the track it then takes.</summary>
        public int Shift(Window x, Runner runner, out int track)
        {
            var tracks = section.TracksFor(x.Down);
            var best = int.MaxValue;
            track = 0;
            for (var t = 0; t < tracks; t++)
            {
                var shift = ShiftOnTrack(x, runner, t);
                if (shift < best)
                {
                    best = shift;
                    track = t;
                }

                if (best == 0)
                {
                    break;
                }
            }

            return best;
        }

        private int ShiftOnTrack(Window x, Runner runner, int track)
        {
            var total = 0;
            var h = section.HeadwaySeconds;
            for (var settle = 0; settle < MaxSettle; settle++)
            {
                var w = x.Shifted(total);
                var shift = 0;
                foreach (var r in Near(w))
                {
                    if (ReferenceEquals(r.Runner, runner))
                    {
                        continue;
                    }

                    if (r.Window.Down == w.Down)
                    {
                        if (r.Track == track)
                        {
                            shift = Math.Max(shift, SameWay(r.Window, w, h));
                        }
                    }
                    else if (section.SingleLine)
                    {
                        shift = Math.Max(shift, Opposing(r.Window, w, h));
                    }
                }

                if (shift == 0)
                {
                    break;
                }

                total += shift;
            }

            return total;
        }

        /// <summary>Reservations that could clash with <paramref name="w"/>: entering no more than the longest stay plus the headway before it.</summary>
        private IEnumerable<Reservation> Near(Window w)
        {
            var from = w.Enter - _longest - section.HeadwaySeconds - 1;
            var to = w.Exit + section.HeadwaySeconds + 1;
            var lo = 0;
            var hi = _reservations.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_reservations[mid].Window.Enter < from)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (var i = lo; i < _reservations.Count && _reservations[i].Window.Enter <= to; i++)
            {
                yield return _reservations[i];
            }
        }

        /// <summary>
        /// Delay needed for <paramref name="x"/> to follow <paramref name="l"/> on the same track: the headway at both
        /// ends, or the booked spacing if that is less. None if it already runs ahead of it by as much.
        /// </summary>
        private static int SameWay(Window l, Window x, int h)
        {
            if (x.Enter + Gap(x.BookedEnter, l.BookedEnter, h) <= l.Enter && x.Exit + Gap(x.BookedExit, l.BookedExit, h) <= l.Exit)
            {
                return 0; // x runs ahead of l
            }

            var enter = l.Enter + Gap(l.BookedEnter, x.BookedEnter, h) - x.Enter;
            var exit = l.Exit + Gap(l.BookedExit, x.BookedExit, h) - x.Exit;
            return Math.Max(0, Math.Max(enter, exit));
        }

        /// <summary>Delay needed for <paramref name="x"/> to enter a single line only once opposing <paramref name="l"/> has cleared it.</summary>
        private static int Opposing(Window l, Window x, int h)
        {
            const int tolerance = ForecastNetwork.ParallelSeconds;
            if (x.Exit + Gap(x.BookedExit, l.BookedEnter, h, tolerance) <= l.Enter)
            {
                return 0; // x is through before l comes
            }

            return Math.Max(0, l.Exit + Gap(l.BookedExit, x.BookedEnter, h, tolerance) - x.Enter);
        }

        /// <summary>
        /// The spacing needed from a leader's time to a follower's: the headway, or the booked spacing if both are
        /// booked in this order and it is less. Opposing trains on a single line may be booked to overlap by up to
        /// <see cref="ForecastNetwork.ParallelSeconds"/> (booking rounding at a crossing loop), which is kept.
        /// </summary>
        private static int Gap(int? leader, int? follower, int h, int tolerance = 0) =>
            leader is { } a && follower is { } b && b >= a - tolerance ? Math.Min(h, b - a) : h;
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

        public bool Failed { get; set; }

        public bool Done { get; set; }

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

            var points = new ForecastPoint[Points.Count];
            for (var k = 0; k < points.Length; k++)
            {
                var p = Points[k];
                points[k] = Result[k] ?? new ForecastPoint(k, p.Location, null, null, p.IsPass, ForecastBasis.None, null);
            }

            return new TrainForecast(Train.Id, Train.Headcode, Status, points, LiveUid, PossiblyStopped);
        }

        private static SimTime? ToTime(int? seconds) => seconds is { } s ? new SimTime(s) : null;
    }
}
