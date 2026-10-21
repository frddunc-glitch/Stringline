using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Graph;
using Stringline.Planning;
using Stringline.Timetable.Live;

namespace Stringline.App.Tests;

/// <summary>The train detail's notes for WTT rules (S32): a hold the forecast made, and rules it does not forecast.</summary>
public sealed class TrainDetailRuleTests
{
    private static TimingPoint Point(string location, string? arrive, string? depart) => new()
    {
        Index = 0,
        Location = new Tiploc(location),
        Arrive = arrive is null ? null : SimTime.Parse(arrive),
        Depart = depart is null ? null : SimTime.Parse(depart),
    };

    private static Train Train(string headcode, params TimingPoint[] points) => new()
    {
        Id = new TrainId(headcode),
        Headcode = new Headcode(headcode),
        Points = points.Select((p, i) => p with { Index = i }).ToList(),
    };

    private static TimetableRule Rule(RuleKind kind, string x, string y, int seconds) =>
        new(kind, (int)kind, x, y, new TrainId(x), new TrainId(y), seconds, null);

    private static string StatusOf(string train, params TimetableRule[] rules)
    {
        var timetable = new WorkingTimetable(
            new TimetableHeader("test", "5.0", "Test", "", null, SimTime.FromClock(4, 0), SimTime.FromClock(23, 0)),
            [],
            [
                Train("2Y01", Point("A", null, "08:00"), Point("B", "08:05", null)),
                Train("2X01", Point("B", null, "08:02"), Point("A", "08:07", null)),
            ],
            rules);
        var plan = WorkingPlan.FromTimetable(timetable);
        var forecast = Forecaster.Run(new ForecastRequest(plan) { Now = SimTime.Parse("07:50") });
        var snapshot = new PlanSnapshot(1, timetable, [], SimClockReading.None, new MatchSummary(0, 0, 0, 0, 0, new Dictionary<MatchMethod, int>()), 1, null, plan, Forecast: forecast);
        var detail = new TrainDetailViewModel((_, _, _, _) => { });
        detail.Update(snapshot, new GraphSelection(new TrainId(train), null), null);
        return detail.StatusText;
    }

    [Fact]
    public void A_rule_held_train_says_what_holds_it_and_until_when()
    {
        StatusOf("2X01", Rule(RuleKind.XAppearsAfterYArrives, "2X01", "2Y01", 60))
            .Should().Be("Not started, forecast 4 min late at the end, held by rule: after 2Y01 arrives (+1 min), until 08:06");
        StatusOf("2Y01", Rule(RuleKind.XAppearsAfterYArrives, "2X01", "2Y01", 60)).Should().Be("Not started, forecast On time at the end");
    }

    [Fact]
    public void Rules_the_forecast_does_not_apply_are_noted_on_both_trains()
    {
        StatusOf("2X01", Rule(RuleKind.XNotIfY, "2X01", "2Y01", 0)).Should().EndWith("rule: does not run if 2Y01 runs");
        StatusOf("2Y01", Rule(RuleKind.MutuallyExclusive, "2X01", "2Y01", 0)).Should().EndWith("rule: runs or 2X01 runs, not both");
    }
}
