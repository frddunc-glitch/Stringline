using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Planning;
using Stringline.Timetable.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>TRIMS-style grips on the selected train (S26, #10): which grips, which are locked, hit testing, drags and nudges.</summary>
public sealed class GraphGripTests
{
    private static readonly TrainId Express1 = new("1A01");
    private static readonly TrainId Stopper2 = new("2B02");

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static SimTime S(string clock) => SimTime.Parse(clock);

    private static GraphViewport View(GraphOrientation orientation = GraphOrientation.Horizontal) =>
        GraphViewport.Create(T("06:50"), T("07:30"), 1000, 600, orientation: orientation);

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

    private static GraphGrips Grips(PlanSnapshot snapshot, TrainId train) => GraphGrips.For(Scene(snapshot), snapshot, new GraphSelection(train, null))!;

    private static GraphGrip Grip(GraphGrips grips, GraphGripKind kind, string location) =>
        grips.Grips.Single(g => g.Kind == kind && g.Location == new Tiploc(location));

    private static ScreenPoint ScreenOf(GraphGrip grip, GraphViewport? view = null) => (view ?? View()).ToScreen(grip.At.Time, grip.At.Axis);

    private static ScreenPoint Moved(ScreenPoint p, int seconds) => new(p.X + (seconds * View().PixelsPerSecond), p.Y);

    private static LiveTrain OnTime1A01(params (string, TrainLocationAction, string)[] reports) => Live("901", "1A01", MatchStatus.Matched, "1A01", reports);

    // Which grips

    [Fact]
    public void A_train_not_started_has_a_square_at_each_timing_point_a_bar_at_its_stop_and_a_body_handle()
    {
        var grips = Grips(Snapshot("06:30"), Stopper2);

        grips.Paths.Should().ContainSingle().Which.Layer.Should().Be(GraphLayer.Wtt);
        grips.Grips.Select(g => (g.Kind, g.Location.ToString(), g.Index)).Should().Equal(
            (GraphGripKind.Point, "D", 0), (GraphGripKind.Point, "C", 1), (GraphGripKind.Dwell, "C", 1), (GraphGripKind.Point, "B", 2), (GraphGripKind.Point, "A", 3));
        grips.Grips.Should().OnlyContain(g => !g.IsLocked);
        Grip(grips, GraphGripKind.Point, "C").At.Should().Be(new GraphVertex(T("07:10"), 2.0 / 3), "the square is at the arrival");
        Grip(grips, GraphGripKind.Dwell, "C").At.Should().Be(new GraphVertex(T("07:12"), 2.0 / 3), "the bar is at the departure");
        Grip(grips, GraphGripKind.Dwell, "C").LocationName.Should().Be("Charlie");

        var body = grips.InView(View()).Single(g => g.Kind == GraphGripKind.Body);
        body.Index.Should().Be(0, "a body drag moves the whole run");
        body.At.Time.Should().BeApproximately(T("07:07:30"), 1, "the middle of the first stretch, D to C");
        body.IsLocked.Should().BeFalse();
    }

    [Fact]
    public void There_is_no_bar_where_the_run_ends_though_the_wtt_times_a_departure_there()
    {
        var timetable = Tt(Train("2B02", "OO", Call("D", null, "07:05"), Call("C", "07:10", "07:12"), Call("A", "07:21", "07:25")));
        var snapshot = Snapshot("06:30", WorkingPlan.FromTimetable(timetable));

        Grips(snapshot, Stopper2).Grips.Where(g => g.Kind == GraphGripKind.Dwell).Select(g => g.Location.ToString()).Should().Equal("C");
    }

    [Fact]
    public void Only_the_selected_train_has_grips()
    {
        var snapshot = Snapshot("06:30");
        GraphGrips.For(Scene(snapshot), snapshot, null).Should().BeNull();
        GraphGrips.For(Scene(snapshot), snapshot, new GraphSelection(null, "901")).Should().BeNull("an unmatched live train has no plan");
        GraphGrips.For(Scene(snapshot), snapshot, new GraphSelection(new TrainId("9Z99"), null)).Should().BeNull();
        Grips(snapshot, Express1).Grips.Should().OnlyContain(g => g.Train == Express1);
    }

    [Fact]
    public void A_replanned_train_has_its_grips_on_the_plan_line()
    {
        var (plan, _) = WorkingPlan.FromTimetable(TwoTrains()).Apply(new ShiftTrain(Stopper2, 0, 60));
        var grips = Grips(Snapshot("06:30", plan), Stopper2);

        grips.Paths.Should().ContainSingle().Which.Layer.Should().Be(GraphLayer.Plan);
        Grip(grips, GraphGripKind.Point, "D").At.Time.Should().Be(T("07:06"));
    }

    // Locked grips

    [Fact]
    public void Points_passed_are_locked_and_say_why()
    {
        var snapshot = Snapshot("07:05", null, OnTime1A01(("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04")));
        var grips = Grips(snapshot, Express1);

        Grip(grips, GraphGripKind.Point, "A").LockReason.Should().Be("1A01 has already passed A.");
        Grip(grips, GraphGripKind.Point, "B").LockReason.Should().Be("1A01 has already passed B.");
        Grip(grips, GraphGripKind.Point, "C").IsLocked.Should().BeFalse();
        Grip(grips, GraphGripKind.Point, "D").IsLocked.Should().BeFalse();
        var body = grips.InView(View()).Single(g => g.Kind == GraphGripKind.Body);
        body.Index.Should().Be(2, "the body retimes from C, the next point not reached");
        body.At.Time.Should().BeGreaterThan(T("07:08"), "the handle is on the run still to go");
    }

    [Fact]
    public void A_train_at_a_platform_has_its_arrival_locked_and_can_still_change_its_dwell()
    {
        var live = Live("902", "2B02", MatchStatus.Matched, "2B02", ("D", TrainLocationAction.Depart, "07:05"), ("C", TrainLocationAction.Arrive, "07:10"));
        var grips = Grips(Snapshot("07:11", null, live), Stopper2);

        Grip(grips, GraphGripKind.Point, "C").LockReason.Should().Be("2B02 has arrived at C; drag its departure instead.");
        Grip(grips, GraphGripKind.Dwell, "C").IsLocked.Should().BeFalse();
        Grip(grips, GraphGripKind.Point, "D").IsLocked.Should().BeTrue();
    }

    [Fact]
    public void Every_grip_of_a_cancelled_train_is_locked()
    {
        var (plan, _) = WorkingPlan.FromTimetable(TwoTrains()).Apply(new CancelTrain(Stopper2));
        var grips = Grips(Snapshot("06:30", plan), Stopper2);

        grips.InView(View()).Should().NotBeEmpty().And.OnlyContain(g => g.LockReason == "2B02 is cancelled; reinstate it to retime it.");
    }

    // Hit testing

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void Grips_are_hit_within_7_pixels_and_the_nearest_wins(GraphOrientation orientation)
    {
        var view = View(orientation);
        var grips = Grips(Snapshot("06:30"), Stopper2);
        var square = Grip(grips, GraphGripKind.Point, "C");
        var bar = Grip(grips, GraphGripKind.Dwell, "C");
        var at = ScreenOf(square, view);

        grips.HitTest(view, at).Should().Be(square);
        grips.HitTest(view, new ScreenPoint(at.X + 4, at.Y + 4)).Should().Be(square, "5.7 px away");
        grips.HitTest(view, new ScreenPoint(at.X + 6, at.Y + 6)).Should().BeNull("8.5 px away");
        grips.HitTest(view, ScreenOf(bar, view)).Should().Be(bar);
        var body = grips.InView(view).Single(g => g.Kind == GraphGripKind.Body);
        grips.HitTest(view, ScreenOf(body, view)).Should().Be(body);
    }

    [Fact]
    public void Grips_out_of_view_are_not_drawn_or_hit()
    {
        var view = GraphViewport.Create(T("07:09"), T("07:30"), 1000, 600);
        var grips = Grips(Snapshot("06:30"), Stopper2);

        grips.InView(view).Should().NotContain(g => g.Kind != GraphGripKind.Body && g.Location == new Tiploc("D"), "D is at 07:05, before the view");
        grips.HitTest(view, view.ToScreen(T("07:05"), 1)).Should().BeNull();
    }

    /// <summary>
    /// Why a drag "did not work" (#10): over an on-time train its forecast line lies on its WTT line, and the hit test
    /// prefers the forecast, which pans. The selected train's own line is now found under it.
    /// </summary>
    [Fact]
    public void The_selected_line_is_found_under_its_forecast()
    {
        var snapshot = Snapshot("07:01", null, OnTime1A01(("A", TrainLocationAction.Depart, "07:00")));
        var scene = Scene(snapshot);
        var at = View().ToScreen(T("07:06"), 0.5); // between B and C, not yet run

        var hit = GraphHitTester.HitTest(scene, View(), at)!;
        hit.Path.Layer.Should().Be(GraphLayer.Forecast);
        PlanDrag.Begin(hit, snapshot, View(), at, S("07:01"), false, out var refusal).Should().BeNull("a press on the forecast pans…");
        refusal.Should().BeNull("…with nothing said");

        var line = Grips(snapshot, Express1).LineAt(View(), at)!;
        line.Path.Layer.Should().Be(GraphLayer.Wtt);
        PlanDrag.Begin(line, snapshot, View(), at, S("07:01"), false, out _)!.Kind.Should().Be(PlanDragKind.Shift);
    }

    [Fact]
    public void The_selected_line_is_hit_within_6_pixels()
    {
        var grips = Grips(Snapshot("06:30"), Express1);
        var on = View().ToScreen(T("07:02"), 1.0 / 6);

        grips.LineAt(View(), on).Should().NotBeNull();
        grips.LineAt(View(), new ScreenPoint(on.X, on.Y + 5.5)).Should().NotBeNull();
        grips.LineAt(View(), new ScreenPoint(on.X, on.Y + 20)).Should().BeNull();
    }

    // Dragging a grip

    [Fact]
    public void Dragging_a_square_retimes_from_that_point_without_alt()
    {
        var snapshot = Snapshot("06:30");
        var grip = Grip(Grips(snapshot, Stopper2), GraphGripKind.Point, "B");
        var press = ScreenOf(grip);
        var drag = PlanDrag.Begin(grip, snapshot, View(), press, S("06:30"), out var refusal)!;

        refusal.Should().BeNull();
        drag.Kind.Should().Be(PlanDragKind.ShiftFrom);
        drag.Index.Should().Be(2);
        var preview = drag.Update(View(), Moved(press, 180), fine: false);

        preview.Command.Should().Be(new ShiftTrain(Stopper2, 2, 180));
        preview.Time.Should().Be(S("07:19"));
        preview.Label.Should().Be("07:19:00 (+3 min)");
        preview.Train.Points[1].Depart.Should().Be(S("07:12"), "C is before the grip");
    }

    [Fact]
    public void Dragging_a_bar_changes_the_dwell()
    {
        var snapshot = Snapshot("06:30");
        var grip = Grip(Grips(snapshot, Stopper2), GraphGripKind.Dwell, "C");
        var press = ScreenOf(grip);
        var drag = PlanDrag.Begin(grip, snapshot, View(), press, S("06:30"), out _)!;

        drag.Kind.Should().Be(PlanDragKind.Dwell);
        var preview = drag.Update(View(), Moved(press, 120), fine: false);

        preview.Command.Should().Be(new SetDwell(Stopper2, 1, S("07:14")));
        preview.Label.Should().Be("07:14:00 (+2 min)");
    }

    [Fact]
    public void Dragging_the_body_handle_retimes_the_rest_of_the_run()
    {
        var snapshot = Snapshot("07:05", null, OnTime1A01(("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04")));
        var body = Grips(snapshot, Express1).InView(View()).Single(g => g.Kind == GraphGripKind.Body);
        var press = ScreenOf(body);
        var drag = PlanDrag.Begin(body, snapshot, View(), press, S("07:05"), out _)!;

        drag.Kind.Should().Be(PlanDragKind.Shift);
        drag.Update(View(), Moved(press, 60), fine: false).Command.Should().Be(new ShiftTrain(Express1, 2, 60));
    }

    [Fact]
    public void A_locked_grip_will_not_drag()
    {
        var snapshot = Snapshot("07:05", null, OnTime1A01(("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04")));
        var grip = Grip(Grips(snapshot, Express1), GraphGripKind.Point, "B");

        PlanDrag.Begin(grip, snapshot, View(), ScreenOf(grip), S("07:05"), out var refusal).Should().BeNull();
        refusal.Should().Be(grip.LockReason);
    }

    [Fact]
    public void A_clamp_shows_on_the_grip_label()
    {
        var snapshot = Snapshot("07:02:10");
        var grip = Grip(Grips(snapshot, Stopper2), GraphGripKind.Point, "D");
        var press = ScreenOf(grip);
        var drag = PlanDrag.Begin(grip, snapshot, View(), press, S("07:02:10"), out _)!;

        var preview = drag.Update(View(), Moved(press, -600), fine: false);

        preview.Limited.Should().BeTrue();
        preview.Time.Should().Be(S("07:02:15"));
        preview.Label.Should().Be("07:02:15 (−2 min 45 s) · stopped: now");
        preview.Status.Should().EndWith("stopped: nothing can be planned before now (07:02:10).");

        var dwell = PlanDrag.Begin(Grip(Grips(snapshot, Stopper2), GraphGripKind.Dwell, "C"), snapshot, View(), press, S("07:02:10"), out _)!;
        dwell.Update(View(), Moved(press, -600), fine: false).Label.Should().EndWith("· stopped: arrival");
    }

    // Nudges

    [Fact]
    public void A_nudge_moves_the_point_by_its_seconds_and_stops_at_the_same_limits()
    {
        var snapshot = Snapshot("07:04:20");
        var drag = PlanDrag.Start(snapshot, Stopper2, PlanDragKind.ShiftFrom, 0, S("07:04:20"), out var refusal)!;
        refusal.Should().BeNull();

        drag.Nudge(15).Command.Should().Be(new ShiftTrain(Stopper2, 0, 15));
        drag.Nudge(60).Command.Should().Be(new ShiftTrain(Stopper2, 0, 60));
        var early = drag.Nudge(-60);
        early.Limited.Should().BeTrue();
        early.Command.Should().Be(new ShiftTrain(Stopper2, 0, -30), "07:04:30 is the first 15 s step at or after now");

        PlanDrag.Start(snapshot, Stopper2, PlanDragKind.Dwell, 1, S("07:04:20"), out _)!.Nudge(-15).Command.Should().Be(new SetDwell(Stopper2, 1, S("07:11:45")));
    }

    // The tooltip

    [Fact]
    public void The_tooltip_says_what_a_grip_does_or_why_it_is_locked()
    {
        var snapshot = Snapshot("07:05", null, OnTime1A01(("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04")));
        var grips = Grips(snapshot, Express1);

        var free = GraphGrips.Describe(Grip(grips, GraphGripKind.Point, "C"), vertical: false);
        free.Heading.Should().Be("1A01 at Charlie");
        free.ValueOf("Grip").Should().Be("Timing point");
        free.ValueOf("Time").Should().Be("07:08:00");
        free.ValueOf("Drag").Should().Be("retime 1A01 from Charlie");
        free.ValueOf("Keys").Should().Be("Click it, then ←/→ 15 s (Shift: 1 min)");
        GraphGrips.Describe(Grip(grips, GraphGripKind.Point, "C"), vertical: true).ValueOf("Keys").Should().Contain("↑/↓");

        var locked = GraphGrips.Describe(Grip(grips, GraphGripKind.Point, "A"), vertical: false);
        locked.Heading.Should().Be("1A01 at Alpha");
        locked.ValueOf("Locked").Should().Be("1A01 has already passed A.");
        locked.ValueOf("Drag").Should().BeNull();
    }

    // Drawing

    [Fact]
    public void Movable_grips_are_filled_and_locked_ones_hollow()
    {
        var snapshot = Snapshot("07:05", null, OnTime1A01(("A", TrainLocationAction.Depart, "07:00"), ("B", TrainLocationAction.Pass, "07:04")));
        var scene = Scene(snapshot);
        var grips = Grips(snapshot, Express1);
        using var bitmap = Render(scene, grips, new GraphSelection(Express1, null));

        var movable = Pixel(bitmap, ScreenOf(Grip(grips, GraphGripKind.Point, "C")));
        movable.Red.Should().BeGreaterThan(0xB0);
        movable.Green.Should().BeLessThan(0x60, "filled in the express's red");
        var locked = Pixel(bitmap, ScreenOf(Grip(grips, GraphGripKind.Point, "B")));
        locked.Should().Be(new SKColor(0xFF, 0xFF, 0xFF), "hollow: white inside");
    }

    [Fact]
    public void A_dragged_grip_is_drawn_where_it_has_got_to_with_its_time()
    {
        var snapshot = Snapshot("06:30");
        var grips = Grips(snapshot, Stopper2);
        var grip = Grip(grips, GraphGripKind.Point, "B");
        var to = grip.At with { Time = grip.At.Time + 300 };
        using var plain = Render(Scene(snapshot), grips, new GraphSelection(Stopper2, null));
        using var dragged = Render(Scene(snapshot), grips.WithDrag(new GraphGripDrag(grip, to, "07:21:00 (+5 min)", Limited: true)), new GraphSelection(Stopper2, null));

        var moved = Pixel(dragged, View().ToScreen(to.Time, to.Axis));
        moved.Should().Be(new SKColor(0xC6, 0x28, 0x28), "held at a limit: the clamp colour");
        Pixel(dragged, ScreenOf(grip)).Should().NotBe(Pixel(plain, ScreenOf(grip)), "only the dragged grip is drawn, at its new place");
        var label = View().ToScreen(to.Time, to.Axis);
        Pixel(dragged, new ScreenPoint(label.X + 12, label.Y - 12)).Should().NotBe(Pixel(plain, new ScreenPoint(label.X + 12, label.Y - 12)), "the label is beside it");
    }

    [Fact]
    public void The_picked_grip_has_a_ring()
    {
        var snapshot = Snapshot("06:30");
        var grips = Grips(snapshot, Stopper2);
        var grip = Grip(grips, GraphGripKind.Point, "B");
        var ring = ScreenOf(grip) with { X = ScreenOf(grip).X + 10 };
        using var plain = Render(Scene(snapshot), grips, new GraphSelection(Stopper2, null));
        using var picked = Render(Scene(snapshot), grips.WithSelected(grip), new GraphSelection(Stopper2, null));

        grips.WithSelected(grip).Selected.Should().Be(grip);
        Pixel(picked, ring).Should().NotBe(Pixel(plain, ring));
    }

    private static SKBitmap Render(GraphScene scene, GraphGrips grips, GraphSelection selection)
    {
        var view = View();
        using var renderer = new SkiaGraphRenderer { CacheTiles = false };
        var bitmap = new SKBitmap(view.Width, view.Height);
        using var canvas = new SKCanvas(bitmap);
        renderer.Render(canvas, scene with { Now = null }, view, selection, grips: grips);
        return bitmap;
    }

    private static SKColor Pixel(SKBitmap bitmap, ScreenPoint p) => bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));
}
