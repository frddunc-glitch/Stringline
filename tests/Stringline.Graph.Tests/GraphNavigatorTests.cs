using Stringline.Core;

namespace Stringline.Graph.Tests;

public sealed class GraphNavigatorTests
{
    private static readonly double Nine = SimTime.FromClock(9, 0).Seconds;

    private static GraphNavigator Navigator()
    {
        var nav = new GraphNavigator(950, 500);
        nav.Tick(Nine);
        return nav;
    }

    private static double NowShare(GraphNavigator nav) => (nav.Now!.Value - nav.Viewport.TimeStart) / nav.Viewport.TimeSpan;

    [Fact]
    public void Starts_following_now_a_third_of_the_way_across_a_three_hour_view()
    {
        var nav = Navigator();

        nav.IsFollowing.Should().BeTrue();
        nav.Viewport.TimeSpan.Should().Be(3 * 3600);
        NowShare(nav).Should().BeApproximately(1.0 / 3, 1e-9);
        nav.Viewport.ShowsFullAxis.Should().BeTrue();
    }

    [Fact]
    public void Follows_the_clock_and_says_when_it_moved()
    {
        var nav = Navigator();
        var changes = 0;
        nav.Changed += (_, _) => changes++;

        nav.Tick(Nine + 60).Should().BeTrue();
        nav.Tick(Nine + 60).Should().BeFalse("the clock did not move");

        changes.Should().Be(1);
        NowShare(nav).Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void Dragging_in_time_pans_and_stops_following()
    {
        var nav = Navigator();
        var start = nav.Viewport.TimeStart;

        nav.BeginDrag(new ScreenPoint(500, 200));
        nav.IsDragging.Should().BeTrue();
        nav.DragTo(new ScreenPoint(600, 200));
        nav.EndDrag();

        nav.IsDragging.Should().BeFalse();
        nav.IsFollowing.Should().BeFalse();
        nav.Viewport.TimeStart.Should().BeLessThan(start, "dragging right shows earlier times");
        nav.Tick(Nine + 600).Should().BeFalse("not following any more");
        nav.Viewport.TimeStart.Should().BeLessThan(start);
    }

    [Fact]
    public void Dragging_only_along_the_corridor_keeps_following()
    {
        var nav = Navigator();
        nav.Wheel(240, 500, 250, GraphModifiers.Control);

        nav.BeginDrag(new ScreenPoint(500, 200));
        nav.DragTo(new ScreenPoint(500, 260));

        nav.IsFollowing.Should().BeTrue();
        nav.Viewport.ShowsFullAxis.Should().BeFalse();
    }

    [Fact]
    public void A_drag_without_a_start_does_nothing()
    {
        var nav = Navigator();
        var before = nav.Viewport;

        nav.DragTo(new ScreenPoint(10, 10));

        nav.Viewport.Should().Be(before);
    }

    [Fact]
    public void The_wheel_zooms_time_about_now_while_following()
    {
        var nav = Navigator();

        nav.Wheel(120, 100, 200);

        nav.Viewport.TimeSpan.Should().BeApproximately(3 * 3600 / GraphNavigator.WheelZoomStep, 1e-6);
        NowShare(nav).Should().BeApproximately(1.0 / 3, 1e-9, "the now line stays put");
        nav.IsFollowing.Should().BeTrue();
    }

    [Fact]
    public void The_wheel_zooms_time_about_the_cursor_when_not_following()
    {
        var nav = Navigator();
        nav.SetFollowing(false);
        var cursor = nav.Viewport.XToTime(700);

        nav.Wheel(-240, 700, 200);

        nav.Viewport.TimeSpan.Should().BeApproximately(3 * 3600 * GraphNavigator.WheelZoomStep * GraphNavigator.WheelZoomStep, 1e-6);
        nav.Viewport.XToTime(700).Should().BeApproximately(cursor, 1e-6);
    }

    [Fact]
    public void Ctrl_wheel_zooms_along_the_corridor_and_shift_wheel_pans_time()
    {
        var nav = Navigator();
        var span = nav.Viewport.TimeSpan;

        nav.Wheel(120, 500, 300, GraphModifiers.Control);
        nav.Viewport.AxisSpan.Should().BeLessThan(GraphViewport.FullAxisEnd - GraphViewport.FullAxisStart);
        nav.Viewport.TimeSpan.Should().Be(span);
        nav.IsFollowing.Should().BeTrue();

        var start = nav.Viewport.TimeStart;
        nav.Wheel(120, 500, 300, GraphModifiers.Shift);
        nav.IsFollowing.Should().BeFalse();
        nav.Viewport.TimeStart.Should().BeLessThan(start);

        nav.ShowFullCorridor();
        nav.Viewport.ShowsFullAxis.Should().BeTrue();
    }

    [Fact]
    public void A_zero_wheel_turn_does_nothing()
    {
        var nav = Navigator();
        var before = nav.Viewport;

        nav.Wheel(0, 500, 300);

        nav.Viewport.Should().Be(before);
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(6 * 3600)]
    public void Presets_keep_now_in_place_while_following(int seconds)
    {
        var nav = Navigator();

        nav.ShowSpan(seconds);

        nav.Viewport.TimeSpan.Should().BeApproximately(seconds, 1e-6);
        NowShare(nav).Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void Presets_keep_the_centre_when_not_following()
    {
        var nav = Navigator();
        nav.SetFollowing(false);
        var centre = (nav.Viewport.TimeStart + nav.Viewport.TimeEnd) / 2;

        nav.ShowSpan(3600);

        ((nav.Viewport.TimeStart + nav.Viewport.TimeEnd) / 2).Should().BeApproximately(centre, 1e-6);
    }

    [Fact]
    public void Follow_snaps_back_to_now()
    {
        var nav = Navigator();
        nav.CentreOn(Nine - 7200);
        nav.IsFollowing.Should().BeFalse();
        ((nav.Viewport.TimeStart + nav.Viewport.TimeEnd) / 2).Should().BeApproximately(Nine - 7200, 1e-6);

        nav.Follow();

        nav.IsFollowing.Should().BeTrue();
        NowShare(nav).Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void Toggle_follow_flips_and_raises_changed()
    {
        var nav = Navigator();
        var changes = 0;
        nav.Changed += (_, _) => changes++;

        nav.ToggleFollow();
        nav.IsFollowing.Should().BeFalse();
        nav.ToggleFollow();
        nav.IsFollowing.Should().BeTrue();
        nav.SetFollowing(true);

        changes.Should().Be(2);
    }

    [Fact]
    public void The_first_tick_centres_on_now_even_when_not_following()
    {
        var nav = new GraphNavigator(950, 500);
        nav.SetFollowing(false);

        nav.Tick(Nine).Should().BeTrue();

        NowShare(nav).Should().BeApproximately(1.0 / 3, 1e-9);
    }

    [Fact]
    public void Resize_keeps_the_world_range()
    {
        var nav = Navigator();
        var before = nav.Viewport;

        nav.Resize(1200, 700);
        nav.Resize(1200, 700);
        nav.Resize(0, -5);

        nav.Viewport.Width.Should().Be(1);
        nav.Viewport.Height.Should().Be(1);
        nav.Viewport.TimeStart.Should().Be(before.TimeStart);
    }

    [Fact]
    public void Rejects_a_now_position_outside_the_view()
    {
        var act = () => new GraphNavigator(nowFraction: 1.5);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
