using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Live;

/// <summary>Counts of live workings by match outcome, for status displays and the fixture tests.</summary>
public sealed record MatchSummary(int Trains, int Workings, int Matched, int Pending, int Unmatched, IReadOnlyDictionary<MatchMethod, int> ByMethod)
{
    /// <summary>Matched workings as a share of all workings, 0–1 (1 when there are none).</summary>
    public double MatchedShare => Workings == 0 ? 1 : (double)Matched / Workings;
}

/// <summary>
/// The live picture of the simulation, built by applying gateway events in order: the clock, every live train with its
/// actuals and its WTT match, the TD berth map, and entry-delay hints. Matching runs on every movement of a working
/// that is still pending (see <see cref="TrainMatcher"/>).
/// </summary>
/// <remarks>
/// Single writer: the planning engine owns it and applies events from one thread. <see cref="LiveTrain"/>s are immutable,
/// so a reader may keep one it has been given.
/// Manual links are keyed by (sim uid, headcode) and survive <see cref="Reset"/>, so a session can restore them before
/// replaying its events.
/// </remarks>
public sealed partial class LiveState
{
    /// <summary>A train first seen within this many sim seconds of the session start counts as present at the start.</summary>
    public const int PresentAtStartSeconds = 120;

    private readonly TrainMatcher _matcher;
    private readonly ILogger _logger;
    private readonly Dictionary<string, LiveTrain> _trains = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<TrainId, (string Uid, int Working)> _matched = [];
    private readonly Dictionary<(string Uid, Headcode Headcode), TrainId?> _manualLinks = [];
    private readonly Dictionary<string, string> _berths = new(StringComparer.Ordinal);
    private readonly Dictionary<Headcode, int> _entryDelays = [];

    public LiveState(WorkingTimetable timetable, MatcherOptions? options = null, TimeProvider? timeProvider = null, ILogger<LiveState>? logger = null)
        : this(new TrainMatcher(timetable, options), timeProvider, logger)
    {
    }

    public LiveState(TrainMatcher matcher, TimeProvider? timeProvider = null, ILogger<LiveState>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        _matcher = matcher;
        _logger = logger ?? NullLogger<LiveState>.Instance;
        Clock = new SimClock(timeProvider);
    }

    public WorkingTimetable Timetable => _matcher.Timetable;

    public TrainMatcher Matcher => _matcher;

    public SimClock Clock { get; }

    /// <summary>The sim time of the first clock or movement seen since the last reset.</summary>
    public SimTime? SessionStart { get; private set; }

    /// <summary>Goes up by one on every change, so a reader can tell cheaply whether to rebuild.</summary>
    public long Version { get; private set; }

    /// <summary>Live trains in the order first seen.</summary>
    public IEnumerable<LiveTrain> Trains => _order.Select(uid => _trains[uid]);

    public int TrainCount => _order.Count;

    /// <summary>TD berth → description, from CA/CB/CC steps since the last reset (SimSig's snapshot has no berths).</summary>
    public IReadOnlyDictionary<string, string> Berths => _berths;

    /// <summary>Manual links by (sim uid, headcode); a null train means "unlinked by hand: do not match".</summary>
    public IReadOnlyDictionary<(string Uid, Headcode Headcode), TrainId?> ManualLinks => _manualLinks;

    /// <summary>The latest <c>ttsummary</c> reply, if any.</summary>
    public TimetableSummaryEvent? LastSummary { get; private set; }

    public LiveTrain? Find(string uid) => _trains.GetValueOrDefault(uid);

    /// <summary>The live train and working matched to this WTT train, if any.</summary>
    public (LiveTrain Train, int Working)? FindByTrain(TrainId train) =>
        _matched.TryGetValue(train, out var at) ? (_trains[at.Uid], at.Working) : null;

    /// <summary>The latest <c>train_delay</c> for a headcode (seconds, negative = early): a hint for trains not yet in the sim.</summary>
    public int? EntryDelayHint(Headcode headcode) => _entryDelays.TryGetValue(headcode, out var delay) ? delay : null;

    public MatchSummary Summarise()
    {
        var workings = _trains.Values.SelectMany(t => t.Workings).ToList();
        return new MatchSummary(
            _trains.Count,
            workings.Count,
            workings.Count(w => w.Status == MatchStatus.Matched),
            workings.Count(w => w.Status == MatchStatus.Pending),
            workings.Count(w => w.Status == MatchStatus.Unmatched),
            workings.Where(w => w.IsMatched).GroupBy(w => w.Method).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Apply one event. Unknown and irrelevant events are ignored.</summary>
    /// <returns>True if the state changed.</returns>
    public bool Apply(GatewayEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var changed = e switch
        {
            ClockEvent clock => ApplyClock(clock),
            TrainLocationEvent location => ApplyLocation(location),
            TrainDelayEvent delay => ApplyDelay(delay),
            TdStepEvent step => ApplyStep(step),
            TimetableSummaryEvent summary => ApplySummary(summary),
            ReplayRewoundEvent => ResetState(),
            _ => false,
        };
        if (changed)
        {
            Version++;
        }

        return changed;
    }

    /// <summary>
    /// Link a live working to a WTT train by hand, or with null mark it unmatched for good. The link is kept for the uid
    /// and headcode, so it also applies if the working only appears later (e.g. when a session is restored). If the train
    /// was matched to another working, that one becomes unmatched.
    /// </summary>
    /// <exception cref="ArgumentException">The train is not in the timetable.</exception>
    public void Link(string uid, Headcode headcode, TrainId? train)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        if (train is { } id && _matcher.Timetable.Find(id) is null)
        {
            throw new ArgumentException($"No train {id} in the timetable.", nameof(train));
        }

        _manualLinks[(uid, headcode)] = train;
        if (_trains.TryGetValue(uid, out var live))
        {
            var index = live.Workings.FindLastIndex(w => w.Headcode == headcode);
            if (index >= 0)
            {
                ApplyManualLink(live, index, train);
            }
        }

        Version++;
    }

    /// <summary>Link the current working of a live train (see <see cref="Link(string, Headcode, TrainId?)"/>).</summary>
    public void Link(string uid, TrainId? train)
    {
        var live = _trains.TryGetValue(uid, out var t) ? t : throw new ArgumentException($"No live train with uid {uid}.", nameof(uid));
        Link(uid, live.Headcode, train);
    }

    /// <summary>Forget a manual link. The working is matched automatically again from its next movement.</summary>
    public void ClearLink(string uid, Headcode headcode)
    {
        if (!_manualLinks.Remove((uid, headcode)) || !_trains.TryGetValue(uid, out var live))
        {
            return;
        }

        var index = live.Workings.FindLastIndex(w => w.Headcode == headcode);
        if (index >= 0)
        {
            Unassign(live.Workings[index].Train);
            live = Replace(live with { Workings = live.Workings.SetItem(index, live.Workings[index] with { Status = MatchStatus.Pending, Train = null, Method = MatchMethod.None, DeviationSeconds = null, Reason = null }) });
            Rematch(live, index);
        }

        Version++;
    }

    /// <summary>Drop all live state (clock, trains, berths, hints), keeping the manual links. Called on a replay rewind.</summary>
    public void Reset()
    {
        ResetState();
        Version++;
    }

    private bool ResetState()
    {
        _trains.Clear();
        _order.Clear();
        _matched.Clear();
        _berths.Clear();
        _entryDelays.Clear();
        LastSummary = null;
        SessionStart = null;
        Clock.Reset();
        return true;
    }

    private bool ApplyClock(ClockEvent clock)
    {
        // Before a sim has started, the idrequest reply is a placeholder clock 0 (03-…md §3.3): not a session start.
        if (clock.Clock.Seconds == 0 && !Clock.HasTime)
        {
            return false;
        }

        Clock.Observe(clock);
        SessionStart ??= clock.Clock;
        return true;
    }

    private bool ApplyDelay(TrainDelayEvent delay)
    {
        if (delay.Headcode.IsEmpty)
        {
            return false;
        }

        _entryDelays[delay.Headcode] = delay.DelaySeconds;
        return true;
    }

    private bool ApplyStep(TdStepEvent step)
    {
        switch (step.Kind)
        {
            case TdStepKind.Step:
                _berths.Remove(step.From);
                Set(step.To, step.Description);
                break;
            case TdStepKind.Cancel:
                _berths.Remove(step.From);
                break;
            case TdStepKind.Interpose:
                Set(step.To, step.Description);
                break;
            default:
                return false;
        }

        return true;

        void Set(string berth, string description)
        {
            if (berth.Length == 0)
            {
                return;
            }

            if (description.Length == 0)
            {
                _berths.Remove(berth);
            }
            else
            {
                _berths[berth] = description;
            }
        }
    }

    private bool ApplySummary(TimetableSummaryEvent summary)
    {
        LastSummary = summary;
        return true;
    }

    private bool ApplyLocation(TrainLocationEvent e)
    {
        if (e.Uid.Length == 0 || e.Location.IsEmpty)
        {
            return false;
        }

        SessionStart ??= e.Time;
        Clock.ObserveEventTime(e.Time, e.ReceivedAt);
        var atTimingPoint = _matcher.IsTimingPoint(e.Location);

        if (!_trains.TryGetValue(e.Uid, out var live))
        {
            var headcode = e.Headcode;
            var presentAtStart = e.Time - SessionStart.Value <= PresentAtStartSeconds;
            var actual = new ActualEvent(e.Location, e.Action, e.Time, e.Platform, atTimingPoint, 0);
            live = new LiveTrain(e.Uid, [new LiveWorking(headcode, 0)], [actual], presentAtStart);
            _trains.Add(e.Uid, live);
            _order.Add(e.Uid);
            StartWorking(live, 0);
            return true;
        }

        if (!e.Headcode.IsEmpty && e.Headcode != live.Headcode)
        {
            // A new working: the uid carries on under a new headcode.
            var index = live.Workings.Count;
            var actual = new ActualEvent(e.Location, e.Action, e.Time, e.Platform, atTimingPoint, index);
            live = Replace(live with
            {
                Workings = live.Workings.Add(new LiveWorking(e.Headcode, live.Actuals.Count)),
                Actuals = live.Actuals.Add(actual),
            });
            StartWorking(live, index);
            return true;
        }

        live = Replace(live with { Actuals = live.Actuals.Add(new ActualEvent(e.Location, e.Action, e.Time, e.Platform, atTimingPoint, live.CurrentIndex)) });
        if (live.Current.Status == MatchStatus.Pending)
        {
            Rematch(live, live.CurrentIndex);
        }

        return true;
    }

    /// <summary>A working's first movement: honour a manual link, else try to match.</summary>
    private void StartWorking(LiveTrain live, int index)
    {
        if (_manualLinks.TryGetValue((live.SimUid, live.Workings[index].Headcode), out var link))
        {
            ApplyManualLink(live, index, link);
        }
        else
        {
            Rematch(live, index);
        }
    }

    private void Rematch(LiveTrain live, int index)
    {
        var working = live.Workings[index];
        var previous = index > 0 ? live.Workings[index - 1].Train : null;
        var request = new MatchRequest(working.Headcode, live.ActualsOf(index).ToList(), _matched.Keys.ToHashSet(), live.PresentAtStart, previous);
        var decision = _matcher.Match(request);
        if (decision.Status == working.Status && decision.Reason == working.Reason)
        {
            return;
        }

        var updated = working with
        {
            Status = decision.Status,
            Train = decision.Train,
            Method = decision.Method,
            DeviationSeconds = decision.DeviationSeconds,
            Reason = decision.Reason,
        };
        Replace(live with { Workings = live.Workings.SetItem(index, updated) });
        if (decision.Train is { } train)
        {
            _matched[train] = (live.SimUid, index);
            LogMatched(live.SimUid, working.Headcode, train, decision.Method, decision.DeviationSeconds);
        }
        else if (decision.Status == MatchStatus.Unmatched)
        {
            LogUnmatched(live.SimUid, working.Headcode, decision.Reason);
        }
    }

    private void ApplyManualLink(LiveTrain live, int index, TrainId? train)
    {
        Unassign(live.Workings[index].Train);
        if (train is { } id)
        {
            if (_matched.TryGetValue(id, out var other) && _trains.TryGetValue(other.Uid, out var otherLive))
            {
                // The train was matched elsewhere: that working loses it and is not matched again automatically.
                _matched.Remove(id);
                var stolen = otherLive.Workings[other.Working] with
                {
                    Status = MatchStatus.Unmatched,
                    Train = null,
                    Method = MatchMethod.None,
                    DeviationSeconds = null,
                    Reason = $"{id} linked by hand to uid {live.SimUid}",
                };
                Replace(otherLive with { Workings = otherLive.Workings.SetItem(other.Working, stolen) });
                live = _trains[live.SimUid];
            }

            _matched[id] = (live.SimUid, index);
        }

        var working = live.Workings[index] with
        {
            Status = train is null ? MatchStatus.Unmatched : MatchStatus.Matched,
            Train = train,
            Method = train is null ? MatchMethod.None : MatchMethod.Manual,
            DeviationSeconds = null,
            Reason = train is null ? "Unlinked by hand" : null,
        };
        Replace(live with { Workings = live.Workings.SetItem(index, working) });
    }

    private void Unassign(TrainId? train)
    {
        if (train is { } id)
        {
            _matched.Remove(id);
        }
    }

    private LiveTrain Replace(LiveTrain live)
    {
        _trains[live.SimUid] = live;
        return live;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Matched uid {Uid} {Headcode} to {Train} by {Method} (deviation {Deviation} s)")]
    private partial void LogMatched(string uid, Headcode headcode, TrainId train, MatchMethod method, int? deviation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Unmatched uid {Uid} {Headcode}: {Reason}")]
    private partial void LogUnmatched(string uid, Headcode headcode, string? reason);
}
