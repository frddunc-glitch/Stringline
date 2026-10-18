using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Network;
using Stringline.Network.Profiles;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

public class GraphSceneBuilderTests
{
    private const double Third = 1.0 / 3, TwoThirds = 2.0 / 3;

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static GraphVertex V(double time, double axis) => new(time, axis);

    private static GraphScene Build(Core.Timetables.WorkingTimetable? tt = null, IEnumerable<LiveTrain>? live = null, SimTime? now = null) =>
        GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor) { Timetable = tt, LiveTrains = live ?? [], Now = now });

    [Fact]
    public void Locations_take_display_names_and_station_flags_from_the_profile()
    {
        var scene = Build();

        scene.CorridorId.Should().Be("main");
        scene.Title.Should().Be("Test sim — A – D");
        scene.Locations.Select(l => (l.Tiploc.Code, l.Name, l.Axis, l.IsStation, l.Platforms)).Should().Equal(
            ("A", "Alpha", 0, true, 4),
            ("B", "B", Third, false, 0),
            ("C", "Charlie", TwoThirds, true, 2),
            ("D", "D", 1, true, 0));
        scene.Paths.Should().BeEmpty();
        scene.Now.Should().BeNull();
    }

    [Fact]
    public void Wtt_passes_are_vertices_and_stops_are_level_segments()
    {
        var scene = Build(TwoTrains());

        var express = scene.Paths.Single(p => p.Key == "wtt:1A01");
        express.Layer.Should().Be(GraphLayer.Wtt);
        express.Train.Should().Be(new TrainId("1A01"));
        express.Headcode.Should().Be(new Headcode("1A01"));
        express.Colour.Should().Be(Express);
        express.Direction.Should().Be(TrackDirection.Down);
        express.IsUnmatched.Should().BeFalse();
        express.Vertices.Should().Equal(V(T("07:00"), 0), V(T("07:04"), Third), V(T("07:08"), TwoThirds), V(T("07:12"), 1));

        var stopper = scene.Paths.Single(p => p.Key == "wtt:2B02");
        stopper.Colour.Should().Be(Stopper);
        stopper.Direction.Should().Be(TrackDirection.Up);
        stopper.Vertices.Should().Equal(
            V(T("07:05"), 1), V(T("07:10"), TwoThirds), V(T("07:12"), TwoThirds), V(T("07:16"), Third), V(T("07:21"), 0));
        stopper.TimeMin.Should().Be(T("07:05"));
        stopper.TimeMax.Should().Be(T("07:21"));
        (stopper.AxisMin, stopper.AxisMax).Should().Be((0, 1));
    }

    [Fact]
    public void Off_corridor_and_untimed_points_are_skipped()
    {
        var tt = Tt(Train("2C03", "OO",
            Call("X", null, "08:00"), Pass("A", "08:02"), Untimed("B"), Pass("Y", "08:05"), Call("C", "08:08", "08:08"), Pass("D", "08:11"), Call("Z", "08:15", null)));

        var path = Build(tt).Paths.Should().ContainSingle().Subject;

        path.Key.Should().Be("wtt:2C03");
        path.Vertices.Should().Equal(V(T("08:02"), 0), V(T("08:08"), TwoThirds), V(T("08:11"), 1));
    }

    [Fact]
    public void Out_and_back_off_the_corridor_starts_a_new_path()
    {
        // Down to C, out to a branch and back to C, then on to D.
        var tt = Tt(Train("5D04", null,
            Call("A", null, "09:00"), Pass("C", "09:08"), Call("BR1", "09:12", "09:30"), Pass("C", "09:35"), Call("D", "09:40", null)));

        var paths = Build(tt).Paths;

        paths.Select(p => p.Key).Should().Equal("wtt:5D04#0", "wtt:5D04#1");
        paths[0].Vertices.Should().Equal(V(T("09:00"), 0), V(T("09:08"), TwoThirds));
        paths[1].Vertices.Should().Equal(V(T("09:35"), TwoThirds), V(T("09:40"), 1));
        paths.Should().OnlyContain(p => p.Colour == CategoryStyle.Fallback.Colour);
    }

    [Fact]
    public void Leaving_and_rejoining_at_another_location_is_one_path()
    {
        // A diversion round a loop line that rejoins further on draws straight across.
        var tt = Tt(Train("6E05", null, Call("A", null, "10:00"), Pass("L1", "10:03"), Pass("C", "10:08"), Call("D", "10:12", null)));

        Build(tt).Paths.Should().ContainSingle().Which.Vertices.Should().HaveCount(3);
    }

    [Fact]
    public void A_train_touching_one_location_without_stopping_draws_nothing()
    {
        var tt = Tt(
            Train("3F06", null, Call("X", null, "11:00"), Pass("C", "11:05"), Call("Y", "11:10", null)),
            Train("3F07", null, Call("X", null, "11:00"), Call("C", "11:05", "11:09"), Call("Y", "11:10", null)));

        var path = Build(tt).Paths.Should().ContainSingle().Subject;

        path.Key.Should().Be("wtt:3F07");
        path.Vertices.Should().Equal(V(T("11:05"), TwoThirds), V(T("11:09"), TwoThirds));
        path.Direction.Should().BeNull();
    }

    [Fact]
    public void Matched_actuals_take_their_category_colour()
    {
        var live = Live("901", "1A01", MatchStatus.Matched, "1A01",
            ("A", TrainLocationAction.Depart, "07:01:10"),
            ("S123", TrainLocationAction.Pass, "07:02:00"),
            ("B", TrainLocationAction.Pass, "07:05:30"),
            ("C", TrainLocationAction.Arrive, "07:09:00"),
            ("C", TrainLocationAction.Depart, "07:10:00"));

        var scene = Build(TwoTrains(), [live], SimTime.FromClock(7, 11));

        scene.Now.Should().Be(SimTime.FromClock(7, 11));
        var actual = scene.InLayer(GraphLayer.Actual).Should().ContainSingle().Subject;
        actual.Key.Should().Be("act:901/0");
        actual.Train.Should().Be(new TrainId("1A01"));
        actual.Colour.Should().Be(Express);
        actual.IsUnmatched.Should().BeFalse();
        actual.Direction.Should().Be(TrackDirection.Down);
        actual.Vertices.Should().Equal(
            V(T("07:01:10"), 0), V(T("07:05:30"), Third), V(T("07:09:00"), TwoThirds), V(T("07:10:00"), TwoThirds));
    }

    [Fact]
    public void Signals_are_not_vertices_even_when_their_id_looks_like_a_location()
    {
        var live = Live("902", [("1A01", MatchStatus.Matched, "1A01")],
            ("A", TrainLocationAction.Depart, "07:00:00", 0, true),
            ("B", TrainLocationAction.Pass, "07:02:00", 0, false),
            ("C", TrainLocationAction.Pass, "07:08:00", 0, true));

        Build(TwoTrains(), [live]).InLayer(GraphLayer.Actual).Single().Vertices.Should().HaveCount(2);
    }

    [Fact]
    public void Unmatched_and_pending_workings_are_grey()
    {
        var unmatched = Live("903", "9Z99", MatchStatus.Unmatched, null, ("D", TrainLocationAction.Depart, "07:30"), ("C", TrainLocationAction.Pass, "07:34"));
        var pending = Live("904", "2B02", MatchStatus.Pending, null, ("D", TrainLocationAction.Depart, "07:06"), ("C", TrainLocationAction.Arrive, "07:11"));

        var actuals = Build(TwoTrains(), [unmatched, pending]).InLayer(GraphLayer.Actual).ToList();

        actuals.Should().HaveCount(2).And.OnlyContain(p => p.IsUnmatched && p.Colour == GraphSceneBuilder.UnmatchedColour && p.Train == null);
        actuals.Select(p => p.Headcode.Value).Should().Equal("9Z99", "2B02");
    }

    [Fact]
    public void A_match_to_a_train_not_in_the_timetable_uses_the_fallback_colour()
    {
        var live = Live("905", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04"));

        var path = Build(tt: null, [live]).InLayer(GraphLayer.Actual).Single();

        path.Colour.Should().Be(CategoryStyle.Fallback.Colour);
        path.IsUnmatched.Should().BeFalse();
    }

    [Fact]
    public void Each_working_of_a_live_train_is_its_own_path()
    {
        // 2B02 arrives at A and forms 1A01 back out (a next working on the same uid).
        var live = Live("906", [("2B02", MatchStatus.Matched, "2B02"), ("1A01", MatchStatus.Unmatched, null)],
            ("C", TrainLocationAction.Depart, "07:12", 0, true),
            ("A", TrainLocationAction.Arrive, "07:21", 0, true),
            ("A", TrainLocationAction.Depart, "07:40", 1, true),
            ("B", TrainLocationAction.Pass, "07:44", 1, true));

        var actuals = Build(TwoTrains(), [live]).InLayer(GraphLayer.Actual).ToList();

        actuals.Select(p => (p.Key, p.Headcode.Value, p.Colour, p.IsUnmatched)).Should().Equal(
            ("act:906/0", "2B02", Stopper, false),
            ("act:906/1", "1A01", GraphSceneBuilder.UnmatchedColour, true));
    }

    [Fact]
    public void Repeated_reports_at_one_place_and_time_make_one_vertex()
    {
        var live = Live("907", "1A01", MatchStatus.Matched, "1A01",
            ("A", TrainLocationAction.Depart, "07:00"), ("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04"));

        Build(TwoTrains(), [live]).InLayer(GraphLayer.Actual).Single().Vertices.Should().HaveCount(2);
    }

    [Fact]
    public void Layers_can_be_switched_off()
    {
        var live = Live("908", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04"));
        var request = new GraphSceneRequest(Profile, TestCorridor) { Timetable = TwoTrains(), LiveTrains = [live] };

        GraphSceneBuilder.Build(request with { ShowWtt = false }).Paths.Should().OnlyContain(p => p.Layer == GraphLayer.Actual);
        GraphSceneBuilder.Build(request with { ShowActual = false }).Paths.Should().OnlyContain(p => p.Layer == GraphLayer.Wtt).And.HaveCount(2);
    }

    [Fact]
    public void The_profile_area_names_the_scene_when_it_has_no_name()
    {
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(Profile with { Name = null }, TestCorridor));

        scene.Title.Should().Be("test — A – D");
    }
}
