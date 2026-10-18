using System.Text;

namespace Stringline.Gateway;

/// <summary>One MESSAGE frame from the gateway: where it came from, when, and its raw body.</summary>
/// <param name="Topic">The STOMP <c>destination</c> header.</param>
/// <param name="Body">The raw body bytes, normally UTF-8 JSON.</param>
public sealed record GatewayMessage(DateTimeOffset ReceivedAt, string Topic, ReadOnlyMemory<byte> Body)
{
    public GatewayMessage(DateTimeOffset receivedAt, string topic, string body)
        : this(receivedAt, topic, Encoding.UTF8.GetBytes(body))
    {
    }

    public string BodyText => Encoding.UTF8.GetString(Body.Span);
}

public enum GatewayConnectionState
{
    Connecting,
    Connected,
    Disconnected,
}

/// <summary>Raised by <see cref="GatewayClient"/> whenever the connection changes state.</summary>
/// <param name="Detail">Why, for <see cref="GatewayConnectionState.Disconnected"/>; otherwise informational.</param>
/// <param name="ConnectedHeaders">The CONNECTED frame's headers, for <see cref="GatewayConnectionState.Connected"/>.</param>
/// <param name="RetryIn">The delay before the next attempt, when the client will reconnect.</param>
public sealed record GatewayConnectionChange(
    DateTimeOffset At,
    GatewayConnectionState State,
    string? Detail = null,
    IReadOnlyList<KeyValuePair<string, string>>? ConnectedHeaders = null,
    TimeSpan? RetryIn = null);

/// <summary>The gateway answered CONNECT with an ERROR frame — typically bad or missing credentials. Not retried.</summary>
public sealed class GatewayConnectException : Exception
{
    public GatewayConnectException(string message)
        : base(message)
    {
    }

    public GatewayConnectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GatewayConnectException()
    {
    }
}
