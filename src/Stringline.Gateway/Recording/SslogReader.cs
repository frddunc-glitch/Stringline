using System.Globalization;
using System.Text;
using System.Text.Json;
using Stringline.Core;

namespace Stringline.Gateway.Recording;

/// <summary>
/// Reads a <c>.sslog</c> recording written by <see cref="SslogWriter"/>. Lines that cannot be read —
/// typically a truncated last line after a crash — are counted in <see cref="SkippedLines"/> and skipped.
/// </summary>
public sealed class SslogReader : IDisposable
{
    private readonly StreamReader _reader;
    private int _lineNumber;

    public SslogReader(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen);
        try
        {
            var first = _reader.ReadLine();
            _lineNumber = 1;
            Header = first is null ? throw new InvalidDataException("The recording is empty.") : ParseHeader(first);
        }
        catch
        {
            // Not a recording: release the file (unless the caller keeps the stream).
            _reader.Dispose();
            throw;
        }
    }

    public SslogHeader Header { get; }

    /// <summary>Lines skipped so far because they were malformed, with their line numbers and reasons.</summary>
    public IReadOnlyList<string> SkippedLines => _skipped;

    private readonly List<string> _skipped = [];

    public static SslogReader Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024));

    /// <summary>Reads the remaining entries in file order. Enumerate once.</summary>
    public IEnumerable<SslogEntry> ReadEntries()
    {
        while (_reader.ReadLine() is { } line)
        {
            _lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            SslogEntry? entry;
            try
            {
                entry = ParseEntry(line);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
            {
                _skipped.Add($"line {_lineNumber}: {ex.Message}");
                continue;
            }

            if (entry is null)
            {
                _skipped.Add($"line {_lineNumber}: neither a message nor a connection entry");
                continue;
            }

            yield return entry;
        }
    }

    public void Dispose() => _reader.Dispose();

    private static SslogHeader ParseHeader(string line)
    {
        int? format = null;
        string app = string.Empty;
        DateTimeOffset created = default;
        string? host = null;
        int? port = null;
        var topics = new List<string>();
        try
        {
            var reader = Start(line);
            while (NextProperty(ref reader, out var name))
            {
                switch (name)
                {
                    case "sslog":
                        format = reader.GetInt32();
                        break;
                    case "app":
                        app = reader.GetString() ?? string.Empty;
                        break;
                    case "created":
                        created = ParseTime(reader.GetString());
                        break;
                    case "host":
                        host = reader.GetString();
                        break;
                    case "port":
                        port = reader.GetInt32();
                        break;
                    case "topics" when reader.TokenType == JsonTokenType.StartArray:
                        while (reader.Read() && reader.TokenType == JsonTokenType.String)
                        {
                            topics.Add(reader.GetString()!);
                        }

                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Not a .sslog recording: {ex.Message}", ex);
        }

        if (format is not { } version)
        {
            throw new InvalidDataException("Not a .sslog recording: the first line has no \"sslog\" version.");
        }

        if (version > SslogFormat.Version)
        {
            throw new InvalidDataException($"The recording is format {version}; this build reads up to {SslogFormat.Version}.");
        }

        return new SslogHeader(version, app, created, host, port, topics);
    }

    private static SslogEntry? ParseEntry(string line)
    {
        DateTimeOffset? at = null;
        string? topic = null;
        byte[]? body = null;
        SimTime? sim = null;
        GatewayConnectionState? state = null;
        string? detail = null;
        List<KeyValuePair<string, string>>? headers = null;

        var reader = Start(line);
        while (NextProperty(ref reader, out var name))
        {
            switch (name)
            {
                case "t":
                    at = ParseTime(reader.GetString());
                    break;
                case "topic":
                    topic = reader.GetString();
                    break;
                case "body":
                    body = Encoding.UTF8.GetBytes(reader.GetString() ?? string.Empty);
                    break;
                case "body64":
                    body = reader.GetBytesFromBase64();
                    break;
                case "sim":
                    sim = new SimTime(reader.GetInt32());
                    break;
                case "conn":
                    state = Enum.Parse<GatewayConnectionState>(reader.GetString() ?? string.Empty, ignoreCase: true);
                    break;
                case "detail":
                    detail = reader.GetString();
                    break;
                case "headers" when reader.TokenType == JsonTokenType.StartObject:
                    headers = [];
                    while (NextProperty(ref reader, out var header))
                    {
                        headers.Add(new(header, reader.GetString() ?? string.Empty));
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (at is not { } time)
        {
            throw new FormatException("Missing \"t\".");
        }

        if (topic is not null && body is not null)
        {
            return new SslogMessage(time, topic, body, sim);
        }

        return state is { } s ? new SslogConnection(time, s, detail, headers) : null;
    }

    private static Utf8JsonReader Start(string line)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new FormatException("Line is not a JSON object.");
        }

        return reader;
    }

    /// <summary>Moves to the next property's value; false at the end of the current object.</summary>
    private static bool NextProperty(ref Utf8JsonReader reader, out string name)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
        {
            name = string.Empty;
            return false;
        }

        name = reader.GetString()!;
        if (!reader.Read())
        {
            throw new FormatException("Truncated line.");
        }

        return true;
    }

    private static DateTimeOffset ParseTime(string? text) =>
        DateTimeOffset.Parse(text ?? throw new FormatException("Missing time."), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
