using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Planning;

namespace Stringline.Persistence;

/// <summary>Settings for <see cref="SessionWriter"/>.</summary>
public sealed record SessionWriterOptions
{
    /// <summary>How often what has arrived is saved (autosave). Zero saves only on <see cref="SessionWriter.Flush"/>.</summary>
    public TimeSpan AutosaveInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Written to the file's meta, e.g. <c>Stringline 0.1.0</c>.</summary>
    public string App { get; init; } = $"Stringline {typeof(SessionWriter).Assembly.GetName().Version?.ToString(3)}";
}

/// <summary>
/// Autosaves one session (SES-1) to its SQLite file: the gateway frames the engine uses (live sessions), every plan step,
/// and what the session is (timetable, connection or recording, how far it got). Thread-safe: frames arrive on the read
/// loop, steps on the engine loop and the rest on the UI thread; they are buffered and saved together in one transaction
/// every <see cref="SessionWriterOptions.AutosaveInterval"/>, on <see cref="Flush"/> and when disposed.
/// </summary>
/// <remarks>
/// <para>A new session's file is made only once there is something worth resuming: the first frame the engine uses, or
/// the first plan edit. So a refused login or a replay watched without edits leaves no file. It is named
/// <c>{area}-{yyyyMMdd-HHmm}.stringline</c> from the sim's ID as known then.</para>
/// <para>A failed save is logged and retried at the next one, keeping what was buffered.</para>
/// </remarks>
public sealed partial class SessionWriter : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly string? _folder;
    private readonly SessionWriterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ITimer? _timer;
    private SessionDatabase? _db;
    private SessionInfo _info;
    private List<SslogMessage> _frames = [];
    private List<PlanStep> _steps = [];
    private bool _metaDirty;
    private bool _hasSteps;
    private SimTime? _lastClock;
    private bool _failing;
    private bool _disposed;

    private SessionWriter(string? folder, SessionDatabase? db, SessionInfo info, bool hasSteps, SessionWriterOptions? options, TimeProvider? timeProvider, ILogger? logger)
    {
        _folder = folder;
        _db = db;
        _info = info;
        _hasSteps = hasSteps;
        _options = options ?? new SessionWriterOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        if (_options.AutosaveInterval > TimeSpan.Zero)
        {
            _timer = _time.CreateTimer(_ => Flush(), null, _options.AutosaveInterval, _options.AutosaveInterval);
        }
    }

    /// <summary>The session file, once made.</summary>
    public string? Path
    {
        get
        {
            lock (_lock)
            {
                return _db?.Path;
            }
        }
    }

    /// <summary>What the session is, as it will next be saved.</summary>
    public SessionInfo Info
    {
        get
        {
            lock (_lock)
            {
                return _info;
            }
        }
    }

    public SavedSessionKind Kind => Info.Kind;

    /// <summary>A new session, to be saved in <paramref name="folder"/> once there is something to save.</summary>
    public static SessionWriter Create(string folder, SessionInfo info, SessionWriterOptions? options = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(info);
        var time = timeProvider ?? TimeProvider.System;
        var now = time.GetUtcNow();
        return new SessionWriter(folder, null, info with { Created = now, Updated = now, Ended = false, App = (options ?? new SessionWriterOptions()).App }, false, options, time, logger);
    }

    /// <summary>Carry on saving to an existing session file (resume); it is marked as running again.</summary>
    /// <exception cref="SessionFormatException">It is not a session file, or is from a newer version.</exception>
    public static SessionWriter Append(string path, SessionWriterOptions? options = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        var db = SessionDatabase.Open(path);
        try
        {
            var info = db.ReadInfo();
            var writer = new SessionWriter(null, db, info with { Ended = false }, info.Steps > 0, options, timeProvider, logger);
            writer._metaDirty = true;
            writer._lastClock = info.LastSimTime;
            return writer;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>A file name for a new session in <paramref name="folder"/>, not already taken.</summary>
    public static string NewPath(string folder, string? areaId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var area = string.IsNullOrWhiteSpace(areaId) ? "simsig" : string.Concat(areaId.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c));
        var stem = $"{area}-{now.LocalDateTime:yyyyMMdd-HHmm}";
        var path = System.IO.Path.Combine(folder, stem + SessionDatabase.Extension);
        for (var n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{SessionDatabase.Extension}"));
        }

        return path;
    }

    /// <summary>A live frame and the events parsed from it: kept if the engine uses any of them (as it keeps them).</summary>
    public void OnMessage(GatewayMessage message, IReadOnlyList<GatewayEvent> events)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(events);
        var kept = false;
        lock (_lock)
        {
            foreach (var e in events)
            {
                if (e is ClockEvent clock)
                {
                    _lastClock = clock.Clock;
                    if (clock.AreaId is { Length: > 0 } area && area != _info.AreaId)
                    {
                        _info = _info with { AreaId = area };
                        _metaDirty = true;
                    }
                }

                kept |= e is ClockEvent or TrainLocationEvent or TrainDelayEvent or TdStepEvent or TimetableSummaryEvent;
            }

            if (kept && !_disposed)
            {
                _frames.Add(new SslogMessage(message.ReceivedAt, message.Topic, message.Body.ToArray(), _lastClock));
                NoteSim(_lastClock);
            }
        }
    }

    /// <summary>A plan step from <see cref="PlanningEngine.PlanStepped"/>. A reset before any edit is not kept.</summary>
    public void OnStep(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        lock (_lock)
        {
            if (_disposed || (step.Kind == PlanStepKind.Reset && !_hasSteps))
            {
                return;
            }

            _steps.Add(step);
            _hasSteps = true;
            NoteSim(step.SimTime);
        }
    }

    /// <summary>The timetable now in use.</summary>
    public void SetTimetable(string? path, string? name, string? areaId, string? simName)
    {
        lock (_lock)
        {
            var info = _info with
            {
                TimetablePath = path,
                TimetableName = name,
                AreaId = _info.AreaId ?? (areaId is { Length: > 0 } ? areaId : null),
                SimName = simName is { Length: > 0 } ? simName : _info.SimName,
            };
            if (info != _info)
            {
                _info = info;
                _metaDirty = true;
            }
        }
    }

    /// <summary>A replay's position, saved with the next autosave.</summary>
    public void SetPosition(SimTime position)
    {
        lock (_lock)
        {
            if (_info.Position != position)
            {
                _info = _info with { Position = position };
                NoteSim(position);
                _metaDirty = true;
            }
        }
    }

    /// <summary>Save what has arrived. Never throws; a failure is logged and retried next time.</summary>
    public void Flush() => Save(end: false);

    /// <summary>Save, and mark the session as ended so it is not offered for resuming.</summary>
    public void End()
    {
        lock (_lock)
        {
            _info = _info with { Ended = true };
            _metaDirty = true;
        }

        Save(end: true);
    }

    /// <summary>Save what is left and close the file. The session is left running (offered for resuming) unless <see cref="End"/> was called.</summary>
    public void Dispose()
    {
        _timer?.Dispose();
        Save(end: false);
        lock (_saveLock)
        {
            lock (_lock)
            {
                _disposed = true;
                _db?.Dispose();
                _db = null;
            }
        }
    }

    private void NoteSim(SimTime? sim)
    {
        if (sim is { } s && (_info.LastSimTime is not { } last || s > last))
        {
            _info = _info with { LastSimTime = s };
            _metaDirty = true;
        }
    }

    private void Save(bool end)
    {
        lock (_saveLock)
        {
            List<SslogMessage> frames;
            List<PlanStep> steps;
            SessionInfo info;
            SessionDatabase? db;
            lock (_lock)
            {
                if (_disposed || (_frames.Count == 0 && _steps.Count == 0 && !_metaDirty) || (_db is null && _frames.Count == 0 && !_hasSteps))
                {
                    return;
                }

                (frames, _frames) = (_frames, []);
                (steps, _steps) = (_steps, []);
                _info = _info with { Updated = _time.GetUtcNow() };
                info = _info;
                _metaDirty = false;
                db = _db;
            }

            try
            {
                if (db is null)
                {
                    Directory.CreateDirectory(_folder!);
                    db = SessionDatabase.Create(NewPath(_folder!, info.AreaId, info.Created));
                    lock (_lock)
                    {
                        _db = db;
                    }

                    LogCreated(db.Path);
                }

                db.Save(frames, steps, SessionDatabase.ToMeta(info));
                if (_failing)
                {
                    _failing = false;
                    LogRecovered(db.Path);
                }

                if (end)
                {
                    LogEnded(db.Path, info.LastSimTime);
                }
            }
#pragma warning disable CA1031 // Autosave runs on a timer thread: any failure is logged and retried, never thrown.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                lock (_lock)
                {
                    // Keep them for the next try, in order.
                    _frames.InsertRange(0, frames);
                    _steps.InsertRange(0, steps);
                    _metaDirty = true;
                }

                if (!_failing)
                {
                    _failing = true;
                    LogSaveFailed(ex, db?.Path ?? _folder ?? "?");
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session file created: {Path}")]
    private partial void LogCreated(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session ended: {Path} (to {SimTime})")]
    private partial void LogEnded(string path, SimTime? simTime);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the session to {Path}; will retry")]
    private partial void LogSaveFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saving the session to {Path} works again")]
    private partial void LogRecovered(string path);
}
