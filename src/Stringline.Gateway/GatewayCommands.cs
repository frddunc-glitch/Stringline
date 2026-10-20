using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Stringline.Gateway;

/// <summary>An outbound gateway message: a topic and a JSON body built by <see cref="GatewayCommands"/>.</summary>
public sealed record GatewayCommand(string Topic, ReadOnlyMemory<byte> Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body.Span);

    public override string ToString() => $"{Topic} {BodyText}";
}

/// <summary>
/// Builders for every message we may send to SimSig. Malformed JSON makes SimSig show an error dialog
/// to the host, so bodies are only ever written with <see cref="Utf8JsonWriter"/> and arguments are
/// validated. Anything beyond <see cref="IdRequest"/> and <see cref="Snapshot"/> belongs to the opt-in
/// assist feature, which is off by default.
/// </summary>
public static class GatewayCommands
{
    /// <summary><c>{"idrequest":{}}</c> on the SimSig topic: SimSig re-sends its last <c>clock_msg</c>.</summary>
    public static GatewayCommand IdRequest() => Build(GatewayTopics.SimSig, "idrequest");

    /// <summary><c>{"snapshot":{}}</c> on the TD topic: SimSig replies with the state of every signalling object.</summary>
    public static GatewayCommand Snapshot() => Build(GatewayTopics.TrainDescriber, "snapshot");

    /// <summary><c>{"ttsummary":{}}</c> on the SimSig topic; the reply comes on <see cref="GatewayTopics.TrainMovementSummary"/>.</summary>
    public static GatewayCommand TimetableSummary() => Build(GatewayTopics.SimSig, "ttsummary");

    /// <summary>TD step: move <paramref name="description"/> from one berth to another.</summary>
    public static GatewayCommand TdStep(string fromBerth, string toBerth, string description) =>
        Build(GatewayTopics.TrainDescriber, "ca_msg", ("from", Id(fromBerth)), ("to", Id(toBerth)), ("descr", Id(description)));

    /// <summary>TD cancel: clear the description from a berth.</summary>
    public static GatewayCommand TdCancel(string fromBerth) =>
        Build(GatewayTopics.TrainDescriber, "cb_msg", ("from", Id(fromBerth)));

    /// <summary>TD interpose: put <paramref name="description"/> into a berth.</summary>
    public static GatewayCommand TdInterpose(string toBerth, string description) =>
        Build(GatewayTopics.TrainDescriber, "cc_msg", ("to", Id(toBerth)), ("descr", Id(description)));

    /// <summary>Request a route by its ID, e.g. <c>RHJ1AM</c>.</summary>
    public static GatewayCommand RouteRequest(string routeId) =>
        Build(GatewayTopics.TrainDescriber, "routerequest", ("route", Id(routeId)));

    /// <summary>Request the route between two signals, e.g. <c>SHJ3</c> to <c>SHJ5</c>.</summary>
    public static GatewayCommand RouteRequest(string fromSignal, string toSignal) =>
        Build(GatewayTopics.TrainDescriber, "routerequest", ("fromSignal", Id(fromSignal)), ("toSignal", Id(toSignal)));

    /// <summary>Replace (put back) a signal.</summary>
    public static GatewayCommand ReplaceSignal(string signalId) =>
        Build(GatewayTopics.TrainDescriber, "bpull", ("signal", Id(signalId)));

    private static GatewayCommand Build(string topic, string messageType, params (string Name, string Value)[] fields)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(messageType);
            foreach (var (name, value) in fields)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new GatewayCommand(topic, buffer.WrittenMemory.ToArray());
    }

    /// <summary>
    /// True if <paramref name="value"/> can be sent as a SimSig object ID or description: a short code of up to
    /// 32 characters, with no whitespace or control characters. The builders refuse anything else.
    /// </summary>
    public static bool IsValidId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 32 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>SimSig object IDs and descriptions are short codes: no whitespace, no control characters.</summary>
    private static string Id(string value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (!IsValidId(value))
        {
            throw new ArgumentException($"\"{value}\" is not a valid SimSig identifier.", name);
        }

        return value;
    }
}
