using System.Globalization;
using System.Xml.Linq;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Editing;

/// <summary>
/// Edits for the parts of a timetable beside its trips (S45, <c>08-…md</c> §7): activities, rules, seed groups,
/// categories and the structured lists. Each is one <see cref="TimetableEdit"/> with its undo; one SimSig would refuse,
/// or that would leave the file inconsistent, throws <see cref="InvalidOperationException"/> saying why.
/// </summary>
public static class TimetableParts
{
    // ---- Naming other trains ----

    /// <summary>
    /// True if the file names trains in activities and rules by UID (any <c>AssociatedUID</c>, <c>TrainUID</c> or
    /// <c>Train2UID</c>); older files use headcodes.
    /// </summary>
    public static bool UsesUids(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Root.Descendants().Any(e => e.Name.LocalName is "AssociatedUID" or "TrainUID" or "Train2UID");
    }

    /// <summary>How a reference to <paramref name="train"/> is written: by UID where the file uses them and the train has one, else by headcode.</summary>
    public static (string? Headcode, string? Uid) NameOf(WttDocument document, TrainNode train)
    {
        ArgumentNullException.ThrowIfNull(train);
        return UsesUids(document) && train.Uid is { } uid ? (null, uid) : (train.Headcode, null);
    }

    // ---- Activities ----

    /// <summary>The short form SimSig's editor and the trip grid use: <c>N</c>, <c>DR</c>, <c>J</c>…</summary>
    public static string Abbreviation(ActivityKind kind) => kind switch
    {
        ActivityKind.Next => "N",
        ActivityKind.DivideRear => "DR",
        ActivityKind.DivideFront => "DF",
        ActivityKind.Join => "J",
        ActivityKind.DetachEngineRear => "DER",
        ActivityKind.DetachEngineFront => "DEF",
        ActivityKind.DropCoachesRear => "DCR",
        ActivityKind.DropCoachesFront => "DCF",
        ActivityKind.JoinedBy => "JB",
        ActivityKind.PlatformShare => "PS",
        ActivityKind.CrewChange => "CC",
        _ => "?",
    };

    /// <summary>The name SimSig gives the activity, e.g. "Divide (new rear)".</summary>
    public static string Name(ActivityKind kind) => kind switch
    {
        ActivityKind.Next => "Next working",
        ActivityKind.DivideRear => "Divide (new rear)",
        ActivityKind.DivideFront => "Divide (new front)",
        ActivityKind.Join => "Join",
        ActivityKind.DetachEngineRear => "Detach engine (rear)",
        ActivityKind.DetachEngineFront => "Detach engine (front)",
        ActivityKind.DropCoachesRear => "Drop coaches (rear)",
        ActivityKind.DropCoachesFront => "Drop coaches (front)",
        ActivityKind.JoinedBy => "Joined by",
        ActivityKind.PlatformShare => "Platform share",
        ActivityKind.CrewChange => "Crew change",
        _ => "Unknown",
    };

    /// <summary>The kinds that name another train (all but a crew change).</summary>
    public static bool NamesATrain(ActivityKind kind) => kind is not (ActivityKind.CrewChange or ActivityKind.Unknown);

    /// <summary>
    /// A new <c>Activity</c> element: <paramref name="kind"/>, the associated train named as the file names trains
    /// (<see cref="NameOf"/>), and for a crew change its time and duration.
    /// </summary>
    public static XElement NewActivity(WttDocument document, ActivityKind kind, TrainNode? associated, SimTime? crewChangeTime = null, int? durationSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (kind == ActivityKind.Unknown)
        {
            throw new InvalidOperationException("Choose a kind of activity.");
        }

        if (NamesATrain(kind) && associated is null)
        {
            throw new InvalidOperationException($"A {Name(kind).ToLowerInvariant()} needs the train it is with.");
        }

        var element = new XElement("Activity");
        var node = new ActivityNode(document, element) { Code = (int)kind };
        if (NamesATrain(kind))
        {
            (node.AssociatedTrain, node.AssociatedUid) = NameOf(document, associated!);
        }
        else
        {
            node.CrewChangeManualTime = crewChangeTime;
        }

        node.ActivityDuration = durationSeconds is > 0 ? durationSeconds : null;
        return element;
    }

    /// <summary>
    /// Adds <paramref name="activity"/> (from <see cref="NewActivity"/>) to a trip at <paramref name="index"/> (the end
    /// if null), making the trip's <c>Activities</c> if it has none.
    /// </summary>
    public static TimetableEdit AddActivity(TripNode trip, XElement activity, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(activity);
        var codes = trip.Activities.Select(a => a.Code).ToList();
        var at = Math.Clamp(index ?? codes.Count, 0, codes.Count);
        codes.Insert(at, new ActivityNode(trip.Document, activity).Code);
        CheckNext(trip, codes);
        var description = $"Add {Describe(trip.Document, activity)} at {trip.Location}";
        if (trip.ActivitiesElement is { } list)
        {
            return TimetableEdit.Insert(list, activity, at, description);
        }

        return TimetableEdit.Insert(trip.Element, new XElement("Activities", activity), description: description);
    }

    /// <summary>Removes an activity, and its trip's <c>Activities</c> with the last one (SimSig writes none then).</summary>
    public static TimetableEdit RemoveActivity(ActivityNode activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var list = activity.Element.Parent ?? throw new InvalidOperationException("The activity is not in a trip.");
        var description = $"Delete {Describe(activity.Document, activity.Element)}";
        return list.Elements("Activity").Count() == 1 && list.Parent is not null
            ? TimetableEdit.Remove(list, description)
            : TimetableEdit.Remove(activity.Element, description);
    }

    /// <summary>Moves an activity to <paramref name="index"/> among its trip's activities.</summary>
    public static TimetableEdit MoveActivity(ActivityNode activity, int index)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var trip = TripOf(activity);
        var nodes = trip.Activities.ToList();
        var from = nodes.FindIndex(a => a.Element == activity.Element);
        var to = Math.Clamp(index, 0, nodes.Count - 1);
        var codes = nodes.Select(a => a.Code).ToList();
        codes.RemoveAt(from);
        codes.Insert(to, activity.Code);
        CheckNext(trip, codes);
        return TimetableEdit.Move(activity.Element, to, $"Move {Describe(activity.Document, activity.Element)} to {to + 1}");
    }

    /// <summary>Changes an activity's fields (kind, train, crew change time, duration), keeping a next working last.</summary>
    public static TimetableEdit ChangeActivity(ActivityNode activity, Action<ActivityNode> change, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(change);
        var trip = TripOf(activity);
        var edit = TimetableEdit.Change(activity, change, description ?? $"Change {Describe(activity.Document, activity.Element)}");
        var copy = new XElement(activity.Element);
        change(new ActivityNode(activity.Document, copy));
        var codes = trip.Activities.Select(a => a.Element == activity.Element ? new ActivityNode(activity.Document, copy).Code : a.Code).ToList();
        CheckNext(trip, codes);
        return edit;
    }

    /// <summary>
    /// A next working must be the last thing a train does: only on its last trip, and with nothing but alternative next
    /// workings after it there (as <c>TrainChecks</c> has it).
    /// </summary>
    private static void CheckNext(TripNode trip, IReadOnlyList<int?> codes)
    {
        var next = codes.ToList().IndexOf((int)ActivityKind.Next);
        if (next < 0)
        {
            return;
        }

        if (trip.Element.ElementsAfterSelf("Trip").Any())
        {
            throw new InvalidOperationException(
                $"A next working goes at the train's last location, not {trip.Location}: the train becomes the next one there and runs no further.");
        }

        if (codes.Skip(next + 1).Any(c => c != (int)ActivityKind.Next))
        {
            throw new InvalidOperationException("A next working must be the last activity at its location: SimSig does nothing with the train after it becomes the next one.");
        }
    }

    /// <summary>
    /// The trains an activity at <paramref name="trip"/> could name, nearest in time first: those booked at the same
    /// location within <paramref name="windowSeconds"/> of the trip (a train formed there, for a forming kind, first),
    /// then every other train by headcode.
    /// </summary>
    public static IReadOnlyList<TrainNode> Candidates(WttDocumentImport import, TripNode trip, ActivityKind kind, int windowSeconds = 3 * 3600)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(trip);
        var owner = trip.Element.Parent?.Parent;
        var at = trip.ArrTime ?? trip.DepPassTime;
        var forms = kind is ActivityKind.Next or ActivityKind.DivideFront or ActivityKind.DivideRear or ActivityKind.DetachEngineFront
            or ActivityKind.DetachEngineRear or ActivityKind.DropCoachesFront or ActivityKind.DropCoachesRear;
        var near = new List<(TrainNode Node, int Rank, int Distance)>();
        var rest = new List<TrainNode>();
        foreach (var node in import.Document.Trains)
        {
            if (node.Element == owner)
            {
                continue;
            }

            var train = import.TrainOf(node);
            var here = train?.Points.Where(p => string.Equals(p.Location.Code, trip.Location, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
            int? distance = null;
            var startsHere = false;
            foreach (var point in here)
            {
                if (point.FirstTime is { } time && at is { } t && Math.Abs(time - t) is var d && d <= windowSeconds && (distance is null || d < distance))
                {
                    distance = d;
                    startsHere = point.Index == 0;
                }
            }

            if (distance is { } found)
            {
                near.Add((node, forms && startsHere ? 0 : 1, found));
            }
            else
            {
                rest.Add(node);
            }
        }

        return [.. near.OrderBy(n => n.Rank).ThenBy(n => n.Distance).Select(n => n.Node),
            .. rest.OrderBy(n => n.Headcode, StringComparer.Ordinal)];
    }

    private static TripNode TripOf(ActivityNode activity) =>
        activity.Element.Parent?.Parent is { Name.LocalName: "Trip" } trip
            ? new TripNode(activity.Document, trip)
            : throw new InvalidOperationException("The activity is not in a trip.");

    private static string Describe(WttDocument document, XElement activity)
    {
        var node = new ActivityNode(document, activity);
        var kind = node.Code is { } code && Enum.IsDefined((ActivityKind)code) ? (ActivityKind)code : ActivityKind.Unknown;
        var with = node.AssociatedTrain ?? node.AssociatedUid;
        return with is null ? Name(kind).ToLowerInvariant() : $"{Abbreviation(kind)}: {with}";
    }

    // ---- Rules ----

    /// <summary>The rule as SimSig's editor words it, with X and Y.</summary>
    public static string Wording(RuleKind kind) => kind switch
    {
        RuleKind.XAppearsAfterYEnters => "X appears after Y enters",
        RuleKind.XAppearsAfterYLeaves => "X appears after Y leaves",
        RuleKind.XAppearsAfterYArrives => "X appears after Y arrives at Z",
        RuleKind.XNotIfY => "X does not run if Y runs",
        RuleKind.XDepartsAfterYArrives => "X departs Z after Y arrives at Z",
        RuleKind.XDepartsAfterYEnters => "X departs Z after Y enters",
        RuleKind.XDepartsAfterYLeaves => "X departs Z after Y leaves",
        RuleKind.XDepartsAfterYJoins => "X departs Z after Y joins",
        RuleKind.XDepartsAfterYDivides => "X departs Z after Y divides",
        RuleKind.XDepartsAfterYForms => "X departs Z after Y forms",
        RuleKind.MutuallyExclusive => "X and Y are mutually exclusive",
        RuleKind.XAppearsAfterYJoins => "X appears after Y joins",
        RuleKind.XAppearsAfterYDivides => "X appears after Y divides",
        RuleKind.XAppearsAfterYForms => "X appears after Y forms",
        RuleKind.Alternatives => "X and Y are alternatives",
        _ => "Unknown rule",
    };

    /// <summary>True if the rule names a location Z.</summary>
    public static bool HasLocation(RuleKind kind) => kind is RuleKind.XAppearsAfterYArrives or RuleKind.XDepartsAfterYArrives
        or RuleKind.XDepartsAfterYEnters or RuleKind.XDepartsAfterYLeaves or RuleKind.XDepartsAfterYJoins
        or RuleKind.XDepartsAfterYDivides or RuleKind.XDepartsAfterYForms;

    /// <summary>A new rule, its trains named as the file names them, added after the others.</summary>
    public static TimetableEdit AddRule(WttDocument document, RuleKind kind, TrainNode x, TrainNode y, string? location = null, int seconds = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (kind == RuleKind.Unknown)
        {
            throw new InvalidOperationException("Choose a kind of rule.");
        }

        if (x.Element == y.Element)
        {
            throw new InvalidOperationException("A rule names two different trains.");
        }

        if (HasLocation(kind) && string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidOperationException($"'{Wording(kind)}' needs the location Z.");
        }

        var element = new XElement("TimetableRule");
        var rule = new RuleNode(document, element) { Code = (int)kind, Time = seconds > 0 ? seconds : null };
        (rule.Train, rule.TrainUid) = NameOf(document, x);
        (rule.Train2, rule.Train2Uid) = NameOf(document, y);
        rule.Location = HasLocation(kind) ? location!.Trim().ToUpperInvariant() : null;
        var description = $"Rule: {Wording(kind).Replace("X", x.Headcode, StringComparison.Ordinal).Replace("Y", y.Headcode, StringComparison.Ordinal)}";
        if (document.Root.Element("TimetableRules") is { } rules)
        {
            return TimetableEdit.Insert(rules, element, description: description);
        }

        return TimetableEdit.Insert(document.Root, new XElement("TimetableRules", element), description: description);
    }

    /// <summary>Removes a rule (and the empty <c>TimetableRules</c> with the last one).</summary>
    public static TimetableEdit RemoveRule(RuleNode rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var list = rule.Element.Parent ?? throw new InvalidOperationException("The rule is not in the timetable.");
        return list.Elements("TimetableRule").Count() == 1
            ? TimetableEdit.Remove(list, "Delete rule")
            : TimetableEdit.Remove(rule.Element, "Delete rule");
    }

    /// <summary>Sets a rule's train X or Y, named as the file names trains.</summary>
    public static TimetableEdit SetRuleTrain(RuleNode rule, bool second, TrainNode train)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(train);
        var (headcode, uid) = NameOf(rule.Document, train);
        return TimetableEdit.Change(rule, r =>
        {
            if (second)
            {
                (r.Train2, r.Train2Uid) = (headcode, uid);
            }
            else
            {
                (r.Train, r.TrainUid) = (headcode, uid);
            }
        }, $"Rule {(second ? "Y" : "X")}: {train.Headcode}");
    }

    // ---- Seed groups ----

    /// <summary>The trains seeded in a group (by its ID).</summary>
    public static IEnumerable<TrainNode> TrainsIn(WttDocument document, SeedGroupNode group)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(group);
        return document.Trains.Where(t => t.SeedGroup is { } id && string.Equals(id, group.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Trains seeded with no group: in the sim at the timetable's start.</summary>
    public static IEnumerable<TrainNode> SeededAtStart(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var groups = document.SeedGroups.Select(g => g.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return document.Trains.Where(t => t.SeedPoint is not null && (t.SeedGroup is not { } id || !groups.Contains(id)));
    }

    /// <summary>
    /// Seed points two or more trains of one group (or of the start, <paramref name="group"/> null) use: SimSig puts
    /// one train at a signal.
    /// </summary>
    public static IReadOnlyList<(string SeedPoint, IReadOnlyList<TrainNode> Trains)> SeedClashes(WttDocument document, SeedGroupNode? group)
    {
        var trains = group is null ? SeededAtStart(document) : TrainsIn(document, group);
        return [.. trains.Where(t => t.SeedPoint is not null)
            .GroupBy(t => t.SeedPoint!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<TrainNode>)[.. g]))];
    }

    /// <summary>A new seed group with a new ID in SimSig's form (a braced GUID), added after the others.</summary>
    public static TimetableEdit AddSeedGroup(WttDocument document, string title, SimTime start, Guid? id = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("A seed group needs a title.");
        }

        var element = new XElement("SeedGroup");
        _ = new SeedGroupNode(document, element)
        {
            Id = (id ?? Guid.NewGuid()).ToString("B").ToUpperInvariant(),
            StartTime = start,
            Title = title.Trim(),
        };
        var insert = document.Root.Element("SeedGroups") is { } groups
            ? TimetableEdit.Insert(groups, element)
            : TimetableEdit.Insert(document.Root, new XElement("SeedGroups", element));
        return WithSummary(document, $"Add seed group {title.Trim()}", [insert], g => g.Append(element));
    }

    /// <summary>A seed group's title or start time changed.</summary>
    public static TimetableEdit ChangeSeedGroup(SeedGroupNode group, string? title, SimTime? start)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (title is not null && string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("A seed group needs a title.");
        }

        var change = TimetableEdit.Change(group, g =>
        {
            g.Title = title?.Trim() ?? g.Title;
            g.StartTime = start ?? g.StartTime;
        });
        var copy = new XElement(group.Element);
        var copyNode = new SeedGroupNode(group.Document, copy) { Title = title?.Trim() ?? group.Title, StartTime = start ?? group.StartTime };
        return WithSummary(group.Document, $"Seed group {copyNode.Title}", [change], g => g.Select(e => e == group.Element ? copy : e));
    }

    /// <summary>
    /// Deletes a seed group. Its trains are seeded at the timetable's start instead (their group cleared): the caller
    /// asks first, naming <see cref="TrainsIn"/>.
    /// </summary>
    public static TimetableEdit RemoveSeedGroup(SeedGroupNode group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var document = group.Document;
        var edits = TrainsIn(document, group).Select(t => TimetableEdit.Change(t, n => n.SeedGroup = null)).ToList();
        var list = group.Element.Parent!;
        edits.Add(list.Elements("SeedGroup").Count() == 1 ? TimetableEdit.Remove(list) : TimetableEdit.Remove(group.Element));
        return WithSummary(document, $"Delete seed group {group.Title}", edits, g => g.Where(e => e != group.Element));
    }

    /// <summary>The header's <c>SeedGroupSummary</c> as SimSig writes it: <c>{ID},"HH:MM Title"</c> for each group, comma-separated.</summary>
    public static string? Summary(WttDocument document, IEnumerable<XElement> groups)
    {
        var parts = groups.Select(e => new SeedGroupNode(document, e))
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Id},\"{Clock(g.StartTime)} {g.Title}\""))
            .ToList();
        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static string Clock(SimTime? time) =>
        time is { } t ? string.Create(CultureInfo.InvariantCulture, $"{t.Seconds / 3600 % 24:00}:{t.Seconds / 60 % 60:00}") : "00:00";

    /// <summary>The edits, then the header's summary set from the groups as they will be.</summary>
    private static TimetableEdit WithSummary(WttDocument document, string description, List<TimetableEdit> edits, Func<IEnumerable<XElement>, IEnumerable<XElement>> after)
    {
        var groups = after(document.Root.Element("SeedGroups")?.Elements("SeedGroup") ?? []).ToList();
        var summary = Summary(document, groups);
        var existing = document.Root.Element("SeedGroupSummary");
        if (existing is null ? summary is not null : existing.Value != (summary ?? string.Empty))
        {
            // A file that has the element keeps it, empty with no groups, as SimSig writes it.
            edits.Add(TimetableEdit.SetField(document.Root, "SeedGroupSummary", existing is null ? summary : summary ?? string.Empty));
        }

        return TimetableEdit.Composite(description, edits);
    }

    // ---- Categories ----

    /// <summary>The trains of a category, by its ID: their own category, or a new one at a trip.</summary>
    public static IEnumerable<TrainNode> TrainsOf(WttDocument document, string categoryId)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Trains.Where(t => Same(t.Category, categoryId) || t.Trips.Any(p => Same(p.NewCategory, categoryId)));
    }

    /// <summary>
    /// A new category with a new ID (eight hex digits, as SimSig makes them), copied from <paramref name="from"/> if
    /// given, else SimSig's defaults (a 75 mph passenger train, 100 m, all dwell times 0 but the standard ones).
    /// </summary>
    public static TimetableEdit AddCategory(WttDocument document, string description, CategoryNode? from = null, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException("A category needs a description.");
        }

        id ??= NewCategoryId(document);
        if (document.Categories.Any(c => Same(c.Id, id)))
        {
            throw new InvalidOperationException($"There is already a category {id}.");
        }

        var element = from is null ? DefaultCategory() : new XElement(from.Element);
        _ = new CategoryNode(document, element) { Id = id, Description = description.Trim() };
        var edit = document.Root.Element("TrainCategories") is { } list
            ? TimetableEdit.Insert(list, element, description: $"Add category {description.Trim()}")
            : TimetableEdit.Insert(document.Root, new XElement("TrainCategories", element), description: $"Add category {description.Trim()}");
        return edit;
    }

    /// <summary>
    /// Gives a category a new ID, and every train and trip that uses it the new one with it (one edit).
    /// </summary>
    public static TimetableEdit RenameCategory(CategoryNode category, string id)
    {
        ArgumentNullException.ThrowIfNull(category);
        var document = category.Document;
        id = id?.Trim() ?? string.Empty;
        if (id.Length == 0 || id.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("A category ID is one word, such as 5674CA92.");
        }

        if (document.Categories.Any(c => c.Element != category.Element && Same(c.Id, id)))
        {
            throw new InvalidOperationException($"There is already a category {id}.");
        }

        var old = category.Id ?? string.Empty;
        var edits = new List<TimetableEdit> { TimetableEdit.Change(category, c => c.Id = id) };
        edits.AddRange(Repoint(document, old, id));
        return TimetableEdit.Composite($"Category {old} is now {id}", edits);
    }

    /// <summary>
    /// Deletes a category. Refused while trains use it, unless <paramref name="moveTo"/> names the category they move
    /// to (in the same edit).
    /// </summary>
    public static TimetableEdit RemoveCategory(CategoryNode category, CategoryNode? moveTo = null)
    {
        ArgumentNullException.ThrowIfNull(category);
        var document = category.Document;
        var id = category.Id ?? string.Empty;
        var users = TrainsOf(document, id).ToList();
        if (users.Count > 0 && moveTo is null)
        {
            throw new InvalidOperationException(
                $"{users.Count} train{(users.Count == 1 ? " uses" : "s use")} {category.Description ?? id} ({string.Join(", ", users.Take(5).Select(t => t.Headcode))}{(users.Count > 5 ? ", …" : "")}): move them to another category first.");
        }

        if (moveTo is not null && moveTo.Element == category.Element)
        {
            throw new InvalidOperationException("Move its trains to a different category.");
        }

        var edits = moveTo is null ? [] : Repoint(document, id, moveTo.Id ?? string.Empty).ToList();
        var list = category.Element.Parent!;
        edits.Add(list.Elements("TrainCategory").Count() == 1 ? TimetableEdit.Remove(list) : TimetableEdit.Remove(category.Element));
        return TimetableEdit.Composite(
            moveTo is null ? $"Delete category {category.Description ?? id}" : $"Delete category {category.Description ?? id}, its trains to {moveTo.Description ?? moveTo.Id}",
            edits);
    }

    /// <summary>
    /// The speed class (<c>SpeedClass</c>) as its set bits, 1 to 32. Each sim names its own classes (EPS-D, EPS-E…),
    /// which the file does not hold, so the editor shows them by number.
    /// </summary>
    public static IReadOnlyList<int> SpeedClassBits(int? speedClass) =>
        [.. Enumerable.Range(1, 32).Where(b => ((uint)(speedClass ?? 0) & (1u << (b - 1))) != 0)];

    /// <summary>The speed class with bit <paramref name="bit"/> (1 to 32) set or cleared.</summary>
    public static int WithSpeedClassBit(int? speedClass, int bit, bool set)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bit, 32);
        var mask = 1u << (bit - 1);
        var value = (uint)(speedClass ?? 0);
        return unchecked((int)(set ? value | mask : value & ~mask));
    }

    private static IEnumerable<TimetableEdit> Repoint(WttDocument document, string from, string to)
    {
        foreach (var train in document.Trains)
        {
            if (Same(train.Category, from))
            {
                yield return TimetableEdit.Change(train, t => t.Category = to);
            }

            foreach (var trip in train.Trips.Where(p => Same(p.NewCategory, from)))
            {
                yield return TimetableEdit.Change(trip, p => p.NewCategory = to);
            }
        }
    }

    private static string NewCategoryId(WttDocument document)
    {
        var taken = document.Categories.Select(c => c.Id).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var seed = taken.Count; ; seed++)
        {
            var id = ((uint)HashCode.Combine(seed, document.Header.Name, "category")).ToString("X8", CultureInfo.InvariantCulture);
            if (!taken.Contains(id))
            {
                return id;
            }
        }
    }

    private static XElement DefaultCategory() => new(
        "TrainCategory",
        new XElement("Description", string.Empty),
        new XElement("AccelBrakeIndex", 2),
        new XElement("IsFreight", 0),
        new XElement("CanUseGoodsLines", 0),
        new XElement("MaxSpeed", 75),
        new XElement("TrainLength", 100),
        new XElement("SpeedClass", 0),
        new XElement("PowerToWeightCategory", 0),
        new XElement(
            "DwellTimes",
            new XElement("RedSignalMoveOff", 0),
            new XElement("StationForward", 45),
            new XElement("StationReverse", 180),
            new XElement("TerminateForward", 180),
            new XElement("TerminateReverse", 180),
            new XElement("Join", 240),
            new XElement("Divide", 240),
            new XElement("CrewChange", 0)),
        new XElement("Electrification", string.Empty),
        new XElement("CautionSpeedSet", string.Empty));

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- Lists: Decisions, Class of Service, Driving ----

    /// <summary>Adds an item to a list (<c>Decisions</c>, <c>TTClassOfServiceRules</c>, <c>CautionSpeedSets</c>), making the list if the file has none.</summary>
    public static TimetableEdit AddListItem(WttDocument document, string list, XElement item, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(item);
        description ??= $"Add {item.Name.LocalName}";
        return document.Root.Element(list) is { } existing
            ? TimetableEdit.Insert(existing, item, description: description)
            : TimetableEdit.Insert(document.Root, new XElement(list, item), description: description);
    }

    /// <summary>Removes an item from its list (a list left empty stays, as SimSig writes an empty one).</summary>
    public static TimetableEdit RemoveListItem(ElementNode item, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return TimetableEdit.Remove(item.Element, description ?? $"Delete {item.Element.Name.LocalName}");
    }

    /// <summary>Sets one field of a list item (or its <c>ID</c> attribute, as <c>@ID</c>).</summary>
    public static TimetableEdit SetItemField(ElementNode item, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrEmpty(field);
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return field.StartsWith('@')
            ? TimetableEdit.SetAttribute(item.Element, field[1..], value, $"{item.Element.Name.LocalName} {field[1..]}")
            : TimetableEdit.Change(item, i => i.SetText(field, value), $"{item.Element.Name.LocalName} {field}");
    }

    /// <summary>A new decision with two choices, as SimSig makes one: the first choice chosen.</summary>
    public static XElement NewDecision(string id, string description, params string[] choices)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("A decision needs an ID.");
        }

        return new XElement(
            "Decision",
            new XAttribute("ID", id.Trim()),
            new XElement("Chosen", -1),
            new XElement("Description", description?.Trim() ?? string.Empty),
            new XElement("Choices", (choices.Length == 0 ? ["A", "B"] : choices).Select((c, i) => NewChoice(c, i == 0))));
    }

    /// <summary>A decision's choice.</summary>
    public static XElement NewChoice(string id, bool chosen = false)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("A choice needs an ID.");
        }

        var choice = new XElement("Choice", new XAttribute("ID", id.Trim()));
        if (chosen)
        {
            choice.Add(new XElement("Chosen", -1));
        }

        return choice;
    }

    /// <summary>Adds a choice to a decision.</summary>
    public static TimetableEdit AddChoice(ElementNode decision, string id)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var choices = decision.Element.Element("Choices");
        if (choices?.Elements("Choice").Any(c => string.Equals((string?)c.Attribute("ID"), id?.Trim(), StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new InvalidOperationException($"The decision already has a choice {id}.");
        }

        var choice = NewChoice(id);
        return choices is null
            ? TimetableEdit.Insert(decision.Element, new XElement("Choices", choice), description: $"Add choice {id}")
            : TimetableEdit.Insert(choices, choice, description: $"Add choice {id}");
    }

    // ---- Scenario options ----

    /// <summary>The header's <c>ScenarioOptions</c> as (option, value) pairs: SimSig writes <c>NERA,2,NENETEL,2</c>.</summary>
    public static IReadOnlyList<(string Option, string Value)> ScenarioOptions(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var parts = (document.Header.ScenarioOptions ?? string.Empty).Split(',');
        var pairs = new List<(string, string)>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            pairs.Add((parts[i].Trim(), parts[i + 1].Trim()));
        }

        return pairs;
    }

    /// <summary>Sets the scenario options from pairs (an empty option is left out).</summary>
    public static TimetableEdit SetScenarioOptions(WttDocument document, IEnumerable<(string Option, string Value)> options)
    {
        ArgumentNullException.ThrowIfNull(document);
        var pairs = options.Where(o => !string.IsNullOrWhiteSpace(o.Option)).ToList();
        if (pairs.Any(o => o.Option.Contains(',', StringComparison.Ordinal) || o.Value.Contains(',', StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Scenario options and values cannot hold a comma.");
        }

        var text = string.Join(",", pairs.Select(o => $"{o.Option.Trim()},{o.Value.Trim()}"));
        var keep = document.Root.Element("ScenarioOptions") is not null; // SimSig writes it empty when there are none
        return TimetableEdit.SetField(document.Root, "ScenarioOptions", text.Length == 0 && !keep ? null : text, "Scenario options");
    }
}
