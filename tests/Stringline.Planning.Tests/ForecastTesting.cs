using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Network;

namespace Stringline.Planning.Tests;

/// <summary>Live trains, corridors and readable forecasts for the forecaster tests.</summary>
internal static class ForecastTesting
{
    /// <summary>A live train (uid <paramref name="uid"/>) whose one working is matched to <paramref name="train"/>.</summary>
    public static LiveTrain Running(string uid, string train, params (string Location, string Time, TrainLocationAction Action)[] actuals) =>
        new(uid,
            [new LiveWorking(new Headcode(train.Split('#')[0]), 0, MatchStatus.Matched, new TrainId(train), MatchMethod.HeadcodeAndTime)],
            actuals.Select(a => new ActualEvent(new Tiploc(a.Location), a.Action, SimTime.Parse(a.Time), "", true, 0)).ToImmutableList(),
            false);

    public static (string, string, TrainLocationAction) Dep(string location, string time) => (location, time, TrainLocationAction.Depart);

    public static (string, string, TrainLocationAction) Arr(string location, string time) => (location, time, TrainLocationAction.Arrive);

    public static (string, string, TrainLocationAction) Passed(string location, string time) => (location, time, TrainLocationAction.Pass);

    /// <summary>A corridor through these locations, every section double line (or single) with this headway.</summary>
    public static Corridor Line(string id, int headway, bool single, params string[] locations) => new(
        id,
        id,
        locations.Select((l, i) => new CorridorLocation(new Tiploc(l), locations.Length == 1 ? 0 : (double)i / (locations.Length - 1))).ToList(),
        locations.Zip(locations.Skip(1), (a, b) => new Section(new Tiploc(a), new Tiploc(b), single ? Section.SingleLine : Section.DoubleLine, headway, 60)).ToList());

    /// <summary>The forecast points as text: <c>B 07:08:00/07:08:30 F</c> (A actual, R arrived, F forecast, - none), <c>pass</c> for a pass.</summary>
    public static string[] Times(TrainForecast forecast) => forecast.Points.Select(p =>
        $"{p.Location} {(p.IsPass ? "pass " + p.Depart : $"{p.Arrive?.ToString() ?? "-"}/{p.Depart?.ToString() ?? "-"}")} " +
        p.Basis switch { ForecastBasis.Actual => "A", ForecastBasis.Arrived => "R", ForecastBasis.Forecast => "F", _ => "-" }).ToArray();

    public static Forecast Run(WorkingPlan plan, string? now = null, ForecastNetwork? network = null, bool interaction = true, params LiveTrain[] live) =>
        Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = live,
            Now = now is null ? null : SimTime.Parse(now),
            Network = network ?? ForecastNetwork.Empty,
            Options = new ForecastOptions { Interaction = interaction },
        });
}
