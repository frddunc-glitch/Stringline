using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Stringline.Core;

namespace Stringline.Planning;

/// <summary>
/// Re-plans the trains over a horizon to clear their conflicts (<c>06-…md</c> §5, FC-6). Kept behind this interface so
/// the rule-based way (<see cref="RuleReplanner"/>) and the optimiser (<c>Stringline.Optimiser</c>) are interchangeable.
/// Implementations are pure and thread-safe: the result is a proposal, applied (or not) by the caller.
/// </summary>
public interface IReplanner
{
    /// <summary>What it is called in the UI, e.g. "optimiser".</summary>
    string Name { get; }

    ReplanResult Replan(ReplanRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Which <see cref="IReplanner"/> to use.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReplanMethod>))]
public enum ReplanMethod
{
    /// <summary>The CP-SAT optimiser: every clash in the horizon at once.</summary>
    Optimiser,

    /// <summary>The suggestion rules, one conflict at a time, the earliest first.</summary>
    Rules,
}

/// <summary>Settings for re-planning a horizon (Fred, S20: 60 min and 5 s by default, both in Options).</summary>
public sealed record ReplanOptions
{
    /// <summary>How far ahead of now to re-plan: conflicts up to this long from now are cleared.</summary>
    public int HorizonSeconds { get; init; } = 60 * SimTime.SecondsPerMinute;

    /// <summary>How long the re-planner may think in all; the best plan found by then is used.</summary>
    public double TimeLimitSeconds { get; init; } = 5;

    /// <summary>Holds are in steps of this (the WTT's half minute).</summary>
    public int StepSeconds { get; init; } = 30;

    /// <summary>The longest any one hold may be.</summary>
    public int MaxHoldSeconds { get; init; } = 2 * SimTime.SecondsPerHour;

    /// <summary>Let it move a train to another platform at a platform clash (Fred, S20: yes).</summary>
    public bool AllowPlatformChanges { get; init; } = true;

    /// <summary>Most other platforms tried for a train (free ones, nearest the booked one first).</summary>
    public int PlatformCandidates { get; init; } = 3;

    /// <summary>What a platform change costs, in weighted seconds of delay (as for suggestions).</summary>
    public double PlatformChangePenalty { get; init; } = 120;

    /// <summary>What a line change costs (the rules only), in weighted seconds of delay.</summary>
    public double LineChangePenalty { get; init; } = 120;

    /// <summary>What each hold costs besides its delay, so a plan with fewer holds is preferred, in weighted seconds.</summary>
    public double HoldPenalty { get; init; } = 15;

    /// <summary>Most times the optimiser checks its plan with the forecast and conflict detector and tries again.</summary>
    public int MaxRounds { get; init; } = 6;

    /// <summary>Solver threads: 0 for as many as the machine has (at most 8). One makes the solver deterministic.</summary>
    public int Workers { get; init; }
}

/// <summary>What to re-plan: the plan and live trains of a forecast request, up to a horizon.</summary>
public sealed record ReplanRequest(ForecastRequest Forecast)
{
    public ConflictOptions Conflicts { get; init; } = new();

    public ReplanOptions Options { get; init; } = new();

    /// <summary>The forecast of the plan as it stands, if already made (else it is made again).</summary>
    public Forecast? Baseline { get; init; }

    /// <summary>The conflicts of <see cref="Baseline"/>, if already found.</summary>
    public ConflictSet? BaselineConflicts { get; init; }

    /// <summary>A request for a snapshot's plan, with its network, settings, forecast and conflicts.</summary>
    public static ReplanRequest For(PlanSnapshot snapshot, ReplanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var settings = snapshot.Settings;
        var hasForecast = snapshot.Forecast.Trains.Count > 0;
        return new ReplanRequest(ForecastRequest.For(snapshot, settings.Network, settings.Forecast))
        {
            Conflicts = settings.Conflicts,
            Options = options ?? new ReplanOptions(),
            Baseline = hasForecast ? snapshot.Forecast : null,
            BaselineConflicts = hasForecast ? snapshot.Conflicts : null,
        };
    }
}

/// <summary>One change a re-plan makes: a hold (or swap) or a platform or line change.</summary>
/// <param name="Index">The plan point it acts at.</param>
public sealed record ReplanStep(SuggestionKind Kind, TrainId Train, Headcode Headcode, Tiploc Location, int Index)
{
    /// <summary>For a hold, when the train now leaves.</summary>
    public SimTime? Until { get; init; }

    /// <summary>For a hold, how much later than forecast it leaves.</summary>
    public int HoldSeconds { get; init; }

    /// <summary>For a platform change, the new platform.</summary>
    public string? Platform { get; init; }

    /// <summary>For a line change, the new line.</summary>
    public string? Line { get; init; }

    /// <summary>For a swap, the train that goes first.</summary>
    public Headcode? Other { get; init; }

    /// <summary>The step of a suggestion the rules applied.</summary>
    public static ReplanStep Of(Suggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        return new ReplanStep(suggestion.Kind, suggestion.Train, suggestion.Headcode, suggestion.Location, suggestion.Index)
        {
            Until = suggestion.Until,
            HoldSeconds = suggestion.HoldSeconds,
            Platform = suggestion.Platform,
            Line = suggestion.Line,
            Other = suggestion.Other,
        };
    }

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

    public override string ToString() => Describe();
}

/// <summary>How a re-plan came out.</summary>
public enum ReplanOutcome
{
    /// <summary>A plan with fewer conflicts in the horizon (or as few, for less delay) was found.</summary>
    Improved,

    /// <summary>There are no conflicts in the horizon to clear.</summary>
    NoConflicts,

    /// <summary>Nothing better than the plan as it stands was found.</summary>
    NoImprovement,
}

/// <summary>
/// A re-plan of the horizon (FC-6): the commands it makes, and the plan, forecast and conflicts with them. Apply it
/// with <see cref="ToCommand"/>, as one undoable step.
/// </summary>
/// <param name="Method">The re-planner's <see cref="IReplanner.Name"/>.</param>
/// <param name="Commands">The plan commands it makes (empty unless <see cref="ReplanOutcome.Improved"/>).</param>
public sealed record ReplanResult(string Method, ReplanOutcome Outcome, IReadOnlyList<IPlanCommand> Commands)
{
    /// <summary>When the horizon starts (now) and ends.</summary>
    public SimTime From { get; init; }

    public SimTime Until { get; init; }

    /// <summary>The changes, train by train.</summary>
    public IReadOnlyList<ReplanStep> Steps { get; init; } = [];

    /// <summary>The conflicts in the horizon before re-planning.</summary>
    public IReadOnlyList<Conflict> Before { get; init; } = [];

    /// <summary>The conflicts in the horizon with the re-plan.</summary>
    public IReadOnlyList<Conflict> After { get; init; } = [];

    /// <summary>How many of the conflicts before (anywhere) it clears.</summary>
    public int Cleared { get; init; }

    /// <summary>How many new conflicts (anywhere) it makes.</summary>
    public int Added { get; init; }

    /// <summary>Σ weight × added delay over the trains in the horizon, in weighted seconds (negative if trains gain).</summary>
    public double WeightedDelaySeconds { get; init; }

    /// <summary>What the platform and line changes (and any terminations) cost, in weighted seconds.</summary>
    public double Penalty { get; init; }

    /// <summary>The trains it makes later or sooner, by cost, most first.</summary>
    public IReadOnlyList<SuggestionImpact> Impacts { get; init; } = [];

    /// <summary>The trains whose plan it changes.</summary>
    public IReadOnlyList<TrainId> Changed { get; init; } = [];

    /// <summary>The plan with it applied, for a preview.</summary>
    public WorkingPlan? Plan { get; init; }

    /// <summary>How many times the plan was checked by forecasting it.</summary>
    public int Rounds { get; init; }

    /// <summary>True if the optimiser proved its last model optimal (always false for the rules).</summary>
    public bool Optimal { get; init; }

    public TimeSpan Elapsed { get; init; }

    public int Holds => Steps.Count(s => s.Kind is SuggestionKind.Hold or SuggestionKind.Swap);

    public int PlatformChanges => Steps.Count(s => s.Kind == SuggestionKind.Platform);

    /// <summary>For the edit history, e.g. "Re-plan to 10:15: 4 holds, 1 platform change".</summary>
    public string Describe(Func<SimTime, string>? time = null)
    {
        time ??= t => t.ToString("H", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        if (Holds > 0)
        {
            parts.Add(Holds == 1 ? "1 hold" : string.Create(CultureInfo.InvariantCulture, $"{Holds} holds"));
        }

        if (PlatformChanges > 0)
        {
            parts.Add(PlatformChanges == 1 ? "1 platform change" : string.Create(CultureInfo.InvariantCulture, $"{PlatformChanges} platform changes"));
        }

        var others = Steps.Count - Holds - PlatformChanges;
        if (others > 0)
        {
            parts.Add(others == 1 ? "1 other change" : string.Create(CultureInfo.InvariantCulture, $"{others} other changes"));
        }

        return $"Re-plan to {time(Until)}: {(parts.Count == 0 ? "no changes" : string.Join(", ", parts))}";
    }

    /// <summary>The command that applies it: all its steps as one undoable edit.</summary>
    public AcceptSuggestion ToCommand() =>
        new(string.Create(CultureInfo.InvariantCulture, $"replan:{Method}:{From.Seconds}"), Commands) { Title = Describe() };

    public override string ToString() => $"{Describe()} ({Method}, {Outcome}): {Before.Count} → {After.Count} conflicts, +{WeightedDelaySeconds:0} s";
}

/// <summary>
/// What re-planners share: the plan as it stands and its horizon, and the checking and scoring of a candidate set of
/// commands by forecasting it, as the engine would (with the knock-on pass if it is on). Not thread-safe.
/// </summary>
public sealed class ReplanEvaluator
{
    private readonly Dictionary<TrainId, (int Index, int Time)> _references = [];
    private readonly HashSet<string> _baselineIds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ReplanEvaluator(ReplanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Request = request;
        Plan = request.Forecast.Plan;
        Baseline = request.Baseline ?? Forecaster.Run(request.Forecast);
        BaselineConflicts = request.Baseline is not null && request.BaselineConflicts is { } given ? given : Detect(Plan, Baseline);
        _baselineIds = BaselineConflicts.All.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        Now = Baseline.Now ?? request.Forecast.Now ?? (BaselineConflicts.Count > 0 ? BaselineConflicts.All.Min(c => c.Start) : default);
        End = Now + Math.Max(0, request.Options.HorizonSeconds);

        // Each train's delay is measured at its last point still to come within the horizon (as for suggestions).
        foreach (var t in Baseline.Trains)
        {
            if (t.Status is not (TrainForecastStatus.NotStarted or TrainForecastStatus.Running))
            {
                continue;
            }

            for (var k = t.Points.Count - 1; k >= 0; k--)
            {
                var p = t.Points[k];
                if (p.Basis is ForecastBasis.Forecast or ForecastBasis.Arrived && (p.LastTime ?? p.FirstTime) is { } time && time <= End)
                {
                    _references[t.Train] = (k, time.Seconds);
                    break;
                }
            }
        }
    }

    public ReplanRequest Request { get; }

    public ReplanOptions Options => Request.Options;

    /// <summary>The plan as it stands.</summary>
    public WorkingPlan Plan { get; }

    /// <summary>Its forecast (with the knock-on pass if it is on); conflicts are found on its independent times.</summary>
    public Forecast Baseline { get; }

    public ConflictSet BaselineConflicts { get; }

    /// <summary>When the horizon starts: the forecast's now.</summary>
    public SimTime Now { get; }

    /// <summary>When the horizon ends.</summary>
    public SimTime End { get; }

    /// <summary>How long since the evaluator was made.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>The conflicts in the horizon before re-planning.</summary>
    public IReadOnlyList<Conflict> Before => InHorizon(BaselineConflicts);

    /// <summary>The conflicts that a hold or platform change can clear: between two trains, at a section or platform.</summary>
    public static bool IsBetweenTrains(Conflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        return conflict.B is not null && conflict.Kind is ConflictKind.Headway or ConflictKind.Overtake or ConflictKind.SingleLine or ConflictKind.Platform;
    }

    public IReadOnlyList<Conflict> InHorizon(ConflictSet conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        return conflicts.All.Where(c => c.When <= End).ToList();
    }

    /// <summary>The weight of a minute of the train's delay (<see cref="Suggester.WeightOf"/>).</summary>
    public double WeightOf(TrainId train) => WeightOf(Plan.Find(train));

    public double WeightOf(TrainPlan? plan) =>
        Suggester.WeightOf(plan?.CategoryId is { } category && Request.Forecast.Network.Priorities.TryGetValue(category, out var p) ? p : Suggester.DefaultPriority);

    /// <summary>The plan with the commands, forecast and checked; null if they do not apply.</summary>
    public ReplanCandidate? Evaluate(IReadOnlyList<IPlanCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            return new ReplanCandidate(commands, Plan, Baseline, BaselineConflicts, this);
        }

        var (plan, result) = Plan.Apply(new AcceptSuggestion("candidate", commands));
        if (!result.Applied)
        {
            return null;
        }

        var forecast = Forecaster.Run(Request.Forecast with { Plan = plan });
        return new ReplanCandidate(commands, plan, forecast, Detect(plan, forecast), this);
    }

    /// <summary>The result for the best candidate found, or for none (the plan as it stands).</summary>
    public ReplanResult Result(string method, ReplanCandidate? best, IReadOnlyList<ReplanStep> steps, int rounds, bool optimal = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        var before = Before;
        var result = new ReplanResult(method, ReplanOutcome.NoImprovement, [])
        {
            From = Now,
            Until = End,
            Before = before,
            After = before,
            Rounds = rounds,
            Optimal = optimal,
            Elapsed = Elapsed,
        };

        if (before.Count == 0)
        {
            return result with { Outcome = ReplanOutcome.NoConflicts };
        }

        var penalty = steps.Sum(Penalty);
        if (best is null || best.Commands.Count == 0 || !best.BetterThan(null, penalty))
        {
            return result;
        }

        var impacts = Impacts(best.Forecast);
        var changed = new List<TrainId>();
        foreach (var t in best.Plan.Trains)
        {
            if (Plan.Find(t.Id) is not { } plan || (!ReferenceEquals(plan, t) && !plan.SameAs(t)))
            {
                changed.Add(t.Id);
            }
        }

        return result with
        {
            Outcome = ReplanOutcome.Improved,
            Commands = best.Commands,
            Steps = steps,
            After = best.InHorizon,
            Cleared = _baselineIds.Count(id => best.Conflicts.Find(id) is null),
            Added = best.Conflicts.All.Count(c => !_baselineIds.Contains(c.Id)),
            WeightedDelaySeconds = Math.Round(impacts.Sum(i => i.Cost), 1),
            Penalty = penalty,
            Impacts = impacts,
            Changed = changed,
            Plan = best.Plan,
        };
    }

    /// <summary>Σ weight × added delay at each train's reference point, by a forecast of a candidate.</summary>
    internal double WeightedDelay(Forecast forecast) => Impacts(forecast).Sum(i => i.Cost);

    internal double Penalty(ReplanStep step) => step.Kind switch
    {
        SuggestionKind.Platform => Options.PlatformChangePenalty,
        SuggestionKind.Line => Options.LineChangePenalty,
        _ => 0,
    };

    private List<SuggestionImpact> Impacts(Forecast forecast)
    {
        var impacts = new List<SuggestionImpact>();
        foreach (var (id, reference) in _references)
        {
            if (forecast.Find(id)?.At(reference.Index) is not { } p || (p.LastTime ?? p.FirstTime) is not { } time || time.Seconds == reference.Time)
            {
                continue;
            }

            var plan = Plan.Find(id);
            impacts.Add(new SuggestionImpact(id, plan?.Headcode ?? new Headcode(id.Value), time.Seconds - reference.Time, WeightOf(plan)));
        }

        impacts.Sort((x, y) => Math.Abs(y.Cost).CompareTo(Math.Abs(x.Cost)) is var byCost and not 0 ? byCost : string.CompareOrdinal(x.Train.Value, y.Train.Value));
        return impacts;
    }

    private ConflictSet Detect(WorkingPlan plan, Forecast forecast) =>
        ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = Request.Forecast.Network, Options = Request.Conflicts });
}

/// <summary>A candidate re-plan, forecast and checked by a <see cref="ReplanEvaluator"/>.</summary>
public sealed class ReplanCandidate
{
    private readonly ReplanEvaluator _evaluator;
    private double? _delay;

    internal ReplanCandidate(IReadOnlyList<IPlanCommand> commands, WorkingPlan plan, Forecast forecast, ConflictSet conflicts, ReplanEvaluator evaluator)
    {
        (Commands, Plan, Forecast, Conflicts, _evaluator) = (commands, plan, forecast, conflicts, evaluator);
        InHorizon = evaluator.InHorizon(conflicts);
    }

    public IReadOnlyList<IPlanCommand> Commands { get; }

    public WorkingPlan Plan { get; }

    public Forecast Forecast { get; }

    public ConflictSet Conflicts { get; }

    /// <summary>Its conflicts in the horizon.</summary>
    public IReadOnlyList<Conflict> InHorizon { get; }

    /// <summary>Σ weight × severity of its conflicts in the horizon.</summary>
    public double ConflictCost => InHorizon.Sum(c => c.SeveritySeconds * _evaluator.WeightOf(c.A));

    /// <summary>Σ weight × added delay over the trains in the horizon.</summary>
    public double WeightedDelay => _delay ??= _evaluator.WeightedDelay(Forecast);

    /// <summary>
    /// True if it is better than <paramref name="other"/> (a candidate, or the plan as it stands for null): fewer
    /// conflicts in the horizon, or as many but less weighted severity, then less weighted delay.
    /// </summary>
    public bool BetterThan(ReplanCandidate? other, double penalty = 0, double otherPenalty = 0)
    {
        var (count, cost, delay) = other is null
            ? (_evaluator.Before.Count, _evaluator.Before.Sum(c => c.SeveritySeconds * _evaluator.WeightOf(c.A)), 0.0)
            : (other.InHorizon.Count, other.ConflictCost, other.WeightedDelay + otherPenalty);
        if (InHorizon.Count != count)
        {
            return InHorizon.Count < count;
        }

        if (Math.Abs(ConflictCost - cost) > 0.5)
        {
            return ConflictCost < cost;
        }

        return WeightedDelay + penalty < delay - 0.5;
    }
}
