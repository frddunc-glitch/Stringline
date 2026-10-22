using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Planning;
using Stringline.Timetable.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>Locations off the corridor drawn at one of its own (#22, #23).</summary>
public sealed class GraphDrawAtTests
{
    private const double Third = 1.0 / 3, TwoThirds = 2.0 / 3;

    /// <summary>The test corridor with C's loop C10 drawn at C, and entry point EA beyond A drawn at A.</summary>
    private static readonly Corridor WithDrawAt = TestCorridor with
    {
        DrawAt = [new DrawnAt(new Tiploc("C10"), new Tiploc("C")), new DrawnAt(new Tiploc("EA"), new Tiploc("A"))],
    };

    /// <summary>3D01 enters at EA at 06:50, first books B, and stands in C's loop to be overtaken.</summary>
    private static WorkingTimetable Overtaken() => Tt(
        Train("3D01", "OO", Pass("B", "07:00"), Call("C10", "07:04", "07:10"), Pass("D", "07:14"))
            with { Start = TrainStart.EntryPoint, EntryPoint = new Tiploc("EA"), EntryTime = SimTime.Parse("06:50") });

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static GraphVertex V(string time, double axis) => new(T(time), axis);

    private static GraphScene Build(Corridor corridor, PlanSnapshot? snapshot = null, IEnumerable<LiveTrain>? live = null)
    {
        var request = snapshot is null
            ? new GraphSceneRequest(Profile, corridor) { Timetable = Overtaken(), LiveTrains = live ?? [] }
            : GraphSceneRequest.For(Profile, corridor, snapshot);
        return GraphSceneBuilder.Build(request);
    }

    [Fact]
    public void An_entering_train_is_drawn_from_its_entry_and_stands_at_the_station_it_is_overtaken_at()
    {
        var wtt = Build(WithDrawAt).Paths.Single(p => p.Key == "wtt:3D01");

        wtt.Vertices.Should().Equal(V("06:50", 0), V("07:00", Third), V("07:04", TwoThirds), V("07:10", TwoThirds), V("07:14", 1));
        wtt.Points.Should().Equal(GraphSceneBuilder.EntryVertex, 0, 1, 1, 2);
        wtt.PointAt(0).Should().BeNull("the lead-in is at no timing point, so it has no grip");
        wtt.PointAt(1).Should().Be(0);
        wtt.IsDwellEnd(3).Should().BeTrue("the stop in the loop is a dwell at C");
        wtt.Direction.Should().Be(TrackDirection.Down);
    }

    [Fact]
    public void Without_the_mapping_the_loop_and_entry_are_not_drawn()
    {
        var wtt = Build(TestCorridor).Paths.Single(p => p.Key == "wtt:3D01");

        wtt.Vertices.Should().Equal(V("07:00", Third), V("07:14", 1));
    }

    [Fact]
    public void A_retimed_plan_and_a_forecast_not_yet_started_lead_in_from_the_entry()
    {
        var plan = WorkingPlan.FromTimetable(Overtaken());
        plan.Get(new TrainId("3D01")).EntryLeadSeconds.Should().Be(600);
        var forecast = Forecaster.Run(new ForecastRequest(plan)
        {
            Now = SimTime.Parse("06:30"),
            Network = ForecastNetwork.Build([WithDrawAt], plan.Timetable),
            EntryDelays = new Dictionary<Headcode, int> { [new Headcode("3D01")] = 300 }, // drawn: it deviates from the plan
        });
        var clock = SimClockReading.None with { HasTime = true, LastReported = SimTime.Parse("06:30") };
        var snapshot = new PlanSnapshot(1, plan.Timetable, [], clock, new MatchSummary(0, 0, 0, 0, 0, new Dictionary<MatchMethod, int>()),
            1, SimTime.Parse("06:30"), plan, Forecast: forecast);

        var path = Build(WithDrawAt, snapshot).InLayer(GraphLayer.Forecast).Should().ContainSingle().Subject;
        path.Vertices[0].Should().Be(V("06:55", 0));
        path.Vertices[1].Should().Be(V("07:05", Third));

        var shifted = plan.Get(new TrainId("3D01")) is var p ? p with { Points = [.. p.Points.Select(x => x.Shift(300))] } : null;
        GraphSceneBuilder.PlanPaths(WithDrawAt, shifted!, Stopper).Single().Vertices[0].Should().Be(V("06:55", 0), "the lead is kept when the plan is retimed");
    }

    [Fact]
    public void A_train_reported_in_the_loop_is_drawn_standing_at_the_station()
    {
        var live = Live("901", "3D01", MatchStatus.Unmatched, null,
            ("B", TrainLocationAction.Pass, "07:01"), ("C10", TrainLocationAction.Arrive, "07:05"), ("C10", TrainLocationAction.Depart, "07:11"), ("D", TrainLocationAction.Pass, "07:15"));

        var actual = Build(WithDrawAt, live: [live]).InLayer(GraphLayer.Actual).Should().ContainSingle().Subject;

        actual.Vertices.Should().Equal(V("07:01", Third), V("07:05", TwoThirds), V("07:11", TwoThirds), V("07:15", 1));
        actual.IsUnmatched.Should().BeTrue();
    }
}
