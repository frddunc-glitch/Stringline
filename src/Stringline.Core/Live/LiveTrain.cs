using System.Collections.Immutable;
using Stringline.Core.Events;

namespace Stringline.Core.Live;

/// <summary>One reported movement of a live train: a <c>train_location</c> as Stringline keeps it.</summary>
/// <param name="Location">A timing-point TIPLOC or a signal ID.</param>
/// <param name="Platform">Platform at a station, or line at a junction; empty if none (usual on arrivals and at signals).</param>
/// <param name="AtTimingPoint">True if <see cref="Location"/> is a timing point in the WTT; false for signals and unknown locations.</param>
/// <param name="Working">Index into <see cref="LiveTrain.Workings"/> of the working the train was running as.</param>
/// <param name="AspectPassed">At a signal: the aspect it showed as the train passed (<see cref="SignalAspects"/>), if given.</param>
/// <param name="AspectApproach">At a signal: the aspect it showed as the train approached, if given.</param>
public sealed record ActualEvent(Tiploc Location, TrainLocationAction Action, SimTime Time, string Platform, bool AtTimingPoint, int Working, int? AspectPassed = null, int? AspectApproach = null)
{
    public override string ToString() => $"{Action.ToString().ToLowerInvariant()} {Location} {Time}{(Platform.Length > 0 ? " P" + Platform : "")}";
}

/// <summary>Where a live working stands in matching to the WTT.</summary>
public enum MatchStatus
{
    /// <summary>Not decided yet: candidates exist but the evidence so far does not tell them apart.</summary>
    Pending,

    /// <summary>No WTT train fits (none with this headcode, all taken, or none within the time window), or unlinked by hand.</summary>
    Unmatched,

    /// <summary>Linked to a WTT train.</summary>
    Matched,
}

/// <summary>How a matched working was linked to its WTT train.</summary>
public enum MatchMethod
{
    None,

    /// <summary>Same headcode, nearest booked times.</summary>
    HeadcodeAndTime,

    /// <summary>The same sim uid changed headcode to the next working of the train it was matched to.</summary>
    NextWorking,

    /// <summary>A new uid matching a train formed (divide, detach, drop) from a train that is already matched.</summary>
    Formed,

    /// <summary>Linked by the user.</summary>
    Manual,

    /// <summary>
    /// Same headcode, a WTT train with no booked times (#21): one the signaller brings on when wanted, such as a
    /// shunt or a grain train run as required. Taken only when no timed train with the headcode is within the window.
    /// </summary>
    Untimed,
}

/// <summary>
/// One working of a live train: the stretch of its life under one headcode. A sim uid keeps running when SimSig
/// changes its headcode to its next working, so one <see cref="LiveTrain"/> can hold several workings.
/// </summary>
/// <param name="FirstActual">Index into <see cref="LiveTrain.Actuals"/> of the first event under this headcode.</param>
/// <param name="Train">The WTT train, when <see cref="Status"/> is <see cref="MatchStatus.Matched"/>.</param>
/// <param name="DeviationSeconds">Mean |actual − booked| over the timing points that decided the match, if any.</param>
/// <param name="Reason">Why it is pending or unmatched, or a note on a close match; null otherwise.</param>
public sealed record LiveWorking(
    Headcode Headcode,
    int FirstActual,
    MatchStatus Status = MatchStatus.Pending,
    TrainId? Train = null,
    MatchMethod Method = MatchMethod.None,
    int? DeviationSeconds = null,
    string? Reason = null)
{
    public bool IsMatched => Status == MatchStatus.Matched;
}

/// <summary>
/// A train in the running simulation, keyed by SimSig's own uid (unique within one sim run, not the WTT UID).
/// Immutable: <c>LiveState</c> replaces the record on each change.
/// </summary>
/// <param name="SimUid">SimSig's internal train ID.</param>
/// <param name="Workings">Headcodes the train has run under, in order; never empty.</param>
/// <param name="Actuals">Every reported movement, timing points and signals, in order received.</param>
/// <param name="PresentAtStart">True if first seen within a couple of minutes of the session start (so it was probably in the sim already).</param>
public sealed record LiveTrain(
    string SimUid,
    ImmutableList<LiveWorking> Workings,
    ImmutableList<ActualEvent> Actuals,
    bool PresentAtStart)
{
    public LiveWorking Current => Workings[^1];

    public int CurrentIndex => Workings.Count - 1;

    public Headcode Headcode => Current.Headcode;

    /// <summary>The WTT train the current working is matched to, if any.</summary>
    public TrainId? MatchedTrain => Current.Train;

    public SimTime FirstSeen => Actuals[0].Time;

    public SimTime LastSeen => Actuals[^1].Time;

    public ActualEvent Last => Actuals[^1];

    /// <summary>The actuals of one working, timing points and signals.</summary>
    public IEnumerable<ActualEvent> ActualsOf(int working) => Actuals.Skip(Workings[working].FirstActual).TakeWhile(a => a.Working == working);

    /// <summary>The working matched to this WTT train, or -1.</summary>
    public int WorkingOf(TrainId train) => Workings.FindIndex(w => w.Train == train);

    public override string ToString() => $"uid {SimUid} {string.Join("→", Workings.Select(w => w.Headcode))}";
}
