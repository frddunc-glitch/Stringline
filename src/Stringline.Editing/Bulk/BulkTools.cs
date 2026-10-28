using System.Globalization;
using System.Xml.Linq;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Drafts;
using Stringline.Editing.Knowledge;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Bulk;

/// <summary>
/// A bulk tool's result (S46, <c>08-…md</c> §8): one edit to apply (or null if there is nothing to do), what it
/// changes, line by line, for the preview, and what it could not do.
/// </summary>
public sealed record BulkResult(TimetableEdit? Edit, IReadOnlyList<string> Changes, IReadOnlyList<string> Problems)
{
    public static BulkResult Nothing(string why) => new(null, [], [why]);
}

/// <summary>Where a multi-train edit sets a code: the trip's platform, line or path.</summary>
public enum TripCode
{
    Platform,
    Line,
    Path,
}

/// <summary>Which allowance a multi-train edit sets.</summary>
public enum AllowanceKind
{
    Engineering,
    Pathing,
    Performance,
}

/// <summary>What find and replace looks at.</summary>
[Flags]
public enum ReplaceIn
{
    None = 0,
    Headcodes = 1,
    Uids = 2,
    Descriptions = 4,

    /// <summary>Locations, platforms, lines and paths at the trips.</summary>
    Codes = 8,
    All = Headcodes | Uids | Descriptions | Codes,
}

/// <summary>
/// The bulk tools (S46, <c>08-…md</c> §8): repeat as pattern and pairing next workings, auto-fill timings and set line,
/// and multi-train edits (shift, renumber, category, codes at a location, allowances, find and replace). Each makes
/// one composite edit, so one undo takes it back; links (activities and rules naming a renumbered train) are kept.
/// </summary>
public static class BulkTools
{
    /// <summary>SimSig books to the half minute.</summary>
    public const int Step = 30;

    // ---- Shift (SimSig's Adjust times) ----

    /// <summary>
    /// Shifts the trains' times by <paramref name="seconds"/> (whole or half minutes), all of them, or from the first
    /// trip at <paramref name="from"/> on. As SimSig's Adjust times does, a time of 00:00 (no time) is left alone; the
    /// entry or seeding time moves when the whole train does, and so do crew change times in the trips moved.
    /// </summary>
    public static BulkResult Shift(IReadOnlyList<TrainNode> trains, int seconds, string? from = null)
    {
        ArgumentNullException.ThrowIfNull(trains);
        if (seconds % Step != 0)
        {
            throw new InvalidOperationException("Shift by whole or half minutes.");
        }

        if (seconds == 0)
        {
            return BulkResult.Nothing("A shift of 0 changes nothing.");
        }

        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();
        foreach (var train in trains)
        {
            var trips = train.Trips.ToList();
            var start = from is null ? 0 : trips.FindIndex(t => Same(t.Location, from));
            if (start < 0)
            {
                problems.Add($"{train.Headcode} does not call at {from}.");
                continue;
            }

            if (start == 0 && train.DepartTime is { } entry)
            {
                edits.Add(TimetableEdit.Change(train, t => t.DepartTime = Moved(entry, seconds)));
            }

            foreach (var trip in trips.Skip(start))
            {
                edits.Add(TimetableEdit.Change(trip, t =>
                {
                    t.ArrTime = t.ArrTime is { } a ? Moved(a, seconds) : null;
                    t.DepPassTime = t.DepPassTime is { } d ? Moved(d, seconds) : null;
                }));
                foreach (var activity in trip.Activities.Where(a => a.CrewChangeManualTime is not null))
                {
                    edits.Add(TimetableEdit.Change(activity, a => a.CrewChangeManualTime = Moved(a.CrewChangeManualTime!.Value, seconds)));
                }
            }

            changes.Add($"{train.Headcode}: {Minutes(seconds)}{(from is null ? string.Empty : $" from {from}")}");
        }

        return Result($"Shift {trains.Count} train{Plural(trains.Count)} by {Minutes(seconds)}{(from is null ? string.Empty : $" from {from}")}", edits, changes, problems);
    }

    // ---- Renumber ----

    /// <summary>
    /// The headcode <paramref name="steps"/> steps of <paramref name="step"/> on from <paramref name="headcode"/>: its
    /// trailing number goes up, keeping its width (<c>2A01</c> +2 → <c>2A03</c>; <c>1A99</c> +1 is refused).
    /// </summary>
    public static string NextHeadcode(string headcode, int step, int steps = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(headcode);
        var digits = 0;
        while (digits < headcode.Length && char.IsAsciiDigit(headcode[^(digits + 1)]))
        {
            digits++;
        }

        if (digits == 0)
        {
            throw new InvalidOperationException($"{headcode} does not end in a number to count on from.");
        }

        var number = int.Parse(headcode[^digits..], CultureInfo.InvariantCulture) + (step * steps);
        var text = number.ToString(new string('0', digits), CultureInfo.InvariantCulture);
        if (number < 0 || text.Length > digits)
        {
            throw new InvalidOperationException($"Counting on from {headcode} by {step} runs out of numbers after {(Math.Pow(10, digits) - 1 - int.Parse(headcode[^digits..], CultureInfo.InvariantCulture)) / Math.Max(1, Math.Abs(step)):0} trains.");
        }

        return headcode[..^digits] + text;
    }

    /// <summary>Renumbers the trains in order from <paramref name="first"/>, counting by <paramref name="step"/>; their links go with them.</summary>
    public static BulkResult Renumber(WttDocument document, IReadOnlyList<TrainNode> trains, string first, int step)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(trains);
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        for (var i = 0; i < trains.Count; i++)
        {
            var headcode = NextHeadcode(first, step, i);
            if (headcode == trains[i].Headcode)
            {
                continue;
            }

            edits.Add(TimetableEdit.Renumber(document, trains[i], headcode));
            changes.Add($"{trains[i].Headcode} → {headcode}");
        }

        return Result($"Renumber {trains.Count} train{Plural(trains.Count)} from {first}", edits, changes, []);
    }

    // ---- Category, codes, allowances ----

    /// <summary>Gives the trains another category.</summary>
    public static BulkResult SetCategory(WttDocument document, IReadOnlyList<TrainNode> trains, string categoryId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(trains);
        if (!document.Categories.Any(c => Same(c.Id, categoryId)))
        {
            throw new InvalidOperationException($"There is no category {categoryId}.");
        }

        var moving = trains.Where(t => !Same(t.Category, categoryId)).ToList();
        return Result(
            $"Category {categoryId} for {moving.Count} train{Plural(moving.Count)}",
            [.. moving.Select(t => TimetableEdit.Change(t, n => n.Category = categoryId))],
            [.. moving.Select(t => $"{t.Headcode}: {t.Category} → {categoryId}")],
            []);
    }

    /// <summary>Sets the platform, line or path at <paramref name="location"/> for each train that calls there (empty clears it).</summary>
    public static BulkResult SetAtLocation(IReadOnlyList<TrainNode> trains, string location, TripCode code, string? value)
    {
        ArgumentNullException.ThrowIfNull(trains);
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();
        foreach (var train in trains)
        {
            var trips = train.Trips.Where(t => Same(t.Location, location)).ToList();
            if (trips.Count == 0)
            {
                problems.Add($"{train.Headcode} does not call at {location}.");
            }

            foreach (var trip in trips)
            {
                var old = code switch { TripCode.Platform => trip.Platform, TripCode.Line => trip.Line, _ => trip.Path };
                if (old == value)
                {
                    continue;
                }

                edits.Add(TimetableEdit.Change(trip, t =>
                {
                    switch (code)
                    {
                        case TripCode.Platform: t.Platform = value; break;
                        case TripCode.Line: t.Line = value; break;
                        default: t.Path = value; break;
                    }
                }));
                changes.Add($"{train.Headcode} at {location}: {code.ToString().ToLowerInvariant()} {old ?? "(none)"} → {value ?? "(none)"}");
            }
        }

        return Result($"{code} {value ?? "cleared"} at {location}", edits, changes, problems);
    }

    /// <summary>Sets an allowance (0 clears it) at every trip, or at <paramref name="location"/> only.</summary>
    public static BulkResult SetAllowance(IReadOnlyList<TrainNode> trains, AllowanceKind kind, int seconds, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(trains);
        if (seconds < 0 || seconds % Step != 0)
        {
            throw new InvalidOperationException("An allowance is whole or half minutes.");
        }

        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        foreach (var train in trains)
        {
            var count = 0;
            foreach (var trip in train.Trips.Where(t => location is null || Same(t.Location, location)))
            {
                var old = kind switch { AllowanceKind.Engineering => trip.EngAllowanceSeconds, AllowanceKind.Pathing => trip.PathAllowanceSeconds, _ => trip.PerfAllowanceSeconds };
                if (old == seconds)
                {
                    continue;
                }

                edits.Add(TimetableEdit.Change(trip, t =>
                {
                    switch (kind)
                    {
                        case AllowanceKind.Engineering: t.EngAllowanceSeconds = seconds; break;
                        case AllowanceKind.Pathing: t.PathAllowanceSeconds = seconds; break;
                        default: t.PerfAllowanceSeconds = seconds; break;
                    }
                }));
                count++;
            }

            if (count > 0)
            {
                changes.Add($"{train.Headcode}: {kind.ToString().ToLowerInvariant()} allowance {(seconds == 0 ? "cleared" : Minutes(seconds))} at {count} point{Plural(count)}");
            }
        }

        return Result($"{kind} allowance {(seconds == 0 ? "cleared" : Minutes(seconds))}", edits, changes, []);
    }

    // ---- Find and replace ----

    /// <summary>
    /// Replaces <paramref name="find"/> with <paramref name="replace"/> (whole values, ignoring case) in the chosen
    /// fields of the trains. A headcode or UID is changed by renumbering, so the links follow.
    /// </summary>
    public static BulkResult FindReplace(WttDocument document, IReadOnlyList<TrainNode> trains, string find, string replace, ReplaceIn where)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(trains);
        find = find?.Trim() ?? string.Empty;
        replace = replace?.Trim() ?? string.Empty;
        if (find.Length == 0)
        {
            throw new InvalidOperationException("Type what to find.");
        }

        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        foreach (var train in trains)
        {
            if (where.HasFlag(ReplaceIn.Headcodes) && Same(train.Headcode, find))
            {
                edits.Add(TimetableEdit.Renumber(document, train, replace));
                changes.Add($"{train.Headcode}: headcode → {replace}");
            }

            if (where.HasFlag(ReplaceIn.Uids) && Same(train.Uid, find))
            {
                edits.Add(TimetableEdit.Renumber(document, train, train.Headcode ?? replace, replace));
                changes.Add($"{train.Headcode}: UID {train.Uid} → {replace}");
            }

            if (where.HasFlag(ReplaceIn.Descriptions) && train.Description is { } description && description.Contains(find, StringComparison.OrdinalIgnoreCase))
            {
                var text = description.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
                edits.Add(TimetableEdit.Change(train, t => t.Description = text));
                changes.Add($"{train.Headcode}: description → {text}");
            }

            if (where.HasFlag(ReplaceIn.Codes))
            {
                foreach (var trip in train.Trips)
                {
                    var hits = new[] { ("location", trip.Location), ("platform", trip.Platform), ("line", trip.Line), ("path", trip.Path) }
                        .Where(f => Same(f.Item2, find)).Select(f => f.Item1).ToList();
                    if (hits.Count == 0)
                    {
                        continue;
                    }

                    edits.Add(TimetableEdit.Change(trip, t =>
                    {
                        string? With(string? old) => Same(old, find) ? (replace.Length == 0 ? null : replace) : old;
                        (t.Location, t.Platform, t.Line, t.Path) = (With(t.Location), With(t.Platform), With(t.Line), With(t.Path));
                    }));
                    changes.Add($"{train.Headcode} at {trip.Location}: {string.Join(", ", hits)} {find} → {replace}");
                }
            }
        }

        return Result($"Replace {find} with {replace}", edits, changes, []);
    }

    // ---- Repeat as pattern ----

    /// <summary>
    /// Copies <paramref name="train"/> every <paramref name="everyMinutes"/> minutes up to (and including a start at)
    /// <paramref name="until"/>. The headcode counts on by <paramref name="headcodeStep"/>; a UID counts on the same
    /// way where it ends in a number (else the copies have none, as UIDs must be unique). Activities that name another
    /// train are left off the copies (pair them with <see cref="PairNextWorkings"/>); crew changes are kept and moved.
    /// </summary>
    public static BulkResult RepeatPattern(WttDocument document, TrainNode train, int everyMinutes, SimTime until, int headcodeStep)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(train);
        if (everyMinutes <= 0)
        {
            throw new InvalidOperationException("Repeat every one minute or more.");
        }

        var start = train.DepartTime ?? train.Trips.Select(t => t.ArrTime ?? t.DepPassTime).FirstOrDefault(t => t is not null)
            ?? throw new InvalidOperationException($"{train.Headcode} has no times to repeat from.");
        var container = train.Element.Parent!;
        var at = train.Element.ElementsBeforeSelf().Count() + 1;
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();
        var headcodes = document.Trains.Select(t => t.Headcode).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uids = document.Trains.Select(t => t.Uid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; start + (n * everyMinutes * 60) <= until; n++)
        {
            var shift = n * everyMinutes * 60;
            string headcode;
            try
            {
                headcode = NextHeadcode(train.Headcode ?? string.Empty, headcodeStep, n);
            }
            catch (InvalidOperationException ex)
            {
                problems.Add(ex.Message);
                break;
            }

            if (!headcodes.Add(headcode) && headcodeStep != 0)
            {
                problems.Add($"{headcode} is already in the timetable; the copy at {start + shift} has it too.");
            }

            var copy = DraftDocuments.CopyTrain(train, headcode);
            var node = new TrainNode(document, copy);
            if (train.Uid is { } uid && uid.Length > 0 && char.IsAsciiDigit(uid[^1]))
            {
                var next = NextHeadcode(uid, Math.Max(1, Math.Abs(headcodeStep)), n);
                if (uids.Add(next))
                {
                    node.Uid = next;
                }
            }

            foreach (var activity in node.Trips.SelectMany(t => t.Activities).ToList())
            {
                if (activity.Code is { } code && TimetableParts.NamesATrain((ActivityKind)code))
                {
                    var list = activity.Element.Parent!;
                    activity.Element.Remove();
                    if (!list.HasElements)
                    {
                        list.Remove();
                    }
                }
            }

            ShiftInPlace(node, shift);
            edits.Add(TimetableEdit.Insert(container, copy, at + n - 1, $"Add {headcode}"));
            changes.Add($"{headcode}{(node.Uid is { } u ? $" ({u})" : string.Empty)} at {SimTimeNotation.Format(start + shift)}");
        }

        if (changes.Count == 0)
        {
            return BulkResult.Nothing($"No copy of {train.Headcode} starts by {SimTimeNotation.Format(until)}.");
        }

        return Result($"Repeat {train.Headcode} every {everyMinutes} min ({changes.Count} train{Plural(changes.Count)})", edits, changes, problems);
    }

    /// <summary>
    /// Pairs arrivals at <paramref name="location"/> with departures from it as next workings: each arrival (in time
    /// order) takes the earliest departure not yet taken that leaves at least <paramref name="turnaroundSeconds"/>
    /// after it arrives. Trains that already have a next working are left alone.
    /// </summary>
    public static BulkResult PairNextWorkings(WttDocument document, IReadOnlyList<TrainNode> arrivals, IReadOnlyList<TrainNode> departures, string location, int turnaroundSeconds)
    {
        ArgumentNullException.ThrowIfNull(document);
        var arriving = arrivals
            .Select(t => (Train: t, Trip: t.Trips.LastOrDefault()))
            .Where(x => x.Trip is not null && Same(x.Trip.Location, location) && x.Trip.ArrTime is not null
                && !x.Trip.Activities.Any(a => a.Code == (int)ActivityKind.Next))
            .OrderBy(x => x.Trip!.ArrTime!.Value.Seconds)
            .ToList();
        var leaving = departures
            .Select(t => (Train: t, Time: t.Trips.FirstOrDefault() is { } first && Same(first.Location, location) ? first.DepPassTime : null))
            .Where(x => x.Time is not null)
            .OrderBy(x => x.Time!.Value.Seconds)
            .ToList();
        var taken = new HashSet<XElement>();
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();
        foreach (var (train, trip) in arriving)
        {
            var arrives = trip!.ArrTime!.Value;
            var next = leaving.FirstOrDefault(d => !taken.Contains(d.Train.Element) && d.Train.Element != train.Element && d.Time!.Value - arrives >= turnaroundSeconds);
            if (next.Train is null)
            {
                problems.Add($"{train.Headcode} (arrives {SimTimeNotation.Format(arrives)}): no departure {turnaroundSeconds / 60} min or more after it.");
                continue;
            }

            taken.Add(next.Train.Element);
            edits.Add(TimetableParts.AddActivity(trip, TimetableParts.NewActivity(document, ActivityKind.Next, next.Train)));
            changes.Add($"{train.Headcode} (arr {SimTimeNotation.Format(arrives)}) forms {next.Train.Headcode} (dep {SimTimeNotation.Format(next.Time!.Value)}), {(next.Time.Value - arrives) / 60} min");
        }

        return Result($"Pair next workings at {location}", edits, changes, problems);
    }

    // ---- Auto-fill timings, set line ----

    /// <summary>
    /// Fills the untimed points between a train's timed ones from the sim's run times for its band: each pass time takes
    /// a share of the run in proportion to the links' least runs (equal shares where one is unknown), to the half
    /// minute. Untimed points after the last time are timed on from it by the least runs plus allowances, with the
    /// usual dwell where the point has a platform. Where the given times leave less than the least runs, it says by
    /// how much (the shortfall), and still fills.
    /// </summary>
    public static BulkResult AutoFill(WttDocument document, SimKnowledge knowledge, TrainNode train)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(train);
        var trips = train.Trips.ToList();
        var band = RunBand.Of(train.MaxSpeed ?? CategoryOf(document, train)?.MaxSpeed, train.IsFreight || CategoryOf(document, train)?.IsFreight == true);
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();

        int? Least(int a, int b) =>
            Tiploc.TryCreate(trips[a].Location, out var from) && Tiploc.TryCreate(trips[b].Location, out var to)
                ? knowledge.NearestRunTime(from, to, band)?.Times.LeastSeconds ?? knowledge.LeastRun(knowledge.Chain(from, to) ?? [], band)
                : null;

        SimTime? Leaves(TripNode t) => t.DepPassTime ?? t.ArrTime;

        var last = -1;
        for (var i = 0; i < trips.Count; i++)
        {
            if (Leaves(trips[i]) is null && trips[i].ArrTime is null)
            {
                continue;
            }

            if (last >= 0 && i - last > 1)
            {
                // A gap between two timed points.
                var leaves = Leaves(trips[last])!.Value;
                var reaches = trips[i].ArrTime ?? trips[i].DepPassTime!.Value;
                var weights = Enumerable.Range(last, i - last).Select(j => Least(j, j + 1)).ToList();
                var known = weights.All(w => w is > 0);
                if (known && weights.Sum(w => w!.Value) is var least && least > reaches - leaves)
                {
                    problems.Add($"{train.Headcode}: {trips[last].Location} to {trips[i].Location} has {Minutes(reaches - leaves)}, but the least run is {Minutes(least)} ({Minutes(least - (reaches - leaves))} short).");
                }

                var shares = known ? weights.Select(w => (double)w!.Value).ToList() : weights.Select(_ => 1.0).ToList();
                var total = shares.Sum();
                var sofar = 0.0;
                var previous = leaves.Seconds;
                for (var j = last + 1; j < i; j++)
                {
                    sofar += shares[j - last - 1];
                    var seconds = Math.Clamp(leaves.Seconds + Half((reaches - leaves) * (sofar / total)), previous, reaches.Seconds);
                    previous = seconds;
                    edits.Add(PassAt(trips[j], new SimTime(seconds)));
                    changes.Add($"{train.Headcode}: {trips[j].Location} pass {SimTimeNotation.Format(new SimTime(seconds))}");
                }
            }

            last = i;
        }

        if (last >= 0 && last < trips.Count - 1)
        {
            // On from the last time.
            var time = Leaves(trips[last])!.Value.Seconds;
            for (var j = last + 1; j < trips.Count; j++)
            {
                if (Least(j - 1, j) is not { } run)
                {
                    problems.Add($"{train.Headcode}: no known run from {trips[j - 1].Location} to {trips[j].Location}; filled to there.");
                    break;
                }

                time += Half(run + trips[j].EngAllowanceSeconds + trips[j].PathAllowanceSeconds + trips[j].PerfAllowanceSeconds);
                var arrives = new SimTime(time);
                var final = j == trips.Count - 1;
                if (final || trips[j].Platform is null)
                {
                    edits.Add(final ? ArriveAt(trips[j], arrives) : PassAt(trips[j], arrives));
                    changes.Add($"{train.Headcode}: {trips[j].Location} {(final ? "arrive" : "pass")} {SimTimeNotation.Format(arrives)}");
                    continue;
                }

                var dwell = Tiploc.TryCreate(trips[j].Location, out var at) ? knowledge.NearestDwell(at, band)?.Times.UsualSeconds ?? Step : Step;
                time += Half(Math.Max(Step, dwell));
                var departs = new SimTime(time);
                edits.Add(TimetableEdit.Change(trips[j], t =>
                {
                    t.ArrTime = arrives;
                    t.DepPassTime = departs;
                    t.IsPassTime = false;
                }));
                changes.Add($"{train.Headcode}: {trips[j].Location} {SimTimeNotation.Format(arrives)}–{SimTimeNotation.Format(departs)}");
            }
        }

        if (last < 0)
        {
            return BulkResult.Nothing($"{train.Headcode} has no times to fill from: give it at least its first time.");
        }

        return Result($"Fill {train.Headcode}'s timings", edits, changes, problems);
    }

    /// <summary>
    /// Fills in timing points (#32): between each two of the train's points, inserts every timing location other
    /// timetables of the sim book there (<see cref="SimKnowledge.Finest"/>; where no timetable books the two straight
    /// after each other, the chain they do book, as the "no known path" fix does). Each is a pass, timed between the
    /// two points' times in proportion to the sim's usual run times (equal shares where one is unknown), to the half
    /// minute; untimed where either end has no time. The graph then draws the train through each location, and SimSig
    /// shows them in its timetable.
    /// </summary>
    public static BulkResult FillTimingPoints(WttDocument document, SimKnowledge knowledge, TrainNode train)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(train);
        var trips = train.Trips.ToList();
        var band = RunBand.Of(train.MaxSpeed ?? CategoryOf(document, train)?.MaxSpeed, train.IsFreight || CategoryOf(document, train)?.IsFreight == true);
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();
        var inserted = 0; // the edits apply in order, so each gap's points go after those inserted before it
        for (var i = 1; i < trips.Count; i++)
        {
            var (before, after) = (trips[i - 1], trips[i]);
            if (!Tiploc.TryCreate(before.Location, out var from) || !Tiploc.TryCreate(after.Location, out var to) || from == to)
            {
                continue;
            }

            var chain = knowledge.Finest(from, to, booked: true);
            if (chain.Count == 2 && knowledge.Link(from, to) is null)
            {
                if (knowledge.Chain(from, to) is not { Count: > 2 } path)
                {
                    problems.Add($"{train.Headcode}: no timetable of the sim books a way from {from} to {to}; left as it is.");
                    continue;
                }

                var expanded = new List<Tiploc> { from };
                for (var k = 1; k < path.Count; k++)
                {
                    expanded.AddRange(knowledge.Finest(path[k - 1], path[k], booked: true).Skip(1));
                }

                chain = expanded;
            }

            if (chain.Count <= 2)
            {
                continue;
            }

            var times = PassTimes(knowledge, band, before.DepPassTime ?? before.ArrTime, after.ArrTime ?? after.DepPassTime, chain, out var shortfall);
            if (shortfall > 0)
            {
                problems.Add($"{train.Headcode}: {from} to {to} is booked {Minutes(shortfall).TrimStart('+')} quicker than trains have run through its timing locations, so not every pass time keeps to the least run (shared out in proportion).");
            }

            var parent = before.Element.Parent!;
            var at = before.Element.ElementsBeforeSelf().Count() + 1 + inserted;
            for (var j = 1; j < chain.Count - 1; j++)
            {
                var node = new TripNode(document, new XElement("Trip")) { Location = chain[j].Code };
                if (times?[j - 1] is { } time)
                {
                    node.DepPassTime = time;
                    node.IsPassTime = true;
                }

                node.DownDirection = after.DownDirection;
                edits.Add(TimetableEdit.Insert(parent, node.Element, at + j - 1, $"Add {chain[j]}"));
                changes.Add($"{train.Headcode}: {chain[j]} between {from} and {to}{(times?[j - 1] is { } t ? $", pass {SimTimeNotation.Format(t)}" : ", untimed")}");
            }

            inserted += chain.Count - 2;
        }

        if (edits.Count == 0 && problems.Count == 0)
        {
            return BulkResult.Nothing($"{train.Headcode} already has every timing location other timetables book on its way.");
        }

        return Result($"Fill in {train.Headcode}'s timing points", edits, changes, problems);
    }

    /// <summary>
    /// Pass times for a chain's middle locations, between the times at its ends: each link gets the least run seen for
    /// the band (to the half minute above), and what is left over is shared out in proportion to them, so no link is
    /// booked quicker than a train has run it. Where the ends leave less than the least runs (by
    /// <paramref name="shortfall"/> seconds), or a link's run is unknown, the time is shared out in proportion (equally
    /// where one is unknown). Null without both times.
    /// </summary>
    private static SimTime[]? PassTimes(SimKnowledge knowledge, RunBand band, SimTime? leaves, SimTime? reaches, IReadOnlyList<Tiploc> chain, out int shortfall)
    {
        shortfall = 0;
        if (leaves is not { Seconds: > 0 } start || reaches is not { Seconds: > 0 } end || end.Seconds < start.Seconds)
        {
            return null;
        }

        var least = new int[chain.Count - 1];
        var known = true;
        for (var j = 0; j < least.Length; j++)
        {
            if (knowledge.NearestRunTime(chain[j], chain[j + 1], band)?.Times.LeastSeconds is { } run)
            {
                least[j] = (run + Step - 1) / Step * Step;
            }
            else
            {
                known = false;
            }
        }

        var span = end.Seconds - start.Seconds;
        var segments = new int[least.Length];
        if (known && least.Sum() <= span)
        {
            // The slack in half minutes, shared by largest remainder; any odd seconds go to the last link.
            var slack = (span - least.Sum()) / Step;
            var total = (double)Math.Max(1, least.Sum());
            var shares = least.Select(l => slack * l / total).ToArray();
            var whole = shares.Select(x => (int)Math.Floor(x)).ToArray();
            foreach (var j in Enumerable.Range(0, shares.Length).OrderByDescending(j => shares[j] - whole[j]).ThenBy(j => j).Take(slack - whole.Sum()))
            {
                whole[j]++;
            }

            for (var j = 0; j < segments.Length; j++)
            {
                segments[j] = least[j] + (whole[j] * Step);
            }
        }
        else
        {
            shortfall = known ? least.Sum() - span : 0;
            var weights = known && least.Sum() > 0 ? least.Select(l => (double)l).ToArray() : [.. least.Select(_ => 1.0)];
            var total = weights.Sum();
            var sofar = 0.0;
            var previous = 0;
            for (var j = 0; j < segments.Length; j++)
            {
                sofar += weights[j];
                var reached = Math.Clamp(Half(span * (sofar / total)), previous, span);
                segments[j] = reached - previous;
                previous = reached;
            }
        }

        var times = new SimTime[chain.Count - 2];
        var seconds = start.Seconds;
        for (var j = 0; j < times.Length; j++)
        {
            seconds += segments[j];
            times[j] = new SimTime(seconds);
        }

        return times;
    }

    /// <summary>Sets the line at every trip with none to the line other timetables use most there, where they agree on one.</summary>
    public static BulkResult SetLine(SimKnowledge knowledge, IReadOnlyList<TrainNode> trains)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(trains);
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();
        foreach (var train in trains)
        {
            foreach (var trip in train.Trips.Where(t => t.Line is null && t.Location is not null))
            {
                if (!Tiploc.TryCreate(trip.Location, out var at) || knowledge.Location(at)?.Lines is not { Count: > 0 } lines)
                {
                    continue;
                }

                var ranked = lines.OrderByDescending(l => l.Value).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
                if (ranked.Count > 1 && ranked[1].Value == ranked[0].Value)
                {
                    problems.Add($"{train.Headcode} at {trip.Location}: {ranked[0].Key} and {ranked[1].Key} are used as often; left for you.");
                    continue;
                }

                var line = ranked[0].Key;
                edits.Add(TimetableEdit.Change(trip, t => t.Line = line));
                changes.Add($"{train.Headcode} at {trip.Location}: line {line}");
            }
        }

        return Result("Set lines", edits, changes, problems);
    }

    // ---- Helpers ----

    private static void ShiftInPlace(TrainNode train, int seconds)
    {
        train.DepartTime = train.DepartTime is { } d ? Moved(d, seconds) : null;
        foreach (var trip in train.Trips)
        {
            trip.ArrTime = trip.ArrTime is { } a ? Moved(a, seconds) : null;
            trip.DepPassTime = trip.DepPassTime is { } p ? Moved(p, seconds) : null;
            foreach (var activity in trip.Activities.Where(a => a.CrewChangeManualTime is not null))
            {
                activity.CrewChangeManualTime = Moved(activity.CrewChangeManualTime!.Value, seconds);
            }
        }
    }

    /// <summary>A time moved, never to or before 00:00 (SimSig's no time).</summary>
    private static SimTime Moved(SimTime time, int seconds) =>
        time.Seconds + seconds > 0 ? time + seconds : throw new InvalidOperationException($"{SimTimeNotation.Format(time)} moved by {Minutes(seconds)} would be before the day began.");

    private static TimetableEdit PassAt(TripNode trip, SimTime time) => TimetableEdit.Change(trip, t =>
    {
        t.ArrTime = null;
        t.DepPassTime = time;
        t.IsPassTime = true;
    });

    private static TimetableEdit ArriveAt(TripNode trip, SimTime time) => TimetableEdit.Change(trip, t =>
    {
        t.ArrTime = time;
        t.IsPassTime = false;
    });

    private static CategoryNode? CategoryOf(WttDocument document, TrainNode train) =>
        document.Categories.FirstOrDefault(c => Same(c.Id, train.Category));

    private static int Half(double seconds) => (int)(Math.Round(seconds / Step, MidpointRounding.AwayFromZero) * Step);

    private static BulkResult Result(string description, List<TimetableEdit> edits, List<string> changes, List<string> problems) =>
        new(edits.Count == 0 ? null : TimetableEdit.Composite(description, edits), changes, problems);

    private static string Minutes(int seconds)
    {
        var sign = seconds < 0 ? "−" : seconds > 0 ? "+" : string.Empty;
        var a = Math.Abs(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{a / 60}{(a % 60 >= 30 ? "½" : string.Empty)} min");
    }

    private static string Plural(int n) => n == 1 ? string.Empty : "s";

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
