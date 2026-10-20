using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>Why SimSig will not run a WTT train in this session (#16).</summary>
public enum NotRunningKind
{
    /// <summary>Not seeded, and booked to come into the sim before it started.</summary>
    BookedBeforeStart,

    /// <summary>Seeded in a seed group other than the one the sim started with.</summary>
    OtherSeedGroup,

    /// <summary>Formed (next working, divide, detach) only from trains that will not run, so its stock never arrives.</summary>
    FormedFromNotRunning,
}

/// <summary>Why a train will not run, with the words the train list and detail show.</summary>
/// <param name="Text">"not seeded", "seed group 04:00", "formed from 186K".</param>
public sealed record NotRunningReason(NotRunningKind Kind, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// Which WTT trains SimSig will run, from the session's start (S34, #16). Pure: a function of the timetable, the
/// session start, the trains matched live and the clock, so resume and review work it out the same way.
/// </summary>
/// <remarks>
/// <para><b>The start group</b> is the seed group (or the timetable's start, for seeded trains with no group) whose
/// start time is the session start, or the latest before it. A start within <see cref="StartToleranceSeconds"/> of
/// one is a <see cref="FreshStart"/>; anything else is a connection to a sim already running, or a saved game.</para>
/// <para><b>Will not run:</b> a seeded train of another group; at a fresh start only, an unseeded train booked to come
/// in before the start (Penrith's 14:00 recording: none of those ran, the latest booked 90 s before); and a train
/// formed only from trains that will not run (Fred, S34). A train formed from one that runs, runs, whatever its own
/// time. A train matched live always runs.</para>
/// <para><b>Not seen:</b> at a fresh start, a seeded train of the start group not seen by
/// <c>LiveState.PresentAtStartSeconds</c> after it may have been removed. It is flagged, not hidden.</para>
/// </remarks>
public sealed class StartingTrains
{
    /// <summary>A session starting this close to a seed group's (or the timetable's) start is a fresh start.</summary>
    public const int StartToleranceSeconds = 60;

    /// <summary>A seeded train of the start group not seen this long after a fresh start is <see cref="IsNotSeen">not seen</see>.</summary>
    public const int NotSeenAfterSeconds = 120;

    private readonly Dictionary<TrainId, NotRunningReason> _notRunning;
    private readonly HashSet<TrainId> _notSeen;

    private StartingTrains(SimTime? startTime, SeedGroup? group, bool fresh, Dictionary<TrainId, NotRunningReason> notRunning, HashSet<TrainId> notSeen)
    {
        StartTime = startTime;
        StartGroup = group;
        FreshStart = fresh;
        _notRunning = notRunning;
        _notSeen = notSeen;
    }

    /// <summary>Nothing is known about the start: every train runs.</summary>
    public static StartingTrains Unknown { get; } = new(null, null, false, [], []);

    /// <summary>The start time of the start group (the timetable's start for none), or null if unknown.</summary>
    public SimTime? StartTime { get; }

    /// <summary>The seed group the sim started with; null for the timetable's own start, or if unknown.</summary>
    public SeedGroup? StartGroup { get; }

    /// <summary>True if the session started at the timetable's or a seed group's start (not a late connection).</summary>
    public bool FreshStart { get; }

    /// <summary>Trains SimSig will not run, with why.</summary>
    public IReadOnlyDictionary<TrainId, NotRunningReason> NotRunning => _notRunning;

    public NotRunningReason? ReasonFor(TrainId train) => _notRunning.GetValueOrDefault(train);

    /// <summary>A seeded train of the start group not seen soon after a fresh start: it may have been removed.</summary>
    public bool IsNotSeen(TrainId train) => _notSeen.Contains(train);

    /// <summary>Works out which trains run.</summary>
    /// <param name="sessionStart">The sim's first clock this session (<c>LiveState.SessionStart</c>); null runs everything.</param>
    /// <param name="matched">WTT trains that run whatever the rules say: those matched to a live train, and those the user said to run anyway (<see cref="ExpectToRun"/>).</param>
    /// <param name="now">The clock, for <see cref="IsNotSeen"/>.</param>
    public static StartingTrains Work(WorkingTimetable timetable, SimTime? sessionStart, IReadOnlySet<TrainId> matched, SimTime? now)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentNullException.ThrowIfNull(matched);
        if (sessionStart is not { } start || timetable.Trains.Count == 0)
        {
            return Unknown;
        }

        // The candidates: the timetable's start (no group) and each seed group. The latest at or before the start wins;
        // a group beats the timetable's start at the same time.
        var tolerance = StartToleranceSeconds;
        (SeedGroup? Group, SimTime Time)? chosen = null;
        foreach (var candidate in timetable.SeedGroups.Select(g => ((SeedGroup?)g, g.StartTime)).Prepend((null, timetable.Header.StartTime)))
        {
            if (candidate.Item2.Seconds <= start.Seconds + tolerance &&
                (chosen is not { } c || candidate.Item2 > c.Time || (candidate.Item2 == c.Time && candidate.Item1 is not null)))
            {
                chosen = candidate;
            }
        }

        if (chosen is not { } startGroup)
        {
            return Unknown; // started before any start the WTT knows: nothing to go on
        }

        var fresh = Math.Abs(start - startGroup.Time) <= tolerance;
        var formers = Formers(timetable);
        var state = new Dictionary<TrainId, NotRunningReason?>();
        var visiting = new HashSet<TrainId>();
        foreach (var train in timetable.Trains)
        {
            Judge(train);
        }

        var notRunning = state.Where(s => s.Value is not null).ToDictionary(s => s.Key, s => s.Value!);
        var notSeen = new HashSet<TrainId>();
        if (fresh && now is { } clock && clock - start >= NotSeenAfterSeconds)
        {
            foreach (var train in timetable.Trains)
            {
                if (train.Start == TrainStart.Seeded && !matched.Contains(train.Id) && !notRunning.ContainsKey(train.Id))
                {
                    notSeen.Add(train.Id);
                }
            }
        }

        return new StartingTrains(startGroup.Time, startGroup.Group, fresh, notRunning, notSeen);

        NotRunningReason? Judge(Train train)
        {
            if (state.TryGetValue(train.Id, out var known))
            {
                return known;
            }

            if (!visiting.Add(train.Id))
            {
                return null; // a formation loop: let it run rather than go round
            }

            var reason = Decide(train);
            visiting.Remove(train.Id);
            state[train.Id] = reason;
            return reason;
        }

        NotRunningReason? Decide(Train train)
        {
            if (matched.Contains(train.Id))
            {
                return null;
            }

            if (train.Start == TrainStart.Seeded)
            {
                return SeededRuns(train) ? null : new NotRunningReason(NotRunningKind.OtherSeedGroup, SeedGroupText(timetable, train));
            }

            if (formers.TryGetValue(train.Id, out var from) && from.Count > 0)
            {
                // Formed from others: it runs if any of them does (a seeded twin and its unseeded original may both form it).
                Train? blocked = null;
                foreach (var former in from)
                {
                    if (Judge(former) is null)
                    {
                        return null;
                    }

                    blocked ??= former;
                }

                return new NotRunningReason(NotRunningKind.FormedFromNotRunning, $"formed from {blocked!.Headcode}");
            }

            return fresh && train.BookedStart is { } booked && booked < start
                ? new NotRunningReason(NotRunningKind.BookedBeforeStart, "not seeded")
                : null;
        }

        bool SeededRuns(Train train)
        {
            if (train.SeedGroup is null)
            {
                return startGroup.Time == timetable.Header.StartTime;
            }

            // A group the header does not define tells us nothing: let the train run.
            return timetable.FindSeedGroup(train.SeedGroup) is not { } group || (startGroup.Group is { } g && g.Id == group.Id);
        }
    }

    /// <summary>"seed group 04:00", or "seeded at 00:00" for a train seeded at the timetable's start.</summary>
    private static string SeedGroupText(WorkingTimetable timetable, Train train) =>
        timetable.FindSeedGroup(train.SeedGroup) is { } group ? $"seed group {group}" : $"seeded at {timetable.Header.StartTime.ToString("t")}";

    /// <summary>Each train's formers: the trains whose next-working, divide, detach or drop activity names it.</summary>
    private static Dictionary<TrainId, List<Train>> Formers(WorkingTimetable timetable)
    {
        var formers = new Dictionary<TrainId, List<Train>>();
        foreach (var train in timetable.Trains)
        {
            foreach (var point in train.Points)
            {
                foreach (var activity in point.Activities)
                {
                    if (activity.FormsAssociatedTrain && activity.Train is { } formed && formed != train.Id)
                    {
                        if (!formers.TryGetValue(formed, out var list))
                        {
                            formers[formed] = list = [];
                        }

                        if (!list.Contains(train))
                        {
                            list.Add(train);
                        }
                    }
                }
            }
        }

        return formers;
    }

    /// <summary>The WTT trains matched to a live working.</summary>
    public static HashSet<TrainId> Matched(IEnumerable<LiveTrain> trains)
    {
        ArgumentNullException.ThrowIfNull(trains);
        var set = new HashSet<TrainId>();
        foreach (var live in trains)
        {
            foreach (var working in live.Workings)
            {
                if (working is { IsMatched: true, Train: { } id })
                {
                    set.Add(id);
                }
            }
        }

        return set;
    }
}
