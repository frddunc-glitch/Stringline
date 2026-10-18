using System.Text;
using Stringline.Core;

namespace Stringline.Gateway.Recording;

/// <summary>The first line of a <c>.sslog</c> recording.</summary>
/// <param name="Format">File format version; <see cref="SslogFormat.Version"/> for files we write.</param>
/// <param name="App">Writer name and version, e.g. <c>Stringline 0.1.0</c>.</param>
/// <param name="Created">Wall-clock time the recording started.</param>
/// <param name="Host">Gateway host recorded from, if live.</param>
/// <param name="Port">Gateway port recorded from, if live.</param>
/// <param name="Topics">Topics subscribed to.</param>
/// <remarks>The sim's identity (area ID) is not known when the header is written; it is in the first <c>clock_msg</c>.</remarks>
public sealed record SslogHeader(int Format, string App, DateTimeOffset Created, string? Host, int? Port, IReadOnlyList<string> Topics);

/// <summary>One line after the header.</summary>
public abstract record SslogEntry(DateTimeOffset At);

/// <summary>A recorded MESSAGE frame.</summary>
/// <param name="Sim">The latest sim clock known when the frame arrived (from <c>clock_msg</c>), for seeking.</param>
public sealed record SslogMessage(DateTimeOffset At, string Topic, ReadOnlyMemory<byte> Body, SimTime? Sim = null) : SslogEntry(At)
{
    public string BodyText => Encoding.UTF8.GetString(Body.Span);

    public GatewayMessage ToGatewayMessage() => new(At, Topic, Body);
}

/// <summary>A connection state change during recording (connected, disconnected, …).</summary>
public sealed record SslogConnection(
    DateTimeOffset At,
    GatewayConnectionState State,
    string? Detail = null,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null) : SslogEntry(At);

public static class SslogFormat
{
    public const int Version = 1;

    public const string Extension = ".sslog";

    internal const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
}
