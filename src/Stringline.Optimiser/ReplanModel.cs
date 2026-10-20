using System.Globalization;
using Google.OrTools.Sat;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Planning;

namespace Stringline.Optimiser;

/// <summary>An arrival or departure of a train in the model, at one of its plan points.</summary>
internal readonly record struct EventKey(int Train, int Index, bool Depart);

/// <summary>
/// A time in the model: <see cref="From"/> and <see cref="To"/> mixed in thousandths (<see cref="Share"/> of the way
/// from one to the other). A section's entry or exit inside a run is a share of the run, as the forecaster splits it;
/// a time at a point is the event itself (both the same, share 0).
/// </summary>
/// <param name="Constant">A fixed time instead (an event outside the model, such as a train that has arrived).</param>
internal readonly record struct TimeRef(EventKey From, EventKey To, int Share, long? Constant = null)
{
    public static TimeRef At(EventKey e) => new(e, e, 0);

    public static TimeRef Fixed(long time) => new(default, default, 0, time);
}

/// <summary><see cref="Later"/> must be at least <see cref="Gap"/> seconds after <see cref="Earlier"/>.</summary>
internal readonly record struct Separation(TimeRef Later, TimeRef Earlier, int Gap);

/// <summary>A time while the model is built: a constant, or an expression of its variables.</summary>
internal readonly record struct Time(LinearExpr Expr, long? Value)
{
    public static Time Of(long value) => new(LinearExpr.Constant(value), value);

    public static Time operator +(Time t, long seconds) => t.Value is { } v ? Of(v + seconds) : new(t.Expr + seconds, null);

    public static Time operator +(Time t, Time u) => t.Value is { } v && u.Value is { } w ? Of(v + w) : new(t.Expr + u.Expr, null);

    public static Time operator -(Time t, Time u) => t.Value is { } v && u.Value is { } w ? Of(v - w) : new(t.Expr - u.Expr, null);
}

/// <summary>A train at a platform at one of its points.</summary>
internal readonly record struct Presence(int Train, int Index, string Platform);

/// <summary>
/// Two trains that must not clash at one place: either <see cref="X"/> goes first (and <see cref="XFirst"/> holds) or
/// <see cref="Y"/> does (<see cref="YFirst"/>). At a platform it applies only while both are planned there.
/// </summary>
internal sealed class Disjunction(string key, int x, int y, Separation[] xFirst, Separation[] yFirst, Presence[] conditions)
{
    public string Key { get; } = key;

    public int X { get; } = x;

    public int Y { get; } = y;

    public Separation[] XFirst { get; } = xFirst;

    public Separation[] YFirst { get; } = yFirst;

    public Presence[] Conditions { get; } = conditions;

    /// <summary>
    /// <see cref="XFirst"/> applies. On a section with more than one track that way, the train behind may take
    /// another track, so a side applies only once the conflict detector finds the pair clashing that way round.
    /// </summary>
    public bool XActive { get; set; } = true;

    public bool YActive { get; set; } = true;

    /// <summary>More spacing found needed with <see cref="X"/> first, by checking a plan with the forecaster.</summary>
    public int ExtraX { get; set; }

    /// <summary>More spacing found needed with <see cref="Y"/> first.</summary>
    public int ExtraY { get; set; }
}

/// <summary>One train in the model: the part of its plan from its latest actual to just past the horizon.</summary>
internal sealed class ModelTrain(int number, TrainPlan plan, Train? wtt, TrainForecast forecast, double weight)
{
    public int Number { get; } = number;

    public TrainPlan Plan { get; } = plan;

    public Train? Wtt { get; } = wtt;

    /// <summary>Its forecast without the interaction pass: the times the model starts from.</summary>
    public TrainForecast Base { get; } = forecast;

    public double Weight { get; } = weight;

    public IReadOnlyList<PlannedPoint> Points { get; } = plan.RunningPoints;

    /// <summary>The timed points in the model, in order.</summary>
    public List<int> Timed { get; } = [];

    /// <summary>The train it is formed from, the point there it forms this one, and the turnaround.</summary>
    public (int Parent, int Index, int Turnaround)? FormedFrom { get; set; }

    /// <summary>For a train formed at its first point from another: when that one arrives there.</summary>
    public TimeRef? FormedIn { get; set; }

    /// <summary>The sim's entry delay the forecast gave its start, which a hold at its origin must not count twice.</summary>
    public int EntryDelay { get; set; }

    public TrainId Id => Plan.Id;

    /// <summary>The first timed point of its plan: where it starts, or enters the area.</summary>
    public int PlanFirst { get; } = FirstTimed(plan.RunningPoints);

    /// <summary>The base arrival; at a pass (the forecast gives it only as a departure), the pass time.</summary>
    public int? BaseArrive(int k) => (Base.Points[k].Arrive ?? (Base.Points[k].IsPass && k != PlanFirst ? Base.Points[k].Depart : null))?.Seconds;

    public int? BaseDepart(int k) => Base.Points[k].Depart?.Seconds;

    /// <summary>True if the train arrives at the point: it does not start (or enter the area) there.</summary>
    public bool HasArrive(int k) => BaseArrive(k) is not null;

    public bool HasDepart(int k) => Base.Points[k].Depart is not null && k < Timed[^1];

    public int First => Timed[0];

    /// <summary>
    /// The train can be held at <paramref name="k"/> (as suggestions hold it): it has not left, and it is a call,
    /// its first point, or a platform it passes.
    /// </summary>
    public bool Holdable(int k)
    {
        if (!HasDepart(k) || Base.Points[k].Basis == ForecastBasis.Actual)
        {
            return false;
        }

        var p = Points[k];
        return p.Depart is not null && (p.IsCall || (k == First && !HasArrive(k)) || (p.IsPass && !string.IsNullOrWhiteSpace(p.Platform)));
    }

    /// <summary>Its next timed point in the model after <paramref name="k"/>, or -1.</summary>
    public int NextTimed(int k)
    {
        var i = Timed.IndexOf(k);
        return i >= 0 && i + 1 < Timed.Count ? Timed[i + 1] : -1;
    }

    public override string ToString() => $"{Plan.Headcode} {Timed[0]}–{Timed[^1]}";

    private static int FirstTimed(IReadOnlyList<PlannedPoint> points)
    {
        for (var k = 0; k < points.Count; k++)
        {
            if (points[k].IsTimed)
            {
                return k;
            }
        }

        return -1;
    }
}

/// <summary>A train's run through one section, between two of its timed points.</summary>
internal sealed record Passage(ModelTrain Train, int From, int To, ForecastSection Section, bool Down, int EnterShare, int ExitShare, int? BookedEnter, int? BookedExit, int Enter, int Exit)
{
    public TimeRef EnterRef => new(new EventKey(Train.Number, From, true), new EventKey(Train.Number, To, false), EnterShare);

    public TimeRef ExitRef => new(new EventKey(Train.Number, From, true), new EventKey(Train.Number, To, false), ExitShare);
}

/// <summary>A train's time at a platform, by the base forecast.</summary>
internal sealed record Stay(ModelTrain Train, int Index, string Platform, int Start, int End)
{
    /// <summary>
    /// When it is in the platform from: its arrival, or for a train formed there from another, when that one
    /// arrives (as the conflict detector has it).
    /// </summary>
    public TimeRef StartRef => Index == Train.First && Train.FormedIn is { } formed ? formed : TimeRef.At(new EventKey(Train.Number, Index, !Train.HasArrive(Index)));

    public TimeRef EndRef => TimeRef.At(new EventKey(Train.Number, Index, Train.HasDepart(Index)));
}

/// <summary>What a solve found: every event's time, each hold and platform change.</summary>
internal sealed record ModelSolution(Dictionary<EventKey, long> Times, Dictionary<EventKey, long> Holds, Dictionary<(int Train, int Index), string> Platforms, bool Optimal)
{
    /// <summary>The pairs the model could not keep apart, by key, and by how much (for the log and tests).</summary>
    public Dictionary<string, long> Clashes { get; init; } = [];
}

/// <summary>
/// The re-planning model (<c>06-…md</c> §5) for CP-SAT: each train's arrival and departure times from now to the end
/// of the horizon, and the clashes between trains as either-or orders. Built from the forecast without the
/// interaction pass, as conflicts are found on it.
/// </summary>
/// <remarks>
/// <para><b>Trains</b> follow the forecaster's rules exactly, so with no holds the model gives the base forecast: a
/// run takes <c>max(min, min(planned run, planned arrival − departure))</c> (a late train uses up its allowances); a
/// train leaves a stop after its dwell and not before its planned departure. It can be held (made to leave later)
/// where suggestions hold trains: at a call, at its first point, or at a platform it passes.</para>
/// <para><b>Clashes</b> are the conflict detector's rules: in a section, a train following another the same way
/// enters and leaves the headway after it (or as close as the WTT books them); on a single line an opposing train
/// must have cleared; at a platform the next train arrives the margin after the last leaves (or as booked). Each pair
/// that clashes, and each train's neighbours on its route, gets an order to choose. A platform clash also offers each
/// train other platforms (if allowed), with a penalty. A clash the model cannot avoid costs a lot per second, so it is
/// always solvable. Pairs are added as the forecaster finds them in the model's plans (lazily), and a pair still
/// clashing in a plan gets more spacing.</para>
/// <para><b>Cost</b> (minimised): Σ weight × delay at each train's last point in the model, a small cost per hold, and
/// the platform change penalty, in tenths of weighted seconds.</para>
/// </remarks>
internal sealed class ReplanModel
{
    /// <summary>What a second of a clash the model could not avoid costs, in tenths of weighted seconds.</summary>
    private const long ClashCost = 1000;

    /// <summary>
    /// What each second held costs besides the delay at the train's last point, in weighted seconds: a train that
    /// makes up its time by then is still late on the way, so the shortest and latest hold is preferred.
    /// </summary>
    private const double HeldSecondCost = 0.2;

    /// <summary>The latest time a variable can take (well past any sim's days).</summary>
    private const long MaxTime = 30L * SimTime.SecondsPerDay;

    /// <summary>A stay at another platform this close in time to a train moving there is checked against it.</summary>
    private const int PlatformWindowSeconds = 30 * SimTime.SecondsPerMinute;

    private readonly ReplanEvaluator _evaluator;
    private readonly ReplanOptions _options;
    private readonly WorkingPlan _plan;
    private readonly ForecastNetwork _network;
    private readonly int _margin;
    private readonly int _tolerance;
    private readonly List<ModelTrain> _trains = [];
    private readonly Dictionary<TrainId, ModelTrain> _byId = [];
    private readonly Dictionary<int, List<Passage>> _passages = [];
    private readonly Dictionary<(Tiploc Location, string Platform), List<Stay>> _stays = [];
    private readonly Dictionary<string, Disjunction> _disjunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Train, int Index), List<string>> _platformOptions = [];
    private readonly Dictionary<(int Train, int Index), List<(TrainId Train, int Index, int? Number)>> _platformGroups = [];
    private readonly Dictionary<(int Train, int Index), (int Train, int Index)> _groupLeader = [];
    private readonly HashSet<int> _neighboured = [];
    private readonly HashSet<string> _solved = new(StringComparer.Ordinal);
    private ModelSolution? _last;

    public ReplanModel(ReplanEvaluator evaluator)
    {
        _evaluator = evaluator;
        _options = evaluator.Options;
        _plan = evaluator.Plan;
        _network = evaluator.Request.Forecast.Network;
        _margin = evaluator.Request.Conflicts.PlatformMarginSeconds;
        _tolerance = Math.Max(0, evaluator.Request.Conflicts.MinSeveritySeconds - 1);
        AddTrains(evaluator.Baseline.Independent);
        foreach (var t in _trains)
        {
            Index(t);
        }
    }

    public int TrainCount => _trains.Count;

    public IReadOnlyList<ModelTrain> Trains => _trains;

    /// <summary>The solver's status after the last solve (and why, if the model was invalid), for the log.</summary>
    public string LastStatus { get; private set; } = string.Empty;

    public int DisjunctionCount => _disjunctions.Count;

    /// <summary>A train's times in a solution against its base times, and its pairs, for the debug log and tests.</summary>
    public string Explain(ModelSolution solution, string headcode)
    {
        var lines = new List<string>();
        foreach (var t in _trains.Where(t => t.Plan.Headcode.Value == headcode))
        {
            foreach (var k in t.Timed)
            {
                var a = solution.Times.TryGetValue(new EventKey(t.Number, k, false), out var av) ? new SimTime((int)av).ToString() : "-";
                var d = solution.Times.TryGetValue(new EventKey(t.Number, k, true), out var dv) ? new SimTime((int)dv).ToString() : "-";
                lines.Add($"  {k} {t.Points[k].Location} base {t.Base.Points[k]} model {a}/{d}{(t.Holdable(k) ? " holdable" : string.Empty)}");
            }

            foreach (var d in _disjunctions.Values.Where(d => d.X == t.Number || d.Y == t.Number))
            {
                var other = _trains[d.X == t.Number ? d.Y : d.X];
                var xFirst = Hint(solution, d);
                lines.Add($"  {Describe(d.Key)} chosen {(xFirst == true ? _trains[d.X].Plan.Headcode : _trains[d.Y].Plan.Headcode)} first; other {other}");
                foreach (var sep in (xFirst == true ? d.XFirst : d.YFirst))
                {
                    lines.Add($"    later {Value(solution, sep.Later):0} earlier {Value(solution, sep.Earlier):0} gap {sep.Gap}");
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The pairs in the model, one a line, for the debug log and tests.</summary>
    public string Describe(string? key = null) => string.Join(Environment.NewLine, _disjunctions.Values.Where(d => key is null || d.Key == key).Select(d =>
        $"{d.Key} {_trains[d.X].Plan.Headcode}/{_trains[d.Y].Plan.Headcode}{(d.XActive ? " X" : string.Empty)}{(d.YActive ? " Y" : string.Empty)}" +
        (d.ExtraX + d.ExtraY > 0 ? string.Create(CultureInfo.InvariantCulture, $" +{d.ExtraX}/+{d.ExtraY}") : string.Empty)));

    // ---- Trains ----

    private void AddTrains(Forecast forecast)
    {
        var end = _evaluator.End.Seconds;
        foreach (var f in forecast.Trains)
        {
            if (f.Status is not (TrainForecastStatus.NotStarted or TrainForecastStatus.Running) ||
                _plan.Find(f.Train) is not { Cancelled: false } plan || f.Points.Count != plan.RunningPoints.Count)
            {
                continue;
            }

            var first = f.FirstForecast;
            if (first < 0 || (f.Points[first].FirstTime ?? f.Points[first].LastTime) is not { } starts || starts.Seconds > end)
            {
                continue;
            }

            var wtt = _plan.Timetable.Find(plan.Source);
            var t = new ModelTrain(_trains.Count, plan, wtt, f, _evaluator.WeightOf(plan));

            // From its latest actual (the run on from it is fixed) to the first timed point past the horizon.
            var start = first;
            for (var k = first - 1; k >= 0; k--)
            {
                if (f.Points[k].FirstTime is not null)
                {
                    start = f.Points[k].Basis == ForecastBasis.Actual ? k : first;
                    break;
                }
            }

            for (var k = start; k < f.Points.Count; k++)
            {
                if (f.Points[k].FirstTime is null || !plan.RunningPoints[k].IsTimed)
                {
                    continue;
                }

                t.Timed.Add(k);
                if (f.Points[k].FirstTime!.Value.Seconds > end)
                {
                    break;
                }
            }

            if (t.Timed.Count == 0)
            {
                continue;
            }

            _trains.Add(t);
            _byId[t.Id] = t;
        }

        foreach (var t in _trains)
        {
            Origin(t);
        }
    }

    /// <summary>How the train's start depends on the train it is formed from, and on the sim's entry delay.</summary>
    private void Origin(ModelTrain t)
    {
        var k = t.First;
        if (t.Base.Status != TrainForecastStatus.NotStarted || t.HasArrive(k) || t.Points[k].Depart is not { } planned)
        {
            return;
        }

        var start = t.BaseDepart(k) ?? planned.Seconds;
        if (_evaluator.Request.Forecast.EntryDelays?.TryGetValue(t.Plan.Headcode, out var delay) == true && delay > 0 && start == planned.Seconds + delay)
        {
            t.EntryDelay = delay;
        }

        if (t.Plan.Origin != PlanOrigin.Wtt || t.Wtt?.FormedFrom is not { } parentId)
        {
            return;
        }

        // In the platform from when the train it is formed from arrives (a fixed time if that one is not modelled).
        var location = t.Points[k].Location;
        if (_evaluator.Baseline.Independent.Find(parentId) is { } pf)
        {
            for (var i = pf.Points.Count - 1; i >= 0; i--)
            {
                if (pf.Points[i].Location == location && pf.Points[i].FirstTime is { } arrives)
                {
                    t.FormedIn = _byId.TryGetValue(parentId, out var p) && p.Timed.Contains(i) && p.HasArrive(i)
                        ? TimeRef.At(new EventKey(p.Number, i, false))
                        : TimeRef.Fixed(arrives.Seconds);
                    break;
                }
            }
        }

        if (!_byId.TryGetValue(parentId, out var parent) || parent.Wtt is not { } pw)
        {
            return;
        }

        for (var i = 0; i < pw.Points.Count; i++)
        {
            if (pw.Points[i].Activities.FirstOrDefault(a => a.FormsAssociatedTrain && a.Train == t.Plan.Source) is { } activity)
            {
                if (parent.Timed.Contains(i) && parent.HasArrive(i))
                {
                    var dwell = pw.Dwell;
                    var turnaround = (activity.Kind == ActivityKind.Next ? dwell.TerminateReverse ?? dwell.TerminateForward
                        : activity.Kind is ActivityKind.DivideFront or ActivityKind.DivideRear ? dwell.Divide
                        : null) ?? _evaluator.Request.Forecast.Options.DefaultTurnaroundSeconds;
                    t.FormedFrom = (parent.Number, i, turnaround);
                }

                return;
            }
        }
    }

    /// <summary>The shortest dwell at a stop, as the forecaster has it.</summary>
    private int Dwell(ModelTrain t, int k)
    {
        if (t.Points[k].IsPass)
        {
            return 0;
        }

        var wttPoint = t.Wtt is { } wtt && k < wtt.Points.Count ? wtt.Points[k] : null;
        return wttPoint?.DwellSeconds ?? t.Wtt?.Dwell.StationForward ?? _evaluator.Request.Forecast.Options.DefaultDwellSeconds;
    }

    /// <summary>The dwell the forecaster gives a pass made a stop (by <see cref="AddStop"/>).</summary>
    private int StopDwell(ModelTrain t, int k)
    {
        var wttPoint = t.Wtt is { } wtt && k < wtt.Points.Count ? wtt.Points[k] : null;
        return wttPoint?.DwellSeconds ?? t.Wtt?.Dwell.StationForward ?? _evaluator.Request.Forecast.Options.DefaultDwellSeconds;
    }

    private void Index(ModelTrain t)
    {
        for (var i = 0; i + 1 < t.Timed.Count; i++)
        {
            int k = t.Timed[i], j = t.Timed[i + 1];
            if (t.BaseDepart(k) is not { } d || t.BaseArrive(j) is not { } a || _network.Route(t.Points[k].Location, t.Points[j].Location) is not { } route)
            {
                continue;
            }

            foreach (var p in Passages(t, k, j, d, a, route))
            {
                if (!_passages.TryGetValue(p.Section.Id, out var list))
                {
                    _passages[p.Section.Id] = list = [];
                }

                list.Add(p);
            }
        }

        foreach (var k in t.Timed)
        {
            if (t.Points[k].Platform is { } platform && !string.IsNullOrWhiteSpace(platform) && StayOf(t, k, platform) is { } stay)
            {
                var key = (t.Points[k].Location, stay.Platform);
                if (!_stays.TryGetValue(key, out var list))
                {
                    _stays[key] = list = [];
                }

                list.Add(stay);
            }
        }
    }

    private static IEnumerable<Passage> Passages(ModelTrain t, int k, int j, int depart, int arrive, RouteStep[] route)
    {
        var booked = Booked(t, k, j);
        foreach (var step in route)
        {
            var window = SectionWindow.Of(step, depart, arrive - depart, booked);
            yield return new Passage(t, k, j, step.Section, step.Down, Milli(step.EnterShare), Milli(step.ExitShare), window.BookedEnter, window.BookedExit, window.Enter, window.Exit);
        }
    }

    private static int Milli(double share) => (int)Math.Round(share * 1000);

    private static (int Depart, int Arrive)? Booked(ModelTrain t, int from, int to) =>
        t.Plan.Origin == PlanOrigin.Wtt && t.Wtt is { } wtt && to < wtt.Points.Count &&
        wtt.Points[from].LastTime is { } d && wtt.Points[to].FirstTime is { } a && a >= d
            ? (d.Seconds, a.Seconds)
            : null;

    private static Stay? StayOf(ModelTrain t, int k, string platform)
    {
        var f = t.Base.Points[k];
        if (f.FirstTime is not { } first || f.LastTime is not { } last)
        {
            return null;
        }

        return new Stay(t, k, Upper(platform), first.Seconds, Math.Max(first.Seconds, last.Seconds));
    }

    private static string Upper(string platform) => platform.Trim().ToUpperInvariant();

    // ---- Clashes ----

    /// <summary>
    /// Adds the conflicts (found in a plan the model made, or in the plan as it stands) as orders to choose, with
    /// their trains' neighbours; a pair already in the model that still clashes gets more spacing on the side chosen.
    /// </summary>
    /// <param name="plan">The plan the conflicts were found in (its platforms).</param>
    /// <param name="forecast">The forecast without the interaction pass they were found on.</param>
    /// <returns>True if anything was added or changed.</returns>
    public bool Add(IEnumerable<Conflict> conflicts, WorkingPlan plan, Forecast forecast)
    {
        var changed = false;
        foreach (var c in conflicts)
        {
            if (!ReplanEvaluator.IsBetweenTrains(c) || !_byId.TryGetValue(c.A, out var a) || !_byId.TryGetValue(c.B!.Value, out var b))
            {
                continue;
            }

            var made = c.Kind == ConflictKind.Platform ? PlatformClash(c, a, b, plan, forecast) : SectionClash(c, a, b, forecast);
            changed |= made;
            foreach (var t in new[] { a, b })
            {
                if (_neighboured.Add(t.Number))
                {
                    changed |= Neighbours(t);
                }
            }
        }

        return changed;
    }

    private bool SectionClash(Conflict c, ModelTrain a, ModelTrain b, Forecast forecast)
    {
        // A's run into the clash, from the section it enters at Where to the end of the run.
        var from = c.PointIndex;
        var to = a.NextTimed(from);
        if (to < 0 || forecast.Find(a.Id) is not { } fa || forecast.Find(b.Id) is not { } fb ||
            Times(fa, from, to) is not var (ad, aa) || _network.Route(a.Points[from].Location, a.Points[to].Location) is not { } route)
        {
            return false;
        }

        var steps = Passages(a, from, to, ad, aa, route).ToList();
        var startAt = steps.FindIndex(p => (p.Down ? p.Section.From : p.Section.To) == c.Where);
        var changed = false;
        for (var s = Math.Max(0, startAt); s < steps.Count; s++)
        {
            var pa = steps[s];
            var single = c.Kind == ConflictKind.SingleLine;
            if (BestPassage(b, fb, pa.Section.Id, single ? !pa.Down : pa.Down, pa.Enter) is not { } pb)
            {
                if (s == Math.Max(0, startAt))
                {
                    continue;
                }

                break;
            }

            if (_disjunctions.TryGetValue(PairKey(pa, pb), out var d))
            {
                // Still clashing as the model planned it: more spacing on the side it chose (B first, as A gives way).
                if (s == Math.Max(0, startAt))
                {
                    changed |= MoreSpacing(d, b.Number, c.SeveritySeconds);
                }
            }
            else if (Pair(pa, pb) is { } key)
            {
                // With B first they clash: that side applies even where A could otherwise take another track.
                changed = true;
                Activate(_disjunctions[key], b.Number);
            }

            if (c.To is { } end && (pa.Down ? pa.Section.To : pa.Section.From) == end)
            {
                break;
            }
        }

        return changed;
    }

    /// <summary>The train's run through the section (that way) nearest in time to <paramref name="enter"/>, by <paramref name="forecast"/>.</summary>
    private Passage? BestPassage(ModelTrain t, TrainForecast forecast, int section, bool down, int enter)
    {
        Passage? best = null;
        var distance = int.MaxValue;
        for (var i = 0; i + 1 < t.Timed.Count; i++)
        {
            int k = t.Timed[i], j = t.Timed[i + 1];
            if (Times(forecast, k, j) is not var (d, a) || _network.Route(t.Points[k].Location, t.Points[j].Location) is not { } route)
            {
                continue;
            }

            foreach (var p in Passages(t, k, j, d, a, route))
            {
                if (p.Section.Id == section && p.Down == down && Math.Abs(p.Enter - enter) < distance)
                {
                    (best, distance) = (p, Math.Abs(p.Enter - enter));
                }
            }
        }

        return best;
    }

    private static (int Depart, int Arrive)? Times(TrainForecast f, int k, int j) =>
        f.Points[k].LastTime is { } d && f.Points[j].FirstTime is { } a ? (d.Seconds, a.Seconds) : null;

    private static string PairKey(Passage x, Passage y)
    {
        if (x.Train.Number > y.Train.Number)
        {
            (x, y) = (y, x);
        }

        return string.Create(CultureInfo.InvariantCulture, $"S:{x.Section.Id}:{x.Train.Number}:{x.From}:{y.Train.Number}:{y.From}");
    }

    /// <summary>The order to choose between two runs through a section (made if new); its key, or null if they cannot clash.</summary>
    private string? Pair(Passage x, Passage y)
    {
        if (ReferenceEquals(x.Train, y.Train) || x.Section.Id != y.Section.Id)
        {
            return null;
        }

        if (x.Train.Number > y.Train.Number)
        {
            (x, y) = (y, x);
        }

        var key = PairKey(x, y);
        if (_disjunctions.ContainsKey(key))
        {
            return key;
        }

        var h = x.Section.HeadwaySeconds;
        Separation[] xFirst, yFirst;
        if (x.Down == y.Down)
        {
            xFirst = [new(y.EnterRef, x.EnterRef, Gap(x.BookedEnter, y.BookedEnter, h)), new(y.ExitRef, x.ExitRef, Gap(x.BookedExit, y.BookedExit, h))];
            yFirst = [new(x.EnterRef, y.EnterRef, Gap(y.BookedEnter, x.BookedEnter, h)), new(x.ExitRef, y.ExitRef, Gap(y.BookedExit, x.BookedExit, h))];
        }
        else if (x.Section.SingleLine)
        {
            const int tolerance = ForecastNetwork.ParallelSeconds;
            xFirst = [new(y.EnterRef, x.ExitRef, Gap(x.BookedExit, y.BookedEnter, h, tolerance))];
            yFirst = [new(x.EnterRef, y.ExitRef, Gap(y.BookedExit, x.BookedEnter, h, tolerance))];
        }
        else
        {
            return null;
        }

        // The same way on more than one track, neither order needs spacing until the detector finds it does.
        var tracks = x.Down == y.Down && x.Section.TracksFor(x.Down) > 1;
        _disjunctions[key] = new Disjunction(key, x.Train.Number, y.Train.Number, xFirst, yFirst, []) { XActive = !tracks, YActive = !tracks };
        return key;
    }

    /// <summary>The spacing from a leader's time to a follower's, as <see cref="SectionOccupancy{T}"/> has it.</summary>
    private static int Gap(int? leader, int? follower, int h, int tolerance = 0) =>
        leader is { } a && follower is { } b && b >= a - tolerance ? Math.Min(h, b - a) : h;

    /// <summary>
    /// The pair still clashes with <paramref name="first"/> first: that side applies, and if it already did when the
    /// model made the plan, it needs <paramref name="seconds"/> more spacing.
    /// </summary>
    private bool MoreSpacing(Disjunction d, int first, int seconds)
    {
        if (Activate(d, first))
        {
            return true;
        }

        if (seconds <= 0 || !_solved.Contains(d.Key))
        {
            return false;
        }

        if (first == d.X)
        {
            d.ExtraX += seconds;
        }
        else
        {
            d.ExtraY += seconds;
        }

        return true;
    }

    /// <summary>Makes the side with <paramref name="first"/> first apply; true if it did not before.</summary>
    private static bool Activate(Disjunction d, int first)
    {
        if (first == d.X ? d.XActive : d.YActive)
        {
            return false;
        }

        if (first == d.X)
        {
            d.XActive = true;
        }
        else
        {
            d.YActive = true;
        }

        return true;
    }

    private bool PlatformClash(Conflict c, ModelTrain a, ModelTrain b, WorkingPlan plan, Forecast forecast)
    {
        if (c.Platform is not { } platform || forecast.Find(b.Id) is not { } fb || plan.Find(b.Id) is not { } pb)
        {
            return false;
        }

        // B's point at the platform nearest in time to A's.
        var upper = Upper(platform);
        int? kb = null;
        var distance = int.MaxValue;
        foreach (var k in b.Timed)
        {
            if (pb.RunningPoints[k].Location == c.Where && pb.RunningPoints[k].Platform is { } p && Upper(p) == upper &&
                (fb.Points[k].FirstTime ?? fb.Points[k].LastTime) is { } time && Math.Abs(time - c.When) < distance)
            {
                (kb, distance) = (k, Math.Abs(time - c.When));
            }
        }

        if (kb is not { } index || !a.Timed.Contains(c.PointIndex))
        {
            return false;
        }

        var changed = _disjunctions.TryGetValue(PlatformKey(a, c.PointIndex, b, index, upper), out var d)
            ? MoreSpacing(d, b.Number, c.SeveritySeconds)
            : PlatformPair(a, c.PointIndex, b, index, upper) is not null;
        changed |= Options(a, c.PointIndex);
        changed |= Options(b, index);
        return changed;
    }

    private static string PlatformKey(ModelTrain x, int kx, ModelTrain y, int ky, string platform)
    {
        if (x.Number > y.Number)
        {
            (x, kx, y, ky) = (y, ky, x, kx);
        }

        return string.Create(CultureInfo.InvariantCulture, $"P:{x.Number}:{kx}:{y.Number}:{ky}:{platform}");
    }

    /// <summary>The order to choose between two trains at a platform; its key, or null if they cannot clash there.</summary>
    private string? PlatformPair(ModelTrain x, int kx, ModelTrain y, int ky, string platform)
    {
        if (ReferenceEquals(x, y) || Associated(x, kx, y, ky))
        {
            return null;
        }

        if (x.Number > y.Number)
        {
            (x, kx, y, ky) = (y, ky, x, kx);
        }

        var key = PlatformKey(x, kx, y, ky, platform);
        if (_disjunctions.ContainsKey(key))
        {
            return key;
        }

        if (StayOf(x, kx, platform) is not { } sx || StayOf(y, ky, platform) is not { } sy)
        {
            return null;
        }

        var (bxs, bxe) = BookedStay(x, kx, platform);
        var (bys, bye) = BookedStay(y, ky, platform);
        _disjunctions[key] = new Disjunction(
            key,
            x.Number,
            y.Number,
            [new(sy.StartRef, sx.EndRef, PlatformGap(bxs, bxe, bys))],
            [new(sx.StartRef, sy.EndRef, PlatformGap(bys, bye, bxs))],
            [new Presence(x.Number, kx, platform), new Presence(y.Number, ky, platform)]);
        return key;
    }

    /// <summary>The time needed from one leaving the platform to the next arriving: the margin, or as booked if less.</summary>
    private int PlatformGap(int? firstStart, int? firstEnd, int? nextStart) =>
        firstStart is { } xs && firstEnd is { } xe && nextStart is { } ys && ys >= xs ? Math.Min(_margin, ys - xe) : _margin;

    private static (int? Start, int? End) BookedStay(ModelTrain t, int k, string platform) =>
        t.Wtt is { } wtt && t.Plan.Origin == PlanOrigin.Wtt && k < wtt.Points.Count && wtt.Points[k] is { FirstTime: { } s, LastTime: { } e } booked &&
        booked.Platform is { } p && Upper(p) == platform
            ? (s.Seconds, e.Seconds)
            : (null, null);

    /// <summary>True if the trains are associated there (one forms the other, or the WTT names the other), as the detector has it.</summary>
    private static bool Associated(ModelTrain x, int kx, ModelTrain y, int ky) =>
        Names(x, kx, y.Plan.Source) || Names(y, ky, x.Plan.Source) || x.Wtt?.FormedFrom == y.Plan.Source || y.Wtt?.FormedFrom == x.Plan.Source;

    private static bool Names(ModelTrain t, int index, TrainId other) =>
        t.Wtt is { } wtt && index < wtt.Points.Count && wtt.Points[index].Activities.Any(a => a.Train == other);

    /// <summary>
    /// Other platforms the train could use at its point (if allowed), each checked against the trains there. A train
    /// formed there from another, or forming one there, moves with it (as suggestions move them).
    /// </summary>
    private bool Options(ModelTrain t, int k)
    {
        if (!_options.AllowPlatformChanges || _groupLeader.ContainsKey((t.Number, k)) || Group(t, k) is not { } group)
        {
            return false;
        }

        var location = t.Points[k].Location;
        var current = Upper(t.Points[k].Platform!);
        var (start, end) = (t.Base.Points[k].FirstTime!.Value.Seconds, t.Base.Points[k].LastTime!.Value.Seconds);
        var members = group.Select(m => m.Train).ToHashSet();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var train in _plan.Trains)
        {
            if (train.Cancelled)
            {
                continue;
            }

            var forecast = _evaluator.Baseline.Independent.Find(train.Id);
            var points = train.RunningPoints;
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i].Location != location || string.IsNullOrWhiteSpace(points[i].Platform))
                {
                    continue;
                }

                var platform = Upper(points[i].Platform!);
                used.Add(platform);
                if (!members.Contains(train.Id) && forecast?.At(i) is { FirstTime: { } s, LastTime: { } e } && s.Seconds <= end + _margin && e.Seconds >= start - _margin)
                {
                    busy.Add(platform);
                }
            }
        }

        var options = used.Where(p => p != current && !busy.Contains(p))
            .OrderBy(p => Distance(p, current))
            .ThenBy(p => p, StringComparer.Ordinal)
            .Take(Math.Max(0, _options.PlatformCandidates))
            .ToList();
        _platformOptions[(t.Number, k)] = options;
        _platformGroups[(t.Number, k)] = group;
        foreach (var m in group)
        {
            if (m.Number is { } n)
            {
                _groupLeader[(n, m.Index)] = (t.Number, k);
            }
        }

        // A train moved to another platform must not clash with the trains already planned there.
        foreach (var platform in options)
        {
            if (!_stays.TryGetValue((location, platform), out var stays))
            {
                continue;
            }

            foreach (var m in group.Where(m => m.Number is not null))
            {
                var member = _trains[m.Number!.Value];
                foreach (var other in stays)
                {
                    if (other.Start <= end + PlatformWindowSeconds && other.End >= start - PlatformWindowSeconds)
                    {
                        PlatformPair(member, m.Index, other.Train, other.Index, platform);
                    }
                }
            }
        }

        return options.Count > 0;
    }

    /// <summary>
    /// The trains that move platform together with <paramref name="t"/> at its point <paramref name="k"/>: itself,
    /// the train it is formed from there and the trains formed from it there, in the same platform. Null if it cannot
    /// move: it (or the train it is formed from) is there already.
    /// </summary>
    private List<(TrainId Train, int Index, int? Number)>? Group(ModelTrain t, int k)
    {
        var f = t.Base.Points[k];
        if (string.IsNullOrWhiteSpace(t.Points[k].Platform) || f.Basis is ForecastBasis.Actual or ForecastBasis.Arrived || f.FirstTime is null || f.LastTime is null)
        {
            return null;
        }

        var location = t.Points[k].Location;
        var platform = Upper(t.Points[k].Platform!);
        var group = new List<(TrainId, int, int?)> { (t.Id, k, t.Number) };
        var independent = _evaluator.Baseline.Independent;

        // The train it is formed from, in the platform.
        if (k == t.First && !t.HasArrive(k) && t.Plan.Origin == PlanOrigin.Wtt && t.Wtt?.FormedFrom is { } parentId && _plan.Find(parentId) is { Cancelled: false } parent)
        {
            var points = parent.RunningPoints;
            for (var i = points.Count - 1; i >= 0; i--)
            {
                if (points[i].Location == location && points[i].Platform is { } pp && Upper(pp) == platform)
                {
                    if (independent.Find(parentId)?.At(i)?.Basis is not (ForecastBasis.Forecast or ForecastBasis.None))
                    {
                        return null; // it has brought the train in already
                    }

                    group.Add((parentId, i, _byId.TryGetValue(parentId, out var pm) ? pm.Number : null));
                    break;
                }
            }
        }

        // Trains formed from it there, in the platform.
        foreach (var wtt in _plan.Timetable.Trains)
        {
            if (wtt.FormedFrom != t.Plan.Source || _plan.Find(wtt.Id) is not { Cancelled: false } child)
            {
                continue;
            }

            var points = child.RunningPoints;
            var first = Enumerable.Range(0, points.Count).FirstOrDefault(i => points[i].IsTimed, -1);
            if (first >= 0 && points[first].Location == location && points[first].Platform is { } cp && Upper(cp) == platform)
            {
                group.Add((child.Id, first, _byId.TryGetValue(child.Id, out var cm) ? cm.Number : null));
            }
        }

        return group;
    }

    private static int Distance(string platform, string current) =>
        int.TryParse(platform, NumberStyles.None, CultureInfo.InvariantCulture, out var a) && int.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            ? Math.Abs(a - b)
            : int.MaxValue;

    /// <summary>
    /// Orders with the trains either side of <paramref name="t"/> on its route (the same way on a section with one
    /// track that way; opposing on a single line) and at its platforms, so a hold does not just move the clash on.
    /// </summary>
    private bool Neighbours(ModelTrain t)
    {
        var changed = false;
        foreach (var list in _passages.Values)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (!ReferenceEquals(p.Train, t))
                {
                    continue;
                }

                var sameWay = list.Where(q => q.Down == p.Down && !ReferenceEquals(q.Train, t)).ToList();
                if (p.Section.TracksFor(p.Down) == 1)
                {
                    changed |= Nearest(p, sameWay);
                }

                if (p.Section.SingleLine)
                {
                    changed |= Nearest(p, list.Where(q => q.Down != p.Down).ToList());
                }
            }
        }

        foreach (var stays in _stays.Values)
        {
            foreach (var s in stays.Where(s => ReferenceEquals(s.Train, t)).ToList())
            {
                var before = stays.Where(o => !ReferenceEquals(o.Train, t) && o.Start <= s.Start).MaxBy(o => o.Start);
                var after = stays.Where(o => !ReferenceEquals(o.Train, t) && o.Start > s.Start).MinBy(o => o.Start);
                foreach (var o in new[] { before, after }.OfType<Stay>())
                {
                    changed |= PlatformPair(t, s.Index, o.Train, o.Index, s.Platform) is not null;
                }
            }
        }

        return changed;
    }

    private bool Nearest(Passage p, List<Passage> others)
    {
        var before = others.Where(q => q.Enter <= p.Enter).MaxBy(q => q.Enter);
        var after = others.Where(q => q.Enter > p.Enter).MinBy(q => q.Enter);
        var changed = false;
        foreach (var q in new[] { before, after }.OfType<Passage>())
        {
            var count = _disjunctions.Count;
            Pair(p, q);
            changed |= _disjunctions.Count > count;
        }

        return changed;
    }

    // ---- Solving ----

    /// <summary>Builds the CP-SAT model and solves it within the time limit; null if no solution was found.</summary>
    /// <param name="fixedDepartures">Departures to fix at these times (for tests: the model against the forecaster).</param>
    public ModelSolution? Solve(TimeSpan limit, CancellationToken cancellationToken, IReadOnlyDictionary<EventKey, long>? fixedDepartures = null)
    {
        var model = new CpModel();
        var times = new Dictionary<EventKey, LinearExpr>();
        var vars = new Dictionary<EventKey, IntVar>();
        var holds = new Dictionary<EventKey, IntVar>();
        var objective = LinearExpr.NewBuilder();
        var built = new bool[_trains.Count];
        var maxHold = Math.Max(_options.StepSeconds, _options.MaxHoldSeconds);
        var span = maxHold + (3 * SimTime.SecondsPerHour);

        Time Max(long lowerBound, params Time[] terms)
        {
            if (terms.All(x => x.Value is not null))
            {
                return Time.Of(terms.Max(x => x.Value!.Value));
            }

            var v = model.NewIntVar(lowerBound, MaxTime, string.Empty);
            model.AddMaxEquality(v, terms.Select(x => x.Expr));
            return new Time(v, null);
        }

        Time Min(long lowerBound, long upperBound, params Time[] terms)
        {
            if (terms.All(x => x.Value is not null))
            {
                return Time.Of(terms.Min(x => x.Value!.Value));
            }

            var v = model.NewIntVar(lowerBound, upperBound, string.Empty);
            model.AddMinEquality(v, terms.Select(x => x.Expr));
            return new Time(v, null);
        }

        // The plan time a hold makes: the held departure if held (less any entry delay at an origin), else as it was.
        Time Held(Time cascade, BoolVar held, LinearExpr departure, long lowerBound)
        {
            var tmp = model.NewIntVar(lowerBound - span, MaxTime, string.Empty);
            model.Add(tmp == departure).OnlyEnforceIf(held);
            model.Add(tmp <= cascade.Expr).OnlyEnforceIf(held.Not());
            return Max(lowerBound, cascade, new Time(tmp, null));
        }

        void Set(EventKey key, Time time)
        {
            times[key] = time.Expr;
            if (time.Expr is IntVar v)
            {
                vars[key] = v;
            }
        }

        void Build(ModelTrain t)
        {
            if (built[t.Number])
            {
                return;
            }

            built[t.Number] = true;
            if (t.FormedFrom is { } formed)
            {
                Build(_trains[formed.Parent]);
            }

            // Each point's forecast times, and its plan times as holds change them: a hold moves later points of the
            // plan only as far as minimum runs need, keeping their dwells (WorkingPlanBuilder), and the forecaster
            // then never runs the train before its plan.
            var shift = Time.Of(0);
            Time? departed = null, planDeparted = null;
            var previous = -1;
            foreach (var k in t.Timed)
            {
                var f = t.Base.Points[k];
                var p = t.Points[k];
                var a = new EventKey(t.Number, k, false);
                var d = new EventKey(t.Number, k, true);
                Time? arrive = null, planArrive = null;

                if (t.HasArrive(k))
                {
                    var baseA = (long)t.BaseArrive(k)!.Value;
                    var plannedA = (long)(p.Arrive ?? p.Depart)!.Value.Seconds;
                    if (f.Basis is ForecastBasis.Actual or ForecastBasis.Arrived || departed is not { } dp || planDeparted is not { } pdp || dp.Value is not null)
                    {
                        arrive = Time.Of(baseA);
                        planArrive = shift + plannedA;
                    }
                    else
                    {
                        var (min, plannedRun, plannedArrive) = Run(t, previous, k);
                        var pa = Max(plannedArrive, shift + plannedArrive, pdp + Math.Min(min, plannedRun));
                        var inner = Min(0, MaxTime, dp + pa - pdp, pa);
                        arrive = Max(baseA, dp + min, inner, Time.Of(baseA));
                        planArrive = pa;
                    }

                    Set(a, arrive.Value);
                }

                if (t.HasDepart(k))
                {
                    var baseD = (long)t.BaseDepart(k)!.Value;
                    var plannedD = (long)p.Depart!.Value.Seconds;
                    Time natural, planDepart;
                    var origin = arrive is null;
                    if (f.Basis == ForecastBasis.Actual)
                    {
                        (natural, planDepart) = (Time.Of(baseD), Time.Of(plannedD));
                    }
                    else if (origin)
                    {
                        var formation = k == t.First && t.FormedFrom is { } parent && times.TryGetValue(new EventKey(parent.Parent, parent.Index, false), out var pa) && pa is IntVar
                            ? new Time(pa + parent.Turnaround, null)
                            : Time.Of(baseD);
                        (natural, planDepart) = (Max(baseD, formation, Time.Of(baseD)), shift + plannedD);
                    }
                    else if (p.IsPass)
                    {
                        (natural, planDepart) = (arrive!.Value, planArrive!.Value);
                    }
                    else
                    {
                        var dwell = plannedD - (p.Arrive?.Seconds ?? plannedD);
                        planDepart = Max(plannedD, shift + plannedD, planArrive!.Value + dwell);
                        natural = Max(baseD, arrive!.Value + Dwell(t, k), planDepart, Time.Of(baseD));
                    }

                    if (t.Holdable(k))
                    {
                        var h = model.NewIntVar(0, maxHold, string.Empty);
                        var held = model.NewBoolVar(string.Empty);
                        model.Add(h <= maxHold * held);
                        model.Add(h >= held);
                        holds[d] = h;
                        var depart = model.NewIntVar(baseD, baseD + span, string.Empty);
                        model.Add(depart == natural.Expr + h);
                        if (p.IsPass && !origin)
                        {
                            // A pass made a stop gets the forecaster's dwell there.
                            model.Add(depart >= arrive!.Value.Expr + StopDwell(t, k)).OnlyEnforceIf(held);
                        }

                        if (origin && p.IsPass)
                        {
                            // Entering the area later moves its whole plan (as suggestions hold it).
                            var moved = model.NewIntVar(0, span, string.Empty);
                            model.Add(moved == depart - (t.EntryDelay + plannedD)).OnlyEnforceIf(held);
                            model.Add(moved == 0).OnlyEnforceIf(held.Not());
                            shift = new Time(moved, null);
                            planDepart = shift + plannedD;
                        }
                        else
                        {
                            planDepart = Held(planDepart, held, origin ? depart - t.EntryDelay : depart, plannedD);
                        }

                        Set(d, new Time(depart, null));
                        departed = new Time(depart, null);
                        objective.AddTerm(held, Cost(_options.HoldPenalty * t.Weight));
                        objective.AddTerm(h, Cost(HeldSecondCost * t.Weight));
                    }
                    else
                    {
                        Set(d, natural);
                        departed = natural;
                    }

                    planDeparted = planDepart;
                }

                previous = k;
            }

            // The delay at its last point in the model, weighted.
            var last = t.Timed[^1];
            var end = t.HasDepart(last) ? new EventKey(t.Number, last, true) : new EventKey(t.Number, last, false);
            if (times[end] is IntVar endVar)
            {
                var baseEnd = end.Depart ? t.BaseDepart(last)!.Value : t.BaseArrive(last)!.Value;
                objective.AddTerm(endVar, Cost(t.Weight));
                objective.Add(LinearExpr.Constant(-Cost(t.Weight) * baseEnd));
            }
        }

        foreach (var t in _trains)
        {
            Build(t);
        }

        // Platform choices.
        var stay = new Dictionary<(int, int), BoolVar>();
        var moves = new Dictionary<(int, int, string), BoolVar>();
        foreach (var ((train, index), options) in _platformOptions)
        {
            if (options.Count == 0)
            {
                continue;
            }

            var s = model.NewBoolVar(string.Empty);
            stay[(train, index)] = s;
            var all = new List<ILiteral> { s };
            foreach (var p in options)
            {
                var m = model.NewBoolVar(string.Empty);
                moves[(train, index, p)] = m;
                all.Add(m);
                objective.AddTerm(m, Cost(_options.PlatformChangePenalty));
            }

            model.AddExactlyOne(all);
        }

        // Orders.
        var slacks = new Dictionary<string, IntVar>(StringComparer.Ordinal);
        _solved.Clear();
        _solved.UnionWith(_disjunctions.Keys);
        foreach (var d in _disjunctions.Values)
        {
            var conditions = new List<ILiteral>();
            var possible = true;
            foreach (var c in d.Conditions)
            {
                var current = Upper(_trains[c.Train].Points[c.Index].Platform ?? string.Empty);
                var (lt, li) = _groupLeader.GetValueOrDefault((c.Train, c.Index), (c.Train, c.Index));
                if (current == c.Platform)
                {
                    if (stay.TryGetValue((lt, li), out var s))
                    {
                        conditions.Add(s);
                    }
                }
                else if (moves.TryGetValue((lt, li, c.Platform), out var m))
                {
                    conditions.Add(m);
                }
                else
                {
                    possible = false;
                }
            }

            if (!possible || !d.XFirst.Concat(d.YFirst).All(s => Has(times, s)))
            {
                continue;
            }

            var order = model.NewBoolVar(string.Empty);
            var slack = model.NewIntVar(0, span, string.Empty);
            slacks[d.Key] = slack;
            objective.AddTerm(slack, ClashCost);
            // A side not yet applying still holds its order: the train behind enters after (on another track).
            // Clashes under the least conflict reported are left, as the detector leaves them (booking rounding).
            foreach (var s in d.XActive ? d.XFirst : d.XFirst.Take(1).Select(s => s with { Gap = 0 }))
            {
                var gap = d.XActive ? Math.Max(0, s.Gap + d.ExtraX - _tolerance) : 0;
                model.Add(Scaled(times, s.Later) - Scaled(times, s.Earlier) + (1000 * slack) >= 1000L * gap).OnlyEnforceIf([order, .. conditions]);
            }

            foreach (var s in d.YActive ? d.YFirst : d.YFirst.Take(1).Select(s => s with { Gap = 0 }))
            {
                var gap = d.YActive ? Math.Max(0, s.Gap + d.ExtraY - _tolerance) : 0;
                model.Add(Scaled(times, s.Later) - Scaled(times, s.Earlier) + (1000 * slack) >= 1000L * gap).OnlyEnforceIf([order.Not(), .. conditions]);
            }

            if (_last is { } last && Hint(last, d) is { } xFirst)
            {
                model.AddHint(order, xFirst ? 1 : 0);
            }
        }

        if (_last is { } previousSolution)
        {
            var hinted = new HashSet<int>();
            foreach (var (key, v) in vars)
            {
                if (previousSolution.Times.TryGetValue(key, out var value) && hinted.Add(v.GetIndex()))
                {
                    model.AddHint(v, value);
                }
            }
        }

        foreach (var (key, time) in fixedDepartures ?? new Dictionary<EventKey, long>())
        {
            model.Add(times[key] == time);
        }

        model.Minimize(objective);
        var workers = _options.Workers > 0 ? _options.Workers : Math.Clamp(Environment.ProcessorCount, 1, 8);
        var solver = new CpSolver
        {
            StringParameters = string.Create(CultureInfo.InvariantCulture, $"max_time_in_seconds:{Math.Max(0.05, limit.TotalSeconds):0.###} num_workers:{workers} random_seed:1"),
        };
        using var registration = cancellationToken.Register(solver.StopSearch);
        var status = solver.Solve(model);
        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
        {
            LastStatus = status == CpSolverStatus.ModelInvalid ? $"{status}: {model.Validate()}" : status.ToString();
            return null;
        }

        LastStatus = status.ToString();

        var result = new Dictionary<EventKey, long>();
        foreach (var (key, v) in vars)
        {
            result[key] = solver.Value(v);
        }

        foreach (var (key, e) in times)
        {
            if (!result.ContainsKey(key))
            {
                result[key] = solver.Value(e);
            }
        }

        var held = new Dictionary<EventKey, long>();
        foreach (var (key, h) in holds)
        {
            if (solver.Value(h) is var value and > 0)
            {
                held[key] = value;
            }
        }

        var platforms = new Dictionary<(int, int), string>();
        foreach (var ((train, index, p), m) in moves)
        {
            if (solver.BooleanValue(m))
            {
                platforms[(train, index)] = p;
            }
        }

        var clashes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, slack) in slacks)
        {
            if (solver.Value(slack) is var value and > 0)
            {
                clashes[key] = value;
            }
        }

        _last = new ModelSolution(result, held, platforms, status == CpSolverStatus.Optimal) { Clashes = clashes };
        return _last;
    }

    /// <summary>The run from <paramref name="k"/> to <paramref name="j"/>: its minimum (as the forecaster has it), planned run and planned arrival.</summary>
    private (int Min, int PlannedRun, int PlannedArrive) Run(ModelTrain t, int k, int j)
    {
        var points = t.Points;
        var plannedDepart = points[k].LastTime!.Value.Seconds;
        var plannedArrive = points[j].FirstTime!.Value.Seconds;
        var plannedRun = plannedArrive - plannedDepart;
        var depart = t.BaseDepart(k) ?? plannedDepart;
        var min = _plan.MinRuns.Along(points, k, j, new SimTime(depart), _plan.Disruptions, plannedRun) ?? plannedRun;
        return (min, plannedRun, plannedArrive);
    }

    private static long Cost(double weightedSeconds) => (long)Math.Round(weightedSeconds * 10);

    private static bool Has(Dictionary<EventKey, LinearExpr> times, Separation s) => Has(times, s.Later) && Has(times, s.Earlier);

    private static bool Has(Dictionary<EventKey, LinearExpr> times, TimeRef r) => r.Constant is not null || (times.ContainsKey(r.From) && times.ContainsKey(r.To));

    /// <summary>A time × 1000.</summary>
    private static LinearExpr Scaled(Dictionary<EventKey, LinearExpr> times, TimeRef r) =>
        r.Constant is { } c ? LinearExpr.Constant(c * 1000)
        : r.From == r.To || r.Share == 0 ? times[r.From] * 1000
        : r.Share == 1000 ? times[r.To] * 1000
        : (times[r.From] * (1000 - r.Share)) + (times[r.To] * r.Share);

    /// <summary>Whether <see cref="Disjunction.X"/> went first in a solution, to start the next solve from it.</summary>
    private static bool? Hint(ModelSolution solution, Disjunction d)
    {
        var s = d.XFirst[0];
        return Value(solution, s.Later) is { } later && Value(solution, s.Earlier) is { } earlier ? later >= earlier : null;
    }

    private static double? Value(ModelSolution solution, TimeRef r) =>
        r.Constant is { } c ? c
        : solution.Times.TryGetValue(r.From, out var a) && solution.Times.TryGetValue(r.To, out var b) ? ((a * (1000 - r.Share)) + (b * r.Share)) / 1000.0 : null;

    // ---- Commands ----

    /// <summary>The plan commands that make a solution: holds (to the next half minute) and platform changes.</summary>
    /// <returns>The commands, the steps, and each step's commands (for dropping steps that do not help).</returns>
    public (List<IPlanCommand> Commands, List<ReplanStep> Steps, List<IPlanCommand[]> ByStep) Commands(ModelSolution solution)
    {
        var commands = new List<IPlanCommand>();
        var steps = new List<ReplanStep>();
        var byStep = new List<IPlanCommand[]>();
        var scratch = _plan;
        var step = Math.Max(1, _options.StepSeconds);
        foreach (var t in _trains)
        {
            foreach (var k in t.Timed)
            {
                if (solution.Platforms.TryGetValue((t.Number, k), out var platform))
                {
                    var moves = new List<IPlanCommand>();
                    foreach (var (train, index, _) in _platformGroups[(t.Number, k)])
                    {
                        moves.Add(new ChangePlatform(train, index, platform));
                        scratch = scratch.Apply(moves[^1]).Plan;
                    }

                    commands.AddRange(moves);
                    byStep.Add([.. moves]);

                    steps.Add(new ReplanStep(SuggestionKind.Platform, t.Id, t.Plan.Headcode, t.Points[k].Location, k) { Platform = platform });
                }

                var key = new EventKey(t.Number, k, true);
                if (!solution.Holds.ContainsKey(key) || !solution.Times.TryGetValue(key, out var depart))
                {
                    continue;
                }

                var until = new SimTime((int)((depart + step - 1) / step * step));
                var baseD = t.BaseDepart(k)!.Value;
                if (until.Seconds <= baseD)
                {
                    continue;
                }

                // At its first point a train starts later: entering the area (a pass there) its whole plan moves, as
                // suggestions hold it; starting here it leaves later. Elsewhere it waits at a call or a platform.
                var p = scratch.Get(t.Id).Points[k];
                var origin = k == t.First && !t.HasArrive(k);
                IPlanCommand? command = p switch
                {
                    { IsPass: true, Depart: { } entry } when origin => until - t.EntryDelay - entry is var shift and > 0 ? new ShiftTrain(t.Id, k, shift) : null,
                    { IsPass: true, Depart: { } pass } => new AddStop(t.Id, k, Math.Clamp(until - pass, step, AddStop.MaxDwellSeconds)),
                    { Depart: not null } when origin => new SetDwell(t.Id, k, until - t.EntryDelay),
                    { Depart: not null } => new SetDwell(t.Id, k, until),
                    _ => null,
                };

                if (command is null || scratch.Apply(command) is not ({ } next, { Applied: true }))
                {
                    continue;
                }

                scratch = next;
                commands.Add(command);
                byStep.Add([command]);
                steps.Add(new ReplanStep(SuggestionKind.Hold, t.Id, t.Plan.Headcode, t.Points[k].Location, k) { Until = until, HoldSeconds = until.Seconds - baseD });
            }
        }

        return (commands, steps, byStep);
    }
}
