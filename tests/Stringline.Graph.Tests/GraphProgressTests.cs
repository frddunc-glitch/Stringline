using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Planning;
using Stringline.Timetable.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>
/// S36 (#17): progress between booked points on the graph: the live head carried on to now, the forecast from an
/// unbooked timing location, and minor rows.
/// </summary>
public sealed class GraphProgressTests
{
    private const double Third = 1.0 / 3, TwoThirds = 2.0 / 3;

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static GraphVertex V(double time, double axis) => new(time, axis);

    /// <summary>1A01 booked A–C–D, leaving out B (which is on the corridor).</summary>
    private static Core.Timetables.WorkingTimetable SkipsB() => Tt(
        Train("1A01", "XP", Call("A", null, "07:00"), Pass("C", "07:08"), Call("D", "07:12", null)));

    private static PlanSnapshot Snapshot(Core.Timetables.WorkingTimetable tt, string now, params LiveTrain[] live)
    {
        var plan = WorkingPlan.FromTimetable(tt);
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

    private static GraphScene Build(PlanSnapshot snapshot, Func<GraphSceneRequest, GraphSceneRequest>? change = null)
    {
        var request = GraphSceneRequest.For(Profile, TestCorridor, snapshot);
        return GraphSceneBuilder.Build(change is null ? request : change(request));
    }

    // ---- The head against the clock ----

    private static readonly GraphLiveHead Moving = new("head:1A01", new TrainId("1A01"), new Headcode("1A01"), Express, LineStyle.Solid,
        [V(T("07:02"), 0), V(T("07:06"), Third)]);

    [Fact]
    public void The_head_follows_the_forecast_with_the_clock()
    {
        Moving.At(T("07:02")).Should().BeEmpty("nothing to draw at the last report itself");
        Moving.At(T("07:01")).Should().BeEmpty();
        Moving.At(T("07:03")).Should().Equal(V(T("07:02"), 0), V(T("07:03"), Third / 4));
        Moving.HeadAt(T("07:05")).Should().Be(V(T("07:05"), Third * 3 / 4));
    }

    [Fact]
    public void An_overdue_head_stops_short_of_the_next_point_and_waits()
    {
        var held = V(T("07:02") + (0.95 * 240), 0.95 * Third);

        Moving.At(T("07:06")).Should().Equal(V(T("07:02"), 0), held, held with { Time = T("07:06") });
        Moving.At(T("07:20")).Should().Equal([V(T("07:02"), 0), held, held with { Time = T("07:20") }], "it never runs ahead of a report");
        Moving.HeadAt(T("07:20"))!.Value.Axis.Should().BeLessThan(Third);
    }

    [Fact]
    public void A_standing_head_waits_at_the_stop_then_moves_on()
    {
        var standing = Moving with { Vertices = [V(T("07:11"), TwoThirds), V(T("07:12"), TwoThirds), V(T("07:16"), Third)] };

        standing.At(T("07:11:30")).Should().Equal(V(T("07:11"), TwoThirds), V(T("07:11:30"), TwoThirds));
        standing.At(T("07:14")).Should().Equal(V(T("07:11"), TwoThirds), V(T("07:12"), TwoThirds), V(T("07:14"), (TwoThirds + Third) / 2));
    }

    // ---- The scene's heads ----

    [Fact]
    public void Each_running_train_has_a_head_from_its_last_report_to_its_next_point()
    {
        var snapshot = Snapshot(TwoTrains(), "07:03",
            Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02")),
            Live("902", "2B02", MatchStatus.Matched, "2B02", ("D", TrainLocationAction.Depart, "07:06"), ("C", TrainLocationAction.Arrive, "07:11")));

        var heads = Build(snapshot).LiveHeads;

        var fast = heads.Single(h => h.Train == new TrainId("1A01"));
        fast.Key.Should().Be("head:1A01");
        fast.LiveUid.Should().Be("901");
        fast.Colour.Should().Be(Express);
        fast.Vertices.Should().Equal(V(T("07:02"), 0), V(T("07:06"), Third));

        heads.Single(h => h.Train == new TrainId("2B02")).Vertices.Should().Equal(
            [V(T("07:11"), TwoThirds), V(T("07:12"), TwoThirds), V(T("07:16"), Third)], "standing at C until its forecast departure");
    }

    [Fact]
    public void The_head_can_be_turned_off_and_needs_a_clock()
    {
        var snapshot = Snapshot(TwoTrains(), "07:03", Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02")));

        Build(snapshot).LiveHeads.Should().ContainSingle();
        Build(snapshot, r => r with { ShowLiveHead = false }).LiveHeads.Should().BeEmpty();
        Build(snapshot, r => r with { Now = null }).LiveHeads.Should().BeEmpty();
        Build(snapshot, r => r with { ShowForecast = false }).LiveHeads.Should().ContainSingle("the head does not need the forecast layer");
    }

    [Fact]
    public void Coloured_by_delay_the_head_takes_the_latest_delay()
    {
        var snapshot = Snapshot(TwoTrains(), "07:03", Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02")));

        Build(snapshot, r => r with { ColourMode = GraphColourMode.Delay }).LiveHeads.Single().Colour.Should().Be(DelayBands.ColourOfSeconds(120));
    }

    // ---- An unbooked timing location ----

    [Fact]
    public void The_forecast_and_the_head_go_on_from_an_unbooked_location_passed()
    {
        // Two minutes late at A, but through B (half way in run time, not booked) at 07:05: the rest is at the minimum.
        var snapshot = Snapshot(SkipsB(), "07:05:10",
            Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02"), ("B", TrainLocationAction.Pass, "07:05")));
        var scene = Build(snapshot);

        snapshot.Forecast.Find(new TrainId("1A01"))!.Anchor!.Location.Should().Be(new Tiploc("B"));
        scene.InLayer(GraphLayer.Actual).Single().Vertices.Should().Equal([V(T("07:02"), 0), V(T("07:05"), Third)], "B keeps its place on the actual line");
        var forecast = scene.InLayer(GraphLayer.Forecast).Single();
        forecast.Vertices[0].Should().Be(V(T("07:05"), Third), "the forecast starts where the actual line ends");
        scene.LiveHeads.Single().Vertices[0].Should().Be(V(T("07:05"), Third));
    }

    [Fact]
    public void Delay_colours_carry_across_an_unbooked_location()
    {
        var snapshot = Snapshot(SkipsB(), "07:15:10",
            Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02"), ("B", TrainLocationAction.Pass, "07:05"), ("C", TrainLocationAction.Pass, "07:15")));

        var actual = Build(snapshot, r => r with { ColourMode = GraphColourMode.Delay }).InLayer(GraphLayer.Actual).Single();

        actual.Vertices.Should().HaveCount(3);
        actual.SegmentColours.Should().Equal(DelayBands.ColourOfSeconds(120), DelayBands.ColourOfSeconds(420)); // A–B keeps A's 2 min, B having none
    }

    [Fact]
    public void The_tooltip_says_an_unbooked_location_is_not_booked()
    {
        var snapshot = Snapshot(SkipsB(), "07:05:10",
            Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02"), ("B", TrainLocationAction.Pass, "07:05")));
        var scene = Build(snapshot);
        var actual = scene.InLayer(GraphLayer.Actual).Single();

        var info = GraphHover.Describe(new GraphHit(actual, 1, 0, 1, actual.Vertices[1].Time), scene, snapshot);

        info.ValueOf(GraphHover.Booked).Should().Be(GraphHover.NotBooked);
        info.ValueOf(GraphHover.Actual).Should().Be("pass 07:05:00");
        info.ValueOf(GraphHover.Delay).Should().BeNull();
        info.Lines.Should().Contain(new HoverLine(GraphHover.Note, "Passed B 07:05 (not booked): forecast on from there"));
    }

    // ---- Minor rows ----

    private static readonly Corridor MinorB = TestCorridor with { Locations = [.. TestCorridor.Locations.Select(l => l.Tiploc == new Tiploc("B") ? l with { Minor = true } : l)] };

    [Fact]
    public void A_minor_location_is_a_minor_row()
    {
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(Profile with { Corridors = [MinorB] }, MinorB) { Timetable = TwoTrains() });

        var b = scene.Locations.Single(l => l.Tiploc == new Tiploc("B"));
        b.IsMinor.Should().BeTrue();
        b.IsStation.Should().BeFalse();
        scene.Locations.Where(l => l.Tiploc != new Tiploc("B")).Should().OnlyContain(l => !l.IsMinor);
        scene.InLayer(GraphLayer.Wtt).Single(p => p.Train == new TrainId("1A01")).Vertices.Should().HaveCount(4, "trains still draw through it");
    }

    [Fact]
    public Task Minor_row_and_live_head_snapshot()
    {
        // B is a minor row (labelled: there is room); 1A01 left A late and is between A and B.
        var snapshot = Snapshot(TwoTrains(), "07:04", Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02")));
        var scene = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile with { Corridors = [MinorB] }, MinorB, snapshot));
        return SkiaGraphRendererTests.VerifyPng(scene, GraphViewport.Create(SimTime.FromClock(6, 55).Seconds, SimTime.FromClock(7, 20).Seconds, 600, 300));
    }

    [Fact]
    public void A_minor_row_is_labelled_only_with_room()
    {
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(Profile with { Corridors = [MinorB] }, MinorB) { Timetable = TwoTrains() });
        using var renderer = new SkiaGraphRenderer();
        var tall = renderer.RenderPng(scene, GraphViewport.Create(T("06:55"), T("07:20"), 600, 300));
        var squat = renderer.RenderPng(scene, GraphViewport.Create(T("06:55"), T("07:20"), 600, 120));
        var plain = GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor) { Timetable = TwoTrains() });
        var squatPlain = renderer.RenderPng(plain, GraphViewport.Create(T("06:55"), T("07:20"), 600, 120));

        tall.Should().NotBeEmpty();
        squat.Should().NotEqual(squatPlain, "the minor row still draws its faint line");
    }
}
