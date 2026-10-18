using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Planning;
using Stringline.Timetable.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>Editing on the graph (S12): point indices on paths, the cancelled style, ghost previews and drag edits.</summary>
public sealed class GraphEditTests
{
    private static readonly TrainId Express1 = new("1A01");
    private static readonly TrainId Stopper2 = new("2B02");

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static SimTime S(string clock) => SimTime.Parse(clock);

    private static GraphViewport View() => GraphViewport.Create(T("06:50"), T("07:30"), 1000, 600);

    private static PlanSnapshot Snapshot(string now, WorkingPlan? plan = null, params LiveTrain[] live)
    {
        plan ??= WorkingPlan.FromTimetable(TwoTrains());
        var clock = SimClockReading.None with { HasTime = true, LastReported = S(now) };
        var forecast = Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = live,
            Now = S(now),
            Network = ForecastNetwork.Build(Profile.Corridors, plan.Timetable),
        });
        return new PlanSnapshot(1, plan.Timetable, live, clock, new MatchSummary(live.Length, live.Length, live.Length, 0, 0, new Dictionary<MatchMethod, int>()),
            1, S("06:30"), plan, Forecast: forecast);
    }

    private static GraphScene Scene(PlanSnapshot snapshot) => GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, snapshot));

    private static ScreenPoint At(string time, double axis) => View().ToScreen(T(time), axis);

    private static ScreenPoint Moved(ScreenPoint p, int seconds) => new(p.X + (seconds * View().PixelsPerSecond), p.Y);

    /// <summary>Starts a drag on the WTT or plan line of a train at a point (the hit test must find that train there).</summary>
    private static PlanDrag? Begin(PlanSnapshot snapshot, ScreenPoint at, out string? refusal, bool fromVertex = false, SimTime? now = null)
    {
        var scene = Scene(snapshot);
        var hit = GraphHitTester.HitTest(scene, View(), at);
        hit.Should().NotBeNull();
        return PlanDrag.Begin(hit!, snapshot, View(), at, now ?? snapshot.Clock.LastReported, fromVertex, out refusal);
    }

    private static PlanDrag Drag(PlanSnapshot snapshot, ScreenPoint at, bool fromVertex = false)
    {
        var drag = Begin(snapshot, at, out var refusal, fromVertex);
        refusal.Should().BeNull();
        return drag!;
    }

    // Paths carry their plan points.

    [Fact]
    public void Booked_and_planned_paths_know_their_points_and_stop_ends()
    {
        var (plan, _) = WorkingPlan.FromTimetable(TwoTrains()).ApplyAll([new ShiftTrain(Stopper2, 0, 60)]);
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor) { Timetable = TwoTrains(), Plan = plan });

        var wtt = scene.Paths.Single(p => p.Key == "wtt:2B02");
        wtt.Points.Should().Equal(0, 1, 1, 2, 3);
        wtt.IsDwellEnd(2).Should().BeTrue("the second vertex at C is the departure");
        wtt.IsDwellEnd(1).Should().BeFalse();
        wtt.IsDwellEnd(3).Should().BeFalse();
        scene.Paths.Single(p => p.Key == "plan:2B02").Points.Should().Equal(0, 1, 1, 2, 3);
        scene.Paths.Single(p => p.Key == "wtt:1A01").Points.Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public void Actual_paths_have_no_points_and_forecast_paths_do()
    {
        var snapshot = Snapshot("07:06:30", null, Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02"), ("B", TrainLocationAction.Pass, "07:06")));
        var scene = Scene(snapshot);

        scene.Paths.Single(p => p.Key == "fc:1A01").Points.Should().Equal([1, 2, 3], "it carries on from the latest actual, at B");
        scene.InLayer(GraphLayer.Actual).Should().ContainSingle().Which.Points.Should().BeNull();
    }

    // Cancelled trains

    [Fact]
    public void A_cancelled_train_draws_thin_and_grey_on_the_plan_layer()
    {
        var (plan, _) = WorkingPlan.FromTimetable(TwoTrains()).ApplyAll([new CancelTrain(Stopper2)]);
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor) { Timetable = TwoTrains(), Plan = plan });

        var cancelled = scene.Paths.Single(p => p.Key == "plan:2B02");
        cancelled.IsCancelled.Should().BeTrue();
        cancelled.Colour.Should().Be(GraphSceneBuilder.CancelledColour);
        scene.Paths.Single(p => p.Key == "wtt:2B02").IsCancelled.Should().BeFalse();
    }

    [Fact]
    public void A_cancelled_train_has_crosses_at_its_ends()
    {
        var (plan, _) = WorkingPlan.FromTimetable(TwoTrains()).ApplyAll([new CancelTrain(Stopper2)]);
        var request = new GraphSceneRequest(Profile, TestCorridor) { Plan = plan, ShowWtt = false };
        var scene = GraphSceneBuilder.Build(request);
        var view = View();
        using var renderer = new SkiaGraphRenderer();

        using var bitmap = SKBitmap.Decode(renderer.RenderPng(scene, view));

        // 2B02 starts at D (axis 1) at 07:05: the cross's arm is drawn 3 px up and to the left of the end, off the line.
        var end = view.ToScreen(T("07:05"), 1);
        var arm = bitmap.GetPixel((int)Math.Round(end.X - 3), (int)Math.Round(end.Y - 3));
        arm.Red.Should().BeLessThan(0xE0, "the cross is drawn there");
        var plain = GraphSceneBuilder.Build(request with { Plan = WorkingPlan.FromTimetable(TwoTrains()).ApplyAll([new ShiftTrain(Stopper2, 0, 60)]).Plan });
        using var shifted = SKBitmap.Decode(renderer.RenderPng(plain, view));
        shifted.GetPixel((int)Math.Round(end.X - 3), (int)Math.Round(end.Y - 3)).Red.Should().BeGreaterThan(0xE0, "a train not cancelled has no cross");
    }

    // Ghosts

    [Fact]
    public void Ghosts_draw_over_the_scene_without_changing_it()
    {
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor) { Timetable = TwoTrains() });
        var (plan, _) = WorkingPlan.FromTimetable(TwoTrains()).Apply(new ShiftTrain(Express1, 0, 600));
        var ghost = GraphSceneBuilder.PlanPaths(TestCorridor, plan.Get(Express1), Express, "ghost");
        ghost.Should().ContainSingle().Which.Key.Should().Be("ghost:1A01");
        var view = View();
        using var renderer = new SkiaGraphRenderer();

        var with = renderer.RenderPng(scene, view);
        using var canvasBitmap = new SKBitmap(view.Width, view.Height);
        using (var canvas = new SKCanvas(canvasBitmap))
        {
            renderer.Render(canvas, scene, view, ghosts: ghost);
        }

        using var plain = SKBitmap.Decode(with);
        var onGhost = view.ToScreen(T("07:14"), 1.0 / 3); // the ghost passes B ten minutes after 1A01's booking
        var elsewhere = view.ToScreen(T("07:27"), 0.5);
        Pixel(canvasBitmap, onGhost).Should().NotBe(Pixel(plain, onGhost));
        Pixel(canvasBitmap, elsewhere).Should().Be(Pixel(plain, elsewhere), "nothing else changes");

        static SKColor Pixel(SKBitmap bitmap, ScreenPoint p) => bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    // Body drag

    [Fact]
    public void Dragging_a_train_not_started_retimes_the_whole_run()
    {
        var snapshot = Snapshot("06:30");
        var press = At("07:04", 1.0 / 3);
        var drag = Drag(snapshot, press);

        drag.Kind.Should().Be(PlanDragKind.Shift);
        drag.Train.Should().Be(Express1);
        drag.Index.Should().Be(0);
        drag.Preview.Command.Should().BeNull();
        drag.Preview.Status.Should().Be("Drag to retime 1A01 from A.");

        var preview = drag.Update(View(), Moved(press, 300), fine: false);

        preview.Command.Should().Be(new ShiftTrain(Express1, 0, 300));
        preview.DeltaSeconds.Should().Be(300);
        preview.Limited.Should().BeFalse();
        preview.Train.Points[0].Depart.Should().Be(S("07:05"));
        preview.Train.Points[3].Arrive.Should().Be(S("07:17"));
        preview.Status.Should().Be("Shift 1A01 by +5 min from A");
    }

    [Fact]
    public void Dragging_snaps_to_15_seconds_or_to_1_second_with_fine()
    {
        var press = At("07:04", 1.0 / 3);
        var drag = Drag(Snapshot("06:30"), press);

        drag.Update(View(), Moved(press, 292), fine: false).DeltaSeconds.Should().Be(285);
        drag.Update(View(), Moved(press, 293), fine: false).DeltaSeconds.Should().Be(300);
        drag.Update(View(), Moved(press, 292), fine: true).DeltaSeconds.Should().Be(292);
        drag.Update(View(), Moved(press, 5), fine: false).Command.Should().BeNull("less than half a snap is no change");
    }

    [Fact]
    public void A_drag_follows_the_world_time_when_the_view_pans_under_it()
    {
        var press = At("07:04", 1.0 / 3);
        var drag = Drag(Snapshot("06:30"), press);
        var panned = View().PanTime(120);

        // The same pixel two minutes later in world time.
        drag.Update(panned, press, fine: false).DeltaSeconds.Should().Be(120);
    }

    [Fact]
    public void A_train_cannot_be_planned_before_now()
    {
        var press = At("07:04", 1.0 / 3);
        var drag = Drag(Snapshot("06:58:10"), press);

        var preview = drag.Update(View(), Moved(press, -300), fine: false);

        preview.Limited.Should().BeTrue();
        preview.DeltaSeconds.Should().Be(-105, "it leaves A at 06:58:15, the first snap at or after now");
        preview.Status.Should().EndWith("— stopped: nothing can be planned before now (06:58:10).");
    }

    [Fact]
    public void A_running_train_retimes_from_its_next_point_and_keeps_its_minimum_run()
    {
        var live = Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02"));
        var snapshot = Snapshot("07:02:30", null, live);
        var press = At("07:08", 2.0 / 3);
        var drag = Drag(snapshot, press);

        drag.Index.Should().Be(1, "it has left A, so the rest of the run is from B");
        var earlier = drag.Update(View(), Moved(press, -120), fine: false);
        earlier.Limited.Should().BeTrue();
        earlier.Command.Should().BeNull("A–B is already at its 4 min minimum");
        earlier.Status.Should().Contain("stopped: 1A01 needs at least 4 min from A to B.");

        drag.Update(View(), Moved(press, 120), fine: false).Command.Should().Be(new ShiftTrain(Express1, 1, 120));
    }

    [Fact]
    public void Alt_drag_retimes_from_the_vertex()
    {
        var snapshot = Snapshot("06:30");
        var press = At("07:08", 2.0 / 3);
        var drag = Drag(snapshot, press, fromVertex: true);

        drag.Kind.Should().Be(PlanDragKind.ShiftFrom);
        drag.Index.Should().Be(2);
        drag.Location.Should().Be(new Tiploc("C"));
        var preview = drag.Update(View(), Moved(press, 180), fine: false);
        preview.Command.Should().Be(new ShiftTrain(Express1, 2, 180));
        preview.Train.Points[1].Depart.Should().Be(S("07:04"), "B is before the vertex");
        preview.Train.Points[2].Depart.Should().Be(S("07:11"));
    }

    [Fact]
    public void Alt_drag_from_a_point_already_passed_is_refused()
    {
        var live = Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02"), ("B", TrainLocationAction.Pass, "07:06"));
        var snapshot = Snapshot("07:06:30", null, live);

        Begin(snapshot, At("07:04", 1.0 / 3), out var refusal, fromVertex: true).Should().BeNull();

        refusal.Should().Be("1A01 has already passed B.");
    }

    // Dwell drag

    [Fact]
    public void Dragging_the_end_of_a_stop_changes_the_dwell_and_pushes_the_rest_on_if_it_must()
    {
        var press = At("07:12", 2.0 / 3);
        var drag = Drag(Snapshot("06:30"), press);

        drag.Kind.Should().Be(PlanDragKind.Dwell);
        drag.Train.Should().Be(Stopper2);
        drag.Index.Should().Be(1);
        var preview = drag.Update(View(), Moved(press, 180), fine: false);

        preview.Command.Should().Be(new SetDwell(Stopper2, 1, S("07:15")));
        preview.Status.Should().StartWith("2B02 departs C at 07:15:00 (+3 min) — 2B02 needs at least 4 min from C to B");
        preview.Warnings.Should().HaveCount(2);
        preview.Train.Points[2].Depart.Should().Be(S("07:19"));
    }

    [Fact]
    public void A_dwell_cannot_end_before_the_arrival()
    {
        var press = At("07:12", 2.0 / 3);
        var drag = Drag(Snapshot("06:30"), press);

        var preview = drag.Update(View(), Moved(press, -300), fine: false);

        preview.Command.Should().Be(new SetDwell(Stopper2, 1, S("07:10")));
        preview.Limited.Should().BeTrue();
        preview.Status.Should().EndWith("stopped: 2B02 cannot leave C before it arrives (07:10:00).");
    }

    [Fact]
    public void Pressing_the_middle_of_a_stop_drags_the_body()
    {
        Drag(Snapshot("06:30"), At("07:11", 2.0 / 3)).Kind.Should().Be(PlanDragKind.Shift);
    }

    // Not editable

    [Fact]
    public void Actual_lines_are_not_dragged()
    {
        var live = Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:02"), ("B", TrainLocationAction.Pass, "07:06"));
        var snapshot = Snapshot("07:06:30", null, live);
        var scene = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, snapshot) with { ShowWtt = false, ShowForecast = false });
        var at = At("07:04", 1.0 / 6);
        var hit = GraphHitTester.HitTest(scene, View(), at)!;
        hit.Path.Layer.Should().Be(GraphLayer.Actual);

        PlanDrag.Begin(hit, snapshot, View(), at, S("07:06:30"), false, out var refusal).Should().BeNull();
        refusal.Should().BeNull("the graph pans instead");
    }

    [Fact]
    public void Cancelled_and_finished_trains_are_refused()
    {
        var (cancelled, _) = WorkingPlan.FromTimetable(TwoTrains()).Apply(new CancelTrain(Express1));
        Begin(Snapshot("06:30", cancelled), At("07:04", 1.0 / 3), out var refusal).Should().BeNull();
        refusal.Should().Be("1A01 is cancelled; reinstate it to retime it.");

        var finished = Live("901", "1A01", MatchStatus.Matched, "1A01",
            ("A", TrainLocationAction.Depart, "07:03"), ("B", TrainLocationAction.Pass, "07:07"), ("C", TrainLocationAction.Pass, "07:11"), ("D", TrainLocationAction.Arrive, "07:15"));
        Begin(Snapshot("07:16", null, finished), At("07:04", 1.0 / 3), out refusal).Should().BeNull();
        refusal.Should().Be("1A01 has finished its run.");
    }
}
