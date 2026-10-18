using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Planning;
using Stringline.Timetable.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>The forecast layer (S11): dashed lines on from each running train's latest actual.</summary>
public sealed class GraphForecastTests
{
    private const double Third = 1.0 / 3, TwoThirds = 2.0 / 3;

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static GraphVertex V(double time, double axis) => new(time, axis);

    /// <summary>1A01 left A two minutes late; 2B02 is at C a minute late.</summary>
    private static readonly LiveTrain[] Running =
    [
        Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02")),
        Live("902", "2B02", MatchStatus.Matched, "2B02", ("D", TrainLocationAction.Depart, "07:06"), ("C", TrainLocationAction.Arrive, "07:11")),
    ];

    private static PlanSnapshot Snapshot(string now, params LiveTrain[] live)
    {
        var plan = WorkingPlan.FromTimetable(TwoTrains());
        var clock = SimClockReading.None with { HasTime = true, LastReported = SimTime.Parse(now) };
        var forecast = Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = live,
            Now = SimTime.Parse(now),
            Network = ForecastNetwork.Build(Profile.Corridors, plan.Timetable),
        });
        return new PlanSnapshot(1, plan.Timetable, live, clock, new MatchSummary(live.Length, live.Length, live.Length, 0, 0, new Dictionary<MatchMethod, int>()),
            1, SimTime.Parse("06:30"), plan, Forecast: forecast);
    }

    private static GraphScene Build(PlanSnapshot snapshot, GraphColourMode mode = GraphColourMode.Category) =>
        GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, snapshot) with { ColourMode = mode });

    [Fact]
    public void A_running_train_is_forecast_on_from_its_latest_actual()
    {
        var scene = Build(Snapshot("07:02:30", Running[0]));

        var forecast = scene.InLayer(GraphLayer.Forecast).Should().ContainSingle().Subject;
        forecast.Key.Should().Be("fc:1A01");
        forecast.Train.Should().Be(new TrainId("1A01"));
        forecast.LiveUid.Should().Be("901");
        forecast.Colour.Should().Be(Express);
        forecast.Direction.Should().Be(Network.TrackDirection.Down);
        forecast.Vertices.Should().Equal(V(T("07:02"), 0), V(T("07:06"), Third), V(T("07:10"), TwoThirds), V(T("07:14"), 1));
    }

    [Fact]
    public void A_train_waiting_at_a_stop_is_forecast_from_its_arrival()
    {
        var scene = Build(Snapshot("07:11:10", Running[1]));

        scene.InLayer(GraphLayer.Forecast).Single(p => p.Train == new TrainId("2B02")).Vertices.Should().Equal(
            V(T("07:11"), TwoThirds), V(T("07:12"), TwoThirds), V(T("07:16"), Third), V(T("07:21"), 0));
    }

    [Fact]
    public void Trains_not_started_and_on_time_have_no_forecast_line()
    {
        var scene = Build(Snapshot("07:02:30", Running[0]));

        scene.InLayer(GraphLayer.Forecast).Should().NotContain(p => p.Train == new TrainId("2B02"));
    }

    [Fact]
    public void The_forecast_layer_can_be_hidden()
    {
        var request = GraphSceneRequest.For(Profile, TestCorridor, Snapshot("07:02:30", Running));

        GraphSceneBuilder.Build(request).InLayer(GraphLayer.Forecast).Should().HaveCount(2);
        GraphSceneBuilder.Build(request with { ShowForecast = false }).InLayer(GraphLayer.Forecast).Should().BeEmpty();
    }

    [Fact]
    public void Coloured_by_delay_the_forecast_takes_the_band_of_its_forecast_delay()
    {
        var scene = Build(Snapshot("07:02:30", Running[0]), GraphColourMode.Delay);

        var forecast = scene.InLayer(GraphLayer.Forecast).Single();
        forecast.Colour.Should().Be(DelayBands.ColourOfSeconds(120));
        forecast.SegmentColours.Should().BeNull("it is two minutes late all the way");
    }

    [Fact]
    public void The_tooltip_gives_the_forecast_at_the_location()
    {
        var snapshot = Snapshot("07:02:30", Running[0]);
        var scene = Build(snapshot);
        var path = scene.InLayer(GraphLayer.Forecast).Single();
        var atC = path.Vertices.ToList().FindIndex(v => Math.Abs(v.Axis - TwoThirds) < 1e-9);

        var info = GraphHover.Describe(new GraphHit(path, atC, 0, atC, path.Vertices[atC].Time), scene, snapshot);

        info.ValueOf(GraphHover.Booked).Should().Be("pass 07:08");
        info.ValueOf(GraphHover.Forecast).Should().Be("pass 07:10  (2 min late)");
        info.ValueOf(GraphHover.Train).Should().Be("1A01, sim uid 901");
    }

    [Fact]
    public void The_tooltip_notes_knock_on_and_a_possible_stop()
    {
        // 1A01 left A at 07:02 and has not been seen for ten minutes.
        var snapshot = Snapshot("07:12", Running[0]);
        var scene = Build(snapshot);
        var path = scene.InLayer(GraphLayer.Forecast).Single(p => p.Train == new TrainId("1A01"));

        var info = GraphHover.Describe(new GraphHit(path, 0, 0, 1, path.Vertices[1].Time), scene, snapshot);

        info.Lines.Should().Contain(new HoverLine(GraphHover.Note, "Possibly stopped: overdue at its next timing point"));
    }

    [Fact]
    public Task Small_corridor_with_forecast_snapshot()
    {
        // 1A01 left A seven minutes late and passed B five late; 2B02 is at C a minute late.
        var snapshot = Snapshot("07:11:10",
            Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:07"), ("B", TrainLocationAction.Pass, "07:09")),
            Running[1]);
        var scene = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, snapshot));
        return SkiaGraphRendererTests.VerifyPng(scene, GraphViewport.Create(SimTime.FromClock(6, 55).Seconds, SimTime.FromClock(7, 30).Seconds, 800, 400));
    }
}
