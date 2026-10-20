using Stringline.Core;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

public sealed class CorridorBuilderTests
{
    private static readonly Core.Timetables.WorkingTimetable Line = Of(
        Train("1D01", Call("A", null, "10:00", platform: "1"), Pass("B", "10:02"), Call("C", "10:08", null, platform: "2")),
        Train("1U01", Call("C", null, "11:00", platform: "3"), Pass("B", "11:05"), Call("A", "11:07", null)));

    [Fact]
    public void Create_derives_values_from_the_timetable()
    {
        var c = CorridorBuilder.Create("main", "A – C", Order("A", "B", "C"), Line);

        c.Id.Should().Be("main");
        c.Axis.Should().Be(AxisMode.Derived);
        c.Locations.Should().Equal(
            new CorridorLocation(new Tiploc("A"), 0, Platforms: 1),
            new CorridorLocation(new Tiploc("B"), 120.0 / 420, IsTimingPointOnly: true),
            new CorridorLocation(new Tiploc("C"), 1, Platforms: 2));
        c.Sections[0].Should().BeEquivalentTo(new Section(new Tiploc("A"), new Tiploc("B"), Section.DoubleLine, 180, 120, 120, 120, 2));
        c.Sections[1].Should().BeEquivalentTo(new Section(new Tiploc("B"), new Tiploc("C"), Section.DoubleLine, 180, 300, 360, 300, 2));
    }

    [Fact]
    public void A_section_no_train_is_timed_over_gets_the_default_run_time()
    {
        var c = CorridorBuilder.Create("x", "x", Order("A", "Z"), Line);

        c.Sections[0].MinRunSeconds.Should().Be(Section.DefaultMinRunSeconds);
        c.Sections[0].Samples.Should().Be(0);
    }

    [Fact]
    public void Refresh_keeps_user_settings_and_recomputes_the_rest()
    {
        var created = CorridorBuilder.Create("main", "A – C", Order("A", "B", "C"), Line);
        var edited = created with
        {
            Name = "My line",
            Axis = AxisMode.Distance,
            Locations = [created.Locations[0] with { DistanceKm = 0, Platforms = 4 }, created.Locations[1] with { DistanceKm = 8 }, created.Locations[2] with { DistanceKm = 10 }],
            Sections = [created.Sections[0] with { Tracks = Section.SingleLine, DefaultHeadwaySeconds = 300, HeadwaySource = HeadwaySource.Hand, MinRunSeconds = 1 }, created.Sections[1]],
        };

        var refreshed = CorridorBuilder.Refresh(edited, Line);

        refreshed.Name.Should().Be("My line");
        refreshed.Axis.Should().Be(AxisMode.Distance);
        refreshed.Locations[0].Platforms.Should().Be(4);
        refreshed.Locations[2].Platforms.Should().Be(2);
        refreshed.Sections[0].Tracks.Should().BeSameAs(Section.SingleLine);
        refreshed.Sections[0].DefaultHeadwaySeconds.Should().Be(300, "it was set by hand");
        refreshed.Sections[0].HeadwaySource.Should().Be(HeadwaySource.Hand);
        refreshed.Sections[0].MinRunSeconds.Should().Be(120, "derived values are recomputed");
        refreshed.Locations.Select(l => l.AxisPosition).Should().Equal(0, 0.8, 1);
    }

    [Fact]
    public void Refresh_rebuilds_sections_for_a_new_order()
    {
        var created = CorridorBuilder.Create("main", "A – C", Order("A", "B", "C"), Line);
        var single = created with { Sections = [created.Sections[0] with { Tracks = Section.SingleLine }, created.Sections[1]] };

        // Reversed, and with B removed.
        var reversed = CorridorBuilder.Refresh(single with { Locations = [.. single.Locations.Reverse()] }, Line);
        var shortened = CorridorBuilder.Refresh(single with { Locations = [single.Locations[0], single.Locations[2]] }, Line);

        reversed.Sections.Select(s => (s.From.Code, s.To.Code)).Should().Equal(("C", "B"), ("B", "A"));
        reversed.Sections[1].Tracks.Should().BeSameAs(Section.SingleLine, "A–B keeps its settings in either order");
        reversed.Sections[1].MinRunDownSeconds.Should().Be(120, "Down is now B to A");
        shortened.Sections.Should().ContainSingle().Which.Should().BeEquivalentTo(new Section(new Tiploc("A"), new Tiploc("C"), Section.DoubleLine, 180, 420, 480, 420, 2));
    }
}
