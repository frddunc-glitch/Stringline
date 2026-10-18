using System.Text;

namespace Stringline.Gateway.Stomp;

/// <summary>
/// One STOMP 1.1 frame. Headers keep their wire order; a repeated header keeps every entry, and
/// <see cref="GetHeader"/> returns the first, as the specification requires.
/// </summary>
public sealed class StompFrame
{
    public StompFrame(string command, IReadOnlyList<KeyValuePair<string, string>>? headers = null, ReadOnlyMemory<byte> body = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        Command = command;
        Headers = headers ?? [];
        Body = body;
    }

    public string Command { get; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    public ReadOnlyMemory<byte> Body { get; }

    public string? GetHeader(string name)
    {
        foreach (var (key, value) in Headers)
        {
            if (key == name)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>The body decoded as UTF-8 (invalid sequences become U+FFFD).</summary>
    public string BodyText => Encoding.UTF8.GetString(Body.Span);

    public override string ToString() => $"{Command} ({Headers.Count} headers, {Body.Length} bytes)";
}

/// <summary>STOMP command names used by the client and the test server.</summary>
public static class StompCommands
{
    public const string Connect = "CONNECT";
    public const string Connected = "CONNECTED";
    public const string Subscribe = "SUBSCRIBE";
    public const string Unsubscribe = "UNSUBSCRIBE";
    public const string Send = "SEND";
    public const string Disconnect = "DISCONNECT";
    public const string Message = "MESSAGE";
    public const string Receipt = "RECEIPT";
    public const string Error = "ERROR";
}

/// <summary>The peer broke the STOMP framing rules, or sent a frame larger than allowed.</summary>
public sealed class StompProtocolException : IOException
{
    public StompProtocolException(string message)
        : base(message)
    {
    }

    public StompProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StompProtocolException()
    {
    }
}
