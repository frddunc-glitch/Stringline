using System.Globalization;
using Stringline.Core;

namespace Stringline.Planning;

/// <summary>The kinds of resolution the <see cref="Suggester"/> offers (<c>06-…md</c> §4).</summary>
public enum SuggestionKind
{
    /// <summary>Hold the train that must give way where it can wait, until it can go.</summary>
    Hold,

    /// <summary>Hold the other train instead, so the one that would give way goes first.</summary>
    Swap,

    /// <summary>Plan the train into another platform.</summary>
    Platform,

    /// <summary>Plan the train onto another line, off a blocked one.</summary>
    Line,

    /// <summary>Retime the train from the end of a run planned too fast.</summary>
    Retime,

    /// <summary>Terminate the train short before a block (only if cancellations may be suggested).</summary>
    TerminateShort,

    /// <summary>Cancel a train not yet started (only if cancellations may be suggested).</summary>
    Cancel,
}

/// <summary>Settings for the <see cref="Suggester"/>.</summary>
public sealed record SuggestionOptions
{
    /// <summary>How many suggestions to offer, best first.</summary>
    public int MaxSuggestions { get; init; } = 3;

    /// <summary>Delay is measured at each train's last point within this long of now (<c>06-…md</c> §4: 90 minutes).</summary>
    public int HorizonSeconds { get; init; } = 90 * SimTime.SecondsPerMinute;

    /// <summary>Suggest terminating short or cancelling a train for a block (off unless the user turns it on).</summary>
    public bool AllowCancellation { get; init; }

    /// <summary>Holds and retimes are in steps of this (the WTT's half minute).</summary>
    public int StepSeconds { get; init; } = 30;

    /// <summary>The longest hold suggested.</summary>
    public int MaxHoldSeconds { get; init; } = 2 * SimTime.SecondsPerHour;

    /// <summary>Most re-checks of a hold that does not yet clear the clash, each adding what is still missing.</summary>
    public int MaxRounds { get; init; } = 4;

    /// <summary>Most other platforms tried (free ones, nearest the booked one first).</summary>
    public int PlatformCandidates { get; init; } = 3;

    /// <summary>Most other lines tried for a block.</summary>
    public int LineCandidates { get; init; } = 3;

    /// <summary>What a platform change costs in the score, in weighted seconds of delay.</summary>
    public double PlatformChangePenalty { get; init; } = 120;

    /// <summary>What a line change costs in the score, in weighted seconds of delay.</summary>
    public double LineChangePenalty { get; init; } = 120;

    /// <summary>What terminating a train short costs in the score, in weighted seconds of delay.</summary>
    public double TerminatePenalty { get; init; } = 30 * SimTime.SecondsPerMinute;

    /// <summary>What cancelling a train costs in the score, in weighted seconds of delay.</summary>
    public double CancelPenalty { get; init; } = 60 * SimTime.SecondsPerMinute;
}

/// <summary>What to suggest resolutions for: one conflict of the plan, and the forecast it was found on.</summary>
/// <param name="Forecast">The forecast request the conflict came from (plan, live trains, now, network, settings).</param>
public sealed record SuggestionRequest(ForecastRequest Forecast, Conflict Conflict)
{
    public ConflictOptions Conflicts { get; init; } = new();

    public SuggestionOptions Options { get; init; } = new();

    /// <summary>The forecast of the plan as it stands, if already made (else it is made again).</summary>
    public Forecast? Baseline { get; init; }

    /// <summary>The conflicts of <see cref="Baseline"/>, if already found.</summary>
    public ConflictSet? BaselineConflicts { get; init; }

    /// <summary>A request for a conflict of a snapshot, with the snapshot's network, settings, forecast and conflicts.</summary>
    public static SuggestionRequest For(PlanSnapshot snapshot, Conflict conflict, SuggestionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(conflict);
        var settings = snapshot.Settings;
        var hasForecast = snapshot.Forecast.Trains.Count > 0;
        return new SuggestionRequest(ForecastRequest.For(snapshot, settings.Network, settings.Forecast), conflict)
        {
            Conflicts = settings.Conflicts,
            Options = options ?? new SuggestionOptions(),
            Baseline = hasForecast ? snapshot.Forecast : null,
            BaselineConflicts = hasForecast ? snapshot.Conflicts : null,
        };
    }
}

/// <summary>How much later (or sooner) a train runs with a suggestion than without, at its last point in the horizon.</summary>
/// <param name="Weight">Its category's weight (<see cref="Suggester.WeightOf"/>).</param>
public sealed record SuggestionImpact(TrainId Train, Headcode Headcode, int DelaySeconds, double Weight)
{
    public double Cost => DelaySeconds * Weight;
}

/// <summary>
/// One way to resolve a conflict (FC-5), with what it would cost, worked out by forecasting the plan with it. Apply it
/// with <see cref="ToCommand"/>, as one undoable step.
/// </summary>
/// <param name="Id">Unique among the suggestions for a conflict, and the same each time it is worked out.</param>
/// <param name="Train">The train it changes (for <see cref="SuggestionKind.Swap"/>, the other train of the conflict).</param>
/// <param name="Commands">The plan commands it makes.</param>
public sealed record Suggestion(string Id, SuggestionKind Kind, TrainId Train, Headcode Headcode, IReadOnlyList<IPlanCommand> Commands)
{
    /// <summary>The conflict it is for.</summary>
    public required Conflict Conflict { get; init; }

    /// <summary>Where it acts: the hold point, the platform's or line's location, or where a retime starts.</summary>
    public Tiploc Location { get; init; }

    /// <summary>The plan point it acts at.</summary>
    public int Index { get; init; }

    /// <summary>For a hold or swap, when the train now leaves.</summary>
    public SimTime? Until { get; init; }

    /// <summary>For a hold or swap, how much later than forecast it leaves; for a retime, how far it moves.</summary>
    public int HoldSeconds { get; init; }

    /// <summary>For a swap, the train that goes first.</summary>
    public Headcode? Other { get; init; }

    /// <summary>For a platform change, the new platform.</summary>
    public string? Platform { get; init; }

    /// <summary>For a line change, the new line.</summary>
    public string? Line { get; init; }

    /// <summary>Σ weight × added delay over the trains in the horizon, in weighted seconds (negative if trains gain).</summary>
    public double WeightedDelaySeconds { get; init; }

    /// <summary>What the change itself costs (a platform or line change, a termination or cancellation), in weighted seconds.</summary>
    public double Penalty { get; init; }

    /// <summary>
    /// What the new conflicts it makes would cost that the forecast does not show as delay: Σ weight × severity of
    /// each new platform clash (the forecast does not hold trains for platforms), and of every new clash when the
    /// forecast has no knock-on pass.
    /// </summary>
    public double ConflictCost { get; init; }

    /// <summary>Lower is better: <see cref="WeightedDelaySeconds"/> plus <see cref="Penalty"/> and <see cref="ConflictCost"/>.</summary>
    public double Score => WeightedDelaySeconds + Penalty + ConflictCost;

    /// <summary>True if the trains of the conflict no longer clash (or the train is no longer held by the block, or planned too fast).</summary>
    public bool Resolves { get; init; }

    /// <summary>How many of the plan's conflicts it clears (this one included).</summary>
    public int Cleared { get; init; }

    /// <summary>How many new conflicts it makes.</summary>
    public int Added { get; init; }

    /// <summary>The trains it makes later or sooner, by cost, most first.</summary>
    public IReadOnlyList<SuggestionImpact> Impacts { get; init; } = [];

    /// <summary>The trains whose plan it changes.</summary>
    public IReadOnlyList<TrainId> Changed { get; init; } = [];

    /// <summary>The plan with it applied, for a preview.</summary>
    public WorkingPlan? Plan { get; init; }

    /// <summary>What it does, e.g. "Hold 1F01 at Clapham Jn until 08:11 (+2 min)".</summary>
    /// <param name="name">A location's name (its TIPLOC if null).</param>
    /// <param name="time">A time as text (<c>08:11½</c> if null).</param>
    public string Describe(Func<Tiploc, string>? name = null, Func<SimTime, string>? time = null)
    {
        name ??= t => t.Code;
        time ??= t => t.ToString("H", CultureInfo.InvariantCulture);
        var at = name(Location);
        var until = Until is { } u ? time(u) : "?";
        return Kind switch
        {
            SuggestionKind.Hold => $"Hold {Headcode} at {at} until {until} ({PlanText.WithSign(HoldSeconds)})",
            SuggestionKind.Swap => $"Hold {Headcode} at {at} until {until} ({PlanText.WithSign(HoldSeconds)}) so {Other} goes first",
            SuggestionKind.Platform => $"{Headcode} into platform {Platform} at {at}",
            SuggestionKind.Line => $"{Headcode} on the {Line} line out of {at}",
            SuggestionKind.Retime => $"Retime {Headcode} from {at} by {PlanText.WithSign(HoldSeconds)}",
            SuggestionKind.TerminateShort => $"Terminate {Headcode} short at {at}",
            _ => $"Cancel {Headcode}",
        };
    }

    /// <summary>The command that applies it: all its steps as one undoable edit.</summary>
    public AcceptSuggestion ToCommand() => new(Id, Commands) { Title = Describe() };

    public override string ToString() => $"{Describe()} score {Score:0}{(Resolves ? string.Empty : " (does not resolve)")}";
}

/// <summary>
/// Rule-based resolutions for a conflict (<c>06-…md</c> §4, FC-5). Pure, deterministic and thread-safe.
/// </summary>
/// <remarks>
/// <para>Candidates, by kind of conflict:</para>
/// <list type="bullet">
/// <item><b>Hold</b> the train that gives way at the last point before the clash where it can wait (a stop, a platform
/// it passes, or its origin), and not one it has already left. The hold starts at the delay the conflict needs; if the
/// forecast with it still clashes (the train makes up time on the way), what is missing is added, a few times.</item>
/// <item><b>Swap</b>: hold the other train instead, until the first can go ahead of it with its spacing.</item>
/// <item><b>Platform</b> (platform conflicts): another platform used at the location that is free while the train is
/// there, nearest the booked one first. A train formed from it in the platform moves with it.</item>
/// <item><b>Block</b>: the hold planned (so the plan no longer runs into it), another line out of the location if the
/// block is on one line, and, only if allowed, terminating short before it or cancelling a train not yet started.</item>
/// <item><b>Run time</b>: retime the train from the end of the run by what it is short of.</item>
/// </list>
/// <para>Each candidate is scored by forecasting the plan with it, as the engine would (with the knock-on pass if it
/// is on): Σ weight × added delay at each train's last point within the horizon, where the weight is its category's
/// priority / 50 (Fred, S15), plus a penalty for changing a platform or line or for terminating or cancelling, plus
/// weight × severity of each new conflict the forecast does not show as delay (a platform clash, or any clash without
/// the knock-on pass). Those that resolve the conflict come first, then the lowest score.</para>
/// </remarks>
public static class Suggester
{
    /// <summary>The priority of a train whose category has none in the profile.</summary>
    public const int DefaultPriority = 50;

    /// <summary>The weight of a minute of a train's delay: its priority / 50 (Fred, S15), and at least 0.1.</summary>
    public static double WeightOf(int priority) => Math.Max(0.1, priority / 50.0);

    public static IReadOnlyList<Suggestion> Suggest(SuggestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Search(request).Run();
    }

    /// <summary>A candidate plan, forecast and checked.</summary>
    private sealed record Outcome(WorkingPlan Plan, Forecast Forecast, ConflictSet Conflicts);

    private sealed class Search
    {
        private readonly SuggestionRequest _request;
        private readonly SuggestionOptions _options;
        private readonly WorkingPlan _plan;
        private readonly Conflict _conflict;
        private readonly Forecast _baseline;
        private readonly HashSet<string> _baselineIds;
        private readonly Dictionary<TrainId, (int Index, int Time)> _references = [];

        public Search(SuggestionRequest request)
        {
            _request = request;
            _options = request.Options;
            _plan = request.Forecast.Plan;
            _conflict = request.Conflict;
            _baseline = request.Baseline ?? Forecaster.Run(request.Forecast);
            var conflicts = request.Baseline is not null && request.BaselineConflicts is { } given ? given : Detect(_plan, _baseline);
            _baselineIds = conflicts.All.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

            // Each train's delay is measured at its last point still to come within the horizon.
            var now = _baseline.Now ?? request.Forecast.Now ?? _conflict.Start;
            var end = now.Seconds + _options.HorizonSeconds;
            foreach (var t in _baseline.Trains)
            {
                if (t.Status is not (TrainForecastStatus.NotStarted or TrainForecastStatus.Running))
                {
                    continue;
                }

                for (var k = t.Points.Count - 1; k >= 0; k--)
                {
                    var p = t.Points[k];
                    if (p.Basis is ForecastBasis.Forecast or ForecastBasis.Arrived && (p.LastTime ?? p.FirstTime) is { } time && time.Seconds <= end)
                    {
                        _references[t.Train] = (k, time.Seconds);
                        break;
                    }
                }
            }
        }

        public List<Suggestion> Run()
        {
            var c = _conflict;
            var found = new List<Suggestion?>();
            switch (c.Kind)
            {
                case ConflictKind.Unrealistic:
                    found.Add(Retime());
                    break;
                case ConflictKind.Disruption:
                    found.Add(Hold(SuggestionKind.Hold, c.A, c.PointIndex, strict: false, c.SeveritySeconds, o => Remaining(o, c.A, null), other: null));
                    found.AddRange(Lines());
                    if (_options.AllowCancellation)
                    {
                        found.Add(TerminateShort());
                        found.Add(Cancel());
                    }

                    break;
                case ConflictKind.Platform:
                    found.Add(Hold(SuggestionKind.Hold, c.A, c.PointIndex, strict: true, c.SeveritySeconds, o => Remaining(o, c.A, c.B), other: null));
                    found.Add(Swap());
                    found.AddRange(Platforms());
                    break;
                default:
                    found.Add(Hold(SuggestionKind.Hold, c.A, c.PointIndex, strict: false, c.SeveritySeconds, o => Remaining(o, c.A, c.B), other: null));
                    found.Add(Swap());
                    break;
            }

            return found.OfType<Suggestion>()
                .OrderBy(s => s.Resolves ? 0 : 1)
                .ThenBy(s => s.Score)
                .ThenBy(s => s.Kind)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .Take(Math.Max(0, _options.MaxSuggestions))
                .ToList();
        }

        // ---- Candidates ----

        /// <summary>
        /// Hold <paramref name="train"/> at the last point at (or, if <paramref name="strict"/>, before) <paramref name="reference"/>
        /// where it can wait, starting with <paramref name="initial"/> and adding what <paramref name="remaining"/> says is still missing.
        /// </summary>
        private Suggestion? Hold(SuggestionKind kind, TrainId train, int reference, bool strict, int initial, Func<Outcome, int?> remaining, Headcode? other)
        {
            if (_plan.Find(train) is not { Cancelled: false } plan || _baseline.Independent.Find(train) is not { } forecast ||
                HoldPoint(plan, forecast, reference, strict) is not { } k || forecast.Points[k].LastTime is not { } departs)
            {
                return null;
            }

            // A block the forecast holds it for there already makes it leave later: hold from when it would have left.
            var leaves = forecast.BlockHolds.FirstOrDefault(h => h.Index == k)?.Departure ?? departs;

            SimTime Until(int amount) => RoundUp(leaves + Math.Max(amount, _options.StepSeconds));
            var settled = Settle(initial, amount => HoldCommands(plan, k, Until(amount)), remaining);
            if (settled is not var (outcome, commands, amount))
            {
                return null;
            }

            var until = Until(amount);
            return Make(kind, train, k, outcome, commands, 0) with { Until = until, HoldSeconds = until - leaves, Other = other };
        }

        /// <summary>Hold the other train instead, until the one that would give way has gone ahead with its spacing.</summary>
        private Suggestion? Swap()
        {
            var c = _conflict;
            if (c.B is not { } b || _baseline.Independent.Find(b) is not { } fb || _baseline.Independent.Find(c.A) is not { } fa)
            {
                return null;
            }

            // On a single line the other train is waiting at the far end, to come the other way.
            var at = c.Kind == ConflictKind.SingleLine && c.To is { } to ? to : c.Where;
            if (IndexNear(fb, at, c.When) is not { } reference || Gap(fa, fb, at) is not { } gap)
            {
                return null;
            }

            var strict = c.Kind == ConflictKind.Platform;
            var initial = gap + (strict ? _request.Conflicts.PlatformMarginSeconds : c.SeveritySeconds);
            int? Missing(Outcome o) =>
                Remaining(o, b, c.A) ?? // now behind it, but too close
                (Remaining(o, c.A, b) is { } still ? still + Math.Max(0, Gap(o.Forecast.Independent.Find(c.A), o.Forecast.Independent.Find(b), at) ?? 0) : null);
            return Hold(SuggestionKind.Swap, b, reference, strict, initial, Missing, c.HeadcodeA);
        }

        /// <summary>Other platforms at the location, free while the train would be there.</summary>
        private IEnumerable<Suggestion?> Platforms()
        {
            var c = _conflict;
            if (_plan.Find(c.A) is not { Cancelled: false } plan || _baseline.Independent.Find(c.A)?.At(c.PointIndex) is not { } here ||
                here.Basis is ForecastBasis.Actual or ForecastBasis.Arrived || here.FirstTime is not { } first || here.LastTime is not { } last ||
                c.PointIndex >= plan.RunningPoints.Count)
            {
                yield break;
            }

            var location = c.Where;
            var current = (c.Platform ?? plan.RunningPoints[c.PointIndex].Platform)?.Trim() ?? string.Empty;

            // A train formed here from another is in the other's platform: they move together, from its arrival, and
            // not at all once it has arrived.
            var parent = FormedFrom(plan, c.PointIndex, location, current);
            if (parent is { Arrived: true })
            {
                yield break;
            }

            if (parent?.Arrive < first)
            {
                first = parent.Value.Arrive;
            }

            var margin = _request.Conflicts.PlatformMarginSeconds;
            var used = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _plan.Trains)
            {
                if (t.Cancelled)
                {
                    continue;
                }

                var forecast = t.Id == c.A ? null : _baseline.Independent.Find(t.Id);
                var points = t.RunningPoints;
                for (var k = 0; k < points.Count; k++)
                {
                    if (points[k].Location != location || string.IsNullOrWhiteSpace(points[k].Platform))
                    {
                        continue;
                    }

                    var platform = points[k].Platform!.Trim();
                    used.Add(platform);
                    if (forecast?.At(k) is { } f && f.FirstTime is { } s && f.LastTime is { } e && s.Seconds <= last.Seconds + margin && e.Seconds >= first.Seconds - margin)
                    {
                        busy.Add(platform);
                    }
                }
            }

            var free = used.Where(p => !string.Equals(p, current, StringComparison.OrdinalIgnoreCase) && !busy.Contains(p))
                .OrderBy(p => Distance(p, current))
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(0, _options.PlatformCandidates))
                .ToList();
            foreach (var platform in free)
            {
                var commands = new List<IPlanCommand>();
                if (parent is { } p)
                {
                    commands.Add(new ChangePlatform(p.Train, p.Index, platform));
                }

                commands.Add(new ChangePlatform(c.A, c.PointIndex, platform));
                commands.AddRange(FormedIn(plan, location, current).Select(child => new ChangePlatform(child.Train, child.Index, platform)));
                if (Evaluate(commands) is { } outcome)
                {
                    yield return Make(SuggestionKind.Platform, c.A, c.PointIndex, outcome, commands, _options.PlatformChangePenalty) with { Platform = platform };
                }
            }
        }

        /// <summary>Trains formed from <paramref name="plan"/>'s train that start in its platform at the location.</summary>
        private IEnumerable<(TrainId Train, int Index)> FormedIn(TrainPlan plan, Tiploc location, string platform)
        {
            foreach (var wtt in _plan.Timetable.Trains)
            {
                if (wtt.FormedFrom != plan.Source || _plan.Find(wtt.Id) is not { Cancelled: false } child)
                {
                    continue;
                }

                var points = child.RunningPoints;
                var first = FirstTimed(points);
                if (first >= 0 && points[first].Location == location && string.Equals(points[first].Platform?.Trim(), platform, StringComparison.OrdinalIgnoreCase))
                {
                    yield return (child.Id, first);
                }
            }
        }

        /// <summary>
        /// The train <paramref name="plan"/>'s train is formed from, if that is at its first point <paramref name="index"/>
        /// in the same platform: its point there, when it arrives (forecast) and whether it has.
        /// </summary>
        private (TrainId Train, int Index, SimTime Arrive, bool Arrived)? FormedFrom(TrainPlan plan, int index, Tiploc location, string platform)
        {
            if (index != FirstTimed(plan.RunningPoints) || plan.Origin != PlanOrigin.Wtt || _plan.Timetable.Find(plan.Source)?.FormedFrom is not { } id ||
                _plan.Find(id) is not { Cancelled: false } parent || _baseline.Independent.Find(id) is not { } forecast)
            {
                return null;
            }

            var points = parent.RunningPoints;
            for (var k = points.Count - 1; k >= 0; k--)
            {
                if (points[k].Location == location && string.Equals(points[k].Platform?.Trim(), platform, StringComparison.OrdinalIgnoreCase) &&
                    forecast.At(k) is { FirstTime: { } arrive } f)
                {
                    return (id, k, arrive, f.Basis is ForecastBasis.Actual or ForecastBasis.Arrived);
                }
            }

            return null;
        }

        /// <summary>Other lines out of the location, for a block on one line.</summary>
        private IEnumerable<Suggestion?> Lines()
        {
            var c = _conflict;
            if (c.DisruptionId is not { } id || _plan.FindDisruption(id) is not { Track: { } track, Direction: null } ||
                _plan.Find(c.A) is not { Cancelled: false } plan || c.PointIndex >= plan.RunningPoints.Count)
            {
                yield break;
            }

            var current = plan.RunningPoints[c.PointIndex].Line?.Trim();
            var lines = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _plan.Trains)
            {
                foreach (var p in t.RunningPoints)
                {
                    if (p.Location == c.Where && !string.IsNullOrWhiteSpace(p.Line))
                    {
                        lines.Add(p.Line.Trim());
                    }
                }
            }

            foreach (var line in lines.Where(l => !string.Equals(l, current, StringComparison.OrdinalIgnoreCase) && !string.Equals(l, track.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(Math.Max(0, _options.LineCandidates)))
            {
                IPlanCommand[] commands = [new ChangeLine(c.A, c.PointIndex, line)];
                if (Evaluate(commands) is { } outcome)
                {
                    yield return Make(SuggestionKind.Line, c.A, c.PointIndex, outcome, commands, _options.LineChangePenalty) with { Line = line };
                }
            }
        }

        /// <summary>Terminate the train at the last stop before the block that it has not yet left.</summary>
        private Suggestion? TerminateShort()
        {
            var c = _conflict;
            if (_plan.Find(c.A) is not { Cancelled: false } plan || _baseline.Independent.Find(c.A) is not { } forecast)
            {
                return null;
            }

            var points = plan.RunningPoints;
            for (var k = Math.Min(c.PointIndex, plan.LastIndex - 1); k > 0; k--)
            {
                if (forecast.At(k) is not { } f || f.Basis == ForecastBasis.Actual)
                {
                    return null;
                }

                if (!points[k].IsPass && points[k].Arrive is not null)
                {
                    IPlanCommand[] commands = [new TerminateAt(c.A, k)];
                    return Evaluate(commands) is { } outcome ? Make(SuggestionKind.TerminateShort, c.A, k, outcome, commands, _options.TerminatePenalty) : null;
                }
            }

            return null;
        }

        /// <summary>Cancel the train, if it has not started.</summary>
        private Suggestion? Cancel()
        {
            var c = _conflict;
            if (_baseline.Independent.Find(c.A)?.Status != TrainForecastStatus.NotStarted)
            {
                return null;
            }

            IPlanCommand[] commands = [new CancelTrain(c.A)];
            return Evaluate(commands) is { } outcome ? Make(SuggestionKind.Cancel, c.A, 0, outcome, commands, _options.CancelPenalty) : null;
        }

        /// <summary>Retime the train from the end of the run planned too fast, by what it is short of.</summary>
        private Suggestion? Retime()
        {
            var c = _conflict;
            if (c.To is not { } to || _plan.Find(c.A) is not { Cancelled: false } plan)
            {
                return null;
            }

            var points = plan.RunningPoints;
            var end = -1;
            for (var k = c.PointIndex + 1; k < points.Count; k++)
            {
                if (points[k].IsTimed)
                {
                    end = points[k].Location == to ? k : -1;
                    break;
                }
            }

            if (end < 0)
            {
                return null;
            }

            int RoundedUp(int seconds) => (Math.Max(seconds, 1) + _options.StepSeconds - 1) / _options.StepSeconds * _options.StepSeconds;
            var settled = Settle(c.SeveritySeconds, amount => [new ShiftTrain(c.A, end, RoundedUp(amount))], o => o.Conflicts.All
                .Where(x => x.A == c.A && x.Kind == ConflictKind.Unrealistic && x.Where == c.Where).Select(x => (int?)x.SeveritySeconds).Max());
            return settled is var (outcome, commands, amount)
                ? Make(SuggestionKind.Retime, c.A, end, outcome, commands, 0) with { HoldSeconds = RoundedUp(amount) }
                : null;
        }

        // ---- Holding ----

        /// <summary>
        /// The last point at or before <paramref name="reference"/> (before it if <paramref name="strict"/>) where the
        /// train can wait and has not yet left: a stop, a platform it passes, or its first point.
        /// </summary>
        private static int? HoldPoint(TrainPlan plan, TrainForecast forecast, int reference, bool strict)
        {
            var points = plan.RunningPoints;
            var first = FirstTimed(points);
            for (var k = Math.Min(strict ? reference - 1 : reference, points.Count - 1); k >= 0; k--)
            {
                if (forecast.At(k) is not { } f || f.Basis == ForecastBasis.Actual)
                {
                    return null;
                }

                var p = points[k];
                if (!p.IsTimed || p.Depart is null || f.LastTime is null)
                {
                    continue;
                }

                if (p.IsCall || k == first || (p.IsPass && !string.IsNullOrWhiteSpace(p.Platform)))
                {
                    return k;
                }
            }

            return null;
        }

        /// <summary>The commands that make the train leave point <paramref name="k"/> at <paramref name="until"/>.</summary>
        private IPlanCommand[]? HoldCommands(TrainPlan plan, int k, SimTime until)
        {
            var p = plan.Points[k];
            if (p.IsCall)
            {
                return [new SetDwell(plan.Id, k, until)];
            }

            if (k == FirstTimed(plan.RunningPoints) && p.Depart is { } planned)
            {
                return until > planned ? [new ShiftTrain(plan.Id, k, until - planned)] : null;
            }

            if (p.IsPass && p.Depart is { } pass)
            {
                var dwell = Math.Max(_options.StepSeconds, until - pass);
                return dwell <= AddStop.MaxDwellSeconds ? [new AddStop(plan.Id, k, dwell)] : null;
            }

            return null;
        }

        /// <summary>
        /// Builds and checks a candidate with <paramref name="initial"/>, then adds what <paramref name="remaining"/>
        /// finds still missing and checks it again, until it resolves, the rounds run out or it reaches the longest hold.
        /// Returns the last candidate that could be built.
        /// </summary>
        private (Outcome Outcome, IReadOnlyList<IPlanCommand> Commands, int Amount)? Settle(int initial, Func<int, IReadOnlyList<IPlanCommand>?> build, Func<Outcome, int?> remaining)
        {
            (Outcome, IReadOnlyList<IPlanCommand>, int)? result = null;
            var amount = Math.Max(initial, _options.StepSeconds);
            for (var round = 0; round < Math.Max(1, _options.MaxRounds) && amount <= _options.MaxHoldSeconds; round++)
            {
                if (build(amount) is not { } commands || Evaluate(commands) is not { } outcome)
                {
                    break;
                }

                result = (outcome, commands, amount);
                if (remaining(outcome) is not { } more)
                {
                    break;
                }

                amount += Math.Max(more, _options.StepSeconds);
            }

            return result;
        }

        /// <summary>The worst clash left with <paramref name="a"/> giving way to <paramref name="b"/> (or, with none, held by the block).</summary>
        private int? Remaining(Outcome o, TrainId a, TrainId? b)
        {
            var c = _conflict;
            int? worst = null;
            foreach (var x in o.Conflicts.All)
            {
                if (x.A == a && (b is { } other ? x.B == other : x.Kind == c.Kind && x.DisruptionId == c.DisruptionId) && (worst is null || x.SeveritySeconds > worst))
                {
                    worst = x.SeveritySeconds;
                }
            }

            return worst;
        }

        /// <summary>How long after <paramref name="b"/> leaves <paramref name="at"/> train <paramref name="a"/> does (or arrives, for a platform).</summary>
        private int? Gap(TrainForecast? a, TrainForecast? b, Tiploc at)
        {
            var c = _conflict;
            if (a is null || b is null || IndexNear(a, at, c.When) is not { } i || IndexNear(b, at, c.When) is not { } j)
            {
                return null;
            }

            // A platform: from the other train arriving until this one leaves. A section: between their departures
            // into it (on a single line, from the other's departure until this one arrives off it).
            var (ta, tb) = c.Kind switch
            {
                ConflictKind.Platform => (a.Points[i].LastTime, b.Points[j].FirstTime),
                ConflictKind.SingleLine => (a.Points[i].FirstTime, b.Points[j].LastTime),
                _ => (a.Points[i].LastTime, b.Points[j].LastTime),
            };
            return ta is { } x && tb is { } y ? x - y : null;
        }

        /// <summary>The train's point at <paramref name="location"/> nearest in time to <paramref name="when"/>.</summary>
        private static int? IndexNear(TrainForecast forecast, Tiploc location, SimTime when)
        {
            int? best = null;
            var distance = int.MaxValue;
            for (var k = 0; k < forecast.Points.Count; k++)
            {
                var p = forecast.Points[k];
                if (p.Location != location || (p.FirstTime ?? p.LastTime) is not { } time)
                {
                    continue;
                }

                var d = Math.Abs(time - when);
                if (d < distance)
                {
                    (best, distance) = (k, d);
                }
            }

            return best;
        }

        // ---- Scoring ----

        private Outcome? Evaluate(IReadOnlyList<IPlanCommand> commands)
        {
            var (plan, result) = _plan.Apply(new AcceptSuggestion("candidate", commands));
            if (!result.Applied)
            {
                return null;
            }

            var forecast = Forecaster.Run(_request.Forecast with { Plan = plan });
            return new Outcome(plan, forecast, Detect(plan, forecast));
        }

        private ConflictSet Detect(WorkingPlan plan, Forecast forecast) =>
            ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = _request.Forecast.Network, Options = _request.Conflicts });

        private Suggestion Make(SuggestionKind kind, TrainId train, int index, Outcome o, IReadOnlyList<IPlanCommand> commands, double penalty)
        {
            var impacts = new List<SuggestionImpact>();
            foreach (var (id, reference) in _references)
            {
                if (o.Forecast.Find(id)?.At(reference.Index) is not { } p || (p.LastTime ?? p.FirstTime) is not { } time || time.Seconds == reference.Time)
                {
                    continue;
                }

                var plan = o.Plan.Find(id);
                impacts.Add(new SuggestionImpact(id, plan?.Headcode ?? new Headcode(id.Value), time.Seconds - reference.Time, Weight(plan)));
            }

            impacts.Sort((x, y) => Math.Abs(y.Cost).CompareTo(Math.Abs(x.Cost)) is var byCost and not 0 ? byCost : string.CompareOrdinal(x.Train.Value, y.Train.Value));
            var changed = new List<TrainId>();
            foreach (var t in o.Plan.Trains)
            {
                if (_plan.Find(t.Id) is not { } before || (!ReferenceEquals(before, t) && !before.SameAs(t)))
                {
                    changed.Add(t.Id);
                }
            }

            var c = _conflict;
            var resolves = c.Kind == ConflictKind.Unrealistic
                ? !o.Conflicts.All.Any(x => x.A == c.A && x.Kind == ConflictKind.Unrealistic && x.Where == c.Where)
                : c.B is { } b
                    ? !o.Conflicts.All.Any(x => x.Kind == c.Kind && x.Where == c.Where && x.Involves(c.A) && x.Involves(b))
                    : !o.Conflicts.All.Any(x => x.A == c.A && x.Kind == c.Kind && x.DisruptionId == c.DisruptionId);
            var added = o.Conflicts.All.Where(x => !_baselineIds.Contains(x.Id)).ToList();
            var unseen = added.Where(x => x.Kind == ConflictKind.Platform || !o.Forecast.Interaction).ToList();
            var plan0 = _plan.Get(train);
            var location = index < plan0.Points.Count ? plan0.Points[index].Location : c.Where;
            var detail = kind switch
            {
                SuggestionKind.Platform or SuggestionKind.Line => string.Join(",", commands.Select(x => x switch { ChangePlatform p => p.Platform, ChangeLine l => l.Line, _ => null }).OfType<string>().Take(1)),
                _ => string.Empty,
            };
            return new Suggestion(
                string.Create(CultureInfo.InvariantCulture, $"{c.Id}|{kind}:{train}:{index}{(detail.Length > 0 ? ":" + detail : string.Empty)}"),
                kind,
                train,
                plan0.Headcode,
                commands)
            {
                Conflict = c,
                Location = location,
                Index = index,
                WeightedDelaySeconds = Math.Round(impacts.Sum(i => i.Cost), 1),
                Penalty = penalty,
                ConflictCost = Math.Round(unseen.Sum(x => x.SeveritySeconds * Weight(o.Plan.Find(x.A))), 1),
                Resolves = resolves,
                Cleared = _baselineIds.Count(id => o.Conflicts.Find(id) is null),
                Added = added.Count,
                Impacts = impacts,
                Changed = changed,
                Plan = o.Plan,
            };
        }

        private double Weight(TrainPlan? plan) =>
            WeightOf(plan?.CategoryId is { } category && _request.Forecast.Network.Priorities.TryGetValue(category, out var p) ? p : DefaultPriority);

        private SimTime RoundUp(SimTime time)
        {
            var step = _options.StepSeconds;
            return new SimTime((int)Math.Ceiling(time.Seconds / (double)step) * step);
        }
    }

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

    /// <summary>How far apart two platform names are: by number if both are numbers, else alphabetically after every number.</summary>
    private static int Distance(string platform, string current) =>
        int.TryParse(platform, NumberStyles.None, CultureInfo.InvariantCulture, out var a) && int.TryParse(current, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            ? Math.Abs(a - b)
            : int.MaxValue;
}
