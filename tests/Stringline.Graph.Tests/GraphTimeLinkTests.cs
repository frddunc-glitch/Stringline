using Stringline.Core;

namespace Stringline.Graph.Tests;

/// <summary>Boards locked in time (S21): every linked navigator shows the same time, each its own corridor range.</summary>
public sealed class GraphTimeLinkTests
{
    private static readonly double Nine = SimTime.FromClock(9, 0).Seconds;

    private static (GraphTimeLink Link, GraphNavigator A, GraphNavigator B) Linked()
    {
        var a = new GraphNavigator(950, 500);
        a.Tick(Nine);
        var b = new GraphNavigator(600, 300);
        var link = new GraphTimeLink();
        link.Add(a);
        link.Add(b);
        return (link, a, b);
    }

    private static (double, double) TimeOf(GraphNavigator nav) => (nav.Viewport.TimeStart, nav.Viewport.TimeEnd);

    [Fact]
    public void A_navigator_added_takes_on_the_time_and_clock_of_those_linked()
    {
        var (_, a, b) = Linked();

        TimeOf(b).Should().Be(TimeOf(a));
        b.Now.Should().Be(Nine);
        b.IsFollowing.Should().BeTrue();
        b.Viewport.Width.Should().Be(600, "each keeps its own size");
    }

    [Fact]
    public void Pans_zooms_presets_and_follow_carry_across_both_ways()
    {
        var (_, a, b) = Linked();

        a.BeginDrag(new ScreenPoint(500, 200));
        a.DragTo(new ScreenPoint(600, 200));
        a.EndDrag();
        b.IsFollowing.Should().BeFalse("panning in time on one board stops them all following");
        TimeOf(b).Should().Be(TimeOf(a));

        b.ShowSpan(3600);
        a.Viewport.TimeSpan.Should().Be(3600);
        TimeOf(a).Should().Be(TimeOf(b));

        b.Wheel(120, 300, 150);
        TimeOf(a).Should().Be(TimeOf(b));

        a.Follow();
        b.IsFollowing.Should().BeTrue();
        TimeOf(b).Should().Be(TimeOf(a));
        b.Tick(Nine + 600);
        TimeOf(a).Should().Be(TimeOf(b), "the clock moving one moves the other");
    }

    [Fact]
    public void The_corridor_range_and_orientation_stay_each_boards_own()
    {
        var (_, a, b) = Linked();

        a.Wheel(240, 500, 250, GraphModifiers.Control);
        a.Viewport.ShowsFullAxis.Should().BeFalse();
        b.Viewport.ShowsFullAxis.Should().BeTrue();

        b.SetOrientation(GraphOrientation.Vertical);
        a.Viewport.IsVertical.Should().BeFalse();
        TimeOf(b).Should().Be(TimeOf(a));
    }

    [Fact]
    public void A_navigator_removed_moves_on_its_own()
    {
        var (link, a, b) = Linked();

        link.Remove(b);
        a.ShowSpan(3600);

        b.Viewport.TimeSpan.Should().Be(3 * 3600);
        link.Navigators.Should().Equal(a);
        link.Dispose();
        link.Navigators.Should().BeEmpty();
    }
}
