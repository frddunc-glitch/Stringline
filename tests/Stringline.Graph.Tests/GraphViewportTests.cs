using Stringline.Core;

namespace Stringline.Graph.Tests;

public class GraphViewportTests
{
    private static readonly GraphMargins Margins = new(100, 40, 20, 10);

    /// <summary>07:00–10:00 on a 1000×600 surface: the plot is 880×550 px, so 60 s is 880/180 px.</summary>
    private static GraphViewport View() =>
        new(SimTime.FromClock(7, 0).Seconds, SimTime.FromClock(10, 0).Seconds, 0, 1, 1000, 600, Margins);

    [Fact]
    public void Plot_area_is_the_surface_less_margins()
    {
        var v = View();

        (v.PlotLeft, v.PlotTop, v.PlotRight, v.PlotBottom).Should().Be((100, 40, 980, 590));
        (v.PlotWidth, v.PlotHeight).Should().Be((880, 550));
        v.PixelsPerMinute.Should().BeApproximately(880.0 / 180, 1e-9);
    }

    [Fact]
    public void Time_runs_left_to_right_and_the_up_end_is_at_the_top()
    {
        var v = View();

        v.ToScreen(SimTime.FromClock(7, 0).Seconds, 0).Should().Be(new ScreenPoint(100, 40));
        v.ToScreen(SimTime.FromClock(10, 0).Seconds, 1).Should().Be(new ScreenPoint(980, 590));
        v.ToScreen(SimTime.FromClock(8, 30).Seconds, 0.5).Should().Be(new ScreenPoint(540, 315));
    }

    [Fact]
    public void Screen_and_world_round_trip()
    {
        var v = View();
        var (time, axis) = v.ToWorld(v.ToScreen(30_000.5, 0.37));

        time.Should().BeApproximately(30_000.5, 1e-6);
        axis.Should().BeApproximately(0.37, 1e-9);
    }

    [Fact]
    public void Around_puts_now_at_the_given_share_and_pads_the_axis()
    {
        var v = GraphViewport.Around(SimTime.FromClock(9, 0), 3 * 3600, 1600, 900);

        v.TimeStart.Should().Be(SimTime.FromClock(8, 0).Seconds);
        v.TimeEnd.Should().Be(SimTime.FromClock(11, 0).Seconds);
        v.AxisStart.Should().Be(-GraphViewport.DefaultAxisPadding);
        v.AxisEnd.Should().Be(1 + GraphViewport.DefaultAxisPadding);
        v.Margins.Should().Be(GraphMargins.Default);

        GraphViewport.Around(SimTime.FromClock(9, 0), 3600, 800, 600, nowFraction: 0).TimeStart.Should().Be(SimTime.FromClock(9, 0).Seconds);
    }

    [Fact]
    public void Bad_sizes_and_ranges_are_refused()
    {
        var act = () => GraphViewport.Create(100, 100, 800, 600);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => GraphViewport.Create(0, 100, 0, 600);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => GraphViewport.Around(SimTime.Zero, 3600, 800, 600, nowFraction: 1.5);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => GraphViewport.Around(SimTime.Zero, 0, 800, 600);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => View().ZoomTime(0, 500);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => View().Resize(0, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Zoom_keeps_the_time_under_the_anchor()
    {
        var v = View();
        var anchorX = 320.0;
        var before = v.XToTime(anchorX);

        var zoomed = v.ZoomTime(2, anchorX);

        zoomed.TimeSpan.Should().BeApproximately(v.TimeSpan / 2, 1e-9);
        zoomed.XToTime(anchorX).Should().BeApproximately(before, 1e-6);
    }

    [Fact]
    public void Zoom_is_held_between_five_minutes_and_two_days()
    {
        View().ZoomTime(1000, 500).TimeSpan.Should().Be(GraphViewport.MinTimeSpan);
        View().ZoomTime(0.0001, 500).TimeSpan.Should().Be(GraphViewport.MaxTimeSpan);
    }

    [Fact]
    public void Dragging_right_shows_earlier_times()
    {
        var v = View();

        var panned = v.PanPixels(88); // a tenth of the plot

        panned.TimeStart.Should().BeApproximately(v.TimeStart - 1080, 1e-9);
        panned.TimeSpan.Should().BeApproximately(v.TimeSpan, 1e-9);
        v.PanTime(60).TimeEnd.Should().Be(v.TimeEnd + 60);
    }

    [Fact]
    public void Follow_recentres_on_now_keeping_the_span()
    {
        var followed = View().Follow(SimTime.FromClock(12, 0).Seconds, 0.5);

        followed.TimeStart.Should().Be(SimTime.FromClock(10, 30).Seconds);
        followed.TimeEnd.Should().Be(SimTime.FromClock(13, 30).Seconds);
    }

    [Fact]
    public void Resize_keeps_the_world_range()
    {
        var resized = View().Resize(2000, 1200);

        (resized.Width, resized.Height).Should().Be((2000, 1200));
        (resized.TimeStart, resized.TimeEnd).Should().Be((View().TimeStart, View().TimeEnd));
        resized.PlotWidth.Should().Be(1880);
    }

    [Fact]
    public void Overlaps_and_in_plot()
    {
        var v = View();

        v.Overlaps(0, v.TimeStart, 0, 1).Should().BeTrue("touching the left edge counts");
        v.Overlaps(0, v.TimeStart - 1, 0, 1).Should().BeFalse();
        v.Overlaps(v.TimeEnd + 1, v.TimeEnd + 100, 0, 1).Should().BeFalse();
        v.Overlaps(v.TimeStart, v.TimeEnd, 1.1, 1.2).Should().BeFalse();
        v.IsInPlot(new ScreenPoint(100, 40)).Should().BeTrue();
        v.IsInPlot(new ScreenPoint(99, 40)).Should().BeFalse();
        v.IsInPlot(new ScreenPoint(500, 591)).Should().BeFalse();
    }

    [Fact]
    public void A_surface_smaller_than_its_margins_still_has_a_one_pixel_plot()
    {
        var v = View().Resize(50, 20);

        v.PlotWidth.Should().Be(1);
        v.PlotHeight.Should().Be(1);
    }
}
