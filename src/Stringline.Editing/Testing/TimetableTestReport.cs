using System.Globalization;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Editing.Bulk;
using Stringline.Editing.Validation;
using Stringline.Planning;
using Stringline.Timetable.Editing;
using Stringline.Timetable.Live;

namespace Stringline.Editing.Testing;

/// <summary>Tuning for <see cref="TimetableTestReport"/>.</summary>
public sealed record TimetableTestOptions
{
    /// <summary>A train not seen this long after its booked start (and before the recording ends) never appeared.</summary>
    public int NeverAppearedMarginSeconds { get; init; } = 15 * 60;

    /// <summary>A section is reported when every run over it was at least this much slower than booked.</summary>
    public int SlowerSeconds { get; init; } = 30;

    /// <summary>And there were at least this many runs.</summary>
    public int MinRuns { get; init; } = 2;
}

/// <summary>
/// What a test run of a timetable in SimSig showed (S48, <c>08-…md</c> §9): a recording of the sim running the
/// timetable, played against it, as issues for the editor's Issues panel. Trains that never appeared, workings that
/// did not match, sections every train ran slower than booked (with a quick fix moving the train's later times by
/// what it took), and platforms two trains stood in at once.
/// </summary>
public static class TimetableTestReport
{
    /// <summary>The report: the recording's events replayed against the timetable.</summary>
    public static IReadOnlyList<ValidationIssue> Build(WttDocumentImport import, IEnumerable<GatewayEvent> events, TimetableTestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(events);
        var state = new LiveState(import.Timetable);
        foreach (var e in events)
        {
            state.Apply(e);
        }

        var end = state.Clock.Reading.HasTime ? state.Clock.Reading.LastReported : state.Trains.SelectMany(t => t.Actuals).Select(a => (SimTime?)a.Time).Max();
        return Build(import, [.. state.Trains], state.SessionStart, end, state.EntryDelays, options);
    }

    /// <param name="entryDelays">SimSig's reported entry delays (<c>train_delay</c>), by headcode: a train delayed is late, not missing.</param>
    public static IReadOnlyList<ValidationIssue> Build(WttDocumentImport import, IReadOnlyList<LiveTrain> trains, SimTime? start, SimTime? end, IReadOnlyDictionary<Headcode, int>? entryDelays = null, TimetableTestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(trains);
        options ??= new TimetableTestOptions();
        var issues = new List<ValidationIssue>();
        if (start is null || end is null)
        {
            return issues;
        }

        NeverAppeared(import, trains, start.Value, end.Value, entryDelays ?? new Dictionary<Headcode, int>(), options, issues);
        Unmatched(trains, issues);
        SlowSections(import, trains, options, issues);
        PlatformClashes(import, trains, issues);
        return issues;
    }

    /// <summary>
    /// Trains the forecast still has not started at the recording's end, well after they were due: an entering train
    /// due at its booked entry plus any entry delay SimSig reported, and a train formed by another from when that one
    /// arrived (until it has, it is late, not missing). As-required trains are left out: SimSig may not choose them.
    /// </summary>
    private static void NeverAppeared(WttDocumentImport import, IReadOnlyList<LiveTrain> trains, SimTime start, SimTime end, IReadOnlyDictionary<Headcode, int> entryDelays, TimetableTestOptions options, List<ValidationIssue> issues)
    {
        var forecast = Forecaster.Run(new ForecastRequest(WorkingPlan.FromTimetable(import.Timetable))
        {
            LiveTrains = trains,
            Now = end,
            SessionStart = start,
            EntryDelays = entryDelays,
            Options = new ForecastOptions { Interaction = false },
        });
        foreach (var train in forecast.Trains.Where(t => t.Status == TrainForecastStatus.NotStarted))
        {
            var wtt = import.Timetable.Find(train.Train);
            if (wtt?.BookedStart is not { } booked || booked < start)
            {
                continue;
            }

            if (wtt.AsRequired)
            {
                continue; // SimSig runs an as-required train only some of the time.
            }

            var due = booked + Math.Max(0, entryDelays.GetValueOrDefault(wtt.Headcode));
            if (wtt.FormedFrom is { } parent)
            {
                // Due once its former was seen where it forms it, and has gone on from there: until then it is late or
                // held, not missing (in Waterloo's 4× recording 1S08 is still in platform 14 at the end, so 2R09 has
                // not started).
                if (SeenAt(trains, parent, wtt.Points[0].Location) is not { StillThere: false } seen)
                {
                    continue;
                }

                due = SimTime.Max(due, seen.Time);
            }

            if (due + options.NeverAppearedMarginSeconds > end)
            {
                continue;
            }

            issues.Add(Issue(import, IssueCodes.TestNeverAppeared, wtt, null,
                $"Never appeared (booked to start {SimTimeNotation.Format(due)} at {wtt.EntryPoint ?? wtt.Origin}; the recording runs to {SimTimeNotation.Format(end)})"));
        }
    }

    /// <summary>
    /// When the live working matched to <paramref name="train"/> was first reported at <paramref name="location"/>, if it
    /// was, and whether its last report is still there.
    /// </summary>
    private static (SimTime Time, bool StillThere)? SeenAt(IReadOnlyList<LiveTrain> trains, TrainId train, Tiploc location)
    {
        foreach (var live in trains)
        {
            for (var w = 0; w < live.Workings.Count; w++)
            {
                if (live.Workings[w].Train == train && live.ActualsOf(w).FirstOrDefault(a => a.Location == location) is { } seen)
                {
                    return (seen.Time, live.Actuals[^1].Location == location);
                }
            }
        }

        return null;
    }

    private static void Unmatched(IReadOnlyList<LiveTrain> trains, List<ValidationIssue> issues)
    {
        foreach (var live in trains)
        {
            foreach (var working in live.Workings.Where(w => w.Status == MatchStatus.Unmatched))
            {
                var entry = IssueCatalogue.Of(IssueCodes.TestUnmatched);
                issues.Add(new ValidationIssue(entry.Severity, IssueCodes.TestUnmatched, null, null,
                    $"{working.Headcode} (uid {live.SimUid}) ran but did not match{(working.Reason is { } why ? ": " + why : string.Empty)}", entry.Hint)
                {
                    Headcode = working.Headcode.Value,
                });
            }
        }
    }

    private static void SlowSections(WttDocumentImport import, IReadOnlyList<LiveTrain> trains, TimetableTestOptions options, List<ValidationIssue> issues)
    {
        var runs = new Dictionary<(Tiploc From, Tiploc To), List<(Train Train, int From, int Delta)>>();
        foreach (var live in trains)
        {
            for (var w = 0; w < live.Workings.Count; w++)
            {
                if (live.Workings[w] is not { IsMatched: true, Train: { } id } || import.Timetable.Find(id) is not { } wtt)
                {
                    continue;
                }

                var actuals = live.ActualsOf(w).Where(a => a.AtTimingPoint).ToList();
                var leaves = new SimTime?[wtt.Points.Count];
                var reaches = new SimTime?[wtt.Points.Count];
                var cursor = 0;
                for (var i = 0; i < wtt.Points.Count; i++)
                {
                    for (var k = cursor; k < actuals.Count; k++)
                    {
                        if (actuals[k].Location != wtt.Points[i].Location)
                        {
                            continue;
                        }

                        reaches[i] = actuals[k].Time;
                        leaves[i] = actuals[k].Time;
                        if (actuals[k].Action == TrainLocationAction.Arrive)
                        {
                            for (var d = k + 1; d < actuals.Count && actuals[d].Location == wtt.Points[i].Location; d++)
                            {
                                leaves[i] = actuals[d].Time;
                                k = d;
                            }
                        }

                        cursor = k + 1;
                        break;
                    }
                }

                for (var i = 0; i + 1 < wtt.Points.Count; i++)
                {
                    var (a, b) = (wtt.Points[i], wtt.Points[i + 1]);
                    if (leaves[i] is { } left && reaches[i + 1] is { } got && a.LastTime is { } booked && b.FirstTime is { } due && due > booked)
                    {
                        var key = (a.Location, b.Location);
                        if (!runs.TryGetValue(key, out var list))
                        {
                            runs[key] = list = [];
                        }

                        list.Add((wtt, i, (got - left) - (due - booked)));
                    }
                }
            }
        }

        foreach (var ((from, to), list) in runs)
        {
            if (list.Count < options.MinRuns || list.Any(r => r.Delta < options.SlowerSeconds))
            {
                continue;
            }

            var deltas = list.Select(r => r.Delta).Order().ToList();
            var median = deltas[deltas.Count / 2];
            var add = (median + BulkTools.Step - 1) / BulkTools.Step * BulkTools.Step;
            foreach (var (train, index, delta) in list.DistinctBy(r => r.Train.Id))
            {
                var node = import.NodeOf(train);
                var shift = BulkTools.Shift([node], add, to.Code);
                var fix = shift.Edit is { } edit ? new QuickFix($"Move {train.Headcode} from {to} on by {Minutes(add)}", edit) : null;
                issues.Add(Issue(import, IssueCodes.TestSlowSection, train, index + 1,
                    string.Create(CultureInfo.InvariantCulture, $"{from}–{to}: every train ran it slower than booked ({list.Count} runs, {Minutes(deltas[0])} to {Minutes(deltas[^1])} over; this one {Minutes(delta)}); add about {Minutes(add)}"),
                    fix));
            }
        }
    }

    private static void PlatformClashes(WttDocumentImport import, IReadOnlyList<LiveTrain> trains, List<ValidationIssue> issues)
    {
        var stands = new List<(LiveTrain Live, int Working, Tiploc Location, string Platform, SimTime From, SimTime To)>();
        foreach (var live in trains)
        {
            var actuals = live.Actuals;
            for (var k = 0; k < actuals.Count; k++)
            {
                if (actuals[k] is not { Action: TrainLocationAction.Arrive, Platform.Length: > 0 } arrive)
                {
                    continue;
                }

                var leave = actuals.Skip(k + 1).FirstOrDefault(a => a.Location == arrive.Location && a.Action == TrainLocationAction.Depart);
                if (leave is not null)
                {
                    stands.Add((live, arrive.Working, arrive.Location, arrive.Platform, arrive.Time, leave.Time));
                }
            }
        }

        foreach (var group in stands.GroupBy(s => (s.Location, Platform: s.Platform.ToUpperInvariant())))
        {
            var list = group.OrderBy(s => s.From.Seconds).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count && list[j].From < list[i].To; j++)
                {
                    if (list[j].Live.SimUid == list[i].Live.SimUid)
                    {
                        continue;
                    }

                    var (first, second) = (list[i], list[j]);
                    var until = SimTime.Min(first.To, second.To);
                    var message = $"Platform {first.Platform} at {first.Location}: {first.Live.Workings[first.Working].Headcode} and {second.Live.Workings[second.Working].Headcode} there together {SimTimeNotation.Format(second.From)}–{SimTimeNotation.Format(until)}";
                    var train = first.Live.Workings[first.Working].Train is { } id ? import.Timetable.Find(id) : null;
                    issues.Add(train is null
                        ? new ValidationIssue(IssueCatalogue.Of(IssueCodes.TestPlatformClash).Severity, IssueCodes.TestPlatformClash, null, null, message, IssueCatalogue.Of(IssueCodes.TestPlatformClash).Hint)
                        : Issue(import, IssueCodes.TestPlatformClash, train, train.Points.FirstOrDefault(p => p.Location == first.Location)?.Index, message));
                }
            }
        }
    }

    private static ValidationIssue Issue(WttDocumentImport import, string code, Train train, int? point, string message, QuickFix? fix = null)
    {
        var entry = IssueCatalogue.Of(code);
        var node = import.NodeOf(train);
        return new ValidationIssue(entry.Severity, code, train.Id, point, message, entry.Hint, fix)
        {
            TrainElement = node.Element,
            Headcode = train.Headcode.Value,
            Element = point is { } p ? import.TripOf(train, p).Element : node.Element,
        };
    }

    private static string Minutes(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(seconds < 0 ? "−" : "+")}{Math.Abs(seconds) / 60}:{Math.Abs(seconds) % 60:00}");
}
