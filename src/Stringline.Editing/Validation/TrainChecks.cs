using System.Globalization;
using System.Xml.Linq;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Knowledge;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Validation;

/// <summary>
/// The checks of one train: SimSig's Analyser's (in its wording, from the wiki's Timetable Tools 1 page) and those
/// against the sim's knowledge. A train's issues depend only on itself and the trains linked to it
/// (<see cref="ValidationContext.Links"/>), which is what lets <see cref="TimetableValidator.Revalidate"/> reuse the rest.
/// </summary>
internal static class TrainChecks
{
    /// <summary>SimSig's shortest train, and shortest remainder after dividing.</summary>
    public const int MinimumLengthMetres = 8;

    public static IReadOnlyList<ValidationIssue> Check(ValidationContext c, XElement element)
    {
        var issues = new List<ValidationIssue>();
        if (c.TrainOf(element) is not { } train)
        {
            // The importer skipped it: no headcode, or no usable timing points.
            var node = new TrainNode(c.Document, element);
            var code = Headcode.TryCreate(node.Headcode, out _) ? IssueCodes.NoTrips : IssueCodes.NoHeadcode;
            var message = code == IssueCodes.NoTrips ? "No trips defined" : $"No headcode ('{node.Headcode}')";
            var entry = IssueCatalogue.Of(code);
            issues.Add(new ValidationIssue(entry.Severity, code, null, null, message, entry.Hint) { TrainElement = element, Element = element, Headcode = node.Headcode });
            return issues;
        }

        var visits = Visits(train);
        Header(c, train, issues);
        Times(c, train, visits, issues);
        Activities(c, train, issues);
        Running(c, train, issues);
        Ending(c, train, issues);
        Seeding(c, train, issues);
        if (c.Knowledge is { } k)
        {
            Knowledge(c, k, train, visits, issues);
        }

        return issues;
    }

    /// <summary>Consecutive points at one location are one call there: (first, last) point indexes.</summary>
    private static List<(int First, int Last)> Visits(Train train)
    {
        var visits = new List<(int First, int Last)>();
        for (var i = 0; i < train.Points.Count; i++)
        {
            if (visits.Count > 0 && train.Points[visits[^1].Last].Location == train.Points[i].Location)
            {
                visits[^1] = (visits[^1].First, i);
            }
            else
            {
                visits.Add((i, i));
            }
        }

        return visits;
    }

    private static void Header(ValidationContext c, Train train, List<ValidationIssue> issues)
    {
        if (train.CategoryId is { } category && c.Timetable.FindCategory(category) is null)
        {
            issues.Add(c.Issue(IssueCodes.UnknownCategory, train, null, $"Uses category {category}, which is not defined"));
        }

        if (train.SeedGroup is { } group && c.Timetable.FindSeedGroup(group) is null)
        {
            issues.Add(c.Issue(IssueCodes.UnknownSeedGroup, train, null, $"Seeded in group {group}, which is not defined"));
        }

        // A seed point counts as an entry point (seeded copies of a working often keep only its last location), and so
        // does being formed by another train (a train that only exists to join or form another has one location).
        if (train.Points.Count < 2 && (train.Start == TrainStart.AtFirstLocation && !c.Formers.ContainsKey(train.Id)))
        {
            issues.Add(c.Issue(IssueCodes.NoTrips, train, null, "No trips defined"));
        }

        if (train.LengthMetres is { } length and < MinimumLengthMetres)
        {
            issues.Add(c.Issue(IssueCodes.TooShort, train, null, $"Train shorter than minimum allowed length ({length} m)"));
        }

        if (string.IsNullOrWhiteSpace(train.Electrification))
        {
            issues.Add(c.Issue(IssueCodes.NoTractionPower, train, null, "No traction power defined"));
        }
    }

    private static void Times(ValidationContext c, Train train, List<(int First, int Last)> visits, List<ValidationIssue> issues)
    {
        SimTime? previous = null;
        foreach (var point in train.Points)
        {
            var departsEarly = !point.IsPass && point is { Arrive: { } a, Depart: { } d } && d < a;
            if (departsEarly)
            {
                issues.Add(c.Issue(IssueCodes.DepartureBeforeArrival, train, point.Index, $"Departure time at {point.Location} earlier than arrival time"));
            }

            if (point.Arrive is { } arrive)
            {
                if (previous is { } p && arrive < p)
                {
                    issues.Add(c.Issue(IssueCodes.ArrivalBeforePrevious, train, point.Index, $"Arrival time at {point.Location} earlier than previous location time"));
                }

                previous = arrive;
            }

            if (point.Depart is { } depart)
            {
                if (!departsEarly && previous is { } p && depart < p)
                {
                    issues.Add(c.Issue(IssueCodes.DepartureBeforePrevious, train, point.Index, $"Dep/pass time at {point.Location} earlier than previous location time"));
                }

                previous = depart;
            }
        }

        // Out and back: the middle call is a reversal, and a train cannot reverse on a pass.
        for (var v = 1; v + 1 < visits.Count; v++)
        {
            if (train.Points[visits[v - 1].Last].Location != train.Points[visits[v + 1].First].Location)
            {
                continue;
            }

            for (var i = visits[v].First; i <= visits[v].Last; i++)
            {
                if (train.Points[i].IsPass)
                {
                    var trip = c.TripOf(train, i);
                    var fix = new QuickFix(
                        $"Stop at {train.Points[i].Location}",
                        TimetableEdit.Change(trip, t =>
                        {
                            t.IsPassTime = false;
                            t.ArrTime = t.DepPassTime;
                        }, $"Stop {train.Headcode} at {train.Points[i].Location}"));
                    issues.Add(c.Issue(IssueCodes.ReversesWithPassTime, train, i, $"Reverses at {train.Points[i].Location} but has \"passing time\" ticked", fix));
                }
            }
        }
    }

    private static void Activities(ValidationContext c, Train train, List<ValidationIssue> issues)
    {
        var length = train.LengthMetres;
        var last = train.Points.Count - 1;
        foreach (var point in train.Points)
        {
            for (var a = 0; a < point.Activities.Count; a++)
            {
                var activity = point.Activities[a];
                if (activity.Kind == ActivityKind.Unknown)
                {
                    issues.Add(c.Issue(IssueCodes.UnknownActivity, train, point.Index, $"Unknown activity at {point.Location} (code {activity.Code})"));
                    continue;
                }

                if (activity.Kind == ActivityKind.Next && (point.Index != last || !LastOfItsKind(c, train, point, a)))
                {
                    QuickFix? fix = null;
                    if (point.Index == last)
                    {
                        var node = c.Import.ActivityOf(train, point.Index, a);
                        fix = new QuickFix("Make the next working the last activity", TimetableEdit.Move(node.Element, node.Element.Parent!.Elements().Count() - 1, $"Move {train.Headcode}'s next working last"));
                    }

                    issues.Add(c.Issue(IssueCodes.NextNotFinal, train, point.Index, $"Next at {point.Location} not the final activity", fix));
                }

                if (activity.Kind == ActivityKind.CrewChange)
                {
                    continue; // often names a crew or diagram rather than a train
                }

                if (c.Find(activity.Train) is not { } other)
                {
                    issues.Add(c.Issue(
                        IssueCodes.AssociatedNotDefined,
                        train,
                        point.Index,
                        activity.AssociatedText is { } named ? $"Associated train {named} not defined ({Describe(activity.Kind)} at {point.Location})" : $"Associated train not defined ({Describe(activity.Kind)} at {point.Location})"));
                    continue;
                }

                Associated(c, train, point, activity, other, issues);
                if (activity.FormsAssociatedTrain && EarlierFormer(c, train, point, a, other) is { } first)
                {
                    issues.Add(activity.Kind == ActivityKind.Next
                        ? c.Issue(IssueCodes.NextAlreadyUsed, train, point.Index, $"Next train {other.Headcode} already used (by {first.By.Headcode})")
                        : c.Issue(IssueCodes.DividedPortionAlreadyUsed, train, point.Index, $"Divided portion {other.Headcode} already used (by {first.By.Headcode})"));
                }

                // Length as the train goes: joined trains add theirs, portions take theirs away.
                if (activity.Kind == ActivityKind.JoinedBy && length is { } joined && other.LengthMetres is { } more)
                {
                    length = joined + more;
                }
                else if (activity.FormsAssociatedTrain && activity.Kind != ActivityKind.Next && length is { } whole && other.LengthMetres is { } portion)
                {
                    if (whole < portion)
                    {
                        issues.Add(c.Issue(IssueCodes.ShorterThanDividedPortion, train, point.Index, $"Length {whole}m shorter than divided portion {other.Headcode} ({portion} m)"));
                    }
                    else if (whole - portion < MinimumLengthMetres)
                    {
                        issues.Add(c.Issue(IssueCodes.RemainderTooShort, train, point.Index, $"Remainder too short after dividing {other.Headcode} ({whole - portion} m)"));
                    }

                    length = whole - portion;
                }
                else if (activity.Kind == ActivityKind.Next && length is { } here && other.LengthMetres is { } onward && here != onward)
                {
                    issues.Add(c.Issue(IssueCodes.LengthDiffersFromNext, train, point.Index, $"Length {here}m not the same as next working {other.Headcode} ({onward} m)"));
                }

                if (activity.Kind == ActivityKind.Join && c.RunStateOf(other) != RunState.Runs)
                {
                    issues.Add(c.Issue(IssueCodes.JoiningTrainLost, train, point.Index, $"Joining train {other.Headcode} got lost (it never runs)"));
                }
            }
        }
    }

    /// <summary>
    /// Whether a next working is the last activity at its point but for other next workings that are its alternatives
    /// (their decision choices never meet, as day-of-week workings are written).
    /// </summary>
    private static bool LastOfItsKind(ValidationContext c, Train train, TimingPoint point, int index)
    {
        var mine = c.PresenceOf(train, point.Index, index);
        for (var later = index + 1; later < point.Activities.Count; later++)
        {
            if (point.Activities[later].Kind != ActivityKind.Next || c.PresenceOf(train, point.Index, later).Overlaps(mine))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An earlier forming activity of the same train that can be in one game with this one.</summary>
    private static Former? EarlierFormer(ValidationContext c, Train train, TimingPoint point, int index, Train formed)
    {
        if (!c.Formers.TryGetValue(formed.Id, out var formers))
        {
            return null;
        }

        var mine = c.PresenceOf(train, point.Index, index);
        foreach (var former in formers)
        {
            if (former.By.Id == train.Id && former.Point.Index == point.Index && former.ActivityIndex == index)
            {
                return null;
            }

            if (former.By.Id != train.Id && c.Coexist(former.By, c.PresenceOf(former), train, mine) && !JoinedTogether(former.By, train))
            {
                return former;
            }
        }

        return null;
    }

    /// <summary>One joins the other: older timetables have both name the combined train's next working.</summary>
    private static bool JoinedTogether(Train a, Train b) =>
        a.Points.Any(p => p.Activities.Any(x => x.Kind is ActivityKind.Join or ActivityKind.JoinedBy && x.Train == b.Id))
        || b.Points.Any(p => p.Activities.Any(x => x.Kind is ActivityKind.Join or ActivityKind.JoinedBy && x.Train == a.Id));

    /// <summary>The associated train must be where the activity is: starting there if formed, else calling there; on the same platform.</summary>
    private static void Associated(ValidationContext c, Train train, TimingPoint point, TrainActivity activity, Train other, List<ValidationIssue> issues)
    {
        TimingPoint? there;
        if (activity.FormsAssociatedTrain)
        {
            there = other.Points[0].Location == point.Location ? other.Points[0] : null;
        }
        else
        {
            var calls = other.Points.Where(p => p.Location == point.Location).ToList();
            there = calls.FirstOrDefault(p => SamePlatform(p.Platform, point.Platform)) ?? calls.FirstOrDefault();
        }

        if (there is null)
        {
            var where = activity.FormsAssociatedTrain ? $"starts at {other.Points[0].Location}" : $"does not call at {point.Location}";
            issues.Add(c.Issue(IssueCodes.AssociatedDifferentLocation, train, point.Index, $"Associated train {other.Headcode} is at different location ({where})"));
        }
        else if (!SamePlatform(there.Platform, point.Platform))
        {
            issues.Add(c.Issue(IssueCodes.AssociatedDifferentPlatform, train, point.Index, $"Associated train {other.Headcode} is on different platform ({there.Platform} at {point.Location}, not {point.Platform})"));
        }
    }

    private static bool SamePlatform(string? a, string? b) => a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Running(ValidationContext c, Train train, List<ValidationIssue> issues)
    {
        switch (c.RunStateOf(train))
        {
            case RunState.NeverUsed:
                issues.Add(c.Issue(IssueCodes.NeverUsed, train, null, "Never used"));
                break;
            case RunState.NeverRuns when c.Find(train.FormedFrom) is { } former:
                issues.Add(c.Issue(IssueCodes.NeverRuns, train, null, $"Never runs, referred to by {former.Headcode}"));
                break;
        }
    }

    private static void Ending(ValidationContext c, Train train, List<ValidationIssue> issues)
    {
        // A train ends there only if it arrives and does not depart later: SimSig books a terminating train's departure
        // as its arrival (64 194 of the corpus's 65 252 trains with an onward working); one that departs later leaves
        // the area from there.
        var last = train.Points[^1];
        if (!Terminates(last) || last.Activities.Any(a => a.Kind is ActivityKind.Next or ActivityKind.Join) || c.TripOf(train, last.Index).BerthsHere
            || (last.Activities.Count == 0 && c.AreaEdges.Contains(last.Location)))
        {
            return;
        }

        if (last.Activities.LastOrDefault(a => a.Kind == ActivityKind.JoinedBy) is { } joined)
        {
            issues.Add(c.Issue(IssueCodes.NoFurtherActionAfterJoining, train, last.Index, $"No further action after joining {joined.AssociatedText ?? "the train"}"));
        }
        else if (train.Points.Count > 1)
        {
            issues.Add(c.Issue(IssueCodes.TerminatesWithNoOnward, train, last.Index, $"Terminates at {last.Location} with no onward working"));
        }
    }

    private static bool Terminates(TimingPoint last) => !last.IsPass && last.Arrive is { } arrive && (last.Depart is not { } depart || depart == arrive);

    private static void Seeding(ValidationContext c, Train train, List<ValidationIssue> issues)
    {
        if (train.Start != TrainStart.Seeded || train.SeedPoint is not { } seed)
        {
            return;
        }

        var first = train.Points[0];
        if (first.Arrive is not null)
        {
            var fix = new QuickFix($"Clear the arrival at {first.Location}", TimetableEdit.Change(c.TripOf(train, 0), t => t.ArrTime = null, $"Clear {train.Headcode}'s arrival at {first.Location}"));
            issues.Add(c.Issue(IssueCodes.SeedArrivalTime, train, 0, $"Arrival time at seed start location {first.Location}", fix));
        }

        var key = (seed.Code.ToUpperInvariant(), train.SeedGroup?.Trim().ToUpperInvariant() ?? string.Empty);
        var mine = c.PresenceOf(train);
        if (c.Seeded.TryGetValue(key, out var mates)
            && mates.TakeWhile(m => m.Id != train.Id).FirstOrDefault(m => c.Coexist(m, c.PresenceOf(m), train, mine)) is { } mate)
        {
            var group = train.SeedGroup is null ? "at the timetable's start" : $"in seed group {c.Timetable.FindSeedGroup(train.SeedGroup)?.ToString() ?? train.SeedGroup}";
            issues.Add(c.Issue(IssueCodes.SeedSignalShared, train, null, $"Seeded at {seed} with {mate.Headcode}, {group}"));
        }
    }

    private static void Knowledge(ValidationContext c, SimKnowledge k, Train train, List<(int First, int Last)> visits, List<ValidationIssue> issues)
    {
        if (train.Start == TrainStart.EntryPoint && train.EntryPoint is { } entry)
        {
            var at = k.Location(entry);
            if (at is null || !at.Is(LocationRoles.Entry))
            {
                issues.Add(c.Issue(IssueCodes.UnknownEntryPoint, train, null, $"{entry} not seen as an entry point ({c.Evidence})"));
            }
            else if (at.EntryPower is { Count: > 0 } powers && train.Electrification?.Trim() is { Length: > 0 } own && !own.Any(p => powers.ContainsKey(p.ToString())))
            {
                issues.Add(c.Issue(IssueCodes.WrongTractionPower, train, null, $"Wrong traction power for {entry}: no {own} train enters there (seen: {string.Join(", ", powers.Keys)})"));
            }
        }

        if (train.Start == TrainStart.Seeded && train.SeedPoint is { } seed
            && k.Signal(seed.Code) is null && k.Location(seed)?.Is(LocationRoles.Seed) != true)
        {
            issues.Add(c.Issue(IssueCodes.UnknownSeedPoint, train, null, $"Seed point {seed} is not a known signal ({c.Evidence})"));
        }

        for (var v = 0; v < visits.Count; v++)
        {
            for (var i = visits[v].First; i <= visits[v].Last; i++)
            {
                Point(c, k, train, train.Points[i], v > 0 && v < visits.Count - 1, issues);
            }
        }

        for (var v = 1; v < visits.Count; v++)
        {
            Link(c, k, train, train.Points[visits[v - 1].Last], train.Points[visits[v].First], issues);
            if (v + 1 < visits.Count)
            {
                OutOfOrder(c, train, train.Points[visits[v - 1].Last], train.Points[visits[v].First], train.Points[visits[v + 1].First], issues);
            }
        }

        var last = train.Points[^1];
        if (Terminates(last) && k.Location(last.Location) is { Ends: 0 } && !c.AreaEdges.Contains(last.Location))
        {
            issues.Add(c.Issue(IssueCodes.NotSensibleEnding, train, last.Index, $"{last.Location} not a sensible ending point (no train of the sim ends there)"));
        }
    }

    private static void Point(ValidationContext c, SimKnowledge k, Train train, TimingPoint point, bool between, List<ValidationIssue> issues)
    {
        if (k.Location(point.Location) is not { } at)
        {
            if (k.Signal(point.Location.Code) is null)
            {
                issues.Add(c.Issue(IssueCodes.UnknownLocation, train, point.Index, $"{point.Location} {c.Evidence}"));
            }

            return;
        }

        if (point.Platform is { } platform && !Has(at.Platforms, platform) && !Has(at.ReportedPlatforms, platform))
        {
            issues.Add(c.Issue(IssueCodes.InvalidPlatform, train, point.Index, $"Invalid platform {platform} at {point.Location} ({c.Evidence} there)"));
        }

        if (point.Line is { } line && !Has(at.Lines, line))
        {
            issues.Add(c.Issue(IssueCodes.InvalidLine, train, point.Index, $"Invalid line code {line} at {point.Location} ({c.Evidence} there)"));
        }

        if (point.Path is { } path && !Has(at.Paths, path))
        {
            issues.Add(c.Issue(IssueCodes.InvalidPath, train, point.Index, $"Invalid path code {path} at {point.Location} ({c.Evidence} there)"));
        }

        if (!point.IsPass && point.IsTimed && !at.Is(LocationRoles.Station))
        {
            issues.Add(c.Issue(IssueCodes.StopsAtNonStation, train, point.Index, $"Stops at non-station {point.Location}"));
        }

        if (point.Activities.Count > 0 && !at.Is(LocationRoles.Key))
        {
            issues.Add(c.Issue(IssueCodes.NotSafeForActivities, train, point.Index, $"Not safe to have activities at {point.Location}"));
        }

        if (between && at is { Exits: > 0, Through: 0 })
        {
            issues.Add(c.Issue(IssueCodes.ExitsAndReenters, train, point.Index, $"Exits after {point.Location} and then re-enters the area (trains leave the area there)"));
        }
    }

    private static bool Has(IReadOnlyDictionary<string, int>? codes, string code) => codes is not null && codes.ContainsKey(code);

    private static void Link(ValidationContext c, SimKnowledge k, Train train, TimingPoint from, TimingPoint to, List<ValidationIssue> issues)
    {
        if (k.Location(from.Location) is null || k.Location(to.Location) is null)
        {
            return; // an unknown location is reported already
        }

        if (k.Link(from.Location, to.Location) is not { } link)
        {
            QuickFix? fix = null;
            if (k.Chain(from.Location, to.Location) is { Count: > 2 } chain)
            {
                fix = new QuickFix($"Insert {string.Join(", ", chain.Skip(1).SkipLast(1))}", QuickFixes.InsertChain(c, k, train, from, to, chain));
            }

            issues.Add(c.Issue(IssueCodes.NoKnownPath, train, to.Index, $"Invalid timetable data: no known path from {from.Location} to {to.Location} ({c.Evidence} straight after each other)", fix));
            return;
        }

        // Ambiguous only where every other booking names a line: where some leave it out, SimSig copes.
        if (from.Line is null && link.Lines is { Count: > 1 } lines && lines.Values.Sum() >= link.Count)
        {
            var most = lines.OrderByDescending(l => l.Value).ThenBy(l => l.Key, StringComparer.Ordinal).First().Key;
            var fix = new QuickFix($"Set line {most}", TimetableEdit.Change(c.TripOf(train, from.Index), t => t.Line = most, $"Set {train.Headcode}'s line at {from.Location} to {most}"));
            issues.Add(c.Issue(IssueCodes.AmbiguousLine, train, from.Index, $"Ambiguous line from {from.Location} to {to.Location}: {string.Join(", ", lines.OrderByDescending(l => l.Value).Select(l => $"{l.Key} ({l.Value})"))}", fix));
        }

        var band = RunBand.Of(train);
        if (from.LastTime is { } leaves && to.FirstTime is { } reaches && reaches - leaves - from.AllowanceSeconds is var run and > 0
            && k.RunTime(from.Location, to.Location, band) is { } seen && run < seen.LeastSeconds)
        {
            issues.Add(c.Issue(IssueCodes.RunTooShort, train, to.Index, $"Runs {from.Location} to {to.Location} in {Clock(run)}; the least seen for {band} trains is {Clock(seen.LeastSeconds)} ({seen.Samples} runs)"));
        }
    }

    /// <summary>Booked A, B, C where other timetables book C between A and B.</summary>
    private static void OutOfOrder(ValidationContext c, Train train, TimingPoint a, TimingPoint b, TimingPoint next, List<ValidationIssue> issues)
    {
        // A train that reverses at B may well come back through a location between A and B.
        if (c.SkipChain(a.Location, b.Location) is { } chain && chain.Skip(1).SkipLast(1).Contains(next.Location)
            && c.Knowledge?.Location(b.Location)?.Is(LocationRoles.Reversing) != true)
        {
            issues.Add(c.Issue(IssueCodes.OutOfOrder, train, next.Index, $"{next.Location} out of order in timetable (other timetables book it between {a.Location} and {b.Location})"));
        }
    }

    private static string Describe(ActivityKind kind) => kind switch
    {
        ActivityKind.Next => "next working",
        ActivityKind.DivideRear or ActivityKind.DivideFront => "divide",
        ActivityKind.Join => "join",
        ActivityKind.JoinedBy => "joined by",
        ActivityKind.DetachEngineRear or ActivityKind.DetachEngineFront => "detach engine",
        ActivityKind.DropCoachesRear or ActivityKind.DropCoachesFront => "drop coaches",
        ActivityKind.PlatformShare => "platform share",
        _ => kind.ToString(),
    };

    internal static string Clock(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
}
