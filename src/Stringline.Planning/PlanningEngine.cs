using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Timetable.Live;

namespace Stringline.Planning;

/// <summary>Settings for <see cref="PlanningEngine"/>.</summary>
public sealed record PlanningEngineOptions
{
    /// <summary>
    /// Snapshots are published at most this often (wall time); events arriving in between are coalesced into the next
    /// one. Zero publishes after every batch of queued input. Plan edits, undo and redo publish straight away.
    /// </summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public MatcherOptions? Matcher { get; init; }
}

/// <summary>
/// The single writer (<c>02-…md</c> §2): one loop takes gateway events, plan commands, undo and redo from a queue,
/// applies them to a <see cref="LiveState"/> and the <see cref="WorkingPlan"/>, and publishes immutable
/// <see cref="PlanSnapshot"/>s, at most once per <see cref="PlanningEngineOptions.PublishInterval"/>. Readers never
/// lock; they take <see cref="Current"/> or handle <see cref="SnapshotPublished"/>.
/// </summary>
/// <remarks>
/// <para>The engine keeps every event the live state uses, so a timetable loaded (or changed) mid-session rebuilds the
/// state from the start: trains already running are then matched as if the timetable had been there all along. Until a
/// timetable is set, an empty one is used and every working is unmatched. A replay rewind drops the event history but
/// keeps the plan.</para>
/// <para>The plan is the WTT with the history's commands applied in order. Undo drops the last command and replays the
/// rest over the WTT; redo applies it again; a new command clears the redo list. Reloading the same timetable (same
/// area and name) replays the history over it, dropping any command that no longer applies; a different timetable or
/// <see cref="Clear"/> starts a new history. Manual live links are plan commands, so they undo too; when they change,
/// the live state is rebuilt with them.</para>
/// </remarks>
public sealed partial class PlanningEngine
{
    private static readonly WorkingTimetable NoTimetable = WorkingPlan.Empty.Timetable;

    private readonly Channel<Input> _queue = Channel.CreateUnbounded<Input>(new UnboundedChannelOptions { SingleReader = true });
    private readonly PlanningEngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly List<GatewayEvent> _history = [];
    private readonly List<PlanHistoryEntry> _done = [];
    private readonly List<PlanHistoryEntry> _undone = [];
    private LiveState _state;
    private WorkingTimetable? _timetable;
    private WorkingPlan _basePlan = WorkingPlan.Empty;
    private WorkingPlan _plan = WorkingPlan.Empty;
    private PlanHistory? _planHistory = PlanHistory.Empty;
    private long _planVersion;
    private PlanSnapshot _current = PlanSnapshot.Empty;
    private long _sequence;
    private long _publishedVersion;
    private DateTimeOffset _lastPublish = DateTimeOffset.MinValue;
    private int _running;

    public PlanningEngine(PlanningEngineOptions? options = null, TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? new PlanningEngineOptions();
        _time = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PlanningEngine>();
        _state = CreateState(null);
    }

    /// <summary>Raised on the engine loop after each snapshot is published. Handlers must be quick and must not throw.</summary>
    public event EventHandler<PlanSnapshot>? SnapshotPublished;

    /// <summary>The latest snapshot; safe to read from any thread.</summary>
    public PlanSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Queue an event. Thread-safe; never blocks.</summary>
    public void Post(GatewayEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _queue.Writer.TryWrite(new Input(InputKind.Event, e));
    }

    /// <summary>Use this timetable from now on (null for none), rebuilding the live state from the session's events.</summary>
    public void SetTimetable(WorkingTimetable? timetable) => _queue.Writer.TryWrite(new Input(InputKind.Timetable, Timetable: timetable));

    /// <summary>Start a new session: forget all events, live trains and plan edits; keep the timetable.</summary>
    public void Clear() => _queue.Writer.TryWrite(new Input(InputKind.Clear));

    /// <summary>
    /// Queue a plan command. Thread-safe. The task completes on the engine loop once it has been applied (or refused),
    /// before the snapshot showing it is published; it never completes while the loop is not running.
    /// </summary>
    public Task<PlanCommandResult> Execute(IPlanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var done = new TaskCompletionSource<PlanCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(new Input(InputKind.Execute, Command: command, Result: done));
        return done.Task;
    }

    /// <summary>Undo the last plan command. The task's result is false if there was nothing to undo.</summary>
    public Task<bool> Undo() => QueueStep(InputKind.Undo);

    /// <summary>Redo the last undone plan command. The task's result is false if there was nothing to redo.</summary>
    public Task<bool> Redo() => QueueStep(InputKind.Redo);

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

    private Task<bool> QueueStep(InputKind kind)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(new Input(kind, Step: done));
        return done.Task;
    }

    /// <returns>True if an input asks for a snapshot straight away (a plan change, new timetable or cleared session).</returns>
    private bool Drain(ChannelReader<Input> reader)
    {
        var forced = false;
        while (reader.TryRead(out var input))
        {
            switch (input.Kind)
            {
                case InputKind.Event:
                    Apply(input.Event!);
                    break;
                case InputKind.Timetable:
                    LoadTimetable(input.Timetable);
                    forced = true;
                    break;
                case InputKind.Clear:
                    _history.Clear();
                    ResetPlan();
                    _state = CreateState(_timetable);
                    forced = true;
                    break;
                case InputKind.Execute:
                    forced |= Execute(input.Command!, input.Result!);
                    break;
                case InputKind.Undo:
                    forced |= Step(UndoStep(), input.Step!);
                    break;
                case InputKind.Redo:
                    forced |= Step(RedoStep(), input.Step!);
                    break;
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

    private bool Execute(IPlanCommand command, TaskCompletionSource<PlanCommandResult> result)
    {
        PlanCommandResult outcome;
        try
        {
            var description = command.Describe(_plan);
            var before = _plan;
            (var plan, outcome) = _plan.Apply(command);
            if (outcome.Applied)
            {
                _done.Add(new PlanHistoryEntry(command, description, outcome.Warnings));
                _undone.Clear();
                SetPlan(plan, before);
                LogCommandApplied(description, outcome.Warnings.Count);
            }
            else
            {
                LogCommandRefused(description, outcome.Error);
            }
        }
#pragma warning disable CA1031 // A faulty command is refused; it must not stop the engine.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCommandFailed(ex, command.GetType().Name);
            outcome = PlanCommandResult.Fail($"The command could not be applied: {ex.Message}");
        }

        result.TrySetResult(outcome);
        return outcome.Applied;
    }

    private static bool Step(bool changed, TaskCompletionSource<bool> done)
    {
        done.TrySetResult(changed);
        return changed;
    }

    private bool UndoStep()
    {
        if (_done.Count == 0)
        {
            return false;
        }

        var entry = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        _undone.Add(entry);
        SetPlan(Replay(), _plan);
        return true;
    }

    private bool RedoStep()
    {
        if (_undone.Count == 0)
        {
            return false;
        }

        var entry = _undone[^1];
        _undone.RemoveAt(_undone.Count - 1);
        var before = _plan;
        var (plan, result) = _plan.Apply(entry.Command);
        if (!result.Applied)
        {
            // Cannot happen with deterministic commands over the same timetable; drop it rather than loop.
            LogCommandRefused(entry.Description, result.Error);
            _planHistory = null;
            return true;
        }

        _done.Add(entry with { Warnings = result.Warnings });
        SetPlan(plan, before);
        return true;
    }

    /// <summary>The base plan with every command in the history applied; commands that no longer apply are dropped.</summary>
    private WorkingPlan Replay()
    {
        var (plan, results) = _basePlan.ApplyAll(_done.Select(e => e.Command));
        for (var i = results.Count - 1; i >= 0; i--)
        {
            if (!results[i].Applied)
            {
                LogCommandDropped(_done[i].Description, results[i].Error);
                _done.RemoveAt(i);
            }
            else
            {
                _done[i] = _done[i] with { Warnings = results[i].Warnings };
            }
        }

        return plan;
    }

    private void SetPlan(WorkingPlan plan, WorkingPlan before)
    {
        _plan = plan;
        _planHistory = null;
        _planVersion++;
        if (!plan.Links.SequenceEqual(before.Links))
        {
            Rebuild();
        }
    }

    private void ResetPlan()
    {
        var before = _plan;
        _done.Clear();
        _undone.Clear();
        SetPlan(_basePlan, before);
    }

    private void LoadTimetable(WorkingTimetable? timetable)
    {
        var same = timetable is not null && _timetable is not null
            && timetable.AreaId == _timetable.AreaId && timetable.Header.Name == _timetable.Header.Name;
        _timetable = timetable;
        _basePlan = timetable is null ? WorkingPlan.Empty : WorkingPlan.FromTimetable(timetable);
        if (!same)
        {
            _done.Clear();
        }

        _undone.Clear();
        _plan = Replay();
        _planHistory = null;
        _planVersion++;
        Rebuild();
    }

    private void Rebuild()
    {
        _state = CreateState(_timetable);
        foreach (var link in _plan.Links)
        {
            try
            {
                _state.Link(link.SimUid, link.Headcode, link.Train);
            }
            catch (ArgumentException ex)
            {
                LogApplyFailed(ex, nameof(LinkLiveTrain));
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
        _planHistory ??= new PlanHistory([.. _done], Enumerable.Reverse(_undone).ToArray());
        var snapshot = new PlanSnapshot(
            ++_sequence,
            _timetable,
            _state.Trains.ToArray(),
            _state.Clock.Reading,
            _state.Summarise(),
            _state.Version,
            _state.SessionStart,
            _plan,
            _planHistory,
            _planVersion);
        _publishedVersion = _state.Version;
        _lastPublish = _time.GetUtcNow();
        Volatile.Write(ref _current, snapshot);
        if (SnapshotPublished is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<PlanSnapshot>>())
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

    private enum InputKind
    {
        Event,
        Timetable,
        Clear,
        Execute,
        Undo,
        Redo,
    }

    private readonly record struct Input(
        InputKind Kind,
        GatewayEvent? Event = null,
        WorkingTimetable? Timetable = null,
        IPlanCommand? Command = null,
        TaskCompletionSource<PlanCommandResult>? Result = null,
        TaskCompletionSource<bool>? Step = null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not apply a {Event}; skipped")]
    private partial void LogApplyFailed(Exception ex, string @event);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live state rebuilt for timetable {AreaId} from {Events} events: {Trains} trains")]
    private partial void LogRebuilt(string areaId, int events, int trains);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A snapshot handler failed")]
    private partial void LogHandlerFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan: {Description} ({Warnings} warnings)")]
    private partial void LogCommandApplied(string description, int warnings);

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan command refused: {Description}: {Error}")]
    private partial void LogCommandRefused(string description, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan command {Command} failed; refused")]
    private partial void LogCommandFailed(Exception ex, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan command no longer applies and was dropped from the history: {Description}: {Error}")]
    private partial void LogCommandDropped(string description, string? error);
}
