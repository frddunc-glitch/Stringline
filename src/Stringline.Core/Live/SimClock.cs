using Stringline.Core.Events;

namespace Stringline.Core.Live;

/// <summary>
/// The sim clock between <c>clock_msg</c>s. SimSig sends the clock only on each sim-minute change and on speed or
/// pause changes, so the time in between is interpolated from the wall clock at the measured speed.
/// </summary>
/// <remarks>
/// <para>The speed is <b>measured</b> from successive running clock messages (Δsim / Δwall), because the reported
/// interval says nothing at SimSig's flat-out setting (interval 1) and a replay at 8× still reports the recorded
/// interval. Until a measurement exists, 500 / interval is the first guess, and at flat out the clock holds.</para>
/// <para>Interpolation never runs past the end of the current sim minute (the next <c>clock_msg</c> is due then)
/// or past the latest train event time seen, whichever is later. That keeps the now line from overshooting
/// if messages stall. Wall times come from the events' <see cref="GatewayEvent.ReceivedAt"/>, so live and replayed
/// sources behave the same, and <see cref="Now"/> reads the <see cref="TimeProvider"/>.</para>
/// Not thread-safe: owned by one writer.
/// </remarks>
public sealed class SimClock
{
    /// <summary>A speed measurement needs at least this many sim seconds between the two clocks.</summary>
    private const int MinMeasureSimSeconds = 20;

    /// <summary>Measurements outside this range are noise (e.g. a stale <c>idrequest</c> reply followed by a real tick).</summary>
    private const double MinSpeed = 0.05;

    private const double MaxSpeed = 2000;

    private readonly TimeProvider _timeProvider;

    private double _baseSim;
    private DateTimeOffset _baseWall;
    private double _limit;

    private (SimTime Sim, DateTimeOffset Wall)? _measureFrom;

    public SimClock(TimeProvider? timeProvider = null) => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>True once a clock has been seen.</summary>
    public bool HasTime { get; private set; }

    /// <summary>The sim's area ID from the last clock, e.g. <c>waterloo</c>.</summary>
    public string? AreaId { get; private set; }

    /// <summary>The last clock SimSig reported.</summary>
    public SimTime LastReported { get; private set; }

    public bool Paused { get; private set; }

    /// <summary>The last reported interval in milliseconds (500 at 1×, 1 at flat out).</summary>
    public int IntervalMs { get; private set; }

    /// <summary>The speed measured from clock arrivals, or null until two running clocks at the same interval have been seen.</summary>
    public double? MeasuredSpeed { get; private set; }

    /// <summary>
    /// Sim seconds per wall second used for interpolation: 0 when paused, else the measured speed, else 500 / interval
    /// (0 at flat out until measured).
    /// </summary>
    public double Speed => !HasTime || Paused ? 0 : MeasuredSpeed ?? NominalSpeed;

    private double NominalSpeed => IntervalMs <= 1 ? 0 : 500.0 / IntervalMs;

    /// <summary>The interpolated sim time now, by the <see cref="TimeProvider"/>.</summary>
    public SimTime Now => NowAt(_timeProvider.GetUtcNow());

    /// <summary>The interpolated sim time at <paramref name="wall"/>, to the whole second.</summary>
    public SimTime NowAt(DateTimeOffset wall) => new((int)Math.Floor(SecondsAt(wall)));

    /// <summary>The interpolated sim time at <paramref name="wall"/>, in fractional seconds, for a smooth now line.</summary>
    public double SecondsAt(DateTimeOffset wall) => Reading.SecondsAt(wall);

    /// <summary>The clock as a value, for readers on other threads to interpolate from.</summary>
    public SimClockReading Reading => new(HasTime, AreaId, LastReported, Paused, IntervalMs, MeasuredSpeed, Speed, _baseSim, _baseWall, _limit);

    /// <summary>Take a <c>clock_msg</c>.</summary>
    public void Observe(ClockEvent clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var speedChanged = !HasTime || clock.IntervalMs != IntervalMs || clock.Clock < LastReported;
        if (speedChanged)
        {
            MeasuredSpeed = null;
        }

        if (speedChanged || clock.Paused || Paused || _measureFrom is null)
        {
            // A first clock, speed change, jump back or pause: a measurement can only start from a running clock.
            _measureFrom = clock.Paused ? null : (clock.Clock, clock.ReceivedAt);
        }
        else if (_measureFrom is { } from && clock.Clock - from.Sim >= MinMeasureSimSeconds)
        {
            var wallSeconds = (clock.ReceivedAt - from.Wall).TotalSeconds;
            var speed = wallSeconds > 0 ? (clock.Clock - from.Sim) / wallSeconds : 0;
            if (speed is >= MinSpeed and <= MaxSpeed)
            {
                MeasuredSpeed = speed;
            }

            _measureFrom = (clock.Clock, clock.ReceivedAt);
        }

        HasTime = true;
        AreaId = clock.AreaId;
        LastReported = clock.Clock;
        Paused = clock.Paused;
        IntervalMs = clock.IntervalMs;
        _baseSim = clock.Clock.Seconds;
        _baseWall = clock.ReceivedAt;
        _limit = EndOfMinute(clock.Clock);
    }

    /// <summary>
    /// Take the sim time of a train event. <c>train_location</c> times run up to a minute ahead of the last clock,
    /// so a later one moves the clock on (it never moves it back).
    /// </summary>
    public void ObserveEventTime(SimTime time, DateTimeOffset wall)
    {
        if (!HasTime)
        {
            return;
        }

        if (time.Seconds > SecondsAt(wall))
        {
            _baseSim = time.Seconds;
            _baseWall = wall;
            _limit = Math.Max(_limit, time.Seconds);
        }
    }

    /// <summary>Forget everything, e.g. after a replay is rewound.</summary>
    public void Reset()
    {
        HasTime = false;
        AreaId = null;
        LastReported = default;
        Paused = false;
        IntervalMs = 0;
        MeasuredSpeed = null;
        _measureFrom = null;
        _baseSim = 0;
        _limit = 0;
        _baseWall = default;
    }

    /// <summary>Just before the next minute boundary: the next <c>clock_msg</c> is due there.</summary>
    private static double EndOfMinute(SimTime time) => (Math.Floor(time.Seconds / 60.0) * 60) + 60 - 0.001;
}
