using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>A manual link of a live working (sim uid and headcode) to a WTT train; null <see cref="Train"/> means "do not match".</summary>
public sealed record LiveLink(string SimUid, Headcode Headcode, TrainId? Train);

/// <summary>Orders train IDs by their text, so a plan's run-anyway set is the same however it was built.</summary>
internal sealed class TrainIdOrder : IComparer<TrainId>
{
    public static TrainIdOrder Instance { get; } = new();

    public int Compare(TrainId x, TrainId y) => string.CompareOrdinal(x.Value, y.Value);
}

/// <summary>
/// What control intends (<c>06-…md</c> §1): the WTT with every re-plan command applied, in order. Immutable and
/// deterministic, so the same timetable and commands always give an equal plan, and undo is "drop the last command and
/// replay the rest". Built by <see cref="WorkingPlanBuilder"/>; published in each <see cref="PlanSnapshot"/>.
/// </summary>
public sealed class WorkingPlan
{
    private static readonly WorkingTimetable NoTimetable = new(new TimetableHeader(string.Empty, string.Empty, string.Empty, string.Empty, null, SimTime.Zero, SimTime.Zero), [], [], []);

    private readonly ImmutableDictionary<TrainId, TrainPlan> _trains;
    private readonly ImmutableList<TrainId> _order;
    private TrainPlan[]? _ordered;

    internal WorkingPlan(
        WorkingTimetable timetable,
        MinRunTable minRuns,
        ImmutableDictionary<TrainId, TrainPlan> trains,
        ImmutableList<TrainId> order,
        ImmutableList<Disruption> disruptions,
        ImmutableList<PlanNote> notes,
        ImmutableList<LiveLink> links,
        ImmutableSortedSet<TrainId> runAnyway)
    {
        Timetable = timetable;
        MinRuns = minRuns;
        _trains = trains;
        _order = order;
        DisruptionList = disruptions;
        NoteList = notes;
        LinkList = links;
        RunAnywaySet = runAnyway;
    }

    /// <summary>A plan with no timetable and no trains.</summary>
    public static WorkingPlan Empty { get; } = FromTimetable(NoTimetable, MinRunTable.Empty);

    public WorkingTimetable Timetable { get; }

    /// <summary>The minimum run times re-plans are clamped to.</summary>
    public MinRunTable MinRuns { get; }

    /// <summary>Every train: the WTT's in file order, then clones in the order made.</summary>
    public IReadOnlyList<TrainPlan> Trains => _ordered ??= _order.Select(id => _trains[id]).ToArray();

    public int Count => _order.Count;

    /// <summary>Trains whose plan differs from the WTT (and every clone).</summary>
    public IEnumerable<TrainPlan> Modified => Trains.Where(t => t.IsModified);

    public IReadOnlyList<Disruption> Disruptions => DisruptionList;

    /// <summary>Notes pinned to the graph (S13), in the order made.</summary>
    public IReadOnlyList<PlanNote> Notes => NoteList;

    /// <summary>Manual live links, in the order made (a later link of the same working replaces the earlier one).</summary>
    public IReadOnlyList<LiveLink> Links => LinkList;

    internal ImmutableDictionary<TrainId, TrainPlan> TrainMap => _trains;

    internal ImmutableList<TrainId> Order => _order;

    internal ImmutableList<Disruption> DisruptionList { get; }

    internal ImmutableList<PlanNote> NoteList { get; }

    internal ImmutableList<LiveLink> LinkList { get; }

    internal ImmutableSortedSet<TrainId> RunAnywaySet { get; }

    /// <summary>
    /// WTT trains the user expects to run although the start rules say SimSig will not (S34, <see cref="ExpectToRun"/>).
    /// Not a change to the train's plan, but kept in the history so it can be undone.
    /// </summary>
    public IReadOnlyCollection<TrainId> RunAnyway => RunAnywaySet;

    public bool RunsAnyway(TrainId train) => RunAnywaySet.Contains(train);

    /// <summary>The plan of the timetable as booked, with no re-plans.</summary>
    public static WorkingPlan FromTimetable(WorkingTimetable timetable, MinRunTable? minRuns = null)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        var trains = ImmutableDictionary.CreateBuilder<TrainId, TrainPlan>();
        foreach (var train in timetable.Trains)
        {
            trains[train.Id] = TrainPlan.FromWtt(train);
        }

        return new WorkingPlan(
            timetable,
            minRuns ?? MinRunTable.Build(timetable),
            trains.ToImmutable(),
            timetable.Trains.Select(t => t.Id).ToImmutableList(),
            [],
            [],
            [],
            ImmutableSortedSet.Create<TrainId>(TrainIdOrder.Instance));
    }

    public TrainPlan? Find(TrainId id) => _trains.GetValueOrDefault(id);

    public TrainPlan Get(TrainId id) => _trains.TryGetValue(id, out var plan) ? plan : throw new KeyNotFoundException($"No train {id} in the plan.");

    public Disruption? FindDisruption(string id) => DisruptionList.Find(d => d.Id == id);

    public PlanNote? FindNote(string id) => NoteList.Find(n => n.Id == id);

    /// <summary>A disruption ID not yet used in this plan: <c>B1</c>, <c>B2</c>… for a block, <c>SR1</c>… for a speed restriction.</summary>
    public string NewDisruptionId(DisruptionKind kind) => NewId(kind == DisruptionKind.Block ? "B" : "SR", id => FindDisruption(id) is not null);

    /// <summary>A note ID not yet used in this plan: <c>N1</c>, <c>N2</c>…</summary>
    public string NewNoteId() => NewId("N", id => FindNote(id) is not null);

    private static string NewId(string prefix, Func<string, bool> used)
    {
        for (var n = 1; ; n++)
        {
            var id = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{prefix}{n}");
            if (!used(id))
            {
                return id;
            }
        }
    }

    /// <summary>The manual link of a live working, if any.</summary>
    public LiveLink? FindLink(string simUid, Headcode headcode) => LinkList.Find(l => l.SimUid == simUid && l.Headcode == headcode);

    /// <summary>The headcode of a train, or its ID if it is not in the plan; for describing commands.</summary>
    public string NameOf(TrainId id) => Find(id)?.Headcode.Value ?? id.Value;

    /// <summary>A train ID not yet used in this plan for a clone with this headcode: <c>{headcode}#c1</c>, <c>#c2</c>…</summary>
    public TrainId NewCloneId(Headcode headcode)
    {
        for (var n = 1; ; n++)
        {
            var id = new TrainId($"{headcode}#c{n}");
            if (!_trains.ContainsKey(id))
            {
                return id;
            }
        }
    }

    /// <summary>Applies one command. On failure the plan is returned unchanged with the error.</summary>
    public (WorkingPlan Plan, PlanCommandResult Result) Apply(IPlanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var builder = new WorkingPlanBuilder(this);
        var result = builder.Run(command);
        return (result.Applied ? builder.Build() : this, result);
    }

    /// <summary>Applies commands in order, skipping (and reporting) any that fail.</summary>
    public (WorkingPlan Plan, IReadOnlyList<PlanCommandResult> Results) ApplyAll(IEnumerable<IPlanCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var builder = new WorkingPlanBuilder(this);
        var results = commands.Select(builder.Run).ToList();
        return (builder.Build(), results);
    }
}
