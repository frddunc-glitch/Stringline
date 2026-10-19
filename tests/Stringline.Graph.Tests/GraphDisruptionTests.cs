using SkiaSharp;
using Stringline.Core;
using Stringline.Network;
using Stringline.Planning;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>S13: blocks, speed restrictions and notes on the graph, what is under the mouse, and the span a B+drag picks.</summary>
public sealed class GraphDisruptionTests
{
    private const double Third = 1.0 / 3, TwoThirds = 2.0 / 3;

    private static readonly Disruption Block = new("B1", DisruptionKind.Block, new Tiploc("C"), new Tiploc("A"), "Down", SimTime.Parse("07:00"),
        SimTime.Parse("07:20"), Note: "Engineering", Direction: TrackDirection.Down);

    private static readonly Disruption Restriction = new("SR1", DisruptionKind.SpeedRestriction, new Tiploc("C"), new Tiploc("D"), null,
        SimTime.Parse("06:50"), SimTime.Parse("07:25"), 1.5, 30);

    private static readonly PlanNote Note = new("N1", new Tiploc("B"), SimTime.Parse("07:10"), "Relief driver waiting\nat the signal");

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    private static WorkingPlan Plan(params IPlanCommand[] commands)
    {
        var (plan, results) = WorkingPlan.FromTimetable(TwoTrains()).ApplyAll(commands);
        results.Where(r => !r.Applied).Should().BeEmpty();
        return plan;
    }

    private static WorkingPlan Disrupted() => Plan(
        new AddDisruption(Block),
        new AddDisruption(Restriction),
        new AddDisruption(Restriction with { Id = "SR2", To = new Tiploc("X") }),
        new AddNote(Note),
        new AddNote(Note with { Id = "N2", Location = new Tiploc("X") }));

    private static GraphScene Scene(WorkingPlan plan, bool show = true) => GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
    {
        Timetable = plan.Timetable,
        Plan = plan,
        Now = SimTime.Parse("07:05"),
        ShowDisruptions = show,
    });

    private static GraphViewport View(GraphOrientation orientation = GraphOrientation.Horizontal) =>
        GraphViewport.Create(T("06:45"), T("07:30"), 800, 400, orientation: orientation);

    [Fact]
    public void Disruptions_and_notes_on_the_corridor_are_in_the_scene()
    {
        var scene = Scene(Disrupted());

        scene.Disruptions.Select(d => d.Id).Should().Equal("B1", "SR1");
        var block = scene.Disruptions[0];
        (block.AxisFrom, block.AxisTo).Should().Be((0, TwoThirds), "the ends are ordered along the corridor");
        (block.TimeStart, block.TimeEnd).Should().Be((T("07:00"), T("07:20")));
        block.Label.Should().Be("Block · Down");
        scene.Disruptions[1].Label.Should().Be("SR ×1.5 +30 s");
        var note = scene.Notes.Should().ContainSingle().Subject;
        (note.Id, note.Axis, note.Time, note.Label).Should().Be(("N1", Third, T("07:10"), "Relief driver waiting"));
    }

    [Fact]
    public void Disruptions_can_be_hidden()
    {
        var scene = Scene(Disrupted(), show: false);

        scene.Disruptions.Should().BeEmpty();
        scene.Notes.Should().BeEmpty();
    }

    [Fact]
    public void Labels_name_the_running_and_track()
    {
        GraphDisruption.LabelOf(Restriction with { RunTimeFactor = 1, AddedSeconds = 90, Track = "UF" }).Should().Be("SR +1 min 30 s · UF");
        GraphDisruption.LabelOf(Restriction with { RunTimeFactor = 2.25, AddedSeconds = 0 }).Should().Be("SR ×2.25");
        new GraphNote(Note with { Text = new string('x', 50) }, 0, (Note with { Text = new string('x', 50) }).Summary(GraphAnnotations.NoteLabelLength))
            .Label.Should().HaveLength(GraphAnnotations.NoteLabelLength).And.EndWith("…");
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void A_disruption_covers_its_time_and_locations(GraphOrientation orientation)
    {
        var view = View(orientation);
        var block = Scene(Disrupted()).Disruptions[0];

        var rect = GraphAnnotations.RectOf(block, view);

        var corner = view.ToScreen(T("07:00"), 0);
        var opposite = view.ToScreen(T("07:20"), TwoThirds);
        (rect.Left, rect.Top, rect.Right, rect.Bottom).Should().Be(
            (Math.Min(corner.X, opposite.X), Math.Min(corner.Y, opposite.Y), Math.Max(corner.X, opposite.X), Math.Max(corner.Y, opposite.Y)));
    }

    [Fact]
    public void A_disruption_at_one_location_is_a_narrow_band_along_it()
    {
        var view = View();
        var at = GraphDisruption.For(Block with { From = new Tiploc("C"), To = new Tiploc("C") }, TwoThirds, TwoThirds);

        var rect = GraphAnnotations.RectOf(at, view);

        rect.Height.Should().Be(GraphAnnotations.MinThickness);
        (rect.Top + rect.Bottom).Should().BeApproximately(2 * view.AxisToPixel(TwoThirds), 1e-9);
    }

    [Fact]
    public void The_mouse_finds_notes_then_disruptions()
    {
        var view = View();
        var scene = Scene(Disrupted());

        GraphAnnotations.DisruptionAt(scene, view, view.ToScreen(T("07:10"), 0.5))!.Id.Should().Be("B1");
        GraphAnnotations.DisruptionAt(scene, view, view.ToScreen(T("06:55"), 0.8))!.Id.Should().Be("SR1");
        GraphAnnotations.DisruptionAt(scene, view, view.ToScreen(T("06:55"), 0.5)).Should().BeNull();

        var marker = view.ToScreen(T("07:10"), Third);
        GraphAnnotations.NoteAt(scene, view, marker)!.Id.Should().Be("N1");
        var label = GraphAnnotations.LabelOf(scene.Notes[0], view);
        GraphAnnotations.NoteAt(scene, view, new ScreenPoint(label.Right - 2, label.Top + 2))!.Id.Should().Be("N1");
        GraphAnnotations.NoteAt(scene, view, new ScreenPoint(marker.X - 20, marker.Y + 20)).Should().BeNull();
    }

    [Fact]
    public void A_drag_between_sections_widens_to_the_locations_around_it_and_snaps_to_five_minutes()
    {
        var view = View();
        var scene = Scene(Plan());

        var span = GraphAnnotations.SpanBetween(scene, view, view.ToScreen(T("07:12:40"), 0.8), view.ToScreen(T("07:01:10"), 0.4))!.Value;

        (span.From.Tiploc.Code, span.To.Tiploc.Code).Should().Be(("B", "D"));
        (span.Start, span.End).Should().Be((SimTime.Parse("07:00"), SimTime.Parse("07:15")));
    }

    [Fact]
    public void A_point_picks_the_section_around_it_or_the_location_it_is_on()
    {
        var view = View();
        var scene = Scene(Plan());

        var section = GraphAnnotations.SpanBetween(scene, view, view.ToScreen(T("07:02"), 0.5), view.ToScreen(T("07:02"), 0.5))!.Value;
        (section.From.Tiploc.Code, section.To.Tiploc.Code).Should().Be(("B", "C"));
        (section.Start, section.End).Should().Be((SimTime.Parse("07:00"), SimTime.Parse("07:05")), "at least one step long");

        var onLine = view.ToScreen(T("07:02"), TwoThirds);
        var station = GraphAnnotations.SpanBetween(scene, view, onLine with { Y = onLine.Y + 3 }, onLine with { X = onLine.X + 100, Y = onLine.Y - 2 })!.Value;
        (station.From.Tiploc.Code, station.To.Tiploc.Code).Should().Be(("C", "C"));

        GraphAnnotations.NearestLocation(scene, view, view.ToScreen(T("07:02"), 0.6))!.Tiploc.Code.Should().Be("C");
    }

    [Fact]
    public void A_vertical_drag_picks_the_same_span()
    {
        var view = View(GraphOrientation.Vertical);
        var scene = Scene(Plan());

        var span = GraphAnnotations.SpanBetween(scene, view, view.ToScreen(T("07:12:40"), 0.8), view.ToScreen(T("07:01:10"), 0.4))!.Value;

        (span.From.Tiploc.Code, span.To.Tiploc.Code, span.Start, span.End).Should().Be(("B", "D", SimTime.Parse("07:00"), SimTime.Parse("07:15")));
    }

    [Fact]
    public void Tooltips_describe_disruptions_and_notes()
    {
        var scene = Scene(Disrupted());

        GraphAnnotations.Describe(scene.Disruptions[0], scene).ToString().Should().Be(
            "Block B1" + Environment.NewLine + "Where: Charlie – Alpha" + Environment.NewLine + "When: 07:00:00 – 07:20:00" + Environment.NewLine +
            "Track: Down (Down trains)" + Environment.NewLine + "Note: Engineering");
        var restriction = GraphAnnotations.Describe(scene.Disruptions[1], scene);
        restriction.Heading.Should().Be("Speed restriction SR1");
        restriction.ValueOf("Running").Should().Be("×1.5 +30 s");
        restriction.ValueOf("Track").Should().Be("All lines");
        GraphAnnotations.Describe(scene.Notes[0], scene).ValueOf("At").Should().Be("B 07:10:00");
    }

    // Drawing

    private static SKColor PixelAt(SKBitmap bitmap, ScreenPoint p) => bitmap.GetPixel((int)p.X, (int)p.Y);

    private static SKBitmap Render(GraphScene scene, GraphViewport view, GraphDisruption? draft = null)
    {
        using var renderer = new SkiaGraphRenderer { CacheTiles = false };
        using var bitmap = new SKBitmap(view.Width, view.Height);
        using (var canvas = new SKCanvas(bitmap))
        {
            renderer.Render(canvas, scene, view, draft: draft);
        }

        return bitmap.Copy();
    }

    [Fact]
    public void Blocks_are_tinted_red_and_restrictions_amber()
    {
        var view = View();
        using var bitmap = Render(Scene(Disrupted()), view);

        var block = PixelAt(bitmap, view.ToScreen(T("07:15"), 0.55));
        var restriction = PixelAt(bitmap, view.ToScreen(T("06:52"), 0.9));
        var plain = PixelAt(bitmap, view.ToScreen(T("07:25"), 0.5));
        (block.Red - block.Blue).Should().BeGreaterThan(5);
        (restriction.Red - restriction.Blue).Should().BeGreaterThan(40);
        (plain.Red - plain.Blue).Should().BeLessThan(5);
    }

    [Fact]
    public void A_draft_is_drawn_over_the_graph()
    {
        var view = View();
        var scene = Scene(Plan());
        var draft = GraphDisruption.For(Block with { Start = SimTime.Parse("07:20"), End = SimTime.Parse("07:28") }, 0, 1, isDraft: true);

        using var without = Render(scene, view);
        using var with = Render(scene, view, draft);

        var inside = view.ToScreen(T("07:24"), 0.9);
        PixelAt(with, inside).Should().NotBe(PixelAt(without, inside));
    }

    [Fact]
    public Task Small_corridor_with_disruptions_snapshot() =>
        SkiaGraphRendererTests.VerifyPng(Scene(Disrupted()), View());
}
