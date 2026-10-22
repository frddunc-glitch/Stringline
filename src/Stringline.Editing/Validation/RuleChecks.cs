using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Editing.Validation;

/// <summary>
/// The checks of the timetable rules (<c>08-…md</c> §5): a rule naming no train or several, a loop of rules waiting for
/// each other (cut at the rule that closes it, in file order, as S32's forecast cuts it), and a location the rule's
/// train does not call at. Run in full on every check: rules are few.
/// </summary>
internal static class RuleChecks
{
    public static IReadOnlyList<ValidationIssue> Check(ValidationContext c)
    {
        var issues = new List<ValidationIssue>();
        var waitsFor = new Dictionary<TrainId, HashSet<TrainId>>();
        var rules = c.Timetable.Rules;
        for (var r = 0; r < rules.Count; r++)
        {
            var rule = rules[r];
            var element = c.Import.RuleOf(r).Element;
            var x = c.Find(rule.Train);
            var y = c.Find(rule.Train2);
            ValidationIssue Issue(string code, string message)
            {
                var issue = c.Issue(code, x, null, message, element: element);
                return x is null ? issue with { Headcode = rule.TrainText } : issue;
            }

            if (rule.Kind == RuleKind.Unknown)
            {
                issues.Add(Issue(IssueCodes.RuleUnknown, $"Unknown rule {rule.Code}"));
                continue;
            }

            var name = Name(rule.Kind);
            if (x is null)
            {
                issues.Add(Issue(IssueCodes.RuleUnresolved, $"Rule \"{name}\": X {Unresolved(c, rule.TrainText)}"));
            }

            if (y is null)
            {
                issues.Add(Issue(IssueCodes.RuleUnresolved, $"Rule \"{name}\": Y {Unresolved(c, rule.Train2Text)}"));
            }

            if (rule.Location is { } location)
            {
                if (y is not null && !Calls(y, location))
                {
                    issues.Add(Issue(IssueCodes.RuleLocationNotCalled, $"Rule \"{name}\": Y ({y.Headcode}) does not call at {location}"));
                }

                if (x is not null && IsDeparture(rule.Kind) && !Calls(x, location))
                {
                    issues.Add(Issue(IssueCodes.RuleLocationNotCalled, $"Rule \"{name}\": X ({x.Headcode}) does not call at {location}"));
                }
            }

            if (x is null || y is null || x.Id == y.Id || !Waits(rule.Kind))
            {
                continue; // a train waiting for itself is a deliberate dwell (S32)
            }

            if (Reaches(waitsFor, y.Id, x.Id))
            {
                issues.Add(Issue(IssueCodes.RuleCycle, $"Rule \"{name}\" closes a loop: {x.Headcode} waits for {y.Headcode}, which already waits for {x.Headcode}"));
                continue;
            }

            (waitsFor.TryGetValue(x.Id, out var set) ? set : waitsFor[x.Id] = []).Add(y.Id);
        }

        return issues;
    }

    private static string Unresolved(ValidationContext c, string? text)
    {
        if (text is null)
        {
            return "names no train";
        }

        var count = c.Timetable.Trains.Count(t => string.Equals(t.Uid?.Trim(), text, StringComparison.OrdinalIgnoreCase));
        if (count == 0 && Headcode.TryCreate(text, out var headcode))
        {
            count = c.Timetable.WithHeadcode(headcode).Count();
        }

        return count == 0 ? $"names {text}, which is not in the timetable" : $"names {text}, which is {count} trains";
    }

    private static bool Calls(Train train, Tiploc location) => train.EntryPoint == location || train.Points.Any(p => p.Location == location);

    private static bool IsDeparture(RuleKind kind) => kind is >= RuleKind.XDepartsAfterYArrives and <= RuleKind.XDepartsAfterYForms;

    /// <summary>Rules where X waits for Y (not the exclusions).</summary>
    private static bool Waits(RuleKind kind) => kind is not (RuleKind.XNotIfY or RuleKind.MutuallyExclusive or RuleKind.Alternatives or RuleKind.Unknown);

    private static bool Reaches(Dictionary<TrainId, HashSet<TrainId>> edges, TrainId from, TrainId to)
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

    private static string Name(RuleKind kind) => kind switch
    {
        RuleKind.XAppearsAfterYEnters => "X appears after Y enters",
        RuleKind.XAppearsAfterYLeaves => "X appears after Y leaves",
        RuleKind.XAppearsAfterYArrives => "X appears after Y arrives",
        RuleKind.XNotIfY => "X not if Y",
        RuleKind.XDepartsAfterYArrives => "X departs after Y arrives",
        RuleKind.XDepartsAfterYEnters => "X departs after Y enters",
        RuleKind.XDepartsAfterYLeaves => "X departs after Y leaves",
        RuleKind.XDepartsAfterYJoins => "X departs after Y joins",
        RuleKind.XDepartsAfterYDivides => "X departs after Y divides",
        RuleKind.XDepartsAfterYForms => "X departs after Y forms",
        RuleKind.MutuallyExclusive => "mutually exclusive",
        RuleKind.XAppearsAfterYJoins => "X appears after Y joins",
        RuleKind.XAppearsAfterYDivides => "X appears after Y divides",
        RuleKind.XAppearsAfterYForms => "X appears after Y forms",
        RuleKind.Alternatives => "alternatives",
        _ => kind.ToString(),
    };
}
