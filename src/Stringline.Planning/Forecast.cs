using Stringline.Core;

namespace Stringline.Planning;

/// <summary>Where a <see cref="ForecastPoint"/>'s times come from.</summary>
public enum ForecastBasis
{
    /// <summary>No time: the point is untimed in the plan, or the train went past it without reporting it.</summary>
    None,

    /// <summary>Reported by the sim: the train has been here (and left, or passed).</summary>
    Actual,

    /// <summary>The train has arrived here (reported) and not yet left: the departure is forecast.</summary>
    Arrived,

    /// <summary>Projected by the forecaster.</summary>
    Forecast,
}

/// <summary>
/// A train's expected (or actual) times at one point of its plan (<c>04-…md</c> §2).
/// </summary>
/// <param name="Index">The plan point index (the same as the WTT's).</param>
/// <param name="Arrive">Arrival, or the pass time at a pass; null at an origin or with no time.</param>
/// <param name="Depart">Departure, or the pass time at a pass; null at a destination or with no time.</param>
/// <param name="DelaySeconds">The last time here less the planned one (negative = early), if both are known.</param>
public sealed record ForecastPoint(int Index, Tiploc Location, SimTime? Arrive, SimTime? Depart, bool IsPass, ForecastBasis Basis, int? DelaySeconds)
{
    /// <summary>Arrival if any, else departure.</summary>
    public SimTime? FirstTime => Arrive ?? Depart;

    /// <summary>Departure if any, else arrival.</summary>
    public SimTime? LastTime => Depart ?? Arrive;

    public override string ToString() =>
        $"{Location} {(IsPass ? "pass " + Depart : $"{Arrive?.ToString() ?? "-"}/{Depart?.ToString() ?? "-"}")} {Basis}";
}

/// <summary>Where a train stands in its run, as the forecaster sees it.</summary>
public enum TrainForecastStatus
{
    /// <summary>Not seen in the sim yet: forecast from its planned start (plus any entry delay the sim gives).</summary>
    NotStarted,

    /// <summary>Running: forecast from its latest actual.</summary>
    Running,

    /// <summary>Reached its last point (or became its next working, or left the area); nothing is forecast.</summary>
    Finished,

    /// <summary>The forecast failed for this train (logged); it has no points.</summary>
    Unavailable,

    /// <summary>
    /// SimSig will not run it this session (S34, #16): not seeded and booked before the start, seeded in another group,
    /// or formed from such a train (see <see cref="TrainForecast.NotRunning"/>). It has no points, and is left out of
    /// conflicts, suggestions, the optimiser and the regulation list.
    /// </summary>
    NotRunning,
}

/// <summary>One train's forecast along its working plan.</summary>
/// <param name="Points">The train's running points (<see cref="TrainPlan.RunningPoints"/>) with actual or forecast times.</param>
/// <param name="LiveUid">The sim uid of the live train matched to it, if any.</param>
/// <param name="PossiblyStopped">
/// Running, but not reported for more than <see cref="ForecastOptions.PossiblyStoppedSeconds"/> past when it should
/// have reached its next point: it may be stopped (at a signal, or failed).
/// </param>
/// <param name="KnockOnSeconds">
/// How much later the train reaches its last point because of other trains (the interaction pass), or 0 with the
/// interaction pass off.
/// </param>
public sealed record TrainForecast(
    TrainId Train,
    Headcode Headcode,
    TrainForecastStatus Status,
    IReadOnlyList<ForecastPoint> Points,
    string? LiveUid = null,
    bool PossiblyStopped = false,
    int KnockOnSeconds = 0)
{
    /// <summary>
    /// Where a block in force over a run held the train at the point it runs from, in the order they happened. A
    /// train already on its way is not held, so it has none for that run.
    /// </summary>
    public IReadOnlyList<BlockHold> BlockHolds { get; init; } = [];

    /// <summary>Why the train is <see cref="TrainForecastStatus.NotRunning"/>; null otherwise.</summary>
    public NotRunningReason? NotRunning { get; init; }

    /// <summary>
    /// A seeded train of the start group not seen soon after the sim started (S34): it may have been removed. It is
    /// still forecast.
    /// </summary>
    public bool NotSeen { get; init; }

    /// <summary>
    /// The timing location the train last reported that its plan does not book (S36, #17), when that report came after
    /// its last booked one: the rest of the run to the next point is forecast from there. Null otherwise.
    /// </summary>
    public ProgressAnchor? Anchor { get; init; }

    /// <summary>The index in <see cref="Points"/> of the first point with a forecast time, or -1 if none.</summary>
    public int FirstForecast
    {
        get
        {
            for (var i = 0; i < Points.Count; i++)
            {
                if (Points[i].Basis is ForecastBasis.Forecast or ForecastBasis.Arrived)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// True if some forecast time is a minute or more off the plan. Less is on time, as railway delay minutes go:
    /// the forecast's shortest dwells often differ from the WTT's half-minute bookings by seconds.
    /// </summary>
    public bool Deviates => Points.Any(p => (p.Basis is ForecastBasis.Forecast or ForecastBasis.Arrived) && p.DelaySeconds is { } d && Math.Abs(d) >= SimTime.SecondsPerMinute);

    /// <summary>The delay at the last point with a time, if known.</summary>
    public int? FinalDelaySeconds => Points.LastOrDefault(p => p.DelaySeconds is not null)?.DelaySeconds;

    /// <summary>The point with this plan index, if the train runs to it.</summary>
    public ForecastPoint? At(int index) => index >= 0 && index < Points.Count ? Points[index] : null;

    public override string ToString() => $"{Headcode} {Status}{(FinalDelaySeconds is { } d ? $" {d:+0;-0}s" : string.Empty)}";
}

/// <summary>
/// A report at a timing location between two of a train's booked points that its plan does not book (S36, #17): SimSig
/// reports those too. The forecast of the run on to <paramref name="Before"/> starts from it. It has no booked time, so
/// no delay is measured there.
/// </summary>
/// <param name="Time">When the train passed (or reached) it.</param>
/// <param name="After">The plan point the train last reported before it.</param>
/// <param name="Before">The next timed plan point, which the run is forecast to.</param>
/// <param name="Share">How far along the run from <paramref name="After"/> to <paramref name="Before"/> it lies, in minimum run time (0–1).</param>
public sealed record ProgressAnchor(Tiploc Location, SimTime Time, int After, int Before, double Share)
{
    public override string ToString() => $"passed {Location} {Time} (not booked)";
}

/// <summary>A train held by a block (<c>06-…md</c> §2): it would have left point <paramref name="Index"/> at <paramref name="Departure"/>, into the block.</summary>
/// <param name="Index">The plan point it is held at.</param>
/// <param name="ToIndex">The next timed point, at the other end of the run the block covers.</param>
/// <param name="DisruptionId">The block's <see cref="Disruption.Id"/>.</param>
/// <param name="HeldUntil">The block's end: when it leaves instead.</param>
public sealed record BlockHold(int Index, int ToIndex, string DisruptionId, SimTime Departure, SimTime HeldUntil)
{
    public int Seconds => HeldUntil - Departure;
}

/// <summary>Settings for the <see cref="Forecaster"/>.</summary>
public sealed record ForecastOptions
{
    /// <summary>Run the interaction pass (knock-on delays from headways and single-line sections; <c>06-…md</c> §2 step 4).</summary>
    public bool Interaction { get; init; } = true;

    /// <summary>The shortest dwell at a stop when the WTT gives none (Fred, S11: 30 s).</summary>
    public int DefaultDwellSeconds { get; init; } = 30;

    /// <summary>The shortest time from arriving to forming the next working (or dividing) when the WTT gives none.</summary>
    public int DefaultTurnaroundSeconds { get; init; } = 30;

    /// <summary>A running train not reported for this long past its forecast is <see cref="TrainForecast.PossiblyStopped"/> (Fred, S11: 5 min).</summary>
    public int PossiblyStoppedSeconds { get; init; } = 300;

    /// <summary>
    /// Forecast a running train on from a timing location it reported that its plan does not book (S36,
    /// <see cref="TrainForecast.Anchor"/>); false forecasts from its last booked report only, as before S36.
    /// </summary>
    public bool UnbookedAnchors { get; init; } = true;
}

/// <summary>
/// Every train's forecast at one moment (<c>06-…md</c> §2), made by <see cref="Forecaster.Run"/>. Immutable. Cancelled
/// trains, and trains wholly before the session started, are left out.
/// </summary>
/// <param name="Now">The sim time it was made for; no forecast time is before it. Null when there is no clock.</param>
/// <param name="Interaction">True if the interaction pass ran.</param>
/// <param name="Independent">
/// With the interaction pass, the forecast without it (steps 1–3: trains run through each other), which conflicts are
/// found on (S14); null when this forecast is that one.
/// </param>
public sealed class Forecast(SimTime? Now, bool Interaction, IReadOnlyList<TrainForecast> Trains, Forecast? Independent = null)
{
    /// <summary>Which trains SimSig will run, from the session's start (S34).</summary>
    public StartingTrains Starting { get; init; } = StartingTrains.Unknown;

    private readonly Forecast? _independent = Independent;
    private Dictionary<TrainId, TrainForecast>? _byTrain;

    public static Forecast Empty { get; } = new(null, false, []);

    public SimTime? Now { get; } = Now;

    public bool Interaction { get; } = Interaction;

    /// <summary>The forecast without the interaction pass: this one if it did not run.</summary>
    public Forecast Independent => _independent ?? this;

    /// <summary>In plan order.</summary>
    public IReadOnlyList<TrainForecast> Trains { get; } = Trains ?? throw new ArgumentNullException(nameof(Trains));

    public TrainForecast? Find(TrainId train)
    {
        _byTrain ??= Trains.ToDictionary(t => t.Train);
        return _byTrain.GetValueOrDefault(train);
    }

    /// <summary>
    /// Trains to draw on the forecast layer (Fred, S11): those running, and those not started whose forecast differs
    /// from the plan (a late entry or a knock-on). Trains on time and not started show only their plan.
    /// </summary>
    public IEnumerable<TrainForecast> Drawn => Trains.Where(t =>
        t.Status == TrainForecastStatus.Running || (t.Status == TrainForecastStatus.NotStarted && t.Deviates));
}
