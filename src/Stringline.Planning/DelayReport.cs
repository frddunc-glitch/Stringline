using Stringline.Core;

namespace Stringline.Planning;

/// <summary>Where a delay report time comes from.</summary>
public enum DelayBasis
{
    /// <summary>No time: the point is not reached and has no forecast (cancelled, not run, or untimed).</summary>
    None,

    /// <summary>Reported by the sim.</summary>
    Actual,

    /// <summary>Projected by the forecaster.</summary>
    Forecast,
}

/// <summary>Where a train stands, for the delay report.</summary>
public enum DelayTrainState
{
    NotStarted,
    Running,
    Finished,
    Cancelled,

    /// <summary>SimSig will not run it this session (S34): see <see cref="TrainForecast.NotRunning"/>.</summary>
    NotRunning,

    /// <summary>No forecast: the train ran wholly before the session started, or its forecast failed.</summary>
    NoForecast,
}

/// <summary>Which trains a <see cref="DelayReport"/> lists.</summary>
public enum DelayReportFilter
{
    /// <summary>Trains with at least one actual time (the default: what has run).</summary>
    Seen,

    /// <summary>Every train in the plan.</summary>
    All,
}

/// <summary>
/// One timing point of a train in the delay report (SES-2): booked, planned, and actual or forecast times, with the
/// delay against the WTT and against the working plan.
/// </summary>
/// <param name="Index">The plan point index (the same as the WTT's).</param>
/// <param name="Arrive">The actual arrival (or pass), else the forecast one, if any.</param>
/// <param name="Depart">The actual departure (or pass), else the forecast one, if any.</param>
/// <param name="Runs">False for points after a short termination.</param>
/// <param name="WttDelaySeconds">
/// The actual or forecast time less the booked one (negative = early), at the departure or pass, or at the arrival
/// where there is no departure to compare (a destination). Null with no booking (a clone) or no time.
/// </param>
/// <param name="PlanDelaySeconds">The same against the working plan.</param>
/// <param name="DelayBasis">Whether the time the delays compare is actual or forecast.</param>
public sealed record DelayReportPoint(
    int Index,
    Tiploc Location,
    bool IsPass,
    bool Runs,
    SimTime? WttArrive,
    SimTime? WttDepart,
    SimTime? PlanArrive,
    SimTime? PlanDepart,
    SimTime? Arrive,
    SimTime? Depart,
    DelayBasis ArriveBasis,
    DelayBasis DepartBasis,
    string? WttPlatform,
    string? Platform,
    int? WttDelaySeconds,
    int? PlanDelaySeconds,
    DelayBasis DelayBasis)
{
    /// <summary>The delay against the WTT, or against the plan for a train with no booking (a clone).</summary>
    public int? DelaySeconds => WttDelaySeconds ?? PlanDelaySeconds;

    /// <summary>The train has reported here.</summary>
    public bool IsActual => ArriveBasis == DelayBasis.Actual || DepartBasis == DelayBasis.Actual;
}

/// <summary>
/// One train in the delay report: its points and a summary for a worst-offenders list. Delays are against the WTT, or
/// the plan for a clone (<see cref="DelayReportPoint.DelaySeconds"/>).
/// </summary>
/// <param name="Start">The first planned time.</param>
/// <param name="Latest">The last point the train has reported at, if any.</param>
/// <param name="Worst">The point with the greatest actual delay, if any.</param>
/// <param name="Final">The last point it runs to with a delay, actual or forecast, if any.</param>
public sealed record DelayReportTrain(
    TrainId Train,
    Headcode Headcode,
    string? CategoryId,
    PlanOrigin Origin,
    string? LiveUid,
    DelayTrainState State,
    bool IsModified,
    Tiploc? From,
    Tiploc? To,
    SimTime? Start,
    DelayReportPoint? Latest,
    DelayReportPoint? Worst,
    DelayReportPoint? Final,
    IReadOnlyList<DelayReportPoint> Points)
{
    public int? LatestDelaySeconds => Latest?.DelaySeconds;

    public int? WorstDelaySeconds => Worst?.DelaySeconds;

    public int? FinalDelaySeconds => Final?.DelaySeconds;

    public bool HasActuals => Latest is not null;
}

/// <summary>
/// The delay table (SES-2, <c>05-…md</c> §11): every train's booked, planned, and actual or forecast times at each
/// timing point, with delays. Pure; built from one <see cref="PlanSnapshot"/> through <see cref="TrainSchedule"/>, so
/// it agrees with the train detail panel.
/// </summary>
/// <param name="Trains">In plan order.</param>
/// <param name="UnmatchedLive">Live trains whose current working is not matched to the plan, so are not listed.</param>
public sealed record DelayReport(SimTime? Now, SimTime? SessionStart, IReadOnlyList<DelayReportTrain> Trains, int UnmatchedLive)
{
    /// <summary>Trains SimSig will not run this session (S34), counted apart: none of them is late.</summary>
    public int NotRunning { get; init; }

    public static DelayReport Empty { get; } = new(null, null, [], 0);

    public static DelayReport For(PlanSnapshot snapshot, DelayReportFilter filter = DelayReportFilter.Seen)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var trains = new List<DelayReportTrain>();
        foreach (var plan in snapshot.Plan.Trains)
        {
            // Most of a day's trains have not run: skip building their schedules when only those seen are wanted.
            if (filter == DelayReportFilter.Seen && snapshot.Forecast.Find(plan.Id)?.Points.Any(p => p.Basis is ForecastBasis.Actual or ForecastBasis.Arrived) != true)
            {
                continue;
            }

            if (TrainSchedule.For(snapshot, plan.Id) is not { } schedule)
            {
                continue;
            }

            var train = Of(schedule);
            if (filter == DelayReportFilter.All || train.HasActuals)
            {
                trains.Add(train);
            }
        }

        var unmatched = snapshot.Trains.Count(t => !t.Current.IsMatched);
        var now = snapshot.Forecast.Now ?? (snapshot.Clock.HasTime ? snapshot.Clock.LastReported : null);
        var notRunning = snapshot.Forecast.Trains.Count(t => t.Status == TrainForecastStatus.NotRunning);
        return new DelayReport(now, snapshot.SessionStart, trains, unmatched) { NotRunning = notRunning };
    }

    /// <summary>One train's report from its schedule.</summary>
    public static DelayReportTrain Of(TrainSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var plan = schedule.Plan;
        var points = schedule.Rows.Select(Point).ToList();
        DelayReportPoint? latest = null, worst = null, final = null;
        foreach (var p in points)
        {
            if (p.IsActual)
            {
                latest = p;
                if (p.DelayBasis == DelayBasis.Actual && p.DelaySeconds is { } d && (worst?.DelaySeconds is not { } w || d > w))
                {
                    worst = p;
                }
            }

            if (p.Runs && p.DelaySeconds is not null)
            {
                final = p;
            }
        }

        var state = plan.Cancelled ? DelayTrainState.Cancelled : schedule.Forecast?.Status switch
        {
            TrainForecastStatus.NotStarted => DelayTrainState.NotStarted,
            TrainForecastStatus.Running => DelayTrainState.Running,
            TrainForecastStatus.Finished => DelayTrainState.Finished,
            TrainForecastStatus.NotRunning => DelayTrainState.NotRunning,
            _ => DelayTrainState.NoForecast,
        };
        var running = plan.RunningPoints.Where(p => p.IsTimed).ToList();
        return new DelayReportTrain(
            plan.Id,
            plan.Headcode,
            plan.CategoryId,
            plan.Origin,
            schedule.Live?.SimUid ?? schedule.Forecast?.LiveUid,
            state,
            plan.IsModified,
            running.Count > 0 ? running[0].Location : null,
            running.Count > 0 ? running[^1].Location : null,
            running.Count > 0 ? running[0].FirstTime : null,
            latest,
            worst,
            final,
            points);
    }

    private static DelayReportPoint Point(ScheduleRow r)
    {
        var arriveBasis = r.ActualArrive is not null ? DelayBasis.Actual : r.ForecastArrive is not null ? DelayBasis.Forecast : DelayBasis.None;
        var departBasis = r.ActualDepart is not null ? DelayBasis.Actual : r.ForecastDepart is not null ? DelayBasis.Forecast : DelayBasis.None;
        var arrive = r.ActualArrive ?? r.ForecastArrive;
        var depart = r.ActualDepart ?? r.ForecastDepart;

        var (wtt, wttBasis) = r.Runs ? Compare(arrive, depart, arriveBasis, departBasis, r.WttArrive, r.WttDepart) : default;
        var (planned, planBasis) = r.Runs ? Compare(arrive, depart, arriveBasis, departBasis, r.PlanArrive, r.PlanDepart) : default;
        return new DelayReportPoint(
            r.Index,
            r.Location,
            r.IsPass,
            r.Runs,
            r.WttArrive,
            r.WttDepart,
            r.PlanArrive,
            r.PlanDepart,
            arrive,
            depart,
            arriveBasis,
            departBasis,
            r.WttPlatform,
            r.Platform,
            wtt,
            planned,
            planned is not null ? planBasis : wttBasis);
    }

    /// <summary>
    /// The delay at a point: the departure (or pass) against the booked one if both have one; else the arrival against
    /// the booked arrival; else whichever single time each side has (a pass against a stop). A train standing at a stop
    /// (arrived, departure only forecast) is compared at its arrival, so its delay there is actual until it leaves.
    /// </summary>
    private static (int? Seconds, DelayBasis Basis) Compare(SimTime? arrive, SimTime? depart, DelayBasis arriveBasis, DelayBasis departBasis, SimTime? bookedArrive, SimTime? bookedDepart)
    {
        if (arriveBasis == DelayBasis.Actual && departBasis != DelayBasis.Actual && arrive is { } standing && bookedArrive is { } booked)
        {
            return (standing - booked, DelayBasis.Actual);
        }

        if (depart is { } d && bookedDepart is { } bd)
        {
            return (d - bd, departBasis);
        }

        if (arrive is { } a && bookedArrive is { } ba)
        {
            return (a - ba, arriveBasis);
        }

        if ((depart ?? arrive) is { } t && (bookedDepart ?? bookedArrive) is { } b)
        {
            return (t - b, depart is not null ? departBasis : arriveBasis);
        }

        return (null, DelayBasis.None);
    }
}
