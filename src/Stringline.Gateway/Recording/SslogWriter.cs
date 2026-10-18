using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Stringline.Core;

namespace Stringline.Gateway.Recording;

/// <summary>
/// Writes a <c>.sslog</c> recording: JSON Lines, a header line first, then one line per MESSAGE frame or
/// connection change.
/// <code>
/// {"sslog":1,"app":"Stringline 0.1.0","created":"2026-10-18T09:00:00.000Z","host":"localhost","port":51515,"topics":["/topic/SimSig"]}
/// {"t":"2026-10-18T09:00:00.120Z","conn":"connected","headers":{"version":"1.1"}}
/// {"t":"2026-10-18T09:12:03.123Z","topic":"/topic/SimSig","sim":15720,"body":"{\"clock_msg\":{…}}"}
/// </code>
/// Bodies are stored verbatim as a JSON string, or as <c>body64</c> (base64) if they are not valid UTF-8,
/// so a replay reproduces the exact bytes. Thread-safe. Output is flushed at most once a second and on dispose.
/// </summary>
public sealed class SslogWriter : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    // Relaxed escaping keeps bodies readable ("\"" rather than """); the file is never embedded in HTML.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private long _lastFlush;
    private bool _disposed;

    public SslogWriter(Stream stream, SslogHeader header, bool ownsStream = true, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(header);
        _stream = stream;
        _ownsStream = ownsStream;
        _time = timeProvider ?? TimeProvider.System;
        _lastFlush = _time.GetTimestamp();
        WriteLine(w =>
        {
            w.WriteNumber("sslog", header.Format);
            w.WriteString("app", header.App);
            w.WriteString("created", Format(header.Created));
            if (header.Host is not null)
            {
                w.WriteString("host", header.Host);
            }

            if (header.Port is { } port)
            {
                w.WriteNumber("port", port);
            }

            w.WriteStartArray("topics");
            foreach (var topic in header.Topics)
            {
                w.WriteStringValue(topic);
            }

            w.WriteEndArray();
        });
        _stream.Flush();
    }

    public long MessagesWritten { get; private set; }

    /// <summary>Creates a new file (failing if it exists) and writes the header.</summary>
    public static SslogWriter Create(string path, SslogHeader header, TimeProvider? timeProvider = null)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        return new SslogWriter(stream, header, ownsStream: true, timeProvider);
    }

    /// <summary>A header for a live recording made by this build of Stringline.</summary>
    public static SslogHeader LiveHeader(DateTimeOffset created, string host, int port, IReadOnlyList<string> topics)
    {
        var version = typeof(SslogWriter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return new SslogHeader(SslogFormat.Version, $"Stringline {version}", created, host, port, topics);
    }

    public void WriteMessage(GatewayMessage message, SimTime? sim = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        Write(new SslogMessage(message.ReceivedAt, message.Topic, message.Body, sim));
    }

    public void WriteConnection(GatewayConnectionChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Write(new SslogConnection(change.At, change.State, change.Detail, change.ConnectedHeaders));
    }

    public void Write(SslogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        switch (entry)
        {
            case SslogMessage m:
                WriteLine(w =>
                {
                    w.WriteString("t", Format(m.At));
                    w.WriteString("topic", m.Topic);
                    if (m.Sim is { } sim)
                    {
                        w.WriteNumber("sim", sim.Seconds);
                    }

                    if (TryDecode(m.Body.Span, out var text))
                    {
                        w.WriteString("body", text);
                    }
                    else
                    {
                        w.WriteBase64String("body64", m.Body.Span);
                    }
                });
                MessagesWritten++;
                break;
            case SslogConnection c:
                WriteLine(w =>
                {
                    w.WriteString("t", Format(c.At));
                    w.WriteString("conn", c.State.ToString().ToLowerInvariant());
                    if (c.Detail is not null)
                    {
                        w.WriteString("detail", c.Detail);
                    }

                    if (c.Headers is { Count: > 0 })
                    {
                        w.WriteStartObject("headers");
                        foreach (var (name, value) in c.Headers)
                        {
                            w.WriteString(name, value);
                        }

                        w.WriteEndObject();
                    }
                });
                break;
            default:
                throw new ArgumentException($"Unsupported entry type {entry.GetType().Name}.", nameof(entry));
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _stream.Flush();
                _lastFlush = _time.GetTimestamp();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Flush();
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }
    }

    internal static string Format(DateTimeOffset at) => at.UtcDateTime.ToString(SslogFormat.TimeFormat, CultureInfo.InvariantCulture);

    private void WriteLine(Action<Utf8JsonWriter> body)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using (var writer = new Utf8JsonWriter(_stream, WriterOptions))
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }

            _stream.WriteByte((byte)'\n');
            if (_time.GetElapsedTime(_lastFlush) >= TimeSpan.FromSeconds(1))
            {
                _stream.Flush();
                _lastFlush = _time.GetTimestamp();
            }
        }
    }

    private static bool TryDecode(ReadOnlySpan<byte> body, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(body);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
