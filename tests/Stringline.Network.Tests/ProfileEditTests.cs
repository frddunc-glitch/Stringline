using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network.Profiles;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

/// <summary>S14a: the profile editor's edits and their validation.</summary>
public sealed class ProfileEditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stringline-profile-edit-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>A–B–C–D, with B–C slower (2 min) than the other sections (1 min), and a stopper at B and C.</summary>
    private static readonly WorkingTimetable Line = Of(
        [
            Train("1D01", Pass("A", "10:00", true), Pass("B", "10:01", true), Pass("C", "10:03", true), Pass("D", "10:04", true)),
            Train("1D02", Pass("A", "11:00", true), Pass("B", "11:01", true), Pass("C", "11:03", true), Pass("D", "11:04", true)),
            Train("2U01", "PAX", Call("D", null, "12:00"), Call("C", "12:01", "12:02", "1"), Call("B", "12:04", "12:05", "2"), Call("A", "12:06", null)),
            Train("2U02", "PAX", Call("D", null, "13:00"), Call("C", "13:01", "13:02", "2"), Call("B", "13:04", "13:05", "1"), Call("A", "13:06", null)),
        ],
        [new TrainCategory("PAX", "Stopping", false, null, null, null, null, null, DwellTimes.None)]);

    private static readonly Tiploc A = new("A"), B = new("B"), C = new("C"), D = new("D"), X = new("X");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static StringlineProfile Profile() => StringlineProfile.Create(Line);

    private static IEnumerable<Tiploc> Order(StringlineProfile profile, string id = "main") => profile.FindCorridor(id)!.Locations.Select(l => l.Tiploc);

    [Fact]
    public void The_generated_profile_is_one_corridor_up_end_first()
    {
        var profile = Profile();

        profile.Corridors.Should().ContainSingle().Which.Id.Should().Be("main");
        Order(profile).Should().Equal(A, B, C, D);
        profile.Corridors[0].Sections.Select(s => s.MinRunSeconds).Should().Equal(60, 120, 60);
    }

    [Fact]
    public void Names_the_sim_and_its_locations()
    {
        var profile = Profile().WithName("  Test Sim ").WithLocationName(A, " Aston ").WithLocationName(B, "Bury");

        profile.Name.Should().Be("Test Sim");
        profile.DisplayName(A).Should().Be("Aston");
        profile.WithLocationName(B, " ").LocationNames.Should().NotContainKey(B, "a blank name removes it");
        profile.WithName(" ").Name.Should().BeNull();
        profile.WithLocationNames([new(A, "Acton"), new(C, "Crewe"), new(D, " ")]).LocationNames.Should().BeEquivalentTo(
            new Dictionary<Tiploc, string> { [A] = "Acton", [B] = "Bury", [C] = "Crewe" });
    }

    [Fact]
    public void Gives_an_unnamed_sim_an_id()
    {
        var profile = Profile() with { AreaId = "unknown" };

        profile.WithAreaId(" my-sim ").AreaId.Should().Be("my-sim");
        var act = () => profile.WithAreaId(" ");
        act.Should().Throw<ProfileEditException>().WithMessage("*needs a name*");
    }

    [Fact]
    public void Adds_renames_reorders_and_removes_corridors()
    {
        var profile = Profile();

        var added = profile.AddCorridor("Branch line", B, X, Line);
        added.Corridors.Select(c => c.Id).Should().Equal("main", "branch-line");
        var branch = added.FindCorridor("branch-line")!;
        branch.Name.Should().Be("Branch line");
        branch.Locations.Select(l => l.Tiploc).Should().Equal(B, X);
        branch.Sections.Should().ContainSingle().Which.Tracks.Should().Equal(Section.DoubleLine);
        branch.Locations.Select(l => l.AxisPosition).Should().Equal(0, 1);
        added.AddCorridor("Branch line", C, X, Line).Corridors[^1].Id.Should().Be("branch-line-2");

        var moved = added.MoveCorridor("branch-line", 0);
        moved.Corridors.Select(c => c.Id).Should().Equal("branch-line", "main");
        moved.MoveCorridor("branch-line", 99).Corridors.Select(c => c.Id).Should().Equal("main", "branch-line");

        var renamed = moved.RenameCorridor("MAIN", " Main line ");
        renamed.FindCorridor("main")!.Name.Should().Be("Main line");
        renamed.RemoveCorridor("main").Corridors.Select(c => c.Id).Should().Equal("branch-line");

        var copy = profile.Corridors[0] with { Name = "Copy" };
        profile.AddCorridor(copy with { Id = "copy" }, 0).Corridors.Select(c => c.Id).Should().Equal("copy", "main");
    }

    [Theory]
    [InlineData("rename", "*needs a name*")]
    [InlineData("duplicate", "*already a corridor 'main'*")]
    [InlineData("missing", "*no corridor 'nope'*")]
    [InlineData("same ends", "*ends must be different*")]
    [InlineData("no name", "*needs a name*")]
    [InlineData("id", "*ID cannot be changed*")]
    public void Refuses_bad_corridor_edits(string edit, string message)
    {
        var profile = Profile();
        Func<StringlineProfile> act = edit switch
        {
            "rename" => () => profile.RenameCorridor("main", "  "),
            "duplicate" => () => profile.AddCorridor(profile.Corridors[0]),
            "missing" => () => profile.RemoveCorridor("nope"),
            "same ends" => () => profile.AddCorridor("Loop", A, A, Line),
            "no name" => () => profile.AddCorridor(" ", A, B, Line),
            _ => () => profile.EditCorridor("main", c => c with { Id = "other" }),
        };

        act.Should().Throw<ProfileEditException>().WithMessage(message);
    }

    [Fact]
    public void Inserting_a_location_splits_its_section_and_keeps_the_others()
    {
        var profile = Profile().WithTracks("main", 0, Section.SingleLine).WithHeadway("main", 2, 240);

        var edited = profile.InsertLocation("main", 2, X, Line);

        Order(edited).Should().Equal(A, B, X, C, D);
        var sections = edited.Corridors[0].Sections;
        sections.Select(s => (s.From, s.To)).Should().Equal((A, B), (B, X), (X, C), (C, D));
        sections[0].IsSingleLine.Should().BeTrue("A–B is still a pair of neighbours");
        sections[3].DefaultHeadwaySeconds.Should().Be(240);
        sections[1].Tracks.Should().Equal(Section.DoubleLine, "a new pair gets a double line");
        sections[1].MinRunSeconds.Should().Be(Section.DefaultMinRunSeconds, "no train is timed through X");
        edited.Corridors[0].Locations.Select(l => l.AxisPosition).Should().BeInAscendingOrder();
        ProfileValidator.FindProblem(edited).Should().BeNull();
    }

    [Fact]
    public void A_minor_row_put_in_a_single_line_keeps_both_halves_single()
    {
        var profile = Profile().WithTracks("main", 1, Section.SingleLine);

        var edited = profile.InsertLocation("main", 2, X, Line, minor: true);

        Order(edited).Should().Equal(A, B, X, C, D);
        edited.Corridors[0].Locations[2].Minor.Should().BeTrue();
        edited.Corridors[0].Locations.Where(l => l.Tiploc != X).Should().OnlyContain(l => !l.Minor);
        var sections = edited.Corridors[0].Sections;
        sections[1].IsSingleLine.Should().BeTrue("B–X is half of the single line B–C");
        sections[2].IsSingleLine.Should().BeTrue("and so is X–C");
        sections[0].IsSingleLine.Should().BeFalse();
    }

    [Fact]
    public void Any_location_can_be_made_a_minor_row_and_back()
    {
        var minor = Profile().WithMinor("main", 1, true);

        minor.Corridors[0].Locations[1].Minor.Should().BeTrue();
        minor.Corridors[0].Sections.Should().Equal(Profile().Corridors[0].Sections, "only how it draws changes");
        minor.WithMinor("main", 1, false).Corridors[0].Locations[1].Minor.Should().BeFalse();
        FluentActions.Invoking(() => Profile().WithMinor("main", 9, true)).Should().Throw<ProfileEditException>();
    }

    [Fact]
    public void A_minor_row_is_saved_and_an_ordinary_location_writes_nothing_new()
    {
        var plain = ProfileJson.Serialize(Profile());
        plain.Should().NotContain("minor", "profiles without minor rows are written as before");

        var json = ProfileJson.Serialize(Profile().WithMinor("main", 1, true));
        json.Should().Contain("\"minor\": true");
        ProfileJson.Deserialize(json).Corridors[0].Locations.Select(l => l.Minor).Should().Equal(false, true, false, false);
    }

    [Fact]
    public void Removing_a_location_joins_its_sections_and_measures_the_new_one()
    {
        var edited = Profile().RemoveLocation("main", 1, Line);

        Order(edited).Should().Equal(A, C, D);
        var joined = edited.Corridors[0].Sections[0];
        (joined.From, joined.To).Should().Be((A, C));
        joined.MinRunSeconds.Should().Be(180, "A to C is measured from the WTT: 3 min");
        joined.Tracks.Should().Equal(Section.DoubleLine);
    }

    [Fact]
    public void Moving_a_location_rebuilds_the_sections()
    {
        var profile = Profile().WithHeadway("main", 0, 100);

        var edited = profile.MoveLocation("main", 0, 3, Line);

        Order(edited).Should().Equal(B, C, D, A);
        edited.Corridors[0].Sections.Select(s => (s.From, s.To)).Should().Equal((B, C), (C, D), (D, A));
        edited.Corridors[0].Sections.Should().OnlyContain(s => s.DefaultHeadwaySeconds != 100, "A–B are no longer neighbours");
    }

    [Theory]
    [InlineData("duplicate", "*already on the corridor*")]
    [InlineData("too few", "*at least 2 locations*")]
    [InlineData("index", "*no item 9*")]
    [InlineData("platforms", "*cannot be negative*")]
    [InlineData("distance", "*must be a number*")]
    [InlineData("headway", "*cannot be negative*")]
    [InlineData("no tracks", "*at least one track*")]
    [InlineData("unnamed track", "*needs a name*")]
    public void Refuses_bad_location_and_section_edits(string edit, string message)
    {
        var profile = Profile();
        Func<StringlineProfile> act = edit switch
        {
            "duplicate" => () => profile.InsertLocation("main", 0, C, Line),
            "too few" => () => profile.RemoveLocation("main", 0, Line).RemoveLocation("main", 0, Line).RemoveLocation("main", 0, Line),
            "index" => () => profile.RemoveLocation("main", 8, Line),
            "platforms" => () => profile.WithPlatforms("main", 0, -1),
            "distance" => () => profile.WithDistance("main", 0, double.NaN),
            "headway" => () => profile.WithHeadway("main", 0, -5),
            "no tracks" => () => profile.WithTracks("main", 0, []),
            _ => () => profile.WithTracks("main", 0, [new TrackDef(" ", TrackDirection.Up)]),
        };

        act.Should().Throw<ProfileEditException>().WithMessage(message);
    }

    [Fact]
    public void Distances_space_the_axis_when_it_uses_them()
    {
        var profile = Profile().WithDistance("main", 0, 0).WithDistance("main", 3, 30);
        profile.Corridors[0].Locations.Select(l => l.AxisPosition).Should().Equal(0, 0.25, 0.75, 1);

        var byDistance = profile.WithDistance("main", 1, 20).WithAxis("main", AxisMode.Distance);

        byDistance.Corridors[0].Axis.Should().Be(AxisMode.Distance);
        byDistance.Corridors[0].Locations[1].AxisPosition.Should().BeApproximately(20.0 / 30, 1e-9);
        byDistance.WithAxis("main", AxisMode.Derived).Corridors[0].Locations[1].AxisPosition.Should().Be(0.25);
        byDistance.WithDistance("main", 1, null).Corridors[0].Locations[1].DistanceKm.Should().BeNull();
    }

    [Fact]
    public void Sets_platforms_tracks_and_headways()
    {
        var tracks = TrackLayouts.Presets.Single(p => p.Name.StartsWith("Four", StringComparison.Ordinal)).Tracks;

        var profile = Profile().WithPlatforms("main", 1, 4).WithTracks("main", 1, tracks).WithHeadway("main", 1, 150);

        profile.Corridors[0].Locations[1].Platforms.Should().Be(4);
        profile.Corridors[0].Sections[1].Tracks.Should().Equal(tracks);
        profile.Corridors[0].Sections[1].DefaultHeadwaySeconds.Should().Be(150);
    }

    [Fact]
    public void Styles_categories()
    {
        var profile = Profile();
        var style = profile.StyleFor("PAX") with { Colour = new Colour(1, 2, 3), Style = LineStyle.Dotted, Priority = 100, Name = "Stoppers" };

        var edited = profile.WithCategoryStyle(style);

        edited.StyleFor("PAX").Should().Be(style);
        edited.Categories.Should().HaveCount(profile.Categories.Count);
        edited.WithCategoryStyle(new CategoryStyle("NEW", "New", 0, new Colour(0, 0, 0))).Categories.Should().HaveCount(profile.Categories.Count + 1);
        var act = () => profile.WithCategoryStyle(style with { Priority = 101 });
        act.Should().Throw<ProfileEditException>().WithMessage("*0 to 100*");
        act = () => profile.WithCategoryStyle(style with { Name = "" });
        act.Should().Throw<ProfileEditException>().WithMessage("*needs a name*");
    }

    [Fact]
    public void Knows_which_corridors_are_hand_tuned()
    {
        var profile = Profile();
        profile.HandTuned(Line).Should().BeEmpty("the generated corridor is as generated");
        profile.Refresh(Line).HandTuned(Line).Should().BeEmpty();

        var tuned = profile.WithHeadway("main", 0, 120).AddCorridor("Spur", C, X, Line);

        tuned.HandTuned(Line).Select(c => c.Id).Should().Equal("main", "spur");
        profile.RenameCorridor("main", "Main line").HandTuned(Line).Should().ContainSingle();
        profile.WithPlatforms("main", 0, 9).HandTuned(Line).Should().ContainSingle();
    }

    [Fact]
    public void Regenerating_replaces_or_keeps_hand_tuned_corridors()
    {
        var tuned = Profile().WithHeadway("main", 0, 120).AddCorridor("Spur", C, X, Line).WithLocationName(A, "Aston");

        var replaced = tuned.RegenerateCorridors(Line, keepHandTuned: false);
        replaced.Corridors.Should().ContainSingle().Which.Sections[0].DefaultHeadwaySeconds.Should().Be(Section.StandardHeadwaySeconds);
        replaced.DisplayName(A).Should().Be("Aston", "only corridors are regenerated");

        var kept = tuned.RegenerateCorridors(Line, keepHandTuned: true);
        kept.Corridors.Select(c => c.Id).Should().Equal("main", "spur");
        kept.Corridors[0].Sections[0].DefaultHeadwaySeconds.Should().Be(120);
    }

    [Fact]
    public void Edits_survive_save_load_and_refresh()
    {
        var store = new ProfileStore(_directory);
        var edited = Profile()
            .WithName("Edited")
            .WithLocationName(B, "Bury")
            .InsertLocation("main", 2, X, Line)
            .MoveCorridor("main", 0)
            .WithDistance("main", 0, 0)
            .WithDistance("main", 4, 12.5)
            .WithAxis("main", AxisMode.Distance)
            .WithPlatforms("main", 2, 3)
            .WithTracks("main", 1, Section.SingleLine)
            .WithHeadway("main", 3, 200)
            .AddCorridor("Spur", C, X, Line)
            .WithCategoryStyle(new CategoryStyle("PAX", "Stoppers", 75, new Colour(1, 2, 3), LineStyle.Dashed));

        store.Save(edited);
        var loaded = store.Load(edited.AreaId)!.Refresh(Line);

        loaded.Name.Should().Be("Edited");
        loaded.DisplayName(B).Should().Be("Bury");
        loaded.Corridors.Select(c => c.Id).Should().Equal("main", "spur");
        var main = loaded.Corridors[0];
        main.Locations.Select(l => l.Tiploc).Should().Equal(A, B, X, C, D);
        main.Axis.Should().Be(AxisMode.Distance);
        main.Locations[4].DistanceKm.Should().Be(12.5);
        main.Locations[2].Platforms.Should().Be(3);
        main.Sections[1].IsSingleLine.Should().BeTrue();
        main.Sections[3].DefaultHeadwaySeconds.Should().Be(200);
        loaded.StyleFor("PAX").Should().Be(edited.StyleFor("PAX"));
        loaded.Corridors.Should().BeEquivalentTo(edited.Corridors, o => o.WithStrictOrdering());
    }
}

public sealed class TrackLayoutTests
{
    [Fact]
    public void Presets_are_named_and_found()
    {
        TrackLayouts.PresetOf(Section.DoubleLine).Should().BeSameAs(TrackLayouts.DoubleLine);
        TrackLayouts.Describe(Section.SingleLine).Should().Be("Single line");
        TrackLayouts.Describe([new("Relief", TrackDirection.BiDirectional)]).Should().Be(TrackLayouts.Custom);
        TrackLayouts.Presets.Select(p => p.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Formats_and_parses_tracks()
    {
        var tracks = new TrackDef[] { new("Up Fast", TrackDirection.Up), new("Down", TrackDirection.Down), new("Relief", TrackDirection.BiDirectional) };

        TrackLayouts.Format(tracks).Should().Be("Up Fast:Up, Down:Down, Relief:Bi");
        TrackLayouts.TryParse("Up Fast:Up, Down:Down, Relief:Bi", out var parsed, out _).Should().BeTrue();
        parsed.Should().Equal((IEnumerable<TrackDef>)tracks);
        TrackLayouts.TryParse("up slow; DOWN SLOW ; Goods", out parsed, out _).Should().BeTrue();
        parsed.Should().Equal(new TrackDef("up slow", TrackDirection.Up), new TrackDef("DOWN SLOW", TrackDirection.Down), new TrackDef("Goods", TrackDirection.BiDirectional));
        TrackLayouts.TryParse("Main:both", out parsed, out _).Should().BeTrue();
        parsed.Should().Equal(new TrackDef("Main", TrackDirection.BiDirectional));
    }

    [Theory]
    [InlineData("", "*at least one track*")]
    [InlineData("Up:Sideways", "*'Sideways' is not a direction*")]
    [InlineData(":Up", "*no track name*")]
    [InlineData("Up, up", "*named twice*")]
    public void Refuses_bad_track_text(string text, string message)
    {
        TrackLayouts.TryParse(text, out _, out var error).Should().BeFalse();
        error.Should().Match(message);
    }
}

public sealed class LocationNamesCsvTests
{
    private static readonly Tiploc A = new("A"), B = new("B");

    [Fact]
    public void Reads_names_skipping_a_header_and_comments()
    {
        var csv = "﻿TIPLOC,Name\r\n# a comment\r\nA,Aston\r\n\r\nb , Bury St Edmunds ,extra\r\n";

        var import = LocationNamesCsv.Parse(csv, new HashSet<Tiploc> { A, B });

        import.Names.Should().BeEquivalentTo(new Dictionary<Tiploc, string> { [A] = "Aston", [B] = "Bury St Edmunds" });
        import.Unknown.Should().BeEmpty();
        import.Problems.Should().BeEmpty();
    }

    [Fact]
    public void Reads_quoted_fields()
    {
        var import = LocationNamesCsv.Parse("A,\"Aston, Platform \"\"1\"\"\"\n\"B\",Bury");

        import.Names[A].Should().Be("Aston, Platform \"1\"");
        import.Names[B].Should().Be("Bury");
    }

    [Fact]
    public void A_first_row_that_is_data_is_kept()
    {
        LocationNamesCsv.Parse("A,Aston\nB,Bury").Names.Should().HaveCount(2);
    }

    [Fact]
    public void Reports_unknown_tiplocs_and_bad_rows_without_failing()
    {
        var csv = "A,Aston\nZZZ,Zed\nZZZ,Zed again\nnot a tiploc,Name\nB\nB,\nA,\"open";

        var import = LocationNamesCsv.Parse(csv, new HashSet<Tiploc> { A, B });

        import.Names.Should().BeEquivalentTo(new Dictionary<Tiploc, string> { [A] = "Aston", [new Tiploc("ZZZ")] = "Zed again" });
        import.Unknown.Should().Equal(new Tiploc("ZZZ"));
        import.Problems.Should().Equal(
            "Line 4: 'not a tiploc' is not a TIPLOC.",
            "Line 5: expected TIPLOC,name.",
            "Line 6: B has no name.",
            "Line 7: a quote is not closed.");
    }
}
