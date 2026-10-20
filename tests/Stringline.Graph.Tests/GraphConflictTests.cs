using SkiaSharp;
using Stringline.Core;
using Stringline.Network;
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
        info.ValueOf("Trains").Should().Be("2B02 gives way to 1A01");
        info.ValueOf("Where").Should().Be("Charlie – B");
        info.ValueOf("When").Should().Be("07:12:00 (clash 07:08–07:16)");
        info.ValueOf("Resolve").Should().Be("delay 2B02 2 min 30 s");
        info.ValueOf("Knock-on").Should().Be("2B02 2 min 30 s late by the forecast");
        info.ValueOf("Tip").Should().Be("Click to focus it and see suggestions", "no suggestion has been worked out");
        GraphConflicts.Describe(scene.Conflicts[0], scene).ValueOf("Knock-on").Should().BeNull("there is none");
        GraphConflicts.Describe(scene.Conflicts[0], scene).Heading.Should().Be("Platform: 2B02 due in platform 1 before 1A01 clears it");
    }

    [Fact]
    public void The_tooltip_names_the_headway_and_where_it_comes_from()
    {
        var scene = Scene();
        var wtt = new GraphConflict(Headway with { HeadwaySeconds = 150, HeadwaySource = HeadwaySource.Wtt }, TwoThirds, Third, TwoThirds);
        var hand = new GraphConflict(Headway with { HeadwaySeconds = 120, HeadwaySource = HeadwaySource.Hand }, TwoThirds, Third, TwoThirds);

        var info = GraphConflicts.Describe(wtt, scene);

        info.ValueOf("Headway").Should().Be("2 min 30 s (from the WTT)");
        info.Lines.Select(l => l.Label).Should().ContainInOrder("When", "Headway", "Resolve");
        GraphConflicts.Describe(hand, scene).ValueOf("Headway").Should().Be("2 min (set by hand)");
        GraphConflicts.Describe(scene.Conflicts[0], scene).ValueOf("Headway").Should().BeNull("a platform has no headway");
    }

    [Fact]
    public void The_tooltip_gives_the_top_suggestion_once_worked_out()
    {
        var scene = Scene();

        var info = GraphConflicts.Describe(scene.Conflicts[1], scene, "Hold 2B02 at Charlie until 07:15 (+3 min)");

        info.ValueOf("Suggest").Should().Be("Hold 2B02 at Charlie until 07:15 (+3 min)");
        info.ValueOf("Tip").Should().BeNull();
    }

    [Fact]
    public void The_tooltip_names_a_block_and_a_lone_train()
    {
        var scene = Scene();
        var block = new GraphConflict(Headway with { Kind = ConflictKind.Disruption, B = null, HeadcodeB = null, DisruptionId = "B1" }, TwoThirds, Third, TwoThirds);
        var alone = new GraphConflict(Headway with { Kind = ConflictKind.Unrealistic, B = null, HeadcodeB = null, Start = Headway.When, End = Headway.When }, TwoThirds, Third, TwoThirds);

        GraphConflicts.Describe(block, scene).ValueOf("Trains").Should().Be("2B02 held by block B1");
        GraphConflicts.Describe(alone, scene).ValueOf("Trains").Should().Be("2B02");
        GraphConflicts.Describe(alone, scene).ValueOf("When").Should().Be("07:12:00", "the clash has no length");
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void The_mouse_finds_a_highlight_away_from_its_ring(GraphOrientation orientation)
    {
        var view = View(orientation);
        var scene = Scene();

        GraphConflicts.HighlightAt(scene, view, view.ToScreen(T("07:15"), 0.5))!.Id.Should().Be("H:2B02:1A01:C");
        GraphConflicts.HighlightAt(scene, view, view.ToScreen(T("07:10"), 1))!.Id.Should().Be("P:2B02:1A01:D:1", "the platform's band at D");
        GraphConflicts.HighlightAt(scene, view, view.ToScreen(T("07:25"), 0.5)).Should().BeNull();
    }

    [Fact]
    public void Of_overlapping_highlights_the_smaller_is_found()
    {
        var view = View();
        var wide = Headway with { Id = "wide", Where = new Tiploc("A"), To = new Tiploc("D"), Start = SimTime.Parse("07:00"), End = SimTime.Parse("07:25") };
        var scene = Scene(new ConflictSet([Headway, wide]));

        GraphConflicts.HighlightAt(scene, view, view.ToScreen(T("07:15"), 0.5))!.Id.Should().Be("H:2B02:1A01:C");
        GraphConflicts.HighlightAt(scene, view, view.ToScreen(T("07:02"), 0.5))!.Id.Should().Be("wide");
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
