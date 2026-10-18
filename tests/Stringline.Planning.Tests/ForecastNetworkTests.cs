using Stringline.Core;
using Stringline.Network;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>The sections the interaction pass works on, built from the profile's corridors.</summary>
public sealed class ForecastNetworkTests
{
    private static readonly Corridor Main = Line("main", 180, single: false, "X", "J", "Z");
    private static readonly Corridor Branch = Line("y-z", 120, single: true, "Y", "J", "Z");

    private static Tiploc T(string code) => new(code);

    [Fact]
    public void A_section_on_two_corridors_is_one_section_with_the_first_corridors_settings()
    {
        var network = ForecastNetwork.Build([Main, Branch]);

        network.Sections.Should().HaveCount(3);
        var shared = network.FindSection(T("Z"), T("J"))!;
        shared.Should().BeSameAs(network.FindSection(T("J"), T("Z")));
        shared.HeadwaySeconds.Should().Be(180);
        shared.SingleLine.Should().BeFalse();
        network.FindSection(T("Y"), T("J"))!.SingleLine.Should().BeTrue();
        network.FindSection(T("X"), T("Z")).Should().BeNull("they are not adjacent");
    }

    [Fact]
    public void Tracks_each_way_come_from_the_profile()
    {
        var network = ForecastNetwork.Build([Main, Branch]);

        network.FindSection(T("X"), T("J"))!.Should().Match<ForecastSection>(s => s.DownTracks == 1 && s.UpTracks == 1);
        network.FindSection(T("Y"), T("J"))!.Should().Match<ForecastSection>(s => s.DownTracks == 1 && s.UpTracks == 1);

        var quad = Main with
        {
            Sections = [Main.Sections[0] with { Tracks = [new("UF", TrackDirection.Up), new("US", TrackDirection.Up), new("DF", TrackDirection.Down), new("DS", TrackDirection.Down)] }, Main.Sections[1]],
        };
        ForecastNetwork.Build([quad]).FindSection(T("X"), T("J"))!.Should().Match<ForecastSection>(s => s.DownTracks == 2 && s.UpTracks == 2);
    }

    [Fact]
    public void A_run_maps_onto_the_sections_between_its_ends_in_running_order()
    {
        var network = ForecastNetwork.Build([Main, Branch]);

        var down = network.Route(T("X"), T("Z"))!;
        down.Select(s => (s.Section.From.Code, s.Section.To.Code, s.Down)).Should().Equal(("X", "J", true), ("J", "Z", true));
        down.Select(s => (s.EnterShare, s.ExitShare)).Should().Equal((0, 0.5), (0.5, 1));

        var up = network.Route(T("Z"), T("Y"))!;
        up.Select(s => (s.Section.From.Code, s.Section.To.Code, s.Down)).Should().Equal(("J", "Z", false), ("Y", "J", false));

        network.Route(T("X"), T("X")).Should().BeNull();
        network.Route(T("X"), T("Q")).Should().BeNull();
        ForecastNetwork.Empty.Route(T("X"), T("Z")).Should().BeNull();
    }

    [Fact]
    public void Shares_of_a_run_follow_each_sections_minimum_in_that_direction()
    {
        var corridor = Line("main", 180, single: false, "A", "B", "C") is var c
            ? c with { Sections = [c.Sections[0] with { MinRunSeconds = 100, MinRunDownSeconds = 100 }, c.Sections[1] with { MinRunSeconds = 300, MinRunDownSeconds = 300, MinRunUpSeconds = 100 }] }
            : null!;
        var network = ForecastNetwork.Build([corridor]);

        network.Route(T("A"), T("C"))!.Select(s => s.ExitShare).Should().Equal(0.25, 1);
        network.Route(T("C"), T("A"))!.Select(s => s.ExitShare).Should().Equal(0.5, 1); // Up, B–C is 100 s too
    }

    [Fact]
    public void Tracks_needed_counts_booked_runs_too_close_or_overtaking()
    {
        ForecastNetwork.TracksNeeded([(0, 300), (180, 480), (360, 660)]).Should().Be(1);
        ForecastNetwork.TracksNeeded([(0, 300), (30, 330)]).Should().Be(2, "half a minute apart");
        ForecastNetwork.TracksNeeded([(0, 300), (60, 240)]).Should().Be(2, "the second overtakes the first");
        ForecastNetwork.TracksNeeded([(0, 300), (30, 330), (400, 700)]).Should().Be(2);
        ForecastNetwork.TracksNeeded([]).Should().Be(0);
    }

    [Fact]
    public void Priorities_are_by_category()
    {
        var network = ForecastNetwork.Build([Main], Of(), new Dictionary<string, int> { ["exp"] = 90 });

        network.PriorityOf("exp").Should().Be(90);
        network.PriorityOf("other").Should().Be(0);
        network.PriorityOf(null).Should().Be(0);
    }
}
