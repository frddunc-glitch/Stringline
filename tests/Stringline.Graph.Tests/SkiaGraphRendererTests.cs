using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Network.Profiles;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

public class SkiaGraphRendererTests
{
    /// <summary>The two-train timetable, the express's actuals, and an unmatched train.</summary>
    private static GraphScene Scene(SimTime? now = null, Core.Timetables.WorkingTimetable? timetable = null) =>
        GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
        {
            Timetable = timetable ?? TwoTrains(),
            LiveTrains =
            [
                Live("901", "1A01", MatchStatus.Matched, "1A01",
                    ("A", TrainLocationAction.Depart, "07:01:30"), ("B", TrainLocationAction.Pass, "07:06:00"), ("C", TrainLocationAction.Pass, "07:10:30")),
                Live("902", "9Z99", MatchStatus.Unmatched, null, ("D", TrainLocationAction.Depart, "06:50"), ("C", TrainLocationAction.Arrive, "06:58")),
            ],
            Now = now,
        });

    private static GraphViewport View(int width = 800, int height = 400) =>
        GraphViewport.Create(SimTime.FromClock(6, 45).Seconds, SimTime.FromClock(7, 30).Seconds, width, height);

    private static SKBitmap Render(GraphScene scene, GraphViewport viewport)
    {
        using var renderer = new SkiaGraphRenderer();
        return SKBitmap.Decode(renderer.RenderPng(scene, viewport));
    }

    [Fact]
    public void Embedded_fonts_load()
    {
        GraphFonts.Regular.FamilyName.Should().Be("Noto Sans");
        GraphFonts.Bold.FamilyName.Should().Be("Noto Sans");
        GraphFonts.Bold.FontStyle.Weight.Should().BeGreaterThan(GraphFonts.Regular.FontStyle.Weight);
    }

    [Fact]
    public void Png_has_the_viewport_size()
    {
        using var bitmap = Render(Scene(), View(640, 360));

        (bitmap.Width, bitmap.Height).Should().Be((640, 360));
    }

    [Fact]
    public void Now_line_is_red_at_now_and_the_past_is_shaded()
    {
        var now = SimTime.FromClock(7, 15);
        var view = View();
        using var bitmap = Render(Scene(now), view);

        var x = (int)Math.Floor(view.TimeToX(now.Seconds));
        var y = (int)view.AxisToY(0.9); // between C and D, clear of other lines at 07:15
        var pixel = bitmap.GetPixel(x, y);
        pixel.Red.Should().BeGreaterThan(180);
        pixel.Green.Should().BeLessThan(120);

        var past = bitmap.GetPixel((int)view.TimeToX(SimTime.FromClock(7, 13).Seconds), y);
        var future = bitmap.GetPixel((int)view.TimeToX(SimTime.FromClock(7, 25).Seconds), y);
        past.Red.Should().BeLessThan(future.Red, "the past is shaded");
    }

    [Fact]
    public void Actual_line_is_drawn_in_its_category_colour()
    {
        var view = View();
        using var bitmap = Render(Scene(), view);

        // The express actual runs A 07:01:30 → B 07:06:00; halfway along it is at (07:03:45, ⅙).
        var p = view.ToScreen(SimTime.Parse("07:03:45").Seconds, 1.0 / 6);
        var pixel = bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));
        pixel.Red.Should().BeGreaterThan(150);
        pixel.Green.Should().BeLessThan(100);
        pixel.Blue.Should().BeLessThan(100);
    }

    [Fact]
    public void Paths_out_of_view_are_not_drawn()
    {
        var view = GraphViewport.Create(SimTime.FromClock(12, 0).Seconds, SimTime.FromClock(13, 0).Seconds, 800, 400);
        using var bitmap = Render(Scene(), view);
        using var empty = Render(GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)), view);

        PngComparer.Compare(Png(bitmap), Png(empty)).IsEqual.Should().BeTrue();
    }

    [Fact]
    public void Renderer_can_be_reused_and_disposed_twice()
    {
        var renderer = new SkiaGraphRenderer();
        var first = renderer.RenderPng(Scene(), View());
        var second = renderer.RenderPng(Scene(), View());
        renderer.Dispose();

        second.Should().Equal(first);
        renderer.Theme.Should().Be(GraphTheme.Light);
    }

    [Fact]
    public void Theme_styles_follow_the_line_style_table()
    {
        var theme = GraphTheme.Light;

        theme.StyleOf(GraphLayer.Wtt).Should().Be(new LayerStyle(1f, 0.3));
        theme.StyleOf(GraphLayer.Plan).Should().Be(new LayerStyle(1.5f, 1));
        theme.StyleOf(GraphLayer.Actual).Should().Be(new LayerStyle(2.5f, 1));
        theme.StyleOf(GraphLayer.Forecast).Should().Be(new LayerStyle(1.5f, 1, Dashed: true));
        var act = () => theme.StyleOf((GraphLayer)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Forecast_and_plan_layers_render()
    {
        var vertices = new List<GraphVertex> { new(SimTime.FromClock(7, 0).Seconds, 0), new(SimTime.FromClock(7, 20).Seconds, 1) };
        var scene = new GraphScene("main", "t", [], [
            new GraphPath("plan", GraphLayer.Plan, null, new Headcode("1P01"), Express, vertices),
            new GraphPath("fc", GraphLayer.Forecast, null, new Headcode("1P01"), Express, vertices),
        ], null);

        using var bitmap = Render(scene, View());

        var p = View().ToScreen(SimTime.FromClock(7, 10).Seconds, 0.5);
        bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y)).Red.Should().BeGreaterThan(150);
    }

    [Theory]
    [InlineData(0, 50, 200, 50, 10, 50, 100, 50)] // horizontal, clipped both ends
    [InlineData(50, 0, 50, 200, 50, 20, 50, 80)] // vertical, clipped both ends
    [InlineData(20, 30, 40, 60, 20, 30, 40, 60)] // inside
    public void Segments_clip_to_the_plot(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        var rect = new SKRect(10, 20, 100, 80);

        var clipped = SkiaGraphRenderer.ClipToRect(new ScreenPoint(ax, ay), new ScreenPoint(bx, by), rect);

        clipped.Should().Be((new ScreenPoint(cx, cy), new ScreenPoint(dx, dy)));
    }

    [Theory]
    [InlineData(0, 0, 5, 5)] // before the rect
    [InlineData(0, 90, 200, 90)] // below it
    [InlineData(150, 50, 120, 50)] // beyond the right edge
    [InlineData(5, 50, 5, 60)] // parallel to the left edge, outside
    public void Segments_outside_clip_to_nothing(double ax, double ay, double bx, double by)
    {
        SkiaGraphRenderer.ClipToRect(new ScreenPoint(ax, ay), new ScreenPoint(bx, by), new SKRect(10, 20, 100, 80)).Should().BeNull();
    }

    [Fact]
    public void Labels_are_darker_than_their_lines()
    {
        SkiaGraphRenderer.Darken(new Colour(200, 100, 40, 77)).Should().Be(new Colour(150, 75, 30));
    }

    [Fact]
    public void Snapshot_comparer_catches_a_moved_now_line_and_a_new_label()
    {
        using var renderer = new SkiaGraphRenderer();
        var baseline = renderer.RenderPng(Scene(SimTime.FromClock(7, 12)), View());
        var nowMoved = renderer.RenderPng(Scene(SimTime.FromClock(7, 13)), View());
        var withExtraTrain = Tt([.. TwoTrains().Trains, Train("5Q77", null, Call("A", null, "07:20"), Pass("B", "07:24"))]);
        var extraTrain = renderer.RenderPng(Scene(SimTime.FromClock(7, 12), withExtraTrain), View());

        PngComparer.Compare(new MemoryStream(baseline), new MemoryStream(baseline)).IsEqual.Should().BeTrue();
        PngComparer.Compare(new MemoryStream(nowMoved), new MemoryStream(baseline)).IsEqual.Should().BeFalse();
        PngComparer.Compare(new MemoryStream(extraTrain), new MemoryStream(baseline)).IsEqual.Should().BeFalse();
    }

    [Fact]
    public void Snapshot_comparer_rejects_other_sizes_and_non_images()
    {
        using var renderer = new SkiaGraphRenderer();
        var small = renderer.RenderPng(Scene(), View(400, 300));
        var large = renderer.RenderPng(Scene(), View(401, 300));

        PngComparer.Compare(new MemoryStream(small), new MemoryStream(large)).Message.Should().Be("Size 400×300, expected 401×300.");
        PngComparer.Compare(new MemoryStream([1, 2, 3]), new MemoryStream(small)).Message.Should().Be("Not a readable PNG.");
    }

    [Fact]
    public Task Small_corridor_snapshot() =>
        VerifyPng(Scene(SimTime.FromClock(7, 12)), View());

    internal static Task VerifyPng(GraphScene scene, GraphViewport viewport)
    {
        using var renderer = new SkiaGraphRenderer();
        return Verify(new MemoryStream(renderer.RenderPng(scene, viewport)), "png").UseDirectory("Snapshots");
    }

    private static MemoryStream Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(data.ToArray());
    }
}
