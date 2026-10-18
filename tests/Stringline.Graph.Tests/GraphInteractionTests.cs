using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Planning;
using Stringline.Timetable.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>Hit testing, hover tooltips and the selected-train highlight on the A–D test corridor.</summary>
public sealed class GraphInteractionTests
{
    private static readonly WorkingTimetable Timetable = TwoTrains();

    private static readonly LiveTrain Express = Live("901", "1A01", MatchStatus.Matched, "1A01",
        ("A", TrainLocationAction.Depart, "07:01:30"), ("B", TrainLocationAction.Pass, "07:06:00"), ("C", TrainLocationAction.Pass, "07:10:30"));

    private static readonly LiveTrain Unknown = Live("902", "9Z99", MatchStatus.Unmatched, null,
        ("D", TrainLocationAction.Depart, "06:50"), ("C", TrainLocationAction.Arrive, "06:58"));

    private static readonly PlanSnapshot Snapshot = new(1, Timetable, [Express, Unknown], SimClockReading.None,
        new MatchSummary(2, 2, 1, 0, 1, new Dictionary<MatchMethod, int>()), 1, null);

    private static readonly GraphScene Scene = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, Snapshot));

    private static readonly GraphViewport View = GraphViewport.Create(SimTime.FromClock(6, 45).Seconds, SimTime.FromClock(7, 30).Seconds, 800, 400);

    private static ScreenPoint At(string time, double axis) => View.ToScreen(SimTime.Parse(time).Seconds, axis);

    private static ScreenPoint Offset(ScreenPoint p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    [Fact]
    public void A_scene_from_a_snapshot_has_the_live_uid_and_working_on_actual_paths()
    {
        var actual = Scene.InLayer(GraphLayer.Actual).Single(p => p.Train == new TrainId("1A01"));

        actual.LiveUid.Should().Be("901");
        actual.Working.Should().Be(0);
        Scene.InLayer(GraphLayer.Wtt).Should().OnlyContain(p => p.LiveUid == null && p.Working == null);
        Scene.Now.Should().BeNull("the snapshot has no clock");
    }

    [Fact]
    public void Hits_the_actual_line_under_the_cursor()
    {
        var hit = GraphHitTester.HitTest(Scene, View, Offset(At("07:08:15", 0.5), 2, 0))!;

        hit.Path.Layer.Should().Be(GraphLayer.Actual);
        hit.Path.LiveUid.Should().Be("901");
        hit.Segment.Should().Be(1);
        hit.Distance.Should().BeLessThan(GraphHitTester.DefaultTolerance);
        hit.Time.Should().BeApproximately(SimTime.Parse("07:08:15").Seconds, 30);
    }

    [Fact]
    public void Picks_the_nearer_end_of_the_segment_as_the_timing_point()
    {
        var nearC = GraphHitTester.HitTest(Scene, View, At("07:10:00", 2.0 / 3 - 0.02))!;
        var nearB = GraphHitTester.HitTest(Scene, View, At("07:06:30", 1.0 / 3 + 0.02))!;

        nearC.Vertex.Axis.Should().BeApproximately(2.0 / 3, 1e-9);
        nearB.Vertex.Axis.Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void Hits_a_wtt_line_where_there_is_no_actual()
    {
        var hit = GraphHitTester.HitTest(Scene, View, At("07:06", 0.5))!;

        hit.Path.Layer.Should().Be(GraphLayer.Wtt);
        hit.Path.Train.Should().Be(new TrainId("1A01"));
    }

    [Fact]
    public void Prefers_the_actual_line_when_both_are_about_as_near()
    {
        var wtt = new GraphPath("wtt:X", GraphLayer.Wtt, new TrainId("X"), new Headcode("1X01"), TestData.Express, [new(25_200, 0), new(26_000, 1)]);
        var actual = wtt with { Key = "act:1/0", Layer = GraphLayer.Actual, LiveUid = "1", Vertices = [new(25_205, 0), new(26_005, 1)] };
        var scene = Scene with { Paths = [wtt, actual] };
        var onWtt = View.ToScreen(25_600, 0.5);

        GraphHitTester.HitTest(scene, View, onWtt)!.Path.Layer.Should().Be(GraphLayer.Actual);
    }

    [Fact]
    public void Misses_away_from_every_line_and_outside_the_plot()
    {
        GraphHitTester.HitTest(Scene, View, At("06:47", 0.1)).Should().BeNull();
        GraphHitTester.HitTest(Scene, View, new ScreenPoint(5, 5)).Should().BeNull();
    }

    [Fact]
    public void Measures_the_distance_to_a_segment()
    {
        GraphHitTester.DistanceToSegment(new(5, 5), new(0, 0), new(10, 0)).Should().Be((5, 0.5));
        GraphHitTester.DistanceToSegment(new(-3, 4), new(0, 0), new(10, 0)).Should().Be((5, 0));
        GraphHitTester.DistanceToSegment(new(3, 4), new(0, 0), new(0, 0)).Should().Be((5, 0));
    }

    [Fact]
    public void The_tooltip_of_an_actual_line_gives_the_booked_and_actual_times_and_the_delay()
    {
        var hit = GraphHitTester.HitTest(Scene, View, At("07:10:00", 2.0 / 3 - 0.02))!;

        var info = GraphHover.Describe(hit, Scene, Snapshot);

        info.Heading.Should().Be("1A01");
        info.ValueOf(GraphHover.Location).Should().Be("Charlie (C)");
        info.ValueOf(GraphHover.Booked).Should().Be("pass 07:08");
        info.ValueOf(GraphHover.Actual).Should().Be("pass 07:10:30");
        info.ValueOf(GraphHover.Delay).Should().Be("2 min late");
        info.ValueOf(GraphHover.Match).Should().Be("Matched");
        info.ValueOf(GraphHover.Train).Should().Be("1A01, sim uid 901");
        info.ToString().Should().Contain("Delay: 2 min late");
    }

    [Fact]
    public void The_tooltip_of_a_wtt_line_finds_the_matched_live_train()
    {
        var hit = GraphHitTester.HitTest(Scene, View, At("07:06", 0.5))!;

        var info = GraphHover.Describe(hit, Scene, Snapshot);

        info.ValueOf(GraphHover.Train).Should().Be("1A01, sim uid 901");
        info.ValueOf(GraphHover.Location).Should().NotBeNull();
    }

    [Fact]
    public void The_tooltip_of_a_train_not_running_gives_its_calls()
    {
        var stopper = Scene.InLayer(GraphLayer.Wtt).Single(p => p.Headcode == new Headcode("2B02"));
        var atC = stopper.Vertices.ToList().FindIndex(v => Math.Abs(v.Axis - (2.0 / 3)) < 1e-9);
        var hit = new GraphHit(stopper, atC, 0, atC, stopper.Vertices[atC].Time);

        var info = GraphHover.Describe(hit, Scene, Snapshot);

        info.ValueOf(GraphHover.Booked).Should().Be("arr 07:10  dep 07:12");
        info.ValueOf(GraphHover.Actual).Should().BeNull();
        info.ValueOf(GraphHover.Train).Should().Be("2B02");
    }

    [Fact]
    public void The_tooltip_of_an_unmatched_train_says_so()
    {
        var path = Scene.InLayer(GraphLayer.Actual).Single(p => p.IsUnmatched);
        var hit = new GraphHit(path, 0, 0, 1, path.Vertices[1].Time);

        var info = GraphHover.Describe(hit, Scene, Snapshot);

        info.Heading.Should().Be("9Z99");
        info.ValueOf(GraphHover.Location).Should().Be("Charlie (C)");
        info.ValueOf(GraphHover.Actual).Should().Be("arr 06:58:00");
        info.ValueOf(GraphHover.Booked).Should().BeNull();
        info.ValueOf(GraphHover.Match).Should().Be("Not matched");
        info.ValueOf(GraphHover.Train).Should().Be("sim uid 902");
    }

    [Fact]
    public void The_tooltip_pairs_an_arrival_with_its_departure()
    {
        var live = Live("903", "2B02", MatchStatus.Matched, "2B02",
            ("D", TrainLocationAction.Depart, "07:06"), ("C", TrainLocationAction.Arrive, "07:11"), ("C", TrainLocationAction.Depart, "07:13:20"));
        var snapshot = new PlanSnapshot(1, Timetable, [live], SimClockReading.None, Snapshot.Matches, 1, null);
        var scene = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, snapshot));
        var path = scene.InLayer(GraphLayer.Actual).Single();
        var hit = new GraphHit(path, 1, 0, 2, path.Vertices[2].Time);

        var info = GraphHover.Describe(hit, scene, snapshot);

        info.ValueOf(GraphHover.Actual).Should().Be("arr 07:11:00  dep 07:13:20");
        info.ValueOf(GraphHover.Delay).Should().Be("1 min late", "the departure is 1 min 20 s late");
    }

    [Theory]
    [InlineData(0, "Right time")]
    [InlineData(59, "Right time")]
    [InlineData(-59, "Right time")]
    [InlineData(60, "1 min late")]
    [InlineData(-150, "2 min early")]
    public void Formats_delays(int seconds, string expected)
    {
        GraphHover.FormatDelay(seconds).Should().Be(expected);
    }

    [Fact]
    public void A_selection_matches_its_wtt_and_live_paths()
    {
        var selection = GraphSelection.Of(Scene.InLayer(GraphLayer.Actual).Single(p => p.LiveUid == "901"));

        Scene.Paths.Where(selection.Matches).Select(p => p.Layer).Should().BeEquivalentTo([GraphLayer.Wtt, GraphLayer.Actual]);
        new GraphSelection(null, "902").Matches(Scene.InLayer(GraphLayer.Actual).Single(p => p.IsUnmatched)).Should().BeTrue();
        new GraphSelection(null, null).Matches(Scene.Paths[0]).Should().BeFalse();
    }

    [Fact]
    public void Selecting_a_train_dims_the_others_and_draws_it_opaque()
    {
        using var renderer = new SkiaGraphRenderer();
        using var plain = SKBitmap.Decode(renderer.RenderPng(Scene, View));
        using var selected = SKBitmap.Decode(renderer.RenderPng(Scene, View, new GraphSelection(new TrainId("1A01"), "901")));

        var other = At("06:54", 5.0 / 6);
        Brightness(selected, other).Should().BeGreaterThan(Brightness(plain, other) + 60, "the unmatched train dims");

        var wttOnly = At("07:06", 0.5);
        Brightness(selected, wttOnly).Should().BeLessThan(Brightness(plain, wttOnly), "the selected train's WTT line is drawn opaque");
    }

    private static int Brightness(SKBitmap bitmap, ScreenPoint p)
    {
        var c = bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));
        return c.Red + c.Green + c.Blue;
    }
}
