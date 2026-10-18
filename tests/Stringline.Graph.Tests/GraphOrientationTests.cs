using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>The vertical orientation (GR-1, Fred: time down, the Up end on the left).</summary>
public sealed class GraphOrientationTests
{
    private static readonly double T0 = SimTime.FromClock(6, 45).Seconds;
    private static readonly double T1 = SimTime.FromClock(7, 30).Seconds;

    private static GraphViewport Vertical(int width = 400, int height = 800) =>
        GraphViewport.Create(T0, T1, width, height, orientation: GraphOrientation.Vertical);

    private static GraphScene Scene(SimTime? now = null) => GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
    {
        Timetable = TwoTrains(),
        LiveTrains =
        [
            Live("901", "1A01", MatchStatus.Matched, "1A01",
                ("A", TrainLocationAction.Depart, "07:01:30"), ("B", TrainLocationAction.Pass, "07:06:00"), ("C", TrainLocationAction.Pass, "07:10:30")),
        ],
        Now = now,
    });

    [Fact]
    public void Time_runs_down_and_the_up_end_is_on_the_left()
    {
        var v = Vertical();

        v.ToScreen(T0, v.AxisStart).Should().Be(new ScreenPoint(v.PlotLeft, v.PlotTop));
        v.ToScreen(T1, v.AxisEnd).Should().Be(new ScreenPoint(v.PlotRight, v.PlotBottom));
        v.ToScreen(T0, 1).X.Should().BeGreaterThan(v.ToScreen(T0, 0).X, "the corridor's Down end is on the right");
        v.ToScreen(T1, 0).Y.Should().BeGreaterThan(v.ToScreen(T0, 0).Y, "later is lower");
        (v.TimeLength, v.AxisLength).Should().Be((v.PlotHeight, v.PlotWidth));
        v.Margins.Should().Be(GraphMargins.DefaultVertical);
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void Screen_and_world_round_trip(GraphOrientation orientation)
    {
        var v = GraphViewport.Create(T0, T1, 700, 500, orientation: orientation);
        var p = v.ToScreen(SimTime.FromClock(7, 3).Seconds, 0.4);

        var (time, axis) = v.ToWorld(p);

        time.Should().BeApproximately(SimTime.FromClock(7, 3).Seconds, 1e-6);
        axis.Should().BeApproximately(0.4, 1e-9);
        v.TimePixelOf(p).Should().Be(orientation == GraphOrientation.Vertical ? p.Y : p.X);
        v.AxisPixelOf(p).Should().Be(orientation == GraphOrientation.Vertical ? p.X : p.Y);
    }

    [Fact]
    public void Turning_keeps_the_ranges_and_swaps_the_margins()
    {
        var h = GraphViewport.Create(T0, T1, 900, 600).ZoomAxis(2, 300);

        var v = h.WithOrientation(GraphOrientation.Vertical);

        (v.TimeStart, v.TimeEnd, v.AxisStart, v.AxisEnd, v.Width, v.Height).Should().Be((h.TimeStart, h.TimeEnd, h.AxisStart, h.AxisEnd, h.Width, h.Height));
        v.Margins.Should().Be(GraphMargins.DefaultVertical);
        v.WithOrientation(GraphOrientation.Horizontal).Should().Be(h);
    }

    [Fact]
    public void Dragging_down_shows_earlier_times_and_zoom_keeps_the_time_under_the_cursor()
    {
        var v = Vertical();

        v.PanTimePixels(v.TimeLength / 10).TimeStart.Should().BeApproximately(T0 - ((T1 - T0) / 10), 1e-6);

        var y = v.PlotTop + 200;
        var before = v.PixelToTime(y);
        var zoomed = v.ZoomTime(2, y);
        zoomed.PixelToTime(y).Should().BeApproximately(before, 1e-6);
        zoomed.TimeSpan.Should().BeApproximately((T1 - T0) / 2, 1e-6);
    }

    [Fact]
    public void Navigator_turns_the_view_and_its_gestures_follow()
    {
        var nav = new GraphNavigator(400, 800);
        nav.Tick(SimTime.FromClock(7, 0).Seconds);
        nav.SetOrientation(GraphOrientation.Vertical);
        nav.Viewport.Orientation.Should().Be(GraphOrientation.Vertical);
        nav.IsFollowing.Should().BeTrue();

        // A sideways drag pans the corridor only (once zoomed in), so it keeps following.
        nav.Wheel(120, 200, 400, GraphModifiers.Control);
        var axisStart = nav.Viewport.AxisStart;
        nav.BeginDrag(new ScreenPoint(200, 400));
        nav.DragTo(new ScreenPoint(150, 400));
        nav.Viewport.AxisStart.Should().BeGreaterThan(axisStart);
        nav.IsFollowing.Should().BeTrue();

        // A vertical drag pans time and stops following.
        var start = nav.Viewport.TimeStart;
        nav.DragTo(new ScreenPoint(150, 500));
        nav.EndDrag();
        nav.Viewport.TimeStart.Should().BeLessThan(start);
        nav.IsFollowing.Should().BeFalse();
    }

    [Fact]
    public void Hit_testing_finds_the_same_point_turned()
    {
        var scene = Scene();
        var h = GraphViewport.Create(T0, T1, 800, 400);
        var v = Vertical();
        var time = SimTime.Parse("07:03:45").Seconds;

        var hitH = GraphHitTester.HitTest(scene, h, h.ToScreen(time, 1.0 / 6));
        var hitV = GraphHitTester.HitTest(scene, v, v.ToScreen(time, 1.0 / 6));

        hitV.Should().NotBeNull();
        hitV!.Path.Key.Should().Be(hitH!.Path.Key);
        hitV.Segment.Should().Be(hitH.Segment);
    }

    [Fact]
    public void Now_line_runs_across_and_the_past_is_shaded_above_it()
    {
        var now = SimTime.FromClock(7, 15);
        var v = Vertical();
        using var renderer = new SkiaGraphRenderer();
        using var bitmap = SKBitmap.Decode(renderer.RenderPng(Scene(now), v));

        var y = (int)Math.Floor(v.TimeToPixel(now.Seconds));
        var x = (int)v.AxisToPixel(0.9);
        var pixel = bitmap.GetPixel(x, y);
        pixel.Red.Should().BeGreaterThan(180);
        pixel.Green.Should().BeLessThan(120);

        var past = bitmap.GetPixel(x, (int)v.TimeToPixel(SimTime.FromClock(7, 13).Seconds));
        var future = bitmap.GetPixel(x, (int)v.TimeToPixel(SimTime.FromClock(7, 25).Seconds));
        past.Red.Should().BeLessThan(future.Red, "the past is shaded");
    }

    [Fact]
    public void Actual_line_is_drawn_where_it_should_be()
    {
        var v = Vertical();
        using var renderer = new SkiaGraphRenderer();
        using var bitmap = SKBitmap.Decode(renderer.RenderPng(Scene(), v));

        var p = v.ToScreen(SimTime.Parse("07:03:45").Seconds, 1.0 / 6);
        var pixel = bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));
        pixel.Red.Should().BeGreaterThan(150);
        pixel.Green.Should().BeLessThan(100);
    }

    [Fact]
    public Task Small_corridor_vertical_snapshot() =>
        SkiaGraphRendererTests.VerifyPng(Scene(SimTime.FromClock(7, 12)), Vertical());
}
