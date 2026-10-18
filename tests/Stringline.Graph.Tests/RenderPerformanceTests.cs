using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>The S9 performance work: path caching, the label grid and the hit-test grid behave, and change nothing drawn.</summary>
public sealed class RenderPerformanceTests
{
    private static LiveTrain Express(string lastPass = "07:10:30") => Live("901", "1A01", MatchStatus.Matched, "1A01",
        ("A", TrainLocationAction.Depart, "07:01:30"), ("B", TrainLocationAction.Pass, "07:06:00"), ("C", TrainLocationAction.Pass, lastPass));

    private static GraphScene Scene(LiveTrain? express = null, SimTime? now = null) => GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
    {
        Timetable = TwoTrains(),
        LiveTrains = [express ?? Express()],
        Now = now,
    });

    private static GraphViewport View(GraphOrientation orientation = GraphOrientation.Horizontal) =>
        GraphViewport.Create(SimTime.FromClock(6, 45).Seconds, SimTime.FromClock(7, 30).Seconds, 800, 400, orientation: orientation);

    private static void Draw(SkiaGraphRenderer renderer, GraphScene scene, GraphViewport viewport)
    {
        using var surface = SKSurface.Create(new SKImageInfo(viewport.Width, viewport.Height));
        renderer.Render(surface.Canvas, scene, viewport);
    }

    [Fact]
    public void Panning_and_a_moving_now_line_reuse_the_projected_paths()
    {
        using var renderer = new SkiaGraphRenderer();
        var scene = Scene();
        var view = View();
        Draw(renderer, scene, view);
        var projected = renderer.PathsProjected;
        projected.Should().Be(3, "two WTT paths and one actual");

        Draw(renderer, scene with { Now = SimTime.FromClock(7, 5) }, view.PanTime(37.5));
        Draw(renderer, scene, view.PanAxisPixels(0).Follow(SimTime.FromClock(7, 10).Seconds));
        Draw(renderer, scene, view.ZoomAxis(2, 200).PanAxisPixels(-30));
        var afterAxisZoom = renderer.PathsProjected;

        Draw(renderer, scene, view.ZoomAxis(2, 200).PanAxisPixels(25));

        afterAxisZoom.Should().Be(2 * projected, "only the corridor zoom re-projected");
        renderer.PathsProjected.Should().Be(afterAxisZoom, "panning along the corridor is a translation too");
    }

    [Fact]
    public void Zooming_turning_and_resizing_re_project()
    {
        using var renderer = new SkiaGraphRenderer();
        var scene = Scene();
        Draw(renderer, scene, View());
        var first = renderer.PathsProjected;

        Draw(renderer, scene, View().ZoomTime(1.2, 300));
        Draw(renderer, scene, View(GraphOrientation.Vertical));
        Draw(renderer, scene, View().Resize(900, 400));

        renderer.PathsProjected.Should().Be(4 * first);
    }

    [Fact]
    public void A_new_scene_re_projects_only_the_paths_that_changed()
    {
        using var renderer = new SkiaGraphRenderer();
        Draw(renderer, Scene(), View());
        var first = renderer.PathsProjected;

        Draw(renderer, Scene(), View());
        renderer.PathsProjected.Should().Be(first, "an identical rebuild keeps every path");

        Draw(renderer, Scene(Express("07:11:00")), View());
        renderer.PathsProjected.Should().Be(first + 1, "only the moved actual is projected again");
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void A_panned_frame_from_the_cache_matches_a_fresh_render(GraphOrientation orientation)
    {
        var scene = Scene(now: SimTime.FromClock(7, 12));
        var start = View(orientation);
        var panned = start.PanTime(123.4).PanTimePixels(17.3);

        using var warm = new SkiaGraphRenderer();
        warm.RenderPng(scene, start);
        var fromCache = warm.RenderPng(scene, panned);
        using var cold = new SkiaGraphRenderer();
        var fresh = cold.RenderPng(scene, panned);

        PngComparer.Compare(new MemoryStream(fromCache), new MemoryStream(fresh)).IsEqual.Should().BeTrue();
    }

    /// <summary>Renders a frame on a native-format surface at a device scale, as the app's host does, and returns it as PNG.</summary>
    private static byte[] Frame(SkiaGraphRenderer renderer, GraphScene scene, GraphViewport viewport, float scale = 1, GraphSelection? selection = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)Math.Ceiling(viewport.Width * scale), (int)Math.Ceiling(viewport.Height * scale)));
        surface.Canvas.Scale(scale);
        renderer.Render(surface.Canvas, scene, viewport, selection);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal, 1f)]
    [InlineData(GraphOrientation.Vertical, 1f)]
    [InlineData(GraphOrientation.Horizontal, 1.5f)]
    [InlineData(GraphOrientation.Vertical, 1.25f)]
    public void Frames_from_tiles_match_direct_drawing(GraphOrientation orientation, float scale)
    {
        var scene = Scene(now: SimTime.FromClock(7, 12));
        var view = View(orientation);
        var selection = new GraphSelection(new TrainId("2B02"), null);
        using var tiled = new SkiaGraphRenderer();
        using var direct = new SkiaGraphRenderer { CacheTiles = false };

        Frame(tiled, scene, view, scale); // the first frame at a zoom draws directly; the next builds tiles
        var built = Frame(tiled, scene, view, scale);
        tiled.TilesRendered.Should().BePositive();
        var panned = view.PanTimePixels(40); // whole pixels: tiles are placed on whole device pixels
        var fromTiles = Frame(tiled, scene, panned, scale);

        PngComparer.Compare(new MemoryStream(built), new MemoryStream(Frame(direct, scene, view, scale))).IsEqual.Should().BeTrue();
        PngComparer.Compare(new MemoryStream(fromTiles), new MemoryStream(Frame(direct, scene, panned, scale))).IsEqual.Should().BeTrue();

        Frame(tiled, scene, panned, scale, selection);
        PngComparer.Compare(new MemoryStream(Frame(tiled, scene, panned, scale, selection)), new MemoryStream(Frame(direct, scene, panned, scale, selection)))
            .IsEqual.Should().BeTrue("the selected train is drawn over dimmed tiles");
    }

    [Fact]
    public void Tiles_are_reused_while_panning_and_redrawn_only_where_trains_moved()
    {
        using var renderer = new SkiaGraphRenderer();
        var scene = Scene();
        var view = View();
        Draw(renderer, scene, view);
        renderer.TilesRendered.Should().Be(0, "a frame at a new zoom draws directly");
        Draw(renderer, scene, view);
        var all = renderer.TilesRendered;
        all.Should().BeGreaterThanOrEqualTo(6, "an 800×400 view needs a few 256 px tiles");

        Draw(renderer, scene, view.PanTimePixels(2));
        Draw(renderer, scene with { Now = SimTime.FromClock(7, 20) }, view.PanTimePixels(2));
        var afterPan = renderer.TilesRendered;
        (afterPan - all).Should().BeLessThanOrEqualTo(3, "at most a new column of tiles comes into view");

        Draw(renderer, Scene(Express("07:11:00")), view.PanTimePixels(2));
        var afterUpdate = renderer.TilesRendered;
        (afterUpdate - afterPan).Should().BeInRange(1, all / 2, "only tiles under the moved train's last stretch");

        Draw(renderer, Scene(Express("07:11:00")), view.PanTimePixels(2), new GraphSelection(new TrainId("1A01"), "901"));
        (renderer.TilesRendered - afterUpdate).Should().BeGreaterThanOrEqualTo(all - 2, "a new selection dims everything else");
    }

    [Fact]
    public void A_direct_render_between_tiled_frames_does_not_leave_tiles_stale()
    {
        var view = View();
        var moved = Scene(Express("07:20:00"));
        using var renderer = new SkiaGraphRenderer();
        using var direct = new SkiaGraphRenderer { CacheTiles = false };
        Frame(renderer, Scene(), view);
        Frame(renderer, Scene(), view);

        renderer.RenderPng(moved, view); // draws directly, but sees the express move
        var tiled = Frame(renderer, moved, view);

        PngComparer.Compare(new MemoryStream(tiled), new MemoryStream(Frame(direct, moved, view))).IsEqual.Should().BeTrue();
    }

    private static void Draw(SkiaGraphRenderer renderer, GraphScene scene, GraphViewport viewport, GraphSelection selection)
    {
        using var surface = SKSurface.Create(new SKImageInfo(viewport.Width, viewport.Height));
        renderer.Render(surface.Canvas, scene, viewport, selection);
    }

    [Fact]
    public void Png_export_scales()
    {
        using var renderer = new SkiaGraphRenderer();
        using var bitmap = SKBitmap.Decode(renderer.RenderPng(Scene(), View(), scale: 2));

        (bitmap.Width, bitmap.Height).Should().Be((1600, 800));
        var p = View().ToScreen(SimTime.Parse("07:03:45").Seconds, 1.0 / 6);
        bitmap.GetPixel((int)Math.Round(p.X * 2), (int)Math.Round(p.Y * 2)).Red.Should().BeGreaterThan(150, "the express is where it was, twice as far in");
    }

    [Fact]
    public void Label_grid_finds_overlaps_only_where_labels_are()
    {
        var grid = new LabelGrid();
        grid.Reset(new SKRect(100, 50, 900, 650));
        grid.Add(new SKRect(200, 100, 260, 112));
        grid.Add(new SKRect(500, 300, 640, 420)); // spans several cells

        grid.Count.Should().Be(2);
        grid.Intersects(new SKRect(250, 105, 300, 117)).Should().BeTrue();
        grid.Intersects(new SKRect(261, 105, 300, 117)).Should().BeFalse();
        grid.Intersects(new SKRect(600, 410, 610, 430)).Should().BeTrue();
        grid.Intersects(new SKRect(0, 0, 10, 10)).Should().BeFalse("outside the area clamps to the edge cells");

        grid.Reset(new SKRect(0, 0, 2000, 2000));
        grid.Count.Should().Be(0);
        grid.Intersects(new SKRect(250, 105, 300, 117)).Should().BeFalse();
    }

    [Fact]
    public void Labels_are_still_placed_once_per_train_without_overlaps()
    {
        using var renderer = new SkiaGraphRenderer();
        Draw(renderer, Scene(), View());

        renderer.LabelsPlaced.Should().Be(2, "1A01 on its actual and 2B02 on its WTT");
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void Hit_index_answers_as_the_linear_scan(GraphOrientation orientation)
    {
        var scene = Scene();
        var view = View(orientation);
        var index = GraphHitIndex.Build(scene, view);

        for (var y = 0.0; y < view.Height; y += 2.5)
        {
            for (var x = 0.0; x < view.Width; x += 2.5)
            {
                var p = new ScreenPoint(x, y);
                index.HitTest(p).Should().Be(GraphHitTester.HitTest(scene, view, p), "at ({0}, {1})", x, y);
            }
        }
    }

    [Fact]
    public void Hit_index_knows_when_it_is_stale()
    {
        var scene = Scene();
        var index = GraphHitIndex.Build(scene, View());

        index.IsFor(scene with { Now = SimTime.FromClock(7, 0) }, View()).Should().BeTrue("a new now line does not move the paths");
        index.IsFor(Scene(), View()).Should().BeFalse();
        index.IsFor(scene, View().PanTime(1)).Should().BeFalse();
        index.Entries.Should().BePositive();
    }
}
