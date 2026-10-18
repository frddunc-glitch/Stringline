using Stringline.Core;
using Stringline.Core.Events;

namespace Stringline.Gateway.Recording;

/// <summary>
/// Records a live connection to <c>.sslog</c> on demand (the app's Record toggle, CON-6): feed it every frame and
/// connection change, and start or stop writing at any time, from any thread. It tracks the sim clock even while not
/// recording, so the first frames of a recording started mid-session are stamped with the right time.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private SslogWriter? _writer;
    private SimTime? _lastClock;

    public SessionRecorder(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>Raised (on the calling thread) when recording starts or stops.</summary>
    public event EventHandler? StateChanged;

    public bool IsRecording
    {
        get
        {
            lock (_lock)
            {
                return _writer is not null;
            }
        }
    }

    /// <summary>The file being written, or the last one written.</summary>
    public string? Path { get; private set; }

    /// <summary>Frames written to the current (or last) recording.</summary>
    public long MessagesWritten
    {
        get
        {
            lock (_lock)
            {
                return _writer?.MessagesWritten ?? _lastCount;
            }
        }
    }

    private long _lastCount;

    /// <summary>Start writing a new file (which must not exist). Stops any recording in progress first.</summary>
    /// <exception cref="IOException">The file exists or cannot be created.</exception>
    public void Start(string path, string host, int port, IReadOnlyList<string> topics)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Stop();
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = SslogWriter.Create(path, SslogWriter.LiveHeader(_time.GetUtcNow(), host, port, topics), _time);
        lock (_lock)
        {
            _writer = writer;
            Path = path;
            _lastCount = 0;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finish the file. Does nothing if not recording.</summary>
    public void Stop()
    {
        SslogWriter? writer;
        lock (_lock)
        {
            writer = _writer;
            _writer = null;
            if (writer is not null)
            {
                _lastCount = writer.MessagesWritten;
                writer.Dispose();
            }
        }

        if (writer is not null)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A frame arrived, with the events parsed from it.</summary>
    public void OnMessage(GatewayMessage message, IReadOnlyList<GatewayEvent> events)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(events);
        lock (_lock)
        {
            foreach (var e in events)
            {
                if (e is ClockEvent clock)
                {
                    _lastClock = clock.Clock;
                }
            }

            _writer?.WriteMessage(message, _lastClock);
        }
    }

    /// <summary>The connection changed state.</summary>
    public void OnConnection(GatewayConnectionChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            _writer?.WriteConnection(change);
        }
    }

    /// <summary>A file name for a new recording: <c>{area}-{yyyyMMdd-HHmmss}.sslog</c> in <paramref name="folder"/>.</summary>
    public static string NewPath(string folder, string? areaId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var area = string.IsNullOrWhiteSpace(areaId) ? "simsig" : string.Concat(areaId.Trim().Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return System.IO.Path.Combine(folder, $"{area}-{now.LocalDateTime:yyyyMMdd-HHmmss}.sslog");
    }

    public void Dispose() => Stop();
}
