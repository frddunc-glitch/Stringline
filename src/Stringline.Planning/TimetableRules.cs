using System.Globalization;
using System.Runtime.CompilerServices;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>Which of a train's times at a point a rule waits for.</summary>
public enum RuleEdge
{
    /// <summary>Its first time there: entering the area, arriving, or passing.</summary>
    First,

    /// <summary>Its last time there: departing, passing, or (at its last point) leaving the area.</summary>
    Last,
}

/// <summary>
/// A WTT rule as the forecaster applies it (S32, <c>06-…md</c> §2): train <see cref="Train"/> (X) may not appear, or
/// leave <see cref="Point"/>, before train <see cref="After"/> (Y) reaches its event at <see cref="AfterPoint"/>, plus
/// the rule's <c>Time</c> (seconds; <c>04-…md</c>).
/// </summary>
/// <param name="Point">X's plan point it is held at, or null for an appearance rule (X's first timed point, as it enters or starts).</param>
/// <param name="AfterPoint">Y's plan point its event is at.</param>
/// <param name="Edge">Which of Y's times there.</param>
/// <param name="AfterHeadcode">Y's headcode, for the notes.</param>
public sealed record ForecastRule(TimetableRule Rule, TrainId Train, int? Point, TrainId After, Headcode AfterHeadcode, int AfterPoint, RuleEdge Edge)
{
    public int DelaySeconds => Math.Max(0, Rule.DelaySeconds ?? 0);

    /// <summary>True for "X appears after …" (held at its entry or start); false for "X departs after …".</summary>
    public bool IsAppearance => Point is null;

    /// <summary>What Y does: "leaves", "arrives", and so on.</summary>
    public string Verb => TimetableRules.VerbOf(Rule.Kind);

    /// <summary>"after 2B44 leaves (+60 s)".</summary>
    public string Description => $"after {AfterHeadcode} {Verb}{(DelaySeconds > 0 ? $" (+{TimetableRules.Duration(DelaySeconds)})" : string.Empty)}";

    public override string ToString() => $"{Train} {(IsAppearance ? "appears" : $"departs {Point}")} {Description}";
}

/// <summary>A train the forecast held by a timetable rule (S32): it would have gone at <see cref="From"/>, and went at <see cref="Until"/>.</summary>
/// <param name="Index">The plan point it was held at (its first timed point for an appearance rule).</param>
public sealed record RuleHold(ForecastRule Rule, int Index, SimTime From, SimTime Until)
{
    public int Seconds => Until - From;

    /// <summary>"held by rule: after 2B44 leaves (+60 s)".</summary>
    public override string ToString() => $"held by rule: {Rule.Description}";
}

/// <summary>
/// A working plan's WTT rules resolved for the forecaster (S32): each rule SimSig enforces with X's hold point and Y's
/// event as plan points, with chains that loop broken. Rules the forecaster does not apply (exclusions, unresolved
/// trains, a place a train does not go) are left out; exclusions are kept for the train detail
/// (<see cref="ExclusionsOf"/>). Worked out once per plan.
/// </summary>
public sealed class TimetableRules
{
    private static readonly ConditionalWeakTable<WorkingPlan, TimetableRules> Cache = new();

    private readonly Dictionary<TrainId, List<ForecastRule>> _byTrain = [];
    private readonly Dictionary<TrainId, List<TimetableRule>> _exclusions = [];

    private TimetableRules()
    {
    }

    public static TimetableRules Empty { get; } = new();

    /// <summary>The rules applied, in the timetable's order.</summary>
    public IReadOnlyList<ForecastRule> Rules { get; private set; } = [];

    /// <summary>Rules not applied, and why (a cycle broken, a place a train does not go), for the log.</summary>
    public IReadOnlyList<string> Notes { get; private set; } = [];

    /// <summary>The rules that hold <paramref name="train"/> (as X).</summary>
    public IReadOnlyList<ForecastRule> For(TrainId train) => _byTrain.TryGetValue(train, out var list) ? list : [];

    /// <summary>The exclusion rules (3 X not if Y, 10 mutually exclusive, 14 alternatives) naming <paramref name="train"/>, as X or Y.</summary>
    public IReadOnlyList<TimetableRule> ExclusionsOf(TrainId train) => _exclusions.TryGetValue(train, out var list) ? list : [];

    /// <summary>The plan's rules (cached for the plan).</summary>
    public static TimetableRules For(WorkingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Timetable.Rules.Count == 0 ? Empty : Cache.GetValue(plan, Build);
    }

    /// <summary>True for the rules the forecaster applies: appearance (0–2, 11–13) and departure (4–9) rules.</summary>
    public static bool IsForecast(RuleKind kind) => kind is >= RuleKind.XAppearsAfterYEnters and <= RuleKind.XDepartsAfterYForms and not RuleKind.XNotIfY
        or RuleKind.XAppearsAfterYJoins or RuleKind.XAppearsAfterYDivides or RuleKind.XAppearsAfterYForms;

    public static bool IsExclusion(RuleKind kind) => kind is RuleKind.XNotIfY or RuleKind.MutuallyExclusive or RuleKind.Alternatives;

    public static bool IsAppearance(RuleKind kind) => kind is RuleKind.XAppearsAfterYEnters or RuleKind.XAppearsAfterYLeaves or RuleKind.XAppearsAfterYArrives
        or RuleKind.XAppearsAfterYJoins or RuleKind.XAppearsAfterYDivides or RuleKind.XAppearsAfterYForms;

    public static string VerbOf(RuleKind kind) => kind switch
    {
        RuleKind.XAppearsAfterYEnters or RuleKind.XDepartsAfterYEnters => "enters",
        RuleKind.XAppearsAfterYLeaves or RuleKind.XDepartsAfterYLeaves => "leaves",
        RuleKind.XAppearsAfterYArrives or RuleKind.XDepartsAfterYArrives => "arrives",
        RuleKind.XAppearsAfterYJoins or RuleKind.XDepartsAfterYJoins => "joins",
        RuleKind.XAppearsAfterYDivides or RuleKind.XDepartsAfterYDivides => "divides",
        RuleKind.XAppearsAfterYForms or RuleKind.XDepartsAfterYForms => "forms",
        _ => "?",
    };

    /// <summary>An exclusion rule as the train detail says it, from <paramref name="train"/>'s side.</summary>
    public static string DescribeExclusion(TimetableRule rule, TrainId train, Func<TrainId, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(nameOf);
        var isX = rule.Train == train;
        var other = (isX ? rule.Train2 : rule.Train) is { } id ? nameOf(id) : (isX ? rule.Train2Text : rule.TrainText) ?? "?";
        return rule.Kind switch
        {
            RuleKind.XNotIfY when isX => $"does not run if {other} runs",
            RuleKind.XNotIfY => $"{other} does not run if this one runs",
            RuleKind.MutuallyExclusive => $"runs or {other} runs, not both",
            _ => $"an alternative to {other}",
        };
    }

    /// <summary>"60 s", "25 min", "1 min 30 s".</summary>
    public static string Duration(int seconds)
    {
        if (seconds < 2 * SimTime.SecondsPerMinute && seconds % SimTime.SecondsPerMinute != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds} s");
        }

        var (m, s) = Math.DivRem(seconds, SimTime.SecondsPerMinute);
        return s == 0 ? string.Create(CultureInfo.InvariantCulture, $"{m} min") : string.Create(CultureInfo.InvariantCulture, $"{m} min {s} s");
    }

    private static TimetableRules Build(WorkingPlan plan)
    {
        var result = new TimetableRules();
        var notes = new List<string>();
        var rules = new List<ForecastRule>();
        var edges = new Dictionary<TrainId, List<TrainId>>();
        foreach (var rule in plan.Timetable.Rules)
        {
            if (IsExclusion(rule.Kind))
            {
                foreach (var id in new[] { rule.Train, rule.Train2 }.OfType<TrainId>().Distinct())
                {
                    if (!result._exclusions.TryGetValue(id, out var list))
                    {
                        result._exclusions[id] = list = [];
                    }

                    list.Add(rule);
                }

                continue;
            }

            // Unresolved trains are noted by the import already.
            if (!IsForecast(rule.Kind) || rule.Train is not { } xId || rule.Train2 is not { } yId ||
                plan.Find(xId) is not { Origin: PlanOrigin.Wtt } x || plan.Find(yId) is not { Origin: PlanOrigin.Wtt } y)
            {
                continue;
            }

            var name = $"{rule.TrainText} {(IsAppearance(rule.Kind) ? "appears" : "departs")} after {rule.Train2Text} {VerbOf(rule.Kind)}";
            var self = xId == yId;
            if (self && IsAppearance(rule.Kind))
            {
                notes.Add($"Rule \"{name}\" names one train twice; not applied.");
                continue;
            }

            if (FirstTimed(x.RunningPoints) < 0 || FirstTimed(y.RunningPoints) < 0)
            {
                notes.Add($"Rule \"{name}\": {(FirstTimed(x.RunningPoints) < 0 ? rule.TrainText : rule.Train2Text)} has no booked times; not applied.");
                continue;
            }

            // Held where it leaves the rule's location; else (it joins or ends there, say) at its first stop, with Y's
            // event still at the location.
            var point = IsAppearance(rule.Kind) ? null : HoldPoint(x, rule.Location) ?? (rule.Location is null ? null : HoldPoint(x, null));
            if (!IsAppearance(rule.Kind) && point is null)
            {
                notes.Add($"Rule \"{name}\": {rule.TrainText} has no stop to hold it at; not applied.");
                continue;
            }

            var near = rule.Location ?? (point is { } k ? x.RunningPoints[k].Location : null);
            if (EventOf(rule, plan, y, near) is not { } yEvent)
            {
                notes.Add($"Rule \"{name}\": {rule.Train2Text} does not {VerbOf(rule.Kind).TrimEnd('s')}{(rule.Location is { } l ? $" at {l}" : string.Empty)}; not applied.");
                continue;
            }

            // A train's rule on itself is a least dwell ("departs 2 min after it arrives"): its event must come first.
            if (self)
            {
                if (yEvent.Point > point || (yEvent.Point == point && yEvent.Edge == RuleEdge.Last))
                {
                    notes.Add($"Rule \"{name}\" waits for the train's own later event; not applied.");
                    continue;
                }

                Add(new ForecastRule(rule, xId, point, yId, y.Headcode, yEvent.Point, yEvent.Edge));
                continue;
            }

            // A rule that would close a loop (Y already waits, through other rules, for X) is not applied.
            if (Reaches(edges, yId, xId))
            {
                notes.Add($"Rule \"{name}\" closes a loop of rules; not applied.");
                continue;
            }

            if (!edges.TryGetValue(xId, out var to))
            {
                edges[xId] = to = [];
            }

            to.Add(yId);
            Add(new ForecastRule(rule, xId, point, yId, y.Headcode, yEvent.Point, yEvent.Edge));
        }

        void Add(ForecastRule forecast)
        {
            rules.Add(forecast);
            if (!result._byTrain.TryGetValue(forecast.Train, out var mine))
            {
                result._byTrain[forecast.Train] = mine = [];
            }

            mine.Add(forecast);
        }

        result.Rules = rules;
        result.Notes = notes;
        return result;
    }

    /// <summary>
    /// Where a departure rule holds X: its timed point at <paramref name="location"/> (one it leaves), else its first
    /// stop (or where it starts), as Fred chose (S32).
    /// </summary>
    private static int? HoldPoint(TrainPlan x, Tiploc? location)
    {
        var points = x.RunningPoints;
        var last = LastTimed(points);
        for (var k = 0; k < last; k++)
        {
            var p = points[k];
            if (!p.IsTimed || p.Depart is null)
            {
                continue;
            }

            if (location is { } at ? p.Location == at : !p.IsPass)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>
    /// Y's event as a plan point: at the rule's location if it has one; for a departure rule with none, at X's hold
    /// point if Y goes there (enters only at a location the rule names); else Y's own event (enters: its first point;
    /// leaves: its last; arrives: its destination; joins, divides, forms: the point with that activity, at its arrival).
    /// </summary>
    private static (int Point, RuleEdge Edge)? EventOf(TimetableRule rule, WorkingPlan plan, TrainPlan y, Tiploc? near)
    {
        var points = y.RunningPoints;
        var first = FirstTimed(points);
        var last = LastTimed(points);
        if (first < 0)
        {
            return null;
        }

        int? At(Tiploc? location)
        {
            for (var k = first; location is not null && k <= last; k++)
            {
                if (points[k].IsTimed && points[k].Location == location)
                {
                    return k;
                }
            }

            return null;
        }

        switch (rule.Kind)
        {
            case RuleKind.XAppearsAfterYEnters or RuleKind.XDepartsAfterYEnters:
                return rule.Location is { } entersAt ? (At(entersAt) is { } e ? (e, RuleEdge.First) : null) : (first, RuleEdge.First);

            case RuleKind.XAppearsAfterYLeaves or RuleKind.XDepartsAfterYLeaves:
                return At(near) is { } l ? (l, RuleEdge.Last) : rule.Location is not null ? null : (last, RuleEdge.Last);

            case RuleKind.XAppearsAfterYArrives or RuleKind.XDepartsAfterYArrives:
                return At(near) is { } a ? (a, RuleEdge.First) : rule.Location is not null ? null : (last, RuleEdge.First);

            default:
                var wtt = plan.Timetable.Find(y.Source);
                bool Does(int k) => wtt is not null && k < wtt.Points.Count && wtt.Points[k].Activities.Any(a => rule.Kind switch
                {
                    RuleKind.XAppearsAfterYJoins or RuleKind.XDepartsAfterYJoins => a.Kind == ActivityKind.Join,
                    RuleKind.XAppearsAfterYDivides or RuleKind.XDepartsAfterYDivides => a.Kind is ActivityKind.DivideFront or ActivityKind.DivideRear,
                    _ => a.Kind == ActivityKind.Next,
                });
                var found = Enumerable.Range(first, last - first + 1).Where(k => points[k].IsTimed && Does(k)).ToList();
                var chosen = found.Where(k => points[k].Location == near).Cast<int?>().FirstOrDefault()
                    ?? (rule.Location is null && found.Count > 0 ? found[0] : null);
                return chosen is { } c ? (c, RuleEdge.First) : null;
        }
    }

    private static bool Reaches(Dictionary<TrainId, List<TrainId>> edges, TrainId from, TrainId to)
    {
        var seen = new HashSet<TrainId>();
        var stack = new Stack<TrainId>([from]);
        while (stack.TryPop(out var at))
        {
            if (at == to)
            {
                return true;
            }

            if (seen.Add(at) && edges.TryGetValue(at, out var next))
            {
                foreach (var n in next)
                {
                    stack.Push(n);
                }
            }
        }

        return false;
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

    private static int LastTimed(IReadOnlyList<PlannedPoint> points)
    {
        for (var k = points.Count - 1; k >= 0; k--)
        {
            if (points[k].IsTimed)
            {
                return k;
            }
        }

        return -1;
    }
}
