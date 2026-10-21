using System.Collections.Immutable;
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
/// S38 (#17, #19): learned signal places on the graph: actual lines bend at the signals passed, the forecast and live
/// head go on from where the train is known to be, a train standing at a signal waits there, and placed signals show
/// on the axis as ticks or rows.
/// </summary>
public sealed class GraphSignalTests
{
    private const double Third = 1.0 / 3;

    private static readonly Tiploc A = new("A"), B = new("B"), C = new("C");

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static GraphVertex V(double time, double axis) => new(time, axis);

    /// <summary>
    /// S1 between A and B (half way for trains starting at A, 0.6 running through), S2 and S3 between B and C (a quarter
    /// and three quarters; S3 follows S2), and an Up signal S9 half way from C to B.
    /// </summary>
    private static readonly StringlineProfile Placed = Profile.WithPlaces(Places());

    private static SignallingPlaces Places()
    {
        var places = SignallingPlaces.Empty
            .SetByHand(SignallingKind.Signal, "S1", A, B, 0.5, RunKind.Starting)
            .SetByHand(SignallingKind.Signal, "S1", A, B, 0.6)
            .SetByHand(SignallingKind.Signal, "S2", B, C, 0.25)
            .SetByHand(SignallingKind.Signal, "S3", B, C, 0.75)
            .SetByHand(SignallingKind.Signal, "S9", C, B, 0.5)
            .SetByHand(SignallingKind.TrackCircuit, "T4", B, C, 0.5);
        places = places.With(SignallingKind.TrackCircuit, [.. places.TrackCircuits.Select(p => p with { Signal = "S2" })]);
        return places.With(SignallingKind.Signal, [.. places.Signals.Select(p => p.Id == "S2" ? p with { Next = new Dictionary<string, int> { ["S3"] = 4 } } : p)]);
    }

    /// <summary>A live train; a location starting with S is a signal, passed at green after approaching at <paramref name="approach"/>.</summary>
    private static LiveTrain Live1A01(int approach = 6, params (string Location, TrainLocationAction Action, string Time)[] actuals) =>
        new("901",
            [new LiveWorking(new Headcode("1A01"), 0, MatchStatus.Matched, new TrainId("1A01"), MatchMethod.HeadcodeAndTime)],
            actuals.Select(a => a.Location.StartsWith('S')
                ? new ActualEvent(new Tiploc(a.Location), a.Action, SimTime.Parse(a.Time), "", false, 0, 6, approach)
                : new ActualEvent(new Tiploc(a.Location), a.Action, SimTime.Parse(a.Time), "", true, 0)).ToImmutableList(),
            false);

    private static PlanSnapshot Snapshot(string now, LineSideState? lineSide, params LiveTrain[] live) => Snapshot(now, lineSide, [], live);

    private static PlanSnapshot Snapshot(string now, LineSideState? lineSide, IPlanCommand[] commands, params LiveTrain[] live)
    {
        var plan = WorkingPlan.FromTimetable(TwoTrains()).ApplyAll(commands).Plan;
        var network = ForecastNetwork.For(Placed, plan.Timetable);
        var forecast = Forecaster.Run(new ForecastRequest(plan) { LiveTrains = live, Now = SimTime.Parse(now), Network = network, LineSide = lineSide ?? LineSideState.Empty });
        var clock = SimClockReading.None with { HasTime = true, LastReported = SimTime.Parse(now) };
        return new PlanSnapshot(1, plan.Timetable, live, clock, new MatchSummary(live.Length, live.Length, live.Length, 0, 0, new Dictionary<MatchMethod, int>()),
            1, SimTime.Parse("06:30"), plan, Forecast: forecast, Settings: new ForecastSettings(network, new ForecastOptions(), new ConflictOptions()), LineSide: lineSide);
    }

    private static GraphScene Build(PlanSnapshot snapshot, Func<GraphSceneRequest, GraphSceneRequest>? change = null)
    {
        var request = GraphSceneRequest.For(Placed, TestCorridor, snapshot);
        return GraphSceneBuilder.Build(change is null ? request : change(request));
    }

    private static readonly LiveTrain Through = Live1A01(6, ("A", TrainLocationAction.Depart, "07:02"), ("S1", TrainLocationAction.Pass, "07:04"), ("B", TrainLocationAction.Pass, "07:06"), ("S2", TrainLocationAction.Pass, "07:07"));

    [Fact]
    public void The_actual_line_bends_at_each_placed_signal_passed()
    {
        var actual = Build(Snapshot("07:07:10", null, Through)).InLayer(GraphLayer.Actual).Single();

        actual.Vertices.Should().Equal(V(T("07:02"), 0), V(T("07:04"), Third / 2), V(T("07:06"), Third), V(T("07:07"), Third * 1.25));
        actual.Signals.Should().Equal(null, "S1", null, "S2");
        actual.SignalAt(1).Should().Be("S1");
        actual.SignalAt(0).Should().BeNull();
    }

    [Fact]
    public void A_signal_is_placed_for_the_kind_of_run_the_train_made()
    {
        var passing = Live1A01(6, ("A", TrainLocationAction.Pass, "07:02"), ("S1", TrainLocationAction.Pass, "07:04"), ("B", TrainLocationAction.Pass, "07:06"));

        Build(Snapshot("07:06:10", null, passing)).InLayer(GraphLayer.Actual).Single().Vertices[1].Axis.Should().BeApproximately(0.6 * Third, 1e-12, "it ran through A");
    }

    [Fact]
    public void Without_places_the_line_goes_straight_between_timing_locations()
    {
        var actual = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, Snapshot("07:07:10", null, Through)) with { Places = UsablePlaces.Empty })
            .InLayer(GraphLayer.Actual).Single();

        actual.Vertices.Should().Equal(V(T("07:02"), 0), V(T("07:06"), Third));
        actual.Signals.Should().BeNull();
    }

    [Fact]
    public void The_forecast_and_the_head_go_on_from_the_last_signal_passed()
    {
        var scene = Build(Snapshot("07:07:10", null, Through));

        var forecast = scene.InLayer(GraphLayer.Forecast).Single(p => p.Train == new TrainId("1A01"));
        forecast.Vertices[0].Should().Be(V(T("07:07"), Third * 1.25));
        forecast.Vertices[1].Should().Be(V(T("07:10"), 2 * Third), "three quarters of B–C's 4 min left");
        scene.LiveHeads.Single().Vertices.Should().Equal(V(T("07:07"), Third * 1.25), V(T("07:10"), 2 * Third));
    }

    [Fact]
    public void A_track_circuit_entered_moves_the_head_on_from_the_end_of_the_actual_line()
    {
        var lineSide = LineSideState.Empty.With(new SignallingStateEvent(DateTimeOffset.UnixEpoch, "test", SignallingObjectType.Track, "track", "T4", SimTime.Parse("07:08"), new Dictionary<string, string> { ["clear"] = "False" }));
        var scene = Build(Snapshot("07:08:10", lineSide, Through));

        scene.LiveHeads.Single().Vertices.Should().Equal([V(T("07:07"), Third * 1.25), V(T("07:08"), Third * 1.5), V(T("07:10"), 2 * Third)], "half of B–C's 4 min left");
    }

    [Fact]
    public void A_train_standing_at_a_signal_waits_there_on_the_head_and_the_forecast()
    {
        var lineSide = LineSideState.Empty.With(new SignallingStateEvent(DateTimeOffset.UnixEpoch, "test", SignallingObjectType.Signal, "signal", "S3", SimTime.Parse("07:00"), new Dictionary<string, string> { ["aspect"] = "0" }));
        var snapshot = Snapshot("07:11", lineSide, Through);
        var scene = Build(snapshot);

        snapshot.Forecast.Find(new TrainId("1A01"))!.Standing!.Signal.Should().Be("S3");
        var signal = Third * 1.75;
        var head = scene.LiveHeads.Single();
        head.Vertices.Should().Equal(V(T("07:07"), Third * 1.25), V(T("07:09"), signal), V(T("07:09") + SimTime.SecondsPerDay, signal));
        head.HeadAt(T("07:30")).Should().Be(V(T("07:30"), signal), "it stays at the signal until a report moves it");

        var forecast = scene.InLayer(GraphLayer.Forecast).Single(p => p.Train == new TrainId("1A01"));
        forecast.Vertices.Take(3).Should().Equal(V(T("07:09"), signal), V(T("07:11"), signal), V(T("07:12"), 2 * Third));
        forecast.Signals![0].Should().Be("S3");

        var info = GraphHover.Describe(new GraphHit(forecast, 0, 0, 0, forecast.Vertices[0].Time), scene, snapshot);
        info.Lines.Should().Contain(new HoverLine(GraphHover.Note, "Standing at S3 since 07:09 (at red); forecast to move at 07:11 at the earliest"));
    }

    [Fact]
    public void A_hold_at_a_signal_draws_a_flat_step_at_its_place()
    {
        // S33: 1A01 passed S2 at 07:07 and is held at S3 until 07:12: it gets there at 07:09, waits, and reaches C a
        // minute after leaving.
        var hold = new HoldAtSignal(new TrainId("1A01"), new SignalHold(1, "S3", B, C, 0.75, 0.75, SimTime.Parse("07:12")));
        var snapshot = Snapshot("07:07:10", null, [hold], Through);
        var scene = Build(snapshot);

        var signal = Third * 1.75;
        var forecast = scene.InLayer(GraphLayer.Forecast).Single(p => p.Train == new TrainId("1A01"));
        forecast.Vertices.Take(4).Should().Equal(V(T("07:07"), Third * 1.25), V(T("07:09"), signal), V(T("07:12"), signal), V(T("07:13"), 2 * Third));
        forecast.Signals!.Take(3).Should().Equal("S2", "S3", "S3");

        var info = GraphHover.Describe(new GraphHit(forecast, 1, 0, 1, forecast.Vertices[1].Time), scene, snapshot);
        info.Lines.Should().Contain(new HoverLine(GraphHover.Note, "Held at S3 from 07:09 until 07:12 (planned hold)"));
    }

    [Fact]
    public void The_tooltip_at_a_signal_says_when_it_was_passed_and_at_what()
    {
        var checkedTrain = Live1A01(0, ("A", TrainLocationAction.Depart, "07:02"), ("S1", TrainLocationAction.Pass, "07:04:30"), ("B", TrainLocationAction.Pass, "07:06"));
        var snapshot = Snapshot("07:06:10", null, checkedTrain);
        var scene = Build(snapshot);
        var actual = scene.InLayer(GraphLayer.Actual).Single();

        var info = GraphHover.Describe(new GraphHit(actual, 1, 0, 1, actual.Vertices[1].Time), scene, snapshot);

        info.ValueOf(GraphHover.Location).Should().Be("S1 (signal)");
        info.ValueOf(GraphHover.Actual).Should().Be("passed S1 at 07:04:30 (green, approached at red)");
        info.ValueOf(GraphHover.Booked).Should().BeNull();
        GraphHover.FormatPass(new ActualEvent(new Tiploc("S457"), TrainLocationAction.Pass, SimTime.Parse("09:12:30"), "", false, 0, 2, 2)).Should().Be("passed S457 at 09:12:30 (single yellow)");
    }

    // ---- Signals on the axis ----

    [Fact]
    public void Placed_signals_are_marked_on_the_axis_with_their_direction()
    {
        var scene = Build(Snapshot("07:00", null));

        scene.SignalDisplay.Should().Be(SignalDisplay.Ticks, "ticks are the default (Fred, S38)");
        scene.Signals.Select(s => (s.Id, Math.Round(s.Axis, 6), s.Direction)).Should().Equal(
            ("S1", Math.Round(0.6 * Third, 6), TrackDirection.Down),
            ("S2", Math.Round(1.25 * Third, 6), TrackDirection.Down),
            ("S9", Math.Round(1.5 * Third, 6), TrackDirection.Up),
            ("S3", Math.Round(1.75 * Third, 6), TrackDirection.Down));
        scene.Signals[0].Run.Should().Be(RunKind.Through, "the mark is the place for trains running through");

        Build(Snapshot("07:00", null), r => r with { Signals = SignalDisplay.Off }).Signals.Should().BeEmpty();
        Build(Snapshot("07:00", null), r => r with { Signals = SignalDisplay.Rows }).SignalDisplay.Should().Be(SignalDisplay.Rows);
    }

    [Fact]
    public void A_tick_is_found_under_the_mouse_and_described()
    {
        var scene = Build(Snapshot("07:00", null));
        var viewport = GraphViewport.Create(T("06:55"), T("07:20"), 600, 300);
        var s9 = scene.Signals.Single(s => s.Id == "S9");
        var y = viewport.AxisToPixel(s9.Axis);

        var target = GraphHitTester.TargetAt(scene, viewport, new ScreenPoint(viewport.PlotRight - 2, y));
        target!.Kind.Should().Be(GraphTargetKind.Signal, "an Up signal's tick is on the far edge");
        target.Signal.Should().Be(s9);
        GraphHitTester.TargetAt(scene, viewport, new ScreenPoint(viewport.PlotLeft + 2, y))?.Kind.Should().NotBe(GraphTargetKind.Signal);

        var info = GraphHover.Describe(target, scene, Snapshot("07:00", null));
        info.Heading.Should().Be("S9");
        info.ValueOf("Signal").Should().Be("Up, Charlie → B");
        info.ValueOf("Place").Should().Be("50% of the run (running through)");
        info.ValueOf("Learned").Should().Be("set by hand");
    }

    [Fact]
    public void A_signal_passed_at_an_even_speed_draws_no_corner()
    {
        // The renderer leaves out corners slighter than a quarter pixel, so signal vertices cost nothing where the train ran evenly.
        int Points(params GraphVertex[] vertices)
        {
            var path = new GraphPath("act:1", GraphLayer.Actual, null, new Headcode("1A01"), Express, vertices);
            using var cache = new ProjectedPathCache();
            cache.Begin(new GraphScene("main", "t", [], [path], null), GraphViewport.Create(T("06:55"), T("07:20"), 600, 300));
            return cache.Get(path).Sum(r => r.Path.PointCount);
        }

        Points(V(T("07:00"), 0), V(T("07:02"), 0.2), V(T("07:05"), 0.5), V(T("07:10"), 1)).Should().Be(Points(V(T("07:00"), 0), V(T("07:10"), 1)));
        Points(V(T("07:00"), 0), V(T("07:06"), 0.2), V(T("07:10"), 1)).Should().Be(3, "a real check bends the line");
    }

    [Fact]
    public Task Signal_ticks_snapshot() =>
        SkiaGraphRendererTests.VerifyPng(Build(Snapshot("07:07:10", null, Through)), GraphViewport.Create(T("06:55"), T("07:20"), 600, 300));

    [Fact]
    public Task Signal_rows_snapshot() =>
        SkiaGraphRendererTests.VerifyPng(Build(Snapshot("07:07:10", null, Through), r => r with { Signals = SignalDisplay.Rows }), GraphViewport.Create(T("06:55"), T("07:20"), 600, 300));
}
