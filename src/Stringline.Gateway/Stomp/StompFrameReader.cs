using System.Globalization;
using System.Text;

namespace Stringline.Gateway.Stomp;

/// <summary>
/// Reads STOMP 1.1 frames from a stream. Tolerates heart-beat end-of-lines between frames and CR LF
/// line endings, honours <c>content-length</c>, and otherwise reads the body up to the NUL terminator.
/// Not thread-safe: one reader per connection, used from one read loop.
/// </summary>
public sealed class StompFrameReader
{
    /// <summary>Default maximum frame size. TD snapshots of a big sim can run to several megabytes.</summary>
    public const int DefaultMaxFrameBytes = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private byte[] _buffer;
    private int _start;
    private int _end;

    public StompFrameReader(Stream stream, int maxFrameBytes = DefaultMaxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 16);
        _stream = stream;
        _maxFrameBytes = maxFrameBytes;
        _buffer = new byte[Math.Min(64 * 1024, maxFrameBytes + 1)];
    }

    /// <summary>
    /// Reads the next frame, or returns null if the stream ended cleanly between frames.
    /// </summary>
    /// <exception cref="StompProtocolException">Malformed frame, oversized frame, or the stream ended mid-frame.</exception>
    public async ValueTask<StompFrame?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        // Heart-beats are bare EOLs between frames.
        while (true)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (_buffer[_start] is (byte)'\n' or (byte)'\r')
            {
                _start++;
                continue;
            }

            break;
        }

        var scanFrom = _start;
        int headerEnd; // index just past the blank line that ends the headers
        while (true)
        {
            var found = FindHeaderEnd(scanFrom, out headerEnd);
            if (found)
            {
                break;
            }

            scanFrom = Math.Max(_start, _end - 2);
            var consumed = scanFrom - _start;
            await NeedMoreAsync(cancellationToken).ConfigureAwait(false);
            scanFrom = _start + consumed;
        }

        var (command, headers) = ParseHeaderBlock(_buffer.AsSpan(_start, headerEnd - _start));
        var bodyOffset = headerEnd - _start;

        var lengthText = headers.FirstOrDefault(h => h.Key == "content-length").Value;
        byte[] body;
        if (lengthText is not null)
        {
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                throw new StompProtocolException($"Invalid content-length \"{lengthText}\" on {command} frame.");
            }

            if (bodyOffset + length > _maxFrameBytes)
            {
                throw new StompProtocolException($"{command} frame of {bodyOffset + length} bytes exceeds the {_maxFrameBytes}-byte limit.");
            }

            while (_end - _start < bodyOffset + length + 1)
            {
                await NeedMoreAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_buffer[_start + bodyOffset + length] != 0)
            {
                throw new StompProtocolException($"{command} frame body is not followed by NUL after content-length {length}.");
            }

            body = _buffer.AsSpan(_start + bodyOffset, length).ToArray();
            _start += bodyOffset + length + 1;
        }
        else
        {
            var searched = 0;
            int nul;
            while ((nul = Array.IndexOf(_buffer, (byte)0, _start + bodyOffset + searched, _end - _start - bodyOffset - searched)) < 0)
            {
                searched = _end - _start - bodyOffset;
                await NeedMoreAsync(cancellationToken).ConfigureAwait(false);
            }

            body = _buffer.AsSpan(_start + bodyOffset, nul - _start - bodyOffset).ToArray();
            _start = nul + 1;
        }

        if (_start == _end)
        {
            _start = _end = 0;
        }

        return new StompFrame(command, headers, body);
    }

    private bool FindHeaderEnd(int from, out int headerEnd)
    {
        var span = _buffer.AsSpan(0, _end);
        for (var i = from; i < _end; i++)
        {
            if (span[i] != '\n')
            {
                continue;
            }

            // "\n\n" or "\n\r\n" ends the header block.
            if (i + 1 < _end && span[i + 1] == '\n')
            {
                headerEnd = i + 2;
                return true;
            }

            if (i + 2 < _end && span[i + 1] == '\r' && span[i + 2] == '\n')
            {
                headerEnd = i + 3;
                return true;
            }
        }

        headerEnd = -1;
        return false;
    }

    private static (string Command, List<KeyValuePair<string, string>> Headers) ParseHeaderBlock(ReadOnlySpan<byte> block)
    {
        var text = Encoding.UTF8.GetString(block);
        var lines = text.Split('\n');
        var command = lines[0].TrimEnd('\r');
        if (command.Length == 0)
        {
            throw new StompProtocolException("Frame has no command.");
        }

        var unescape = command is not (StompCommands.Connect or StompCommands.Connected);
        var headers = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new StompProtocolException($"Malformed header line \"{line}\" on {command} frame.");
            }

            var name = line[..colon];
            var value = line[(colon + 1)..];
            headers.Add(unescape ? new(Unescape(name), Unescape(value)) : new(name, value));
        }

        return (command, headers);
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                result.Append(value[i]);
                continue;
            }

            // Undefined escapes are a fatal error in the spec; we keep them literally rather than drop the connection.
            _ = value[++i] switch
            {
                '\\' => result.Append('\\'),
                'n' => result.Append('\n'),
                'c' => result.Append(':'),
                'r' => result.Append('\r'),
                var other => result.Append('\\').Append(other),
            };
        }

        return result.ToString();
    }

    /// <summary>Reads at least one more byte into the buffer, or throws if the stream ends mid-frame.</summary>
    private async ValueTask NeedMoreAsync(CancellationToken cancellationToken)
    {
        if (_end - _start >= _maxFrameBytes + 1)
        {
            throw new StompProtocolException($"Frame exceeds the {_maxFrameBytes}-byte limit.");
        }

        if (!await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new StompProtocolException("Connection closed in the middle of a frame.");
        }
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                Array.Resize(ref _buffer, (int)Math.Min((long)_buffer.Length * 2, _maxFrameBytes + 1L));
            }
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
        _end += read;
        return read > 0;
    }
}
