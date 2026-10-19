using SkiaSharp;
using Stringline.Core;
using Stringline.Planning;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>S14: conflicts on the graph (red rings and section highlights), what is under the mouse, and the tooltip.</summary>
public sealed class GraphConflictTests
{
    private const double Third = 1.0 / 3, TwoThirds = 2.0 / 3;

    private static readonly Conflict Headway = new("H:2B02:1A01:C", ConflictKind.Headway, new TrainId("2B02"), new TrainId("1A01"), new Tiploc("C"),
        SimTime.Parse("07:12"), 150)
    {
        HeadcodeA = new Headcode("2B02"),
        HeadcodeB = new Headcode("1A01"),
        To = new Tiploc("B"),
        Start = SimTime.Parse("07:08"),
        End = SimTime.Parse("07:16"),
        KnockOnSeconds = 150,
    };

    private static readonly Conflict Platform = new("P:2B02:1A01:D:1", ConflictKind.Platform, new TrainId("2B02"), new TrainId("1A01"), new Tiploc("D"),
        SimTime.Parse("07:05"), 420)
    {
        HeadcodeA = new Headcode("2B02"),
        HeadcodeB = new Headcode("1A01"),
        Platform = "1",
        Start = SimTime.Parse("07:05"),
        End = SimTime.Parse("07:12"),
    };

    /// <summary>At B, running off the corridor to X: drawn at B alone.</summary>
    private static readonly Conflict OffTo = Headway with { Id = "H:off", Where = new Tiploc("B"), To = new Tiploc("X"), When = SimTime.Parse("07:20") };

    /// <summary>Wholly off the corridor.</summary>
    private static readonly Conflict Off = Headway with { Id = "H:x", Where = new Tiploc("X"), To = new Tiploc("Y") };

    private static readonly ConflictSet Conflicts = new([Headway, Platform, OffTo, Off]);

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static GraphScene Scene(ConflictSet? conflicts = null, bool show = true) => GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
    {
        Timetable = TwoTrains(),
        Now = SimTime.Parse("07:00"),
        Conflicts = conflicts ?? Conflicts,
        ShowConflicts = show,
    });

    private static GraphViewport View(GraphOrientation orientation = GraphOrientation.Horizontal) =>
        GraphViewport.Create(T("06:45"), T("07:30"), 800, 400, orientation: orientation);

    [Fact]
    public void Conflicts_meeting_the_corridor_are_in_the_scene()
    {
        var scene = Scene();

        scene.Conflicts.Select(c => c.Id).Should().Equal("P:2B02:1A01:D:1", "H:2B02:1A01:C", "H:off");
        var headway = scene.Conflicts[1];
        (headway.Axis, headway.AxisFrom, headway.AxisTo).Should().Be((TwoThirds, Third, TwoThirds), "the ring is where 2B02 enters; the highlight spans the section");
        (headway.Time, headway.TimeStart, headway.TimeEnd).Should().Be((T("07:12"), T("07:08"), T("07:16")));
        var platform = scene.Conflicts[0];
        (platform.AxisFrom, platform.AxisTo).Should().Be((1, 1));
        var offTo = scene.Conflicts[2];
        (offTo.Axis, offTo.AxisFrom, offTo.AxisTo).Should().Be((Third, Third, Third), "its far end is not on the corridor");
        GraphConflicts.Shows(TestCorridor, Off).Should().BeFalse();
        GraphConflicts.Shows(TestCorridor, Headway).Should().BeTrue();
    }

    [Fact]
    public void Conflicts_can_be_hidden()
    {
        Scene(show: false).Conflicts.Should().BeEmpty();
        Scene(ConflictSet.Empty).Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void A_detected_conflict_reaches_the_scene_from_a_snapshot()
    {
        // A block over C–D holds 1A01 at C: a disruption conflict, ringed at C.
        var block = new Disruption("B1", DisruptionKind.Block, new Tiploc("C"), new Tiploc("D"), null, SimTime.Parse("07:00"), SimTime.Parse("07:20"));
        var (plan, _) = WorkingPlan.FromTimetable(TwoTrains()).Apply(new AddDisruption(block));
        var forecast = Forecaster.Run(new ForecastRequest(plan));
        var conflicts = ConflictDetector.Detect(new ConflictRequest(plan, forecast));
        var snapshot = new PlanSnapshot(1, plan.Timetable, [], Core.Live.SimClockReading.None, PlanSnapshot.Empty.Matches, 0, null, plan, Forecast: forecast, Conflicts: conflicts);

        var scene = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, snapshot));

        var c = scene.Conflicts.Should().Contain(c => c.Conflict.A == new TrainId("1A01")).Subject;
        (c.Conflict.Kind, c.Axis, c.Time).Should().Be((ConflictKind.Disruption, TwoThirds, T("07:08")));
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void A_highlight_covers_the_section_for_the_time_of_the_clash(GraphOrientation orientation)
    {
        var view = View(orientation);
        var headway = Scene().Conflicts[1];

        var rect = GraphConflicts.RectOf(headway, view);

        var corner = view.ToScreen(T("07:08"), Third);
        var opposite = view.ToScreen(T("07:16"), TwoThirds);
        (rect.Left, rect.Top, rect.Right, rect.Bottom).Should().Be(
            (Math.Min(corner.X, opposite.X), Math.Min(corner.Y, opposite.Y), Math.Max(corner.X, opposite.X), Math.Max(corner.Y, opposite.Y)));
        GraphConflicts.RingOf(headway, view).Should().Be(view.ToScreen(T("07:12"), TwoThirds));
    }

    [Fact]
    public void A_highlight_at_one_location_is_a_narrow_band_along_it()
    {
        var view = View();
        var rect = GraphConflicts.RectOf(Scene().Conflicts[0], view);

        rect.Height.Should().Be(GraphConflicts.MinThickness);
        (rect.Top + rect.Bottom).Should().BeApproximately(2 * view.AxisToPixel(1), 1e-9);
    }

    [Fact]
    public void The_mouse_finds_the_nearest_ring()
    {
        var view = View();
        var scene = Scene();
        var ring = view.ToScreen(T("07:12"), TwoThirds);

        GraphConflicts.ConflictAt(scene, view, ring)!.Id.Should().Be("H:2B02:1A01:C");
        GraphConflicts.ConflictAt(scene, view, new ScreenPoint(ring.X + 8, ring.Y))!.Id.Should().Be("H:2B02:1A01:C");
        GraphConflicts.ConflictAt(scene, view, new ScreenPoint(ring.X + 20, ring.Y)).Should().BeNull();
    }

    [Fact]
    public void The_tooltip_says_what_clashes_and_how_to_resolve_it()
    {
        var scene = Scene();

        var info = GraphConflicts.Describe(scene.Conflicts[1], scene);

        info.Heading.Should().Be("Headway: 2B02 too close behind 1A01");
        info.ValueOf("Where").Should().Be("Charlie – B");
        info.ValueOf("When").Should().Be("07:12:00");
        info.ValueOf("Resolve").Should().Be("delay 2B02 2 min 30 s");
        info.ValueOf("Knock-on").Should().Be("2B02 2 min 30 s late by the forecast");
        GraphConflicts.Describe(scene.Conflicts[0], scene).ValueOf("Knock-on").Should().BeNull("there is none");
        GraphConflicts.Describe(scene.Conflicts[0], scene).Heading.Should().Be("Platform: 2B02 due in platform 1 before 1A01 clears it");
    }

    // Drawing

    private static SKColor PixelAt(SKBitmap bitmap, ScreenPoint p) => bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));

    private static SKBitmap Render(GraphScene scene, GraphViewport view, GraphSelection? selection = null)
    {
        using var renderer = new SkiaGraphRenderer { CacheTiles = false };
        using var bitmap = new SKBitmap(view.Width, view.Height);
        using (var canvas = new SKCanvas(bitmap))
        {
            renderer.Render(canvas, scene, view, selection);
        }

        return bitmap.Copy();
    }

    [Fact]
    public void Rings_are_red_and_highlights_tinted()
    {
        var view = View();
        using var bitmap = Render(Scene(), view);

        var ring = view.ToScreen(T("07:12"), TwoThirds);
        var onRing = PixelAt(bitmap, new ScreenPoint(ring.X + GraphConflicts.RingRadius, ring.Y));
        var inHighlight = PixelAt(bitmap, view.ToScreen(T("07:15"), 0.5));
        var plain = PixelAt(bitmap, view.ToScreen(T("07:25"), 0.5));
        (onRing.Red - onRing.Blue).Should().BeGreaterThan(100);
        (inHighlight.Red - inHighlight.Blue).Should().BeGreaterThan(5);
        (plain.Red - plain.Blue).Should().BeLessThan(5);
    }

    [Fact]
    public void The_selected_conflict_draws_stronger()
    {
        var view = View();
        var scene = Scene();
        using var plain = Render(scene, view);
        using var selected = Render(scene, view, new GraphSelection(new TrainId("2B02"), null) { ConflictId = "H:2B02:1A01:C" });

        var inside = view.ToScreen(T("07:15"), 0.5);
        PixelAt(selected, inside).Green.Should().BeLessThan(PixelAt(plain, inside).Green);
    }

    [Fact]
    public Task Small_corridor_with_conflicts_snapshot() =>
        SkiaGraphRendererTests.VerifyPng(Scene(), View());
}
