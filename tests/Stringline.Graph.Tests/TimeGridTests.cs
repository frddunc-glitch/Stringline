using Stringline.Core;

namespace Stringline.Graph.Tests;

public class TimeGridTests
{
    [Theory]
    [InlineData(40, 1, 10)] // 1 h on a wide screen
    [InlineData(8, 5, 30)] // 3 h on 1440 px: the usual 5 min / 30 min TCD grid
    [InlineData(4.5, 5, 30)]
    [InlineData(3, 10, 60)] // 6 h
    [InlineData(1, 30, 180)] // a whole day
    [InlineData(0.01, 360, 1440)] // as far out as it goes
    public void Spacing_follows_zoom(double pixelsPerMinute, int minor, int major)
    {
        TimeGrid.Spacing(pixelsPerMinute).Should().Be((minor, major));
    }

    [Fact]
    public void Ticks_cover_the_view_with_hours_heaviest()
    {
        var v = GraphViewport.Create(SimTime.FromClock(6, 50).Seconds, SimTime.FromClock(9, 50).Seconds, 1590, 600);

        var ticks = TimeGrid.Ticks(v);

        ticks[0].Time.Should().Be(SimTime.FromClock(6, 50).Seconds);
        ticks[^1].Time.Should().Be(SimTime.FromClock(9, 50).Seconds);
        ticks.Should().HaveCount(37).And.OnlyContain(t => (t.Time % 300) == 0);
        ticks.Where(t => t.Weight == TickWeight.Hour).Select(t => t.Label).Should().Equal("07:00", "08:00", "09:00");
        ticks.Where(t => t.Weight == TickWeight.Major).Select(t => t.Label).Should().Equal("07:30", "08:30", "09:30");
        ticks.Where(t => t.Weight == TickWeight.Minor).Should().OnlyContain(t => t.Label == null);
    }

    [Fact]
    public void Labels_past_midnight_carry_the_day()
    {
        var v = GraphViewport.Create(SimTime.FromClock(23, 0).Seconds, SimTime.FromClock(1, 0, day: 1).Seconds, 1000, 600);

        TimeGrid.Ticks(v).Where(t => t.Weight == TickWeight.Hour).Select(t => t.Label).Should().Equal("23:00", "00:00+1", "01:00+1");
    }

    [Fact]
    public void Negative_times_still_align()
    {
        var v = GraphViewport.Create(-1800, 1800, 1000, 600);

        var ticks = TimeGrid.Ticks(v);

        ticks.Should().Contain(t => t.Time == 0 && t.Weight == TickWeight.Hour);
        ticks.Should().Contain(t => t.Time == -1800 && t.Weight != TickWeight.Minor);
    }
}
