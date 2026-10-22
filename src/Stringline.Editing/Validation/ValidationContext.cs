using System.Xml.Linq;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Knowledge;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Validation;

/// <summary>Whether a train runs: it enters or is seeded, or a train that runs forms it.</summary>
internal enum RunState
{
    Runs,

    /// <summary>No entry or seed point, and nothing forms it.</summary>
    NeverUsed,

    /// <summary>Formed by a train that never runs.</summary>
    NeverRuns,
}

/// <summary>A forming activity: <see cref="By"/> forms the associated train at its point.</summary>
internal sealed record Former(Train By, TimingPoint Point, int ActivityIndex)
{
    public TrainActivity Activity => Point.Activities[ActivityIndex];
}

/// <summary>What the checks of one revision share: the import, the trains' elements, and facts across trains.</summary>
internal sealed class ValidationContext
{
    private readonly Dictionary<XElement, Train> _trainByElement = [];
    private readonly Dictionary<TrainId, XElement> _elementByTrain = [];
    private readonly Dictionary<TrainId, RunState> _runs = [];
    private readonly Dictionary<(Tiploc, Tiploc), SkipChain> _skipChains = [];
    private readonly Dictionary<TrainId, Presence> _presence = [];
    private readonly HashSet<(TrainId, TrainId)> _exclusive = [];
    private readonly Dictionary<TrainId, List<(string Decision, ChoiceSet Choice)>> _ruleConditions = [];
    private readonly List<(string Start, SimTime Time)> _starts;

    public ValidationContext(WttDocumentImport import, SimKnowledge? knowledge)
    {
        Import = import;
        Knowledge = knowledge;
        Document = import.Document;
        TrainElements = [.. Document.Root.Elements("Timetables").SelectMany(c => c.Elements("Timetable"))];
        foreach (var element in TrainElements)
        {
            if (import.TrainOf(new TrainNode(Document, element)) is { } train)
            {
                _trainByElement[element] = train;
                _elementByTrain[train.Id] = element;
            }
        }

        var links = new Dictionary<XElement, HashSet<XElement>>();
        void Link(TrainId a, TrainId b)
        {
            if (_elementByTrain.TryGetValue(a, out var x) && _elementByTrain.TryGetValue(b, out var y) && x != y)
            {
                (links.TryGetValue(x, out var xs) ? xs : links[x] = []).Add(y);
                (links.TryGetValue(y, out var ys) ? ys : links[y] = []).Add(x);
            }
        }

        var edges = new HashSet<Tiploc>();
        foreach (var train in Timetable.Trains)
        {
            if (train.Points[^1].IsPass)
            {
                edges.Add(train.Points[^1].Location);
            }

            if (train.Start == TrainStart.EntryPoint)
            {
                edges.Add(train.Points[0].Location);
            }

            if (train.FormedFrom is { } from)
            {
                Link(train.Id, from);
            }

            foreach (var point in train.Points)
            {
                for (var a = 0; a < point.Activities.Count; a++)
                {
                    var activity = point.Activities[a];
                    if (activity.Train is not { } other)
                    {
                        continue;
                    }

                    Link(train.Id, other);
                    if (activity.FormsAssociatedTrain)
                    {
                        (Formers.TryGetValue(other, out var list) ? list : Formers[other] = []).Add(new Former(train, point, a));
                    }
                }
            }

            if (train.Start == TrainStart.Seeded && train.SeedPoint is { } seed)
            {
                var key = (seed.Code.ToUpperInvariant(), train.SeedGroup?.Trim().ToUpperInvariant() ?? string.Empty);
                if (Seeded.TryGetValue(key, out var mates))
                {
                    Link(train.Id, mates[0].Id);
                    mates.Add(train);
                }
                else
                {
                    Seeded[key] = [train];
                }
            }
        }

        // Rules that keep two trains out of one game: directly, or as conditions on whether another train runs.
        foreach (var rule in Timetable.Rules)
        {
            if (rule is not { Train: { } x, Train2: { } y } || x == y)
            {
                continue;
            }

            if (rule.Kind is RuleKind.MutuallyExclusive or RuleKind.Alternatives)
            {
                _exclusive.Add((x, y));
                _exclusive.Add((y, x));
            }
            else if (rule.Kind != RuleKind.Unknown)
            {
                var runs = new ChoiceSet(new HashSet<string>(StringComparer.Ordinal) { "RUNS" }, rule.Kind == RuleKind.XNotIfY);
                (_ruleConditions.TryGetValue(x, out var list) ? list : _ruleConditions[x] = []).Add(("RULE:" + y.Value.ToUpperInvariant(), runs));
            }
        }

        _starts = [(string.Empty, Timetable.Header.StartTime), .. Timetable.SeedGroups.Select(g => (g.Id.Trim().ToUpperInvariant(), g.StartTime))];
        AreaEdges = edges;
        Links = links.ToDictionary(l => l.Key, l => (IReadOnlySet<XElement>)l.Value);
        foreach (var chain in knowledge?.SkipChains ?? [])
        {
            _skipChains.TryAdd((chain.Chain[0], chain.Chain[^1]), chain);
        }
    }

    public WttDocumentImport Import { get; }

    public WttDocument Document { get; }

    public WorkingTimetable Timetable => Import.Timetable;

    /// <summary>The sim's knowledge, if usable for this timetable.</summary>
    public SimKnowledge? Knowledge { get; }

    /// <summary>Every train element in file order, imported or not.</summary>
    public IReadOnlyList<XElement> TrainElements { get; }

    /// <summary>The forming activities (next working, divide, detach, drop) that name each train, in file order.</summary>
    public Dictionary<TrainId, List<Former>> Formers { get; } = [];

    /// <summary>Seeded trains by seed signal and seed group (upper case), in file order.</summary>
    public Dictionary<(string Signal, string Group), List<Train>> Seeded { get; } = [];

    public IReadOnlyDictionary<XElement, IReadOnlySet<XElement>> Links { get; }

    /// <summary>
    /// Where the timetable's own trains show the area ends: some train's last point is a pass there (it leaves the
    /// area), or an entering train's first point is there. A train that stops there last is taken to leave too.
    /// </summary>
    public IReadOnlySet<Tiploc> AreaEdges { get; }

    public Train? TrainOf(XElement element) => _trainByElement.GetValueOrDefault(element);

    public XElement? ElementOf(TrainId id) => _elementByTrain.GetValueOrDefault(id);

    public Train? Find(TrainId? id) => id is { } t ? Timetable.Find(t) : null;

    public TripNode TripOf(Train train, int pointIndex) => Import.TripOf(train, pointIndex);

    /// <summary>The skip chain with these ends, either way round, as booked from <paramref name="from"/>.</summary>
    public IReadOnlyList<Tiploc>? SkipChain(Tiploc from, Tiploc to) =>
        _skipChains.TryGetValue((from, to), out var chain) ? chain.Chain
        : _skipChains.TryGetValue((to, from), out var back) ? [.. back.Chain.Reverse()]
        : null;

    /// <summary>
    /// When the train can be in a game: a seeded train in its seed group's start only; an entering train in the starts
    /// before it enters; a formed train when one of its formers is (with the forming activity's choice); and its own
    /// entry decision's choice.
    /// </summary>
    public Presence PresenceOf(Train train)
    {
        if (_presence.TryGetValue(train.Id, out var known))
        {
            return known;
        }

        _presence[train.Id] = Presence.Always; // a loop of formations ends here
        var presence = Presence.Always;
        switch (train.Start)
        {
            case TrainStart.Seeded:
                presence = new Presence(new HashSet<string>(StringComparer.Ordinal) { train.SeedGroup?.Trim().ToUpperInvariant() ?? string.Empty }, presence.Choices);
                break;
            case TrainStart.EntryPoint when train.EntryTime is { } entry:
                var starts = _starts.Where(s => s.Time <= entry).Select(s => s.Start).ToHashSet(StringComparer.Ordinal);
                presence = new Presence(starts.Count > 0 ? starts : new HashSet<string>(StringComparer.Ordinal) { string.Empty }, presence.Choices);
                break;
            case TrainStart.AtFirstLocation when Formers.TryGetValue(train.Id, out var formers):
                presence = formers.Select(PresenceOf).Aggregate((a, b) => a.Or(b));
                break;
        }

        if (ElementOf(train.Id) is { } element)
        {
            var node = new TrainNode(Document, element);
            presence = presence.With(node.GetText("EntryDecision"), node.GetText("EntryChoice"));
        }

        foreach (var (decision, choice) in _ruleConditions.GetValueOrDefault(train.Id) ?? [])
        {
            presence = presence.With(decision, choice);
        }

        return _presence[train.Id] = presence;
    }

    /// <summary>When a forming activity happens: when its train is there, and its own decision's choice is made.</summary>
    public Presence PresenceOf(Former former) => PresenceOf(former.By, former.Point.Index, former.ActivityIndex);

    /// <summary>When a train's activity happens.</summary>
    public Presence PresenceOf(Train train, int point, int activity)
    {
        var node = Import.ActivityOf(train, point, activity);
        return PresenceOf(train).With(node.Decision, node.Choice);
    }

    /// <summary>Whether two trains (or activities, by their presence) can be in one game.</summary>
    public bool Coexist(Train a, Presence inA, Train b, Presence inB) => !_exclusive.Contains((a.Id, b.Id)) && inA.Overlaps(inB);

    /// <summary>Whether the train runs; a loop of formations never does.</summary>
    public RunState RunStateOf(Train train)
    {
        if (_runs.TryGetValue(train.Id, out var known))
        {
            return known;
        }

        if (train.Start is TrainStart.EntryPoint or TrainStart.Seeded)
        {
            return _runs[train.Id] = RunState.Runs;
        }

        if (Find(train.FormedFrom) is not { } former)
        {
            return _runs[train.Id] = RunState.NeverUsed;
        }

        _runs[train.Id] = RunState.NeverRuns; // until shown otherwise, so a loop ends here
        return _runs[train.Id] = RunStateOf(former) == RunState.Runs ? RunState.Runs : RunState.NeverRuns;
    }

    /// <summary>How much the knowledge saw, for "never seen" messages.</summary>
    public string Evidence
    {
        get
        {
            var k = Knowledge!;
            var play = k.Plays > 0 ? $" and {k.Plays} recording{(k.Plays == 1 ? string.Empty : "s")}" : string.Empty;
            return $"never seen in {k.Timetables} timetable{(k.Timetables == 1 ? string.Empty : "s")} ({k.Trains} trains){play}";
        }
    }

    public ValidationIssue Issue(string code, Train? train, int? point, string message, QuickFix? fix = null, XElement? element = null)
    {
        var entry = IssueCatalogue.Of(code);
        var trainElement = train is null ? null : ElementOf(train.Id);
        return new ValidationIssue(entry.Severity, code, train?.Id, point, message, entry.Hint, fix)
        {
            TrainElement = trainElement,
            Headcode = train?.Headcode.Value,
            Element = element ?? (train is not null && point is { } p ? TripOf(train, p).Element : trainElement),
        };
    }
}
