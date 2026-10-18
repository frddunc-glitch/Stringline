using System.Globalization;
using Stringline.Core.Json;

namespace Stringline.Core.Events;

/// <summary>
/// One thing the SimSig Interface Gateway told us. Produced by the gateway message parser from live
/// frames or from a replayed <c>.sslog</c> recording; everything downstream is identical for both.
/// </summary>
/// <param name="ReceivedAt">Wall-clock time the frame arrived (or was recorded).</param>
public abstract record GatewayEvent(DateTimeOffset ReceivedAt);

/// <summary><c>clock_msg</c> on <c>/topic/SimSig</c>: sent on each sim-minute change and on speed or pause changes.</summary>
/// <param name="AreaId">The simulation's area ID, e.g. <c>waterloo</c>; keys the profile.</param>
/// <param name="Clock">Sim time at the moment of sending.</param>
/// <param name="IntervalMs">SimSig's clock interval in milliseconds: 500 at 1×, smaller when faster.</param>
/// <param name="Paused">True while the simulation is paused.</param>
public sealed record ClockEvent(DateTimeOffset ReceivedAt, string AreaId, SimTime Clock, int IntervalMs, bool Paused)
    : GatewayEvent(ReceivedAt)
{
    /// <summary>Sim seconds per real second (500 / interval); 0 if the interval is not positive.</summary>
    public double SpeedRatio => IntervalMs > 0 ? 500.0 / IntervalMs : 0;
}

public enum TrainLocationAction
{
    Unknown,
    Arrive,
    Depart,
    Pass,
}

/// <summary><c>train_location</c> on <c>/topic/TRAIN_MVT_ALL_TOC</c>: a train arrived at, departed from or passed a location.</summary>
/// <param name="Uid">SimSig's internal train ID, unique within one sim run — the key for a live train, not the WTT UID.</param>
/// <param name="Location">A timing-point TIPLOC or a signal ID.</param>
/// <param name="Platform">Platform at a station or line taken at a junction; empty if none.</param>
/// <param name="AspectPassed">Aspect of the signal passed (0 red … 6 green), when the location is a signal.</param>
/// <param name="AspectApproach">Aspect seen on approach, when the location is a signal.</param>
public sealed record TrainLocationEvent(
    DateTimeOffset ReceivedAt,
    Headcode Headcode,
    string Uid,
    TrainLocationAction Action,
    Tiploc Location,
    string Platform,
    SimTime Time,
    int? AspectPassed = null,
    int? AspectApproach = null) : GatewayEvent(ReceivedAt);

/// <summary>
/// <c>train_delay</c>: a train's expected entry delay in seconds (negative = early). Not reliably sent;
/// the UID is often empty. Use only as a hint for trains not yet in the sim.
/// </summary>
public sealed record TrainDelayEvent(DateTimeOffset ReceivedAt, Headcode Headcode, string Uid, int DelaySeconds)
    : GatewayEvent(ReceivedAt);

public enum TdStepKind
{
    /// <summary><c>CA_MSG</c>: description moved from one berth to another.</summary>
    Step,

    /// <summary><c>CB_MSG</c>: description cancelled from a berth.</summary>
    Cancel,

    /// <summary><c>CC_MSG</c>: description interposed into a berth.</summary>
    Interpose,
}

/// <summary>
/// A train describer berth step (<c>CA_MSG</c>, <c>CB_MSG</c>, <c>CC_MSG</c>) on <c>/topic/TD_ALL_SIG_AREA</c>.
/// Berths are empty when not applicable (<see cref="From"/> for an interpose, <see cref="To"/> for a cancel).
/// </summary>
/// <param name="Description">The berth description, usually a headcode; empty for a cancel.</param>
public sealed record TdStepEvent(
    DateTimeOffset ReceivedAt,
    TdStepKind Kind,
    string AreaId,
    string From,
    string To,
    string Description,
    SimTime? Time) : GatewayEvent(ReceivedAt);

public enum SignallingObjectType
{
    Unknown,
    Track,
    Point,
    Signal,
    Route,
    Subroute,
    Flag,
    Frame,
    Crossing,
    Ahb,
}

/// <summary>
/// <c>SG_MSG</c>: the state of one signalling object. The common fields are typed; the object-specific
/// ones (<c>clear</c>, <c>aspect</c>, <c>is_set</c>, …) are kept as text in <see cref="Fields"/>, because
/// their exact set varies by object type and SimSig version. Read them with the lenient accessors.
/// </summary>
/// <param name="ObjectTypeText">The raw <c>obj_type</c>, kept so unknown types can still be told apart.</param>
public sealed record SignallingStateEvent(
    DateTimeOffset ReceivedAt,
    string AreaId,
    SignallingObjectType ObjectType,
    string ObjectTypeText,
    string ObjectId,
    SimTime? Time,
    IReadOnlyDictionary<string, string> Fields) : GatewayEvent(ReceivedAt)
{
    public string? GetString(string field) => Fields.TryGetValue(field, out var value) ? value : null;

    public bool? GetBoolean(string field) =>
        Fields.TryGetValue(field, out var text) && LenientJson.TryParseBoolean(text, out var value) ? value : null;

    public int? GetInt32(string field) =>
        Fields.TryGetValue(field, out var text) && LenientJson.TryParseInt64(text, out var value) && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : null;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"SG {ObjectTypeText} {ObjectId} [{string.Join(", ", Fields.Select(f => $"{f.Key}={f.Value}"))}]");
}

/// <summary>
/// A message the parser could not turn into a typed event: an unknown message type, a known type with
/// missing or invalid fields, or malformed JSON. Logged and skipped downstream, never fatal.
/// </summary>
/// <param name="Topic">The STOMP destination the message arrived on.</param>
/// <param name="MessageType">The top-level JSON property name, or empty if the body could not be read that far.</param>
/// <param name="Raw">The JSON text of the message value (or the whole body if it was malformed).</param>
/// <param name="Reason">Why it was not parsed.</param>
public sealed record UnknownEvent(DateTimeOffset ReceivedAt, string Topic, string MessageType, string Raw, string Reason)
    : GatewayEvent(ReceivedAt);
