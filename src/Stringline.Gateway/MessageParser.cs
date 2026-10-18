using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Json;

namespace Stringline.Gateway;

/// <summary>
/// Turns gateway message bodies into <see cref="GatewayEvent"/>s. A body is a JSON object whose
/// properties are messages keyed by type (<c>{"train_location":{…}}</c>); a TD snapshot is one object
/// with many duplicate <c>SG_MSG</c> keys, so bodies are streamed with <see cref="Utf8JsonReader"/> and
/// each property becomes one event. Never throws on bad input: unknown types, missing fields and
/// malformed JSON become <see cref="UnknownEvent"/>s and are logged.
/// </summary>
/// <remarks>Thread-safe; one instance can serve several sources.</remarks>
public sealed partial class MessageParser
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Fields common to every SG_MSG; the rest go into <see cref="SignallingStateEvent.Fields"/>.</summary>
    private static readonly HashSet<string> SignallingCommonFields = ["obj_id", "obj_type", "area_id", "msg_type", "time"];

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _reportedTypes = new(StringComparer.Ordinal);

    public MessageParser(ILogger<MessageParser>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<GatewayEvent> Parse(GatewayMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Parse(message.Topic, message.Body.Span, message.ReceivedAt);
    }

    public IReadOnlyList<GatewayEvent> Parse(string topic, ReadOnlySpan<byte> body, DateTimeOffset receivedAt)
    {
        topic ??= string.Empty;
        var events = new List<GatewayEvent>(1);
        if (body.Trim(" \t\r\n\0"u8).IsEmpty)
        {
            events.Add(Unknown(receivedAt, topic, string.Empty, string.Empty, "Empty body."));
            return events;
        }

        try
        {
            var reader = new Utf8JsonReader(body, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                events.Add(Unknown(receivedAt, topic, string.Empty, Text(body), "Body is not a JSON object."));
                return events;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var messageType = reader.GetString()!;
                reader.Read();
                var start = (int)reader.TokenStartIndex;
                var value = reader;
                reader.Skip();
                var raw = body[start..(int)reader.BytesConsumed];
                events.Add(ParseMessage(topic, messageType, ref value, raw, receivedAt));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            events.Add(Unknown(receivedAt, topic, string.Empty, Text(body), $"Malformed JSON: {ex.Message}"));
        }

        return events;
    }

    private GatewayEvent ParseMessage(string topic, string messageType, ref Utf8JsonReader value, ReadOnlySpan<byte> raw, DateTimeOffset at)
    {
        if (value.TokenType != JsonTokenType.StartObject)
        {
            return Unknown(at, topic, messageType, Text(raw), "Message value is not an object.");
        }

        var fields = ReadFields(ref value);
        var (result, problem) = messageType.ToUpperInvariant() switch
        {
            "TRAIN_LOCATION" => TrainLocation(fields, at),
            "TRAIN_DELAY" => TrainDelay(fields, at),
            "CLOCK_MSG" => Clock(fields, at),
            "CA_MSG" => TdStep(TdStepKind.Step, fields, at),
            "CB_MSG" => TdStep(TdStepKind.Cancel, fields, at),
            "CC_MSG" => TdStep(TdStepKind.Interpose, fields, at),
            "SG_MSG" => Signalling(fields, at),
            _ => (null, null),
        };

        if (result is not null)
        {
            return result;
        }

        if (problem is null)
        {
            if (_reportedTypes.TryAdd(messageType, true))
            {
                LogUnknownType(messageType, topic);
            }

            return new UnknownEvent(at, topic, messageType, Text(raw), "Unknown message type.");
        }

        return Unknown(at, topic, messageType, Text(raw), problem);
    }

    /// <summary>Reads an object's scalar properties as text; the last duplicate wins, nested values are skipped.</summary>
    private static Dictionary<string, string> ReadFields(ref Utf8JsonReader reader)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString()!;
            reader.Read();
            if (LenientJson.TryGetString(ref reader, out var text))
            {
                fields[name] = text;
            }
            else
            {
                reader.Skip();
            }
        }

        return fields;
    }

    private static (GatewayEvent?, string?) TrainLocation(Dictionary<string, string> f, DateTimeOffset at)
    {
        if (!TryUid(f, out var uid))
        {
            return Missing("uid");
        }

        if (!Tiploc.TryCreate(Get(f, "location"), out var location))
        {
            return Missing("location");
        }

        if (!TryTime(f, "time", out var time))
        {
            return Missing("time");
        }

        var action = Get(f, "action")?.Trim().ToUpperInvariant() switch
        {
            "ARRIVE" => TrainLocationAction.Arrive,
            "DEPART" => TrainLocationAction.Depart,
            "PASS" => TrainLocationAction.Pass,
            _ => TrainLocationAction.Unknown,
        };

        _ = Headcode.TryCreate(Get(f, "headcode"), out var headcode);
        return (new TrainLocationEvent(at, headcode, uid, action, location, Get(f, "platform")?.Trim() ?? string.Empty, time, Int(f, "aspPass"), Int(f, "aspAppr")), null);
    }

    private static (GatewayEvent?, string?) TrainDelay(Dictionary<string, string> f, DateTimeOffset at)
    {
        if (Int(f, "delay") is not { } delay)
        {
            return Missing("delay");
        }

        _ = Headcode.TryCreate(Get(f, "headcode"), out var headcode);
        return (new TrainDelayEvent(at, headcode, Get(f, "uid")?.Trim() ?? string.Empty, delay), null);
    }

    private static (GatewayEvent?, string?) Clock(Dictionary<string, string> f, DateTimeOffset at)
    {
        if (!TryTime(f, "clock", out var clock))
        {
            return Missing("clock");
        }

        if (Int(f, "interval") is not { } interval)
        {
            return Missing("interval");
        }

        var paused = LenientJson.TryParseBoolean(Get(f, "paused"), out var p) && p;
        return (new ClockEvent(at, Get(f, "area_id")?.Trim() ?? string.Empty, clock, interval, paused), null);
    }

    private static (GatewayEvent?, string?) TdStep(TdStepKind kind, Dictionary<string, string> f, DateTimeOffset at)
    {
        var from = Get(f, "from")?.Trim() ?? string.Empty;
        var to = Get(f, "to")?.Trim() ?? string.Empty;
        if (kind is TdStepKind.Step or TdStepKind.Cancel && from.Length == 0)
        {
            return Missing("from");
        }

        if (kind is TdStepKind.Step or TdStepKind.Interpose && to.Length == 0)
        {
            return Missing("to");
        }

        SimTime? time = TryTime(f, "time", out var t) ? t : null;
        return (new TdStepEvent(at, kind, Get(f, "area_id")?.Trim() ?? string.Empty, from, to, Get(f, "descr")?.Trim() ?? string.Empty, time), null);
    }

    private static (GatewayEvent?, string?) Signalling(Dictionary<string, string> f, DateTimeOffset at)
    {
        var id = Get(f, "obj_id")?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return Missing("obj_id");
        }

        var typeText = Get(f, "obj_type")?.Trim() ?? string.Empty;
        var type = typeText.ToUpperInvariant() switch
        {
            "TRACK" => SignallingObjectType.Track,
            "POINT" => SignallingObjectType.Point,
            "SIGNAL" => SignallingObjectType.Signal,
            "ROUTE" => SignallingObjectType.Route,
            "ULC" or "SUBROUTE" => SignallingObjectType.Subroute,
            "FLAG" => SignallingObjectType.Flag,
            "FRAME" => SignallingObjectType.Frame,
            "CROSSING" => SignallingObjectType.Crossing,
            "AHB" => SignallingObjectType.Ahb,
            "" => TypeFromPrefix(id[0]),
            _ => SignallingObjectType.Unknown,
        };

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in f)
        {
            if (!SignallingCommonFields.Contains(name))
            {
                extra[name] = value;
            }
        }

        SimTime? time = TryTime(f, "time", out var t) ? t : null;
        return (new SignallingStateEvent(at, Get(f, "area_id")?.Trim() ?? string.Empty, type, typeText, id, time, extra), null);
    }

    /// <summary>The object ID's prefix letter identifies the type when <c>obj_type</c> is absent.</summary>
    private static SignallingObjectType TypeFromPrefix(char prefix) => char.ToUpperInvariant(prefix) switch
    {
        'T' => SignallingObjectType.Track,
        'P' => SignallingObjectType.Point,
        'S' => SignallingObjectType.Signal,
        'R' => SignallingObjectType.Route,
        'U' => SignallingObjectType.Subroute,
        'L' => SignallingObjectType.Flag,
        'F' => SignallingObjectType.Frame,
        'G' => SignallingObjectType.Crossing,
        'H' => SignallingObjectType.Ahb,
        _ => SignallingObjectType.Unknown,
    };

    private static string? Get(Dictionary<string, string> fields, string name) => fields.GetValueOrDefault(name);

    private static bool TryUid(Dictionary<string, string> fields, out string uid)
    {
        uid = Get(fields, "uid")?.Trim() ?? string.Empty;
        return uid.Length > 0;
    }

    private static int? Int(Dictionary<string, string> fields, string name) =>
        LenientJson.TryParseInt64(Get(fields, name), out var value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    /// <summary>Sim times arrive as seconds (number or numeric string); clock text is accepted too.</summary>
    private static bool TryTime(Dictionary<string, string> fields, string name, out SimTime time)
    {
        if (Int(fields, name) is { } seconds)
        {
            time = new SimTime(seconds);
            return true;
        }

        return SimTime.TryParse(Get(fields, name), out time);
    }

    private static (GatewayEvent?, string?) Missing(string field) => (null, $"Missing or invalid \"{field}\".");

    private static string Text(ReadOnlySpan<byte> utf8) => Encoding.UTF8.GetString(utf8);

    private UnknownEvent Unknown(DateTimeOffset at, string topic, string messageType, string raw, string reason)
    {
        LogUnparsed(topic, messageType, reason, raw.Length > 500 ? raw[..500] + "…" : raw);
        return new UnknownEvent(at, topic, messageType, raw, reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "First {MessageType} message on {Topic}: not a known type, skipped")]
    private partial void LogUnknownType(string messageType, string topic);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {MessageType} message on {Topic}: {Reason} {Raw}")]
    private partial void LogUnparsed(string topic, string messageType, string reason, string raw);
}
