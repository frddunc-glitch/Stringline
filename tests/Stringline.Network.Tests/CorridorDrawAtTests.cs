using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network.Profiles;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

/// <summary>Locations off a corridor drawn at one of its own (#22, #23), on a small Moss Vale.</summary>
public sealed class CorridorDrawAtTests
{
    /// <summary>
    /// Yass Junction – Harden – Cootamundra – Junee. 3MB7 enters at EJUNEE and first books Cootamundra, and is booked
    /// into Harden's platform 10 (HARDN10) to be overtaken. HARDING is a different place 20 minutes away.
    /// </summary>
    private static WorkingTimetable MossVale() => Of(
        Train("3MB7", Pass("CTMNDR", "01:21"), Pass("HARDEN", "02:04"), Call("HARDN10", "02:07", "02:30", platform: "10"), Pass("YASSJ", "03:39"))
            with { Start = TrainStart.EntryPoint, EntryPoint = new Tiploc("EJUNEE"), EntryTime = SimTime.Parse("00:27") },
        Train("1311", Pass("YASSJ", "10:00"), Pass("HARDEN", "10:40"), Pass("CTMNDR", "11:20"), Pass("JUNEE", "11:56")),
        Train("6H01", Call("HARDEN", null, "12:00"), Call("HARDING", "12:20", null)),
        Train("6Z01", Call("CTMNDR", null, "13:00"), Call("STKBGL", "13:02", null)));

    private static Corridor MainLine(WorkingTimetable tt) => CorridorBuilder.Create("main", "Main", Order("YASSJ", "HARDEN", "CTMNDR", "JUNEE"), tt);

    [Fact]
    public void Finds_a_platform_within_a_station_and_an_entry_beyond_the_end()
    {
        var corridor = MainLine(MossVale());

        corridor.DrawAt.Should().Equal(
            new DrawnAt(new Tiploc("EJUNEE"), new Tiploc("JUNEE")),
            new DrawnAt(new Tiploc("HARDN10"), new Tiploc("HARDEN")));
        corridor.DrawIndexOf(new Tiploc("HARDN10")).Should().Be(1);
        corridor.DrawIndexOf(new Tiploc("EJUNEE")).Should().Be(3);
        corridor.DrawIndexOf(new Tiploc("HARDING")).Should().Be(-1, "it shares a prefix but is 20 minutes away");
        corridor.DrawIndexOf(new Tiploc("STKBGL")).Should().Be(-1, "its code is nothing like Cootamundra's");
        corridor.IndexOf(new Tiploc("HARDN10")).Should().Be(-1, "only drawing sees the mapping");
    }

    [Theory]
    [InlineData("HARDN10", "HARDEN", 4)]
    [InlineData("YASSJYD", "YASSJ", 5)]
    [InlineData("JUNEE", "JUNEE", 5)]
    [InlineData("HARD", "HARDEN", 4)]
    [InlineData("HARDING", "HARDEN", 4)] // as close by code as HARDN10: the run time tells them apart
    [InlineData("HARDEN", "HARTLEY", null)]
    [InlineData("ABC1", "ABC2", null)]
    public void A_shared_prefix_must_be_long_and_nearly_all_the_shorter_code(string a, string b, int? expected) =>
        CorridorDrawAt.PrefixOf(a, b).Should().Be(expected);

    [Fact]
    public void A_refresh_finds_them_again_and_keeps_those_set_by_hand()
    {
        var tt = MossVale();
        var profile = StringlineProfile.Create(tt, [MainLine(tt)])
            .WithDrawAt("main", new Tiploc("HARDN10"), null)
            .WithDrawAt("main", new Tiploc("STKBGL"), new Tiploc("CTMNDR"));

        var refreshed = profile.Refresh(tt).Corridors[0];

        refreshed.DrawAt.Should().Equal(
            new DrawnAt(new Tiploc("EJUNEE"), new Tiploc("JUNEE")),
            new DrawnAt(new Tiploc("HARDN10"), null, HandSet: true),
            new DrawnAt(new Tiploc("STKBGL"), new Tiploc("CTMNDR"), HandSet: true));
        refreshed.DrawIndexOf(new Tiploc("HARDN10")).Should().Be(-1, "turned off by hand");

        var reset = profile.ResetDrawAt("main", new Tiploc("HARDN10"), tt).ResetDrawAt("main", new Tiploc("STKBGL"), tt).Corridors[0];
        reset.DrawAt.Should().Equal(MainLine(tt).DrawAt, "back to what the WTT suggests");
    }

    [Fact]
    public void Drawing_at_a_location_off_the_corridor_or_mapping_one_on_it_is_refused()
    {
        var tt = MossVale();
        var profile = StringlineProfile.Create(tt, [MainLine(tt)]);

        profile.Invoking(p => p.WithDrawAt("main", new Tiploc("HARDEN"), new Tiploc("YASSJ"))).Should().Throw<ProfileEditException>().WithMessage("*on the corridor*");
        profile.Invoking(p => p.WithDrawAt("main", new Tiploc("HARDN10"), new Tiploc("STKBGL"))).Should().Throw<ProfileEditException>().WithMessage("*not on the corridor*");
    }

    [Fact]
    public void Saved_only_when_there_are_some()
    {
        var tt = MossVale();
        var with = StringlineProfile.Create(tt, [MainLine(tt)]);
        var without = StringlineProfile.Create(tt, [MainLine(tt) with { DrawAt = [] }]);

        var json = ProfileJson.Serialize(with);
        json.Should().Contain("\"drawAt\"").And.Contain("HARDN10");
        ProfileJson.Deserialize(json).Corridors[0].DrawAt.Should().Equal(with.Corridors[0].DrawAt);
        var off = with.WithDrawAt("main", new Tiploc("HARDN10"), null);
        ProfileJson.Deserialize(ProfileJson.Serialize(off)).Corridors[0].DrawAt.Should().Equal(off.Corridors[0].DrawAt, "an entry turned off is saved with no place");
        ProfileJson.Serialize(without).Should().NotContain("drawAt");
        ProfileJson.Deserialize(ProfileJson.Serialize(without)).Corridors[0].DrawAt.Should().BeEmpty();
    }
}
