using Stringline.Core;

namespace Stringline.Planning;

/// <summary>What a train does at a location in the <see cref="RegulationList"/>.</summary>
public enum RegulationMove
{
    /// <summary>Starts there.</summary>
    Start,

    /// <summary>Stops there and goes on.</summary>
    Call,

    /// <summary>Passes without stopping.</summary>
    Pass,

    /// <summary>Terminates there (or is terminated short there).</summary>
    Terminate,
}

/// <summary>One movement at a location: a train's call, pass, start or termination, as planned, booked and forecast.</summary>
/// <param name="Index">The plan point.</param>
/// <param name="Planned">When it goes in the working plan: its departure or pass, or its arrival if it terminates.</param>
/// <param name="PlannedArrive">The planned arrival at a call, if any.</param>
/// <param name="Booked">The same time in the WTT; null for a clone.</param>
/// <param name="Forecast">The same time as forecast, if any.</param>
public sealed record RegulationRow(
    TrainId Train,
    Headcode Headcode,
    int Index,
    RegulationMove Move,
    SimTime Planned,
    SimTime? PlannedArrive,
    SimTime? Booked,
    SimTime? Forecast,
    string? Platform,
    string? Line)
{
    /// <summary>Trains booked to go before this one that the plan has after it (it goes ahead of them).</summary>
    public IReadOnlyList<Headcode> Ahead { get; init; } = [];

    /// <summary>Trains booked to go after this one that the plan has before it (it goes behind them).</summary>
    public IReadOnlyList<Headcode> Behind { get; init; } = [];

    /// <summary>A train the WTT does not have (a clone).</summary>
    public bool IsAdded => Booked is null;

    /// <summary>The plan differs from the WTT here (time or platform, or a hold at a signal before it).</summary>
    public bool IsChanged { get; init; }

    /// <summary>
    /// The plan holds the train at a signal on the way here (S33): "hold 1A23 at S457 until 09:12". A reminder only, as
    /// the gateway has no command to hold a train at a signal; the signaller holds it on the panel.
    /// </summary>
    public SignalHold? SignalHold { get; init; }

    /// <summary>True if the plan has it in a different order from the WTT.</summary>
    public bool OutOfOrder => Ahead.Count > 0 || Behind.Count > 0;

    /// <summary>The forecast less the plan, if forecast.</summary>
    public int? DelaySeconds => Forecast is { } f ? f - Planned : null;
}

/// <summary>
/// The regulation list (RP-9, <c>05-…md</c> §6): the movements still to come at one location, in working-plan order,
/// with where that order differs from the WTT's. The controller's crib sheet while signalling. Pure.
/// </summary>
public static class RegulationList
{
    /// <summary>How far ahead of now the list looks, by default.</summary>
    public const int DefaultHorizonSeconds = 3 * SimTime.SecondsPerHour;

    /// <summary>
    /// The movements at <paramref name="location"/> not yet made (by the forecast), planned or forecast within
    /// <paramref name="horizonSeconds"/> of <paramref name="now"/> (all of them with no time), in plan order.
    /// Cancelled trains, and trains the forecast leaves out or has finished, are not listed.
    /// </summary>
    public static IReadOnlyList<RegulationRow> For(WorkingPlan plan, Forecast forecast, Tiploc location, SimTime? now, int horizonSeconds = DefaultHorizonSeconds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(forecast);
        var forecasting = forecast.Trains.Count > 0;
        var end = now is { } n ? n.Seconds + horizonSeconds : int.MaxValue;
        var rows = new List<(RegulationRow Row, int Order)>();
        for (var order = 0; order < plan.Trains.Count; order++)
        {
            var train = plan.Trains[order];
            var f = forecast.Find(train.Id);
            if (train.Cancelled || (forecasting && f is not { Status: TrainForecastStatus.NotStarted or TrainForecastStatus.Running }))
            {
                continue;
            }

            var wtt = train.Origin == PlanOrigin.Wtt ? plan.Timetable.Find(train.Source) : null;
            var points = train.RunningPoints;
            var first = -1;
            for (var k = 0; k < points.Count; k++)
            {
                var p = points[k];
                if (!p.IsTimed)
                {
                    continue;
                }

                first = first < 0 ? k : first;
                if (p.Location != location || f?.At(k) is { Basis: ForecastBasis.Actual })
                {
                    continue;
                }

                var move = p.IsPass ? RegulationMove.Pass
                    : p.Depart is null ? RegulationMove.Terminate
                    : k == first && p.Arrive is null ? RegulationMove.Start
                    : RegulationMove.Call;
                var planned = Key(p.Arrive, p.Depart)!.Value;
                var fp = f?.At(k);
                var forecastTime = fp is null ? null : Key(fp.Arrive, fp.Depart);
                if (planned.Seconds > end && (forecastTime ?? planned).Seconds > end)
                {
                    continue;
                }

                var booked = wtt is not null && k < wtt.Points.Count ? wtt.Points[k] : null;
                var bookedTime = booked is null ? null : move == RegulationMove.Terminate ? booked.FirstTime : Key(booked.Arrive, booked.Depart);
                var held = HeldBefore(train, k);
                var changed = held is not null || (booked is not null && (booked.Arrive != p.Arrive || booked.Depart != p.Depart ||
                    !string.Equals(booked.Platform?.Trim(), p.Platform?.Trim(), StringComparison.OrdinalIgnoreCase)));
                rows.Add((new RegulationRow(train.Id, train.Headcode, k, move, planned, move == RegulationMove.Call ? p.Arrive : null, bookedTime, forecastTime, p.Platform, p.Line)
                {
                    IsChanged = changed,
                    SignalHold = held,
                }, order));
            }
        }

        rows.Sort((a, b) => a.Row.Planned != b.Row.Planned ? a.Row.Planned.CompareTo(b.Row.Planned)
            : (a.Row.PlannedArrive ?? a.Row.Planned).CompareTo(b.Row.PlannedArrive ?? b.Row.Planned) is var byArrival and not 0 ? byArrival
            : a.Order != b.Order ? a.Order.CompareTo(b.Order)
            : a.Row.Index.CompareTo(b.Row.Index));

        // Out of WTT order: a train planned before another it is booked after.
        var ahead = new List<Headcode>[rows.Count];
        var behind = new List<Headcode>[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                var (x, y) = (rows[i].Row, rows[j].Row);
                if (x.Train != y.Train && x.Booked is { } bx && y.Booked is { } by && bx > by)
                {
                    (ahead[i] ??= []).Add(y.Headcode);
                    (behind[j] ??= []).Add(x.Headcode);
                }
            }
        }

        return rows.Select((r, i) => ahead[i] is null && behind[i] is null ? r.Row : r.Row with { Ahead = ahead[i] ?? [], Behind = behind[i] ?? [] }).ToList();
    }

    /// <summary>The train's latest hold at a signal on the run into point <paramref name="k"/>, if any (S33).</summary>
    private static SignalHold? HeldBefore(TrainPlan train, int k)
    {
        if (train.SignalHolds.Count == 0)
        {
            return null;
        }

        var points = train.RunningPoints;
        var c = k - 1;
        while (c >= 0 && !points[c].IsTimed)
        {
            c--;
        }

        return train.SignalHolds.Where(h => h.Index == c).MaxBy(h => h.Share);
    }

    /// <summary>When a train goes from a point: its departure or pass, else its arrival.</summary>
    private static SimTime? Key(SimTime? arrive, SimTime? depart) => depart ?? arrive;
}
