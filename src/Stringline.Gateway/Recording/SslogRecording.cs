using Stringline.Core;

namespace Stringline.Gateway.Recording;

/// <summary>
/// A whole <c>.sslog</c> recording held in memory, for replay. Bodies stay as raw bytes and are parsed only
/// when replayed. An hour of a busy sim with the TD topic is a few megabytes, so this is cheap, and it lets
/// a replay seek backwards.
/// </summary>
public sealed class SslogRecording
{
    public SslogRecording(SslogHeader header, IReadOnlyList<SslogMessage> messages, IReadOnlyList<SslogConnection>? connections = null, IReadOnlyList<string>? skippedLines = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(messages);
        Header = header;
        Messages = messages;
        Connections = connections ?? [];
        SkippedLines = skippedLines ?? [];
    }

    public SslogHeader Header { get; }

    /// <summary>The recorded MESSAGE frames, in file order.</summary>
    public IReadOnlyList<SslogMessage> Messages { get; }

    /// <summary>Connection changes during the recording.</summary>
    public IReadOnlyList<SslogConnection> Connections { get; }

    /// <summary>Unreadable lines that were skipped while loading.</summary>
    public IReadOnlyList<string> SkippedLines { get; }

    /// <summary>The earliest real sim time stamped on a message, ignoring the <c>0</c> SimSig reports before a sim starts.</summary>
    public SimTime? FirstSim => Messages.Select(m => m.Sim).FirstOrDefault(s => s is { Seconds: > 0 });

    /// <summary>The sim time stamped on the last message.</summary>
    public SimTime? LastSim => Messages.Select(m => m.Sim).LastOrDefault(s => s is not null);

    /// <summary>Wall-clock time from the first message to the last.</summary>
    public TimeSpan Duration => Messages.Count > 1 ? Messages[^1].At - Messages[0].At : TimeSpan.Zero;

    public static SslogRecording Load(string path)
    {
        using var reader = SslogReader.Open(path);
        return Load(reader);
    }

    public static SslogRecording Load(Stream stream)
    {
        using var reader = new SslogReader(stream, leaveOpen: true);
        return Load(reader);
    }

    private static SslogRecording Load(SslogReader reader)
    {
        var messages = new List<SslogMessage>();
        var connections = new List<SslogConnection>();
        foreach (var entry in reader.ReadEntries())
        {
            switch (entry)
            {
                case SslogMessage message:
                    messages.Add(message);
                    break;
                case SslogConnection connection:
                    connections.Add(connection);
                    break;
            }
        }

        return new SslogRecording(reader.Header, messages, connections, [.. reader.SkippedLines]);
    }

    /// <summary>
    /// The index of the first message stamped at or after <paramref name="target"/>, or <see cref="Messages"/>'
    /// count if none is. Messages are stamped with the latest <c>clock_msg</c>, which SimSig sends once a sim
    /// minute, so this is accurate to the minute.
    /// </summary>
    public int IndexOf(SimTime target)
    {
        for (var i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].Sim is { } sim && sim >= target)
            {
                return i;
            }
        }

        return Messages.Count;
    }
}
