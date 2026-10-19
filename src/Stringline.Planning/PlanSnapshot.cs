using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Timetable.Live;

namespace Stringline.Planning;

/// <summary>
/// An immutable picture published by <see cref="PlanningEngine"/> for the UI to read without locks: the timetable in
/// use, every live train, the clock and match counts, and the working plan with its edit history.
/// </summary>
/// <param name="Sequence">Goes up by one with every snapshot published.</param>
/// <param name="Timetable">The WTT in use, or null if none is loaded yet (every working is then unmatched).</param>
/// <param name="Trains">Live trains in the order first seen.</param>
/// <param name="StateVersion"><see cref="LiveState.Version"/> when taken; a reader can skip work if it has not changed.</param>
/// <param name="SessionStart">The sim time of the first clock or movement this session.</param>
/// <param name="Plan">The working plan (the WTT as booked if there are no edits; empty with no timetable).</param>
/// <param name="History">The plan's edit history, for undo and redo.</param>
/// <param name="PlanVersion">Goes up whenever the plan changes (an edit, undo, redo or new timetable).</param>
/// <param name="EntryDelays">The sim's expected entry delays by headcode (<c>train_delay</c>), for trains not yet entered.</param>
/// <param name="Forecast">Every train's forecast as of this snapshot (S11).</param>
/// <param name="Conflicts">The plan's conflicts as of this snapshot (S14).</param>
public sealed class PlanSnapshot(
    long Sequence,
    WorkingTimetable? Timetable,
    IReadOnlyList<LiveTrain> Trains,
    SimClockReading Clock,
    MatchSummary Matches,
    long StateVersion,
    SimTime? SessionStart,
    WorkingPlan? Plan = null,
    PlanHistory? History = null,
    long PlanVersion = 0,
    IReadOnlyDictionary<Headcode, int>? EntryDelays = null,
    Forecast? Forecast = null,
    ConflictSet? Conflicts = null)
{
    private Dictionary<TrainId, (LiveTrain Train, int Working)>? _byTrain;
    private Dictionary<string, LiveTrain>? _byUid;

    public long Sequence { get; } = Sequence;

    public WorkingTimetable? Timetable { get; } = Timetable;

    public IReadOnlyList<LiveTrain> Trains { get; } = Trains ?? throw new ArgumentNullException(nameof(Trains));

    public SimClockReading Clock { get; } = Clock;

    public MatchSummary Matches { get; } = Matches ?? throw new ArgumentNullException(nameof(Matches));

    public long StateVersion { get; } = StateVersion;

    public SimTime? SessionStart { get; } = SessionStart;

    public WorkingPlan Plan { get; } = Plan ?? WorkingPlan.Empty;

    public PlanHistory History { get; } = History ?? PlanHistory.Empty;

    public long PlanVersion { get; } = PlanVersion;

    public IReadOnlyDictionary<Headcode, int> EntryDelays { get; } = EntryDelays ?? new Dictionary<Headcode, int>();

    public Forecast Forecast { get; } = Forecast ?? Forecast.Empty;

    public ConflictSet Conflicts { get; } = Conflicts ?? ConflictSet.Empty;

    /// <summary>Before anything has happened.</summary>
    public static PlanSnapshot Empty { get; } = new(0, null, [], SimClockReading.None, new MatchSummary(0, 0, 0, 0, 0, new Dictionary<MatchMethod, int>()), 0, null);

    /// <summary>The sim's area ID from the clock, if one has been seen.</summary>
    public string? AreaId => Clock.AreaId;

    /// <summary>The live train and working matched to a WTT train, if any.</summary>
    public (LiveTrain Train, int Working)? FindByTrain(TrainId train)
    {
        _byTrain ??= BuildByTrain();
        return _byTrain.TryGetValue(train, out var found) ? found : null;
    }

    /// <summary>The live train with this sim uid, if any.</summary>
    public LiveTrain? FindByUid(string uid)
    {
        _byUid ??= Trains.ToDictionary(t => t.SimUid, StringComparer.Ordinal);
        return _byUid.GetValueOrDefault(uid);
    }

    private Dictionary<TrainId, (LiveTrain, int)> BuildByTrain()
    {
        var map = new Dictionary<TrainId, (LiveTrain, int)>();
        foreach (var live in Trains)
        {
            for (var w = 0; w < live.Workings.Count; w++)
            {
                if (live.Workings[w] is { IsMatched: true, Train: { } id })
                {
                    map[id] = (live, w);
                }
            }
        }

        return map;
    }
}
