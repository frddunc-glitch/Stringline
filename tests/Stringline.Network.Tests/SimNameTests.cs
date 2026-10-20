using Stringline.Core.Timetables;
using Stringline.Network.Profiles;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

/// <summary>S31 (Q17): a sim name that is only a timetable's title counts as no name, unless it was typed.</summary>
public sealed class SimNameTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stringline-sim-name-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly WorkingTimetable Old = Titled("Old WTT");

    private static readonly WorkingTimetable New = Titled("New WTT");

    private static WorkingTimetable Titled(string title)
    {
        var line = Of(Train("1A01", Call("A", null, "10:00"), Call("B", "10:05", null)));
        return new WorkingTimetable(line.Header with { Name = title }, line.Categories, line.Trains, line.Rules);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Profiles_remember_the_titles_they_were_made_and_refreshed_from()
    {
        var profile = StringlineProfile.Create(Old);
        profile.TimetableTitles.Should().Equal("Old WTT");

        var refreshed = profile.Refresh(New).Refresh(Titled(" old  wtt ")).Refresh(Titled(" "));

        refreshed.TimetableTitles.Should().Equal(["Old WTT", "New WTT"], "a title is kept once, ignoring case and spaces, and a blank one not at all");
    }

    [Fact]
    public void A_stale_title_shows_the_area_id()
    {
        // A profile made from the old timetable before S22 has its title as the name; S31 has since seen that title.
        var profile = (StringlineProfile.Create(Old) with { Name = "Old WTT" }).Refresh(New);

        profile.SimName(New.Header).Should().BeNull();
        profile.Title(null, New.Header).ToString().Should().Be("Test – New WTT", "not \"Old WTT – New WTT\"");
        profile.Title("waterloo", New.Header).ToString().Should().Be("Waterloo – New WTT", "the gateway's area ID wins");
    }

    [Fact]
    public void The_loaded_timetables_title_counts_too()
    {
        var profile = new StringlineProfile { AreaId = "test", Name = "new  wtt" };

        profile.SimName(New.Header).Should().BeNull();
        profile.SimName(Old.Header).Should().Be("new  wtt");
    }

    [Fact]
    public void A_typed_name_is_always_kept()
    {
        var profile = StringlineProfile.Create(Old).Refresh(New).WithName(" New WTT ");

        profile.NameByHand.Should().BeTrue();
        profile.SimName(New.Header).Should().Be("New WTT");
        profile.Title(null, New.Header).ToString().Should().Be("New WTT – New WTT", "the user chose it");
        profile.WithName("Llangollen Railway").Title(null, New.Header).ToString().Should().Be("Llangollen Railway – New WTT");

        var cleared = profile.WithName("  ");
        cleared.Name.Should().BeNull();
        cleared.NameByHand.Should().BeFalse();
        cleared.Title(null, New.Header).ToString().Should().Be("Test – New WTT");
    }

    [Fact]
    public void No_name_shows_the_area_id()
    {
        var profile = StringlineProfile.Create(New);

        profile.SimName(New.Header).Should().BeNull();
        profile.Title(null, New.Header).ToString().Should().Be("Test – New WTT");
        profile.HasUnsettledName.Should().BeFalse();
    }

    [Fact]
    public void Json_round_trips_titles_and_a_typed_name()
    {
        var profile = StringlineProfile.Create(Old).Refresh(New).WithName("Penrith");

        var json = ProfileJson.Serialize(profile);
        var back = ProfileJson.Deserialize(json);

        json.Should().Contain("\"nameByHand\": true").And.Contain("\"timetableTitles\": [\n    \"Old WTT\",\n    \"New WTT\"\n  ]");
        back.Should().BeEquivalentTo(profile);
        ProfileJson.Serialize(back).Should().Be(json);
        ProfileJson.Serialize(profile.WithName(null)).Should().NotContain("nameByHand", "only written when set");
    }

    [Fact]
    public void A_profile_from_before_S31_has_no_titles_and_an_unsettled_name()
    {
        var profile = ProfileJson.Deserialize("""{ "version": 2, "areaId": "test", "name": "Old WTT" }""");

        profile.TimetableTitles.Should().BeEmpty();
        profile.NameByHand.Should().BeFalse();
        profile.HasUnsettledName.Should().BeTrue();
        profile.Refresh(New).HasUnsettledName.Should().BeTrue("the new timetable's title is not the name");
        ProfileJson.Deserialize("""{ "version": 2, "areaId": "test", "name": "New WTT" }""").Refresh(New).HasUnsettledName.Should().BeFalse();
    }

    [Fact]
    public void Settling_a_name_finds_an_older_title_or_marks_it_typed()
    {
        var old = new StringlineProfile { AreaId = "test", Name = "Old WTT" };

        var stale = old.SettleName(["Something else", "old wtt"]);
        stale.TimetableTitles.Should().Equal("old wtt");
        stale.NameByHand.Should().BeFalse();
        stale.SimName(New.Header).Should().BeNull();

        var typed = old.SettleName(["Something else"]);
        typed.TimetableTitles.Should().BeEmpty();
        typed.NameByHand.Should().BeTrue();
        typed.SimName(New.Header).Should().Be("Old WTT");

        stale.SettleName(["Old WTT"]).Should().BeSameAs(stale, "there is nothing left to settle");
        typed.SettleName(["Old WTT"]).Should().BeSameAs(typed);
    }

    [Fact]
    public void Settling_reads_the_titles_of_the_sims_other_timetables_in_the_folder()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "older"));
        var loaded = Wtt("new.wtt", "test", "New WTT");
        Wtt(Path.Combine("older", "old.wtt"), "TEST", "Old WTT");
        Wtt("other.wtt", "penrith", "Penrith Title");

        var stale = SimNames.Settle(new StringlineProfile { AreaId = "test", Name = "Old WTT" }.Refresh(New), loaded);
        stale.TimetableTitles.Should().Equal("New WTT", "Old WTT");
        stale.Title(null, New.Header).Sim.Should().Be("Test");

        var otherSim = SimNames.Settle(new StringlineProfile { AreaId = "test", Name = "Penrith Title" }, loaded);
        otherSim.NameByHand.Should().BeTrue("only this sim's timetables count");

        var settled = new StringlineProfile { AreaId = "test", Name = "Kept", NameByHand = true };
        SimNames.Settle(settled, Path.Combine(_directory, "missing", "x.wtt")).Should().BeSameAs(settled);
    }

    private string Wtt(string relative, string areaId, string name)
    {
        var path = Path.Combine(_directory, relative);
        File.WriteAllText(path, $"""<?xml version="1.0" encoding="utf-8"?><SimSigTimetable ID="{areaId}" Version="5.0"><Name>{name}</Name></SimSigTimetable>""");
        return path;
    }
}
