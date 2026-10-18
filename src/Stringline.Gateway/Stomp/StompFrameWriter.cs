using System.Buffers;
using System.Globalization;
using System.Text;

namespace Stringline.Gateway.Stomp;

/// <summary>Serialises STOMP 1.1 frames.</summary>
public static class StompFrameWriter
{
    /// <summary>
    /// Encodes a frame: command, headers, blank line, body, NUL. Header names and values are escaped
    /// (<c>\\</c>, <c>\n</c>, <c>\c</c>) except in CONNECT and CONNECTED frames, which STOMP 1.1 leaves
    /// unescaped. A <c>content-length</c> header is added when the frame has a body and none is given.
    /// </summary>
    public static byte[] Encode(StompFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var buffer = new ArrayBufferWriter<byte>(64 + frame.Body.Length);
        Write(buffer, frame);
        return buffer.WrittenSpan.ToArray();
    }

    public static void Write(IBufferWriter<byte> output, StompFrame frame)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(frame);

        var escape = frame.Command is not (StompCommands.Connect or StompCommands.Connected);
        var text = new StringBuilder();
        text.Append(frame.Command).Append('\n');
        var hasLength = false;
        foreach (var (name, value) in frame.Headers)
        {
            hasLength |= name == "content-length";
            AppendEscaped(text, name, escape);
            text.Append(':');
            AppendEscaped(text, value, escape);
            text.Append('\n');
        }

        if (!hasLength && frame.Body.Length > 0)
        {
            text.Append("content-length:").Append(frame.Body.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        text.Append('\n');
        Encoding.UTF8.GetBytes(text.ToString(), output);
        output.Write(frame.Body.Span);
        output.Write<byte>([0]);
    }

    private static void AppendEscaped(StringBuilder text, string value, bool escape)
    {
        if (!escape)
        {
            if (value.AsSpan().IndexOfAny('\n', '\r') >= 0)
            {
                throw new ArgumentException("CONNECT/CONNECTED header values must not contain line breaks.", nameof(value));
            }

            text.Append(value);
            return;
        }

        foreach (var c in value)
        {
            _ = c switch
            {
                '\\' => text.Append(@"\\"),
                '\n' => text.Append(@"\n"),
                ':' => text.Append(@"\c"),
                _ => text.Append(c),
            };
        }
    }
}
