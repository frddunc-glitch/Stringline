using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Timetable.Live;

namespace Stringline.Planning;

/// <summary>Settings for <see cref="LiveEngine"/>.</summary>
public sealed record LiveEngineOptions
{
    /// <summary>
    /// Snapshots are published at most this often (wall time); events arriving in between are coalesced into the next
    /// one. Zero publishes after every batch of queued input.
    /// </summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public MatcherOptions? Matcher { get; init; }
}

/// <summary>
/// The single writer of the live picture (<c>02-…md</c> §2): one loop takes gateway events and commands from a queue,
/// applies them to a <see cref="LiveState"/> and publishes immutable <see cref="LiveSnapshot"/>s, at most once per
/// <see cref="LiveEngineOptions.PublishInterval"/>. Readers never lock; they take <see cref="Current"/> or handle
/// <see cref="SnapshotPublished"/>. S10's <c>PlanningEngine</c> grows from this.
/// </summary>
/// <remarks>
/// The engine keeps every event the live state uses, so a timetable loaded (or changed) mid-session rebuilds the state
/// from the start: trains already running are then matched as if the timetable had been there all along. Until a
/// timetable is set, an empty one is used and every working is unmatched. A replay rewind drops the history.
/// </remarks>
public sealed partial class LiveEngine
{
    private static readonly WorkingTimetable NoTimetable = new(new TimetableHeader(string.Empty, string.Empty, string.Empty, string.Empty, null, SimTime.Zero, SimTime.Zero), [], [], []);

    private readonly Channel<Input> _queue = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LiveEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly List<GatewayEvent> _history = [];
    private LiveState _state;
    private WorkingTimetable? _timetable;
    private LiveSnapshot _current = LiveSnapshot.Empty;
    private long _sequence;
    private long _publishedVersion;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;
    private int _running;

    public LiveEngine(LiveEngineOptions? options = null, TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? new LiveEngineOptions();
        _time = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<LiveEngine>();
        _state = CreateState(null);
    }

    /// <summary>Raised on the engine loop after each snapshot is published. Handlers must be quick and must not throw.</summary>
    public event EventHandler<LiveSnapshot>? SnapshotPublished;

    /// <summary>The latest snapshot; safe to read from any thread.</summary>
    public LiveSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Queue an event. Thread-safe; never blocks.</summary>
    public void Post(GatewayEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _queue.Writer.TryWrite(new Input(e, null, false));
    }

    /// <summary>Use this timetable from now on (null for none), rebuilding the live state from the session's events.</summary>
    public void SetTimetable(WorkingTimetable? timetable) => _queue.Writer.TryWrite(new Input(null, timetable, true));

    /// <summary>Start a new session: forget all events and live trains, keep the timetable.</summary>
    public void Clear() => _queue.Writer.TryWrite(new Input(null, null, false));

    /// <summary>Read every event from a source into the queue until it ends or <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task PumpAsync(ISimEventSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        await foreach (var e in source.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Post(e);
        }
    }

    /// <summary>Runs the loop until cancelled (then returns normally). Only one loop may run at a time.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("The engine is already running.");
        }

        try
        {
            var reader = _queue.Reader;
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var forced = Drain(reader);
                if (!forced && _state.Version == _publishedVersion)
                {
                    continue;
                }

                var wait = _lastPublish + _options.PublishInterval - _time.GetUtcNow();
                if (!forced && wait > TimeSpan.Zero)
                {
                    // Coalesce: whatever arrives while we wait goes into the same snapshot.
                    await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                    Drain(reader);
                }

                Publish();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <returns>True if a command asks for a snapshot straight away (new timetable or cleared session).</returns>
    private bool Drain(ChannelReader<Input> reader)
    {
        var forced = false;
        while (reader.TryRead(out var input))
        {
            if (input.Event is { } e)
            {
                Apply(e);
            }
            else if (input.IsTimetable)
            {
                _timetable = input.Timetable;
                Rebuild();
                forced = true;
            }
            else
            {
                _history.Clear();
                _state = CreateState(_timetable);
                forced = true;
            }
        }

        return forced;
    }

    private void Apply(GatewayEvent e)
    {
        if (e is ReplayRewoundEvent)
        {
            _history.Clear();
        }
        else if (e is ClockEvent or TrainLocationEvent or TrainDelayEvent or TdStepEvent or TimetableSummaryEvent)
        {
            _history.Add(e);
        }
        else
        {
            return;
        }

        try
        {
            _state.Apply(e);
        }
#pragma warning disable CA1031 // One bad event must not stop the live picture.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogApplyFailed(ex, e.GetType().Name);
        }
    }

    private void Rebuild()
    {
        var links = _state.ManualLinks.ToList();
        _state = CreateState(_timetable);
        foreach (var ((uid, headcode), train) in links)
        {
            if (train is null || _timetable?.Find(train.Value) is not null)
            {
                _state.Link(uid, headcode, train);
            }
        }

        foreach (var e in _history)
        {
            try
            {
                _state.Apply(e);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogApplyFailed(ex, e.GetType().Name);
            }
        }

        LogRebuilt(_timetable?.AreaId ?? "(none)", _history.Count, _state.TrainCount);
    }

    private void Publish()
    {
        var snapshot = new LiveSnapshot(
            ++_sequence,
            _timetable,
            _state.Trains.ToArray(),
            _state.Clock.Reading,
            _state.Summarise(),
            _state.Version,
            _state.SessionStart);
        _publishedVersion = _state.Version;
        _lastPublish = _time.GetUtcNow();
        Volatile.Write(ref _current, snapshot);
        if (SnapshotPublished is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<LiveSnapshot>>())
        {
            try
            {
                handler(this, snapshot);
            }
#pragma warning disable CA1031 // A faulty handler must not stop the engine or the other handlers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogHandlerFailed(ex);
            }
        }
    }

    private LiveState CreateState(WorkingTimetable? timetable) =>
        new(timetable ?? NoTimetable, _options.Matcher, _time, _loggerFactory.CreateLogger<LiveState>());

    private readonly record struct Input(GatewayEvent? Event, WorkingTimetable? Timetable, bool IsTimetable);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not apply a {Event}; skipped")]
    private partial void LogApplyFailed(Exception ex, string @event);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live state rebuilt for timetable {AreaId} from {Events} events: {Trains} trains")]
    private partial void LogRebuilt(string areaId, int events, int trains);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A snapshot handler failed")]
    private partial void LogHandlerFailed(Exception ex);
}
