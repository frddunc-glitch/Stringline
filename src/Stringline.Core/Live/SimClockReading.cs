namespace Stringline.Core.Live;

/// <summary>
/// A frozen copy of a <see cref="SimClock"/>: everything needed to interpolate the sim time, as a value, so a reader on
/// another thread (the UI's now line) can keep interpolating between snapshots without touching the clock itself.
/// </summary>
/// <param name="HasTime">True once a clock has been seen.</param>
/// <param name="AreaId">The sim's area ID from the last clock.</param>
/// <param name="LastReported">The last clock SimSig reported.</param>
/// <param name="IntervalMs">The last reported interval (500 at 1×, 1 at flat out).</param>
/// <param name="MeasuredSpeed">The measured speed, if any.</param>
/// <param name="Speed">Sim seconds per wall second used for interpolation (0 when paused or not yet known).</param>
/// <param name="BaseSim">Sim seconds at <paramref name="BaseWall"/>.</param>
/// <param name="Limit">Interpolation never passes this (the end of the sim minute, or the latest train event time).</param>
public readonly record struct SimClockReading(
    bool HasTime,
    string? AreaId,
    SimTime LastReported,
    bool Paused,
    int IntervalMs,
    double? MeasuredSpeed,
    double Speed,
    double BaseSim,
    DateTimeOffset BaseWall,
    double Limit)
{
    /// <summary>No clock seen yet.</summary>
    public static SimClockReading None => default;

    /// <summary>SimSig's flat-out setting, where the interval says nothing about the speed.</summary>
    public bool IsMaximumSpeed => HasTime && IntervalMs == 1;

    /// <summary>The interpolated sim time at <paramref name="wall"/>, in fractional seconds (0 without a clock).</summary>
    public double SecondsAt(DateTimeOffset wall)
    {
        if (!HasTime)
        {
            return 0;
        }

        var elapsed = Math.Max(0, (wall - BaseWall).TotalSeconds);
        return Math.Min(BaseSim + (elapsed * Speed), Math.Max(BaseSim, Limit));
    }

    /// <summary>The interpolated sim time at <paramref name="wall"/>, to the whole second.</summary>
    public SimTime NowAt(DateTimeOffset wall) => new((int)Math.Floor(SecondsAt(wall)));
}
