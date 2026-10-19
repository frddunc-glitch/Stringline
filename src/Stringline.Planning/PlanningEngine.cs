using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
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

    /// <summary>The forecaster's settings to start with; <see cref="PlanningEngine.SetForecast"/> changes them.</summary>
    public ForecastOptions Forecast { get; init; } = new();

    /// <summary>The conflict detector's settings to start with; <see cref="PlanningEngine.SetForecast"/> changes them.</summary>
    public ConflictOptions Conflicts { get; init; } = new();
}

/// <summary>
/// The single writer (<c>02-…md</c> §2): one loop takes gateway events, plan commands, undo and redo from a queue,
/// applies them to a <see cref="LiveState"/> and the <see cref="WorkingPlan"/>, and publishes immutable
/// <see cref="PlanSnapshot"/>s (with the forecast and its conflicts), at most once per <see cref="PlanningEngineOptions.PublishInterval"/>. Readers never
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

    /// <summary>A forecast slower than this is logged (the budget in <c>06-…md</c> §2 is 50 ms for 500 trains).</summary>
    private static readonly TimeSpan SlowForecast = TimeSpan.FromMilliseconds(100);

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
    private ForecastNetwork _network = ForecastNetwork.Empty;
    private ForecastOptions _forecastOptions;
    private ConflictOptions _conflictOptions;
    private long _forecastSettingsVersion;
    private (long State, long Plan, long Settings) _forecastKey = (-1, -1, -1);
    private Forecast _forecast = Forecast.Empty;
    private ConflictSet _conflicts = ConflictSet.Empty;
    private ForecastSettings _settings = ForecastSettings.Default;
    private IReadOnlyDictionary<Headcode, int> _entryDelays = new Dictionary<Headcode, int>();

    public PlanningEngine(PlanningEngineOptions? options = null, TimeProvider? timeProvider = null, ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? new PlanningEngineOptions();
        _time = timeProvider ?? TimeProvider.System;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PlanningEngine>();
        _forecastOptions = _options.Forecast;
        _conflictOptions = _options.Conflicts;
        _state = CreateState(null);
    }

    /// <summary>Raised on the engine loop after each snapshot is published. Handlers must be quick and must not throw.</summary>
    public event EventHandler<PlanSnapshot>? SnapshotPublished;

    /// <summary>
    /// Raised on the engine loop, in order, for every applied command, undo and redo, and when the history is dropped
    /// (<see cref="Clear"/> or a different timetable), so the session store can keep them (S16). Not raised for
    /// <see cref="RestorePlan"/>. Handlers must be quick.
    /// </summary>
    public event EventHandler<PlanStep>? PlanStepped;

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

    /// <summary>
    /// Forecast over this network (from the profile's corridors) with these settings, and find conflicts with these
    /// options, from now on; null keeps the current one. Publishes a new forecast straight away.
    /// </summary>
    public void SetForecast(ForecastNetwork? network = null, ForecastOptions? options = null, ConflictOptions? conflicts = null) =>
        _queue.Writer.TryWrite(new Input(InputKind.Forecast, Network: network, ForecastOptions: options, ConflictOptions: conflicts));

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

    /// <summary>
    /// Replace the edit history with the one these steps leave (<see cref="PlanLog.Fold"/>) over the current timetable,
    /// and the plan with it: to resume a saved session, or to show a reviewed one as it was. Commands that no longer
    /// apply are dropped. <see cref="PlanStepped"/> is not raised. The task completes on the engine loop once done.
    /// </summary>
    public Task RestorePlan(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Writer.TryWrite(new Input(InputKind.Restore, Steps: steps, Step: done));
        return done.Task;
    }

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
                    RaiseStep(new PlanStep(PlanStepKind.Reset));
                    forced = true;
                    break;
                case InputKind.Restore:
                    Restore(input.Steps!);
                    input.Step!.TrySetResult(true);
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
                case InputKind.Forecast:
                    _network = input.Network ?? _network;
                    _forecastOptions = input.ForecastOptions ?? _forecastOptions;
                    _conflictOptions = input.ConflictOptions ?? _conflictOptions;
                    _forecastSettingsVersion++;
                    forced = true;
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
                RaiseStep(PlanStep.Execute(command, description));
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
        RaiseStep(new PlanStep(PlanStepKind.Undo, Description: entry.Description));
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
        RaiseStep(new PlanStep(PlanStepKind.Redo, Description: entry.Description));
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
            RaiseStep(new PlanStep(PlanStepKind.Reset));
        }

        _undone.Clear();
        _plan = Replay();
        _planHistory = null;
        _planVersion++;
        Rebuild();
    }

    private void Restore(IReadOnlyList<PlanStep> steps)
    {
        var before = _plan;
        var (done, undone) = PlanLog.Fold(steps);
        _done.Clear();
        _undone.Clear();
        var plan = _basePlan;
        foreach (var step in done)
        {
            var command = step.Command!;
            string description;
            try
            {
                description = step.Description ?? command.Describe(plan);
                (plan, var result) = plan.Apply(command);
                if (!result.Applied)
                {
                    LogCommandDropped(description, result.Error);
                    continue;
                }

                _done.Add(new PlanHistoryEntry(command, description, result.Warnings));
            }
#pragma warning disable CA1031 // A command saved by an older version that now throws is dropped, not fatal.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCommandFailed(ex, command.GetType().Name);
            }
        }

        // Next redo last, as the engine keeps them.
        for (var i = undone.Count - 1; i >= 0; i--)
        {
            var command = undone[i].Command!;
            _undone.Add(new PlanHistoryEntry(command, undone[i].Description ?? Describe(command, plan), []));
        }

        SetPlan(plan, before);
        LogRestored(_done.Count, _undone.Count, steps.Count);
    }

    private static string Describe(IPlanCommand command, WorkingPlan plan)
    {
        try
        {
            return command.Describe(plan);
        }
#pragma warning disable CA1031 // Only for display.
        catch (Exception)
#pragma warning restore CA1031
        {
            return command.GetType().Name;
        }
    }

    private void RaiseStep(PlanStep step)
    {
        if (PlanStepped is not { } handlers)
        {
            return;
        }

        var clock = _state.Clock.Reading;
        step = step with { SimTime = clock.HasTime ? clock.LastReported : null, At = _time.GetUtcNow() };
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<PlanStep>>())
        {
            try
            {
                handler(this, step);
            }
#pragma warning disable CA1031 // A faulty handler must not stop the engine or the other handlers.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogHandlerFailed(ex);
            }
        }
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
        var trains = _state.Trains.ToArray();
        var clock = _state.Clock.Reading;
        UpdateForecast(trains, clock);
        var snapshot = new PlanSnapshot(
            ++_sequence,
            _timetable,
            trains,
            clock,
            _state.Summarise(),
            _state.Version,
            _state.SessionStart,
            _plan,
            _planHistory,
            _planVersion,
            _entryDelays,
            _forecast,
            _conflicts,
            _settings);
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

    /// <summary>Re-forecasts if the live state, the plan or the forecast settings changed since the last one.</summary>
    private void UpdateForecast(IReadOnlyList<LiveTrain> trains, SimClockReading clock)
    {
        var key = (_state.Version, _planVersion, _forecastSettingsVersion);
        if (key == _forecastKey)
        {
            return;
        }

        _forecastKey = key;
        _settings = new ForecastSettings(_network, _forecastOptions, _conflictOptions);
        _entryDelays = new Dictionary<Headcode, int>(_state.EntryDelays);
        var started = _time.GetTimestamp();
        try
        {
            _forecast = Forecaster.Run(new ForecastRequest(_plan)
            {
                LiveTrains = trains,
                Now = clock.HasTime ? clock.LastReported : null,
                SessionStart = _state.SessionStart,
                EntryDelays = _entryDelays,
                Network = _network,
                Options = _forecastOptions,
            });
        }
#pragma warning disable CA1031 // A forecaster fault must not stop the live picture: the forecast is dropped.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogForecastFailed(ex);
            _forecast = Forecast.Empty;
            _conflicts = ConflictSet.Empty;
            return;
        }

        var elapsed = _time.GetElapsedTime(started);
        if (elapsed > SlowForecast)
        {
            LogForecastSlow(_forecast.Trains.Count, elapsed.TotalMilliseconds);
        }

        started = _time.GetTimestamp();
        try
        {
            _conflicts = ConflictDetector.Detect(new ConflictRequest(_plan, _forecast) { Network = _network, Options = _conflictOptions });
        }
#pragma warning disable CA1031 // A detector fault must not stop the live picture: no conflicts are shown.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogConflictsFailed(ex);
            _conflicts = ConflictSet.Empty;
        }

        elapsed = _time.GetElapsedTime(started);
        if (elapsed > SlowForecast)
        {
            LogConflictsSlow(_conflicts.Count, elapsed.TotalMilliseconds);
        }

        if (_forecast.Trains.Count(t => t.Status == TrainForecastStatus.Unavailable) is var failed and > 0)
        {
            LogForecastUnavailable(failed);
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
        Forecast,
        Restore,
    }

    private readonly record struct Input(
        InputKind Kind,
        GatewayEvent? Event = null,
        WorkingTimetable? Timetable = null,
        IPlanCommand? Command = null,
        TaskCompletionSource<PlanCommandResult>? Result = null,
        TaskCompletionSource<bool>? Step = null,
        ForecastNetwork? Network = null,
        ForecastOptions? ForecastOptions = null,
        ConflictOptions? ConflictOptions = null,
        IReadOnlyList<PlanStep>? Steps = null);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan restored: {Done} edits, {Undone} to redo, from {Steps} steps")]
    private partial void LogRestored(int done, int undone, int steps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The forecast failed; no forecast is shown")]
    private partial void LogForecastFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Forecast of {Trains} trains took {Milliseconds:0} ms")]
    private partial void LogForecastSlow(int trains, double milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conflict detection failed; no conflicts are shown")]
    private partial void LogConflictsFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Finding {Conflicts} conflicts took {Milliseconds:0} ms")]
    private partial void LogConflictsSlow(int conflicts, double milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The forecast failed for {Trains} trains; they are shown without one")]
    private partial void LogForecastUnavailable(int trains);
}
