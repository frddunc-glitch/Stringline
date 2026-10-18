namespace Stringline.Graph.Tests;

public sealed class GraphViewportAxisTests
{
    private static GraphViewport View() => GraphViewport.Create(0, 3600, 800, 500);

    [Fact]
    public void Zooming_along_the_corridor_keeps_the_axis_position_under_the_cursor()
    {
        var view = View();
        var y = 300;
        var anchor = view.YToAxis(y);

        var zoomed = view.ZoomAxis(2, y);

        zoomed.AxisSpan.Should().BeApproximately(view.AxisSpan / 2, 1e-9);
        zoomed.YToAxis(y).Should().BeApproximately(anchor, 1e-9);
        zoomed.TimeStart.Should().Be(view.TimeStart);
        zoomed.ShowsFullAxis.Should().BeFalse();
    }

    [Fact]
    public void Zoom_is_limited_both_ways()
    {
        var view = View();

        view.ZoomAxis(1000, 250).AxisSpan.Should().BeApproximately(GraphViewport.MinAxisSpan, 1e-9);
        view.ZoomAxis(0.1, 250).Should().Be(view, "the whole corridor is the widest view");
        var act = () => view.ZoomAxis(0, 250);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Panning_along_the_corridor_stops_at_the_ends()
    {
        var zoomed = View().ZoomAxis(4, 44);

        zoomed.PanAxisPixels(-200).AxisStart.Should().BeGreaterThan(zoomed.AxisStart, "dragging up shows further down the corridor");
        zoomed.PanAxisPixels(10_000).AxisStart.Should().BeApproximately(GraphViewport.FullAxisStart, 1e-9);
        zoomed.PanAxisPixels(-10_000).AxisEnd.Should().BeApproximately(GraphViewport.FullAxisEnd, 1e-9);
        View().PanAxisPixels(50).Should().Be(View(), "the whole corridor cannot pan");
    }

    [Fact]
    public void With_full_axis_shows_the_whole_corridor_again()
    {
        var zoomed = View().ZoomAxis(4, 200);

        var full = zoomed.WithFullAxis();

        full.ShowsFullAxis.Should().BeTrue();
        full.AxisStart.Should().Be(GraphViewport.FullAxisStart);
        full.AxisEnd.Should().Be(GraphViewport.FullAxisEnd);
    }
}
