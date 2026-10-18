using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>
/// One timing point of a <see cref="TrainSchedule"/>: what the WTT booked, what the plan now intends, and what the
/// train did or is expected to do there (<c>05-…md</c> §4).
/// </summary>
/// <param name="Index">The plan point index (the same as the WTT's).</param>
/// <param name="WttArrive">Booked arrival; null at an origin or a pass (a pass time is in <paramref name="WttDepart"/>, as in the WTT).</param>
/// <param name="PlanArrive">Planned arrival, as the train now runs (a terminating point arrives and does not depart).</param>
/// <param name="IsPass">The plan passes this point.</param>
/// <param name="TimesChanged">The plan's times or stop/pass differ from the booking.</param>
/// <param name="Runs">False for points after a short termination (the train no longer runs to them).</param>
/// <param name="ActualArrive">Reported arrival (or pass), if the train has been here.</param>
/// <param name="ActualDepart">Reported departure, if the train has left.</param>
/// <param name="ForecastArrive">Forecast arrival (or pass), if not yet reported.</param>
/// <param name="ForecastDepart">Forecast departure, if not yet reported.</param>
/// <param name="DelaySeconds">Actual or forecast time less the planned one, as the forecaster gives it.</param>
public sealed record ScheduleRow(
    int Index,
    Tiploc Location,
    SimTime? WttArrive,
    SimTime? WttDepart,
    string? WttPlatform,
    string? WttLine,
    SimTime? PlanArrive,
    SimTime? PlanDepart,
    bool IsPass,
    string? Platform,
    string? Line,
    bool TimesChanged,
    bool PlatformChanged,
    bool LineChanged,
    bool Runs,
    SimTime? ActualArrive,
    SimTime? ActualDepart,
    SimTime? ForecastArrive,
    SimTime? ForecastDepart,
    int? DelaySeconds)
{
    /// <summary>A stop with both times, whose dwell can be changed or skipped.</summary>
    public bool IsCall => !IsPass && PlanArrive is not null && PlanDepart is not null;
}

/// <summary>
/// A train's schedule for the detail panel (RP-1): every timed point of its plan beside the WTT booking, the actuals
/// and the forecast. Pure; built from one <see cref="PlanSnapshot"/>.
/// </summary>
/// <param name="Wtt">The WTT train it is booked as (null for a clone).</param>
/// <param name="Forecast">Its forecast, if it has one (cancelled trains and trains wholly in the past have none).</param>
/// <param name="Live">The live train matched to it, if any.</param>
public sealed record TrainSchedule(TrainPlan Plan, Train? Wtt, TrainForecast? Forecast, LiveTrain? Live, IReadOnlyList<ScheduleRow> Rows)
{
    /// <summary>The schedule of a train in the snapshot's plan, or null if it is not in the plan.</summary>
    public static TrainSchedule? For(PlanSnapshot snapshot, TrainId train)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Plan.Find(train) is not { } plan)
        {
            return null;
        }

        var wtt = plan.Origin == PlanOrigin.Wtt ? snapshot.Plan.Timetable.Find(plan.Id) : null;
        var forecast = snapshot.Forecast.Find(train);
        var live = snapshot.FindByTrain(train)?.Train ?? (forecast?.LiveUid is { } uid ? snapshot.FindByUid(uid) : null);
        var running = plan.RunningPoints;
        var rows = new List<ScheduleRow>(plan.Points.Count);
        for (var i = 0; i < plan.Points.Count; i++)
        {
            var runs = i <= plan.LastIndex;
            var p = runs ? running[i] : plan.Points[i];
            var booked = wtt is not null && i < wtt.Points.Count ? wtt.Points[i] : null;
            if (!p.IsTimed && booked?.IsTimed != true)
            {
                continue;
            }

            var f = runs ? forecast?.At(i) : null;
            var (actualArrive, actualDepart, forecastArrive, forecastDepart) = f?.Basis switch
            {
                ForecastBasis.Actual => (f.Arrive, f.Depart, (SimTime?)null, (SimTime?)null),
                ForecastBasis.Arrived => (f.Arrive, null, null, f.Depart),
                ForecastBasis.Forecast => (null, null, f.Arrive, f.Depart),
                _ => ((SimTime?)null, (SimTime?)null, (SimTime?)null, (SimTime?)null),
            };
            rows.Add(new ScheduleRow(
                i,
                p.Location,
                booked?.Arrive,
                booked?.Depart,
                booked?.Platform,
                booked?.Line,
                p.Arrive,
                p.Depart,
                p.IsPass,
                p.Platform,
                p.Line,
                booked is not null ? !SameTimes(p, PlannedPoint.From(booked) with { Index = i }) || (runs && i == plan.TerminatesAt) : plan.Origin == PlanOrigin.Clone,
                booked is not null && p.Platform != booked.Platform,
                booked is not null && p.Line != booked.Line,
                runs,
                actualArrive,
                actualDepart,
                forecastArrive,
                forecastDepart,
                f?.DelaySeconds));
        }

        return new TrainSchedule(plan, wtt, forecast, live, rows);
    }

    /// <summary>The row of a plan point, if it is shown.</summary>
    public ScheduleRow? Row(int index) => Rows.FirstOrDefault(r => r.Index == index);

    private static bool SameTimes(PlannedPoint a, PlannedPoint b) => a.Arrive == b.Arrive && a.Depart == b.Depart && a.IsPass == b.IsPass;
}
