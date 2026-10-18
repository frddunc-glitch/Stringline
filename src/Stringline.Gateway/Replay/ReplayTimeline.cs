using System.Runtime.CompilerServices;
using Stringline.Core;
using Stringline.Gateway.Recording;

namespace Stringline.Gateway.Replay;

/// <summary>One recorded frame handed out by a <see cref="ReplayTimeline"/>.</summary>
/// <param name="Index">The frame's index in <see cref="SslogRecording.Messages"/>.</param>
/// <param name="RewoundTo">
/// Set on the first frame after a backward seek, to the seek target: the replay restarted from the
/// beginning, so anything built from earlier frames is stale.
/// </param>
/// <param name="FastForward">True if handed out without pacing, while catching up to a seek target.</param>
public readonly record struct ReplayFrame(SslogMessage Message, int Index, SimTime? RewoundTo, bool FastForward)
{
    public bool Rewound => RewoundTo is not null;
}

/// <summary>
/// Plays a recording's frames back in real time, scaled by <see cref="Speed"/>, using the wall-clock gaps
/// between them as recorded. Can pause, change speed and seek to a sim time while playing; the controls are
/// thread-safe and take effect straight away, even mid-wait.
/// <para>
/// Seeking forward hands out every frame up to the target at once (flagged <see cref="ReplayFrame.FastForward"/>)
/// so consumers' state stays complete. Seeking back restarts from the first frame, flagging it
/// <see cref="ReplayFrame.Rewound"/>, then fast-forwards to the target.
/// </para>
/// </summary>
public sealed class ReplayTimeline
{
    private readonly SslogRecording _recording;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private TaskCompletionSource _changed = NewSignal();
    private double _speed;
    private bool _paused;
    private SimTime? _pendingSeek;
    private int _next;
    private SimTime? _position;

    // Pacing: recorded time "now" = _anchorRecorded + (real time since _anchorReal) × speed.
    private DateTimeOffset _anchorRecorded;
    private long _anchorReal;
    private bool _reading;

    public ReplayTimeline(SslogRecording recording, ReplayOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        options ??= new ReplayOptions();
        _recording = recording;
        _time = timeProvider ?? TimeProvider.System;
        _speed = ValidSpeed(options.Speed);
        _paused = options.StartPaused;
        _pendingSeek = options.StartAt;
    }

    public SslogRecording Recording => _recording;

    /// <summary>
    /// Playback speed relative to the recording: 1 plays as recorded, 8 eight times faster.
    /// <see cref="double.PositiveInfinity"/> plays as fast as the consumer reads.
    /// </summary>
    public double Speed
    {
        get
        {
            lock (_lock)
            {
                return _speed;
            }
        }

        set
        {
            var speed = ValidSpeed(value);
            lock (_lock)
            {
                Reanchor(RecordedNow());
                _speed = speed;
                Signal();
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>The sim time stamped on the last frame handed out, or null before the first.</summary>
    public SimTime? Position
    {
        get
        {
            lock (_lock)
            {
                return _position;
            }
        }
    }

    /// <summary>The index of the next frame to hand out; the frame count once finished.</summary>
    public int NextIndex
    {
        get
        {
            lock (_lock)
            {
                return _next;
            }
        }
    }

    public bool IsFinished
    {
        get
        {
            lock (_lock)
            {
                return _next >= _recording.Messages.Count && _pendingSeek is null;
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_paused)
            {
                Reanchor(RecordedNow());
                _paused = true;
                Signal();
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_paused)
            {
                _anchorReal = _time.GetTimestamp();
                _paused = false;
                Signal();
            }
        }
    }

    /// <summary>
    /// Moves playback to the first frame stamped at or after <paramref name="target"/> (accurate to the sim
    /// minute). Applies on the reading loop; a later seek replaces one not yet applied. Pause state is kept.
    /// </summary>
    public void SeekTo(SimTime target)
    {
        lock (_lock)
        {
            _pendingSeek = target;
            Signal();
        }
    }

    /// <summary>Hands out the frames, paced. Single consumer. Ends after the last frame; cancelling throws.</summary>
    public async IAsyncEnumerable<ReplayFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = _recording.Messages;
        lock (_lock)
        {
            if (_reading)
            {
                throw new InvalidOperationException("A replay timeline can only be read once.");
            }

            _reading = true;
            _anchorReal = _time.GetTimestamp();
            _anchorRecorded = messages.Count > 0 ? messages[0].At : default;
        }

        SimTime? rewoundTo = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayFrame? due = null;
            var catchUpTo = -1;
            TimeSpan? wait = null;
            Task changed;
            lock (_lock)
            {
                changed = _changed.Task;
                if (_pendingSeek is { } target)
                {
                    _pendingSeek = null;
                    catchUpTo = _recording.IndexOf(target);
                    if (catchUpTo < _next)
                    {
                        _next = 0;
                        rewoundTo = target;
                    }
                }

                if (catchUpTo < 0)
                {
                    if (_next >= messages.Count)
                    {
                        yield break;
                    }

                    wait = WaitFor(messages[_next].At);
                    if (wait is { } w && w <= TimeSpan.Zero)
                    {
                        due = Take(rewoundTo, fastForward: false);
                        rewoundTo = null;
                    }
                }
            }

            if (catchUpTo >= 0)
            {
                // Fast-forward outside the lock, a frame at a time, so a consumer can stop part-way.
                while (true)
                {
                    ReplayFrame frame;
                    lock (_lock)
                    {
                        if (_next >= catchUpTo || _next >= messages.Count)
                        {
                            // Pace on from the target, however long the catch-up took.
                            if (_next < messages.Count)
                            {
                                Reanchor(messages[_next].At);
                            }

                            break;
                        }

                        frame = Take(rewoundTo, fastForward: true);
                        rewoundTo = null;
                    }

                    yield return frame;
                }

                continue;
            }

            if (due is { } dueFrame)
            {
                yield return dueFrame;
                continue;
            }

            await WaitAsync(wait, changed, cancellationToken).ConfigureAwait(false);
        }
    }

    private ReplayFrame Take(SimTime? rewoundTo, bool fastForward)
    {
        var index = _next++;
        var message = _recording.Messages[index];
        if (message.Sim is { } sim)
        {
            _position = sim;
        }

        if (double.IsPositiveInfinity(_speed))
        {
            // Keep the anchor on the playhead, so slowing down later carries on from here.
            Reanchor(message.At);
        }

        return new ReplayFrame(message, index, rewoundTo, fastForward);
    }

    /// <summary>How long until <paramref name="at"/> is due; null (indefinitely) while paused.</summary>
    private TimeSpan? WaitFor(DateTimeOffset at)
    {
        if (_paused)
        {
            return null;
        }

        if (double.IsPositiveInfinity(_speed))
        {
            return TimeSpan.Zero;
        }

        var ahead = at - RecordedNow();
        return ahead <= TimeSpan.Zero ? TimeSpan.Zero : ahead / _speed;
    }

    private DateTimeOffset RecordedNow() =>
        _paused || double.IsPositiveInfinity(_speed)
            ? _anchorRecorded
            : _anchorRecorded + (_time.GetElapsedTime(_anchorReal) * _speed);

    private void Reanchor(DateTimeOffset recorded)
    {
        _anchorRecorded = recorded;
        _anchorReal = _time.GetTimestamp();
    }

    private void Signal()
    {
        _changed.TrySetResult();
        _changed = NewSignal();
    }

    /// <summary>Waits for <paramref name="wait"/> (null: indefinitely) or until a control changes.</summary>
    private async Task WaitAsync(TimeSpan? wait, Task changed, CancellationToken cancellationToken)
    {
        if (wait is not { } delayFor)
        {
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var stopDelay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(delayFor, _time, stopDelay.Token);
        await Task.WhenAny(delay, changed).ConfigureAwait(false);
        await stopDelay.CancelAsync().ConfigureAwait(false);
        await delay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static double ValidSpeed(double speed) =>
        speed > 0 && !double.IsNaN(speed)
            ? speed
            : throw new ArgumentOutOfRangeException(nameof(speed), speed, "Speed must be positive (or infinity for as fast as possible).");
}

/// <summary>How a replay starts.</summary>
public sealed record ReplayOptions
{
    /// <summary>Speed relative to the recording; <see cref="double.PositiveInfinity"/> for as fast as possible.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Seek here before playing; frames before it are fast-forwarded.</summary>
    public SimTime? StartAt { get; init; }

    public bool StartPaused { get; init; }

    /// <summary>
    /// Stamp events with the recorded wall-clock times instead of the time they are replayed. Off by default,
    /// so consumers see arrival times consistent with the replay speed, as they would live.
    /// </summary>
    public bool PreserveTimestamps { get; init; }
}
