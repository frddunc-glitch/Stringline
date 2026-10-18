using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network.Profiles;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

public sealed class ColourTests
{
    [Theory]
    [InlineData("#D32F2F", 0xD3, 0x2F, 0x2F, 0xFF)]
    [InlineData(" #d32f2f80 ", 0xD3, 0x2F, 0x2F, 0x80)]
    [InlineData("#000000", 0, 0, 0, 0xFF)]
    public void Parses_hex(string text, int r, int g, int b, int a)
    {
        Colour.Parse(text).Should().Be(new Colour((byte)r, (byte)g, (byte)b, (byte)a));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D32F2F")]
    [InlineData("#D32F2")]
    [InlineData("#GGGGGG")]
    [InlineData("#+1234567")]
    public void Rejects_anything_else(string? text)
    {
        Colour.TryParse(text, out _).Should().BeFalse();
        if (text is not null)
        {
            var act = () => Colour.Parse(text);
            act.Should().Throw<FormatException>();
        }
    }

    [Fact]
    public void Formats_alpha_only_when_not_opaque()
    {
        new Colour(1, 2, 3).ToString().Should().Be("#010203");
        new Colour(1, 2, 3, 4).ToString().Should().Be("#01020304");
    }
}

public sealed class CategoryStyleTests
{
    private static TrainCategory Category(string id, string description = "", bool freight = false) =>
        new(id, description, freight, null, null, null, null, null, DwellTimes.None);

    [Fact]
    public void Defaults_follow_the_headcode_class_most_trains_use()
    {
        var tt = Of(
            [
                Train("1A01", "EXP", Untimed("A")),
                Train("1A02", "EXP", Untimed("A")),
                Train("2A01", "EXP", Untimed("A")),
                Train("6M01", "FRT", Untimed("A")),
                Train("XYZ", "ODD", Untimed("A")),
            ],
            [Category("EXP", "Express"), Category("FRT", "Freight", freight: true), Category("ODD"), Category("NONE", "Unused freight", freight: true), Category("PAX", "Unused"), Category("EXP", "Duplicate")]);

        var styles = CategoryStyle.CreateDefaults(tt);

        styles.Select(s => (s.CategoryId, s.Name, s.Priority)).Should().Equal(
            ("EXP", "Express", 90),
            ("FRT", "Freight", 40),
            ("ODD", "ODD", 70),
            ("NONE", "Unused freight", 50),
            ("PAX", "Unused", 70));
        styles[0].Colour.Should().Be(new Colour(0xD3, 0x2F, 0x2F));
        styles.Should().OnlyContain(s => s.Style == LineStyle.Solid);
    }

    [Fact]
    public void Every_class_has_a_distinct_default()
    {
        var defaults = Enumerable.Range(0, 10).Select(CategoryStyle.ForClass).ToList();

        defaults.Select(d => d.Colour).Should().OnlyHaveUniqueItems();
        defaults.Select(d => d.Priority).Should().OnlyHaveUniqueItems();
        CategoryStyle.ForClass(1).Priority.Should().BeGreaterThan(CategoryStyle.ForClass(2).Priority);
        CategoryStyle.ForClass(2).Priority.Should().BeGreaterThan(CategoryStyle.ForClass(6).Priority);
        CategoryStyle.ForClass(5).Priority.Should().BeGreaterThan(CategoryStyle.ForClass(0).Priority);
    }
}

public sealed class ProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "stringline-profile-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly WorkingTimetable Line = Of(
        [
            Runner("1D01", "10:00", true, "A", "B", "C"),
            Runner("1U01", "11:00", null, "C", "B", "A"),
            Runner("2D01", "12:00", true, "A", "B", "C"),
            Train("2U01", "PAX", Call("C", null, "13:00"), Call("B", "13:02", "13:03"), Call("A", "13:05", null)),
        ],
        [new TrainCategory("PAX", "Stopping", false, null, null, null, null, null, DwellTimes.None)]);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static StringlineProfile Sample() => StringlineProfile.Create(Line) with
    {
        LocationNames = new Dictionary<Tiploc, string> { [new Tiploc("A")] = "Aston", [new Tiploc("B")] = " " },
    };

    [Fact]
    public void Create_proposes_corridors_and_styles()
    {
        var profile = StringlineProfile.Create(Line);

        profile.Version.Should().Be(StringlineProfile.CurrentVersion);
        profile.AreaId.Should().Be("test");
        profile.Name.Should().Be("Test");
        profile.Corridors.Should().ContainSingle().Which.Id.Should().Be("main");
        profile.Categories.Should().ContainSingle().Which.CategoryId.Should().Be("PAX");
    }

    [Fact]
    public void Looks_up_names_corridors_and_styles()
    {
        var profile = Sample();

        profile.DisplayName(new Tiploc("A")).Should().Be("Aston");
        profile.DisplayName(new Tiploc("B")).Should().Be("B", "a blank name falls back to the TIPLOC");
        profile.DisplayName(new Tiploc("C")).Should().Be("C");
        profile.FindCorridor("MAIN").Should().BeSameAs(profile.Corridors[0]);
        profile.FindCorridor("other").Should().BeNull();
        profile.StyleFor("PAX").Name.Should().Be("Stopping");
        profile.StyleFor("ZZZ").Should().BeSameAs(CategoryStyle.Fallback);
        profile.StyleFor(null).Should().BeSameAs(CategoryStyle.Fallback);
    }

    [Fact]
    public void Refresh_recomputes_corridors_and_adds_new_categories()
    {
        var profile = Sample() with { Categories = [new CategoryStyle("OLD", "Kept", 1, new Colour(1, 2, 3))] };
        var stale = profile with { Corridors = [profile.Corridors[0] with { Sections = [.. profile.Corridors[0].Sections.Select(s => s with { MinRunSeconds = 1 })] }] };

        var refreshed = stale.Refresh(Line);

        refreshed.Corridors[0].Sections.Should().OnlyContain(s => s.MinRunSeconds == 60);
        refreshed.Categories.Select(c => c.CategoryId).Should().Equal("OLD", "PAX");
        refreshed.LocationNames.Should().BeSameAs(profile.LocationNames);
    }

    [Fact]
    public void Json_round_trips()
    {
        var profile = Sample();

        var json = ProfileJson.Serialize(profile);
        var back = ProfileJson.Deserialize(json);

        back.Should().BeEquivalentTo(profile);
        ProfileJson.Serialize(back).Should().Be(json);
    }

    [Fact]
    public void Json_is_readable()
    {
        var json = ProfileJson.Serialize(Sample());

        json.Should().StartWith("{\n  \"version\": 1,\n  \"areaId\": \"test\",").And.EndWith("}\n");
        json.Should().Contain("\"tiploc\": \"A\"").And.Contain("\"axis\": \"Derived\"").And.Contain("\"direction\": \"Up\"");
        json.Should().Contain("\"locationNames\": {\n    \"A\": \"Aston\",");
        json.Should().Contain("\"colour\": \"#1976D2\"");
        json.Should().NotContain("isSingleLine").And.NotContain("distanceKm", "nulls are left out");
    }

    [Fact]
    public void Json_accepts_comments_trailing_commas_and_unknown_properties()
    {
        var json = """
            {
              // A hand-edited profile.
              "areaId": "test",
              "future": { "x": 1 },
              "corridors": [
                {
                  "id": "c",
                  "name": "A – B",
                  "locations": [ { "tiploc": "a", "axisPosition": 0 }, { "tiploc": "B", "axisPosition": 1, "distanceKm": 2.5 }, ],
                  "sections": [ { "from": "A", "to": "B", "tracks": [ { "name": "Single", "direction": "BiDirectional" } ], "defaultHeadwaySeconds": 240, "minRunSeconds": 90 } ]
                }
              ],
              "categories": [ { "categoryId": "X", "name": "X", "priority": 5, "colour": "#112233", "style": "Dashed" } ]
            }
            """;

        var profile = ProfileJson.Deserialize(json);

        profile.Version.Should().Be(1, "a missing version is the current one");
        profile.Corridors[0].Axis.Should().Be(AxisMode.Derived);
        profile.Corridors[0].Locations[0].Tiploc.Should().Be(new Tiploc("A"));
        profile.Corridors[0].Locations[1].DistanceKm.Should().Be(2.5);
        profile.Corridors[0].Sections[0].IsSingleLine.Should().BeTrue();
        profile.Categories[0].Style.Should().Be(LineStyle.Dashed);
        profile.LocationNames.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json", "Not a valid profile*")]
    [InlineData("null", "*empty*")]
    [InlineData("""{ "version": 2, "areaId": "x" }""", "*version 2*newer*")]
    [InlineData("""{ "areaId": " " }""", "*no areaId*")]
    [InlineData("""{ "version": 1 }""", "Not a valid profile*")]
    [InlineData("""{ "areaId": "x", "categories": [ { "categoryId": "X", "name": "X", "priority": 5, "colour": "red" } ] }""", "Not a valid profile*colour*")]
    [InlineData("""{ "areaId": "x", "corridors": [ { "id": "c", "name": "c", "locations": [], "sections": [] }, { "id": "C", "name": "c", "locations": [], "sections": [] } ] }""", "*'C' is empty or used twice*")]
    [InlineData("""{ "areaId": "x", "corridors": [ { "id": "c", "name": "c", "locations": [ { "tiploc": "A", "axisPosition": 0 }, { "tiploc": "A", "axisPosition": 1 } ], "sections": [] } ] }""", "*location twice*")]
    [InlineData("""{ "areaId": "x", "corridors": [ { "id": "c", "name": "c", "locations": [ { "tiploc": "A", "axisPosition": 0 }, { "tiploc": "B", "axisPosition": 1 } ], "sections": [] } ] }""", "*needs 1 sections, but has 0*")]
    [InlineData("""{ "areaId": "x", "corridors": [ { "id": "c", "name": "c", "locations": [ { "tiploc": "A", "axisPosition": 0 }, { "tiploc": "B", "axisPosition": 1 } ], "sections": [ { "from": "B", "to": "A", "tracks": [ { "name": "U", "direction": "Up" } ], "defaultHeadwaySeconds": 1, "minRunSeconds": 1 } ] } ] }""", "*section 1 is B–A*")]
    [InlineData("""{ "areaId": "x", "corridors": [ { "id": "c", "name": "c", "locations": [ { "tiploc": "A", "axisPosition": 0 }, { "tiploc": "B", "axisPosition": 1 } ], "sections": [ { "from": "A", "to": "B", "tracks": [], "defaultHeadwaySeconds": 1, "minRunSeconds": 1 } ] } ] }""", "*has no tracks*")]
    public void Json_refuses_unusable_profiles(string json, string message)
    {
        var act = () => ProfileJson.Deserialize(json);

        act.Should().Throw<ProfileFormatException>().WithMessage(message);
    }

    [Fact]
    public void Store_saves_and_loads_by_area_id()
    {
        var store = new ProfileStore(_directory);
        var profile = Sample();

        store.Exists("test").Should().BeFalse();
        store.Load("test").Should().BeNull();

        var path = store.Save(profile);

        path.Should().Be(Path.Combine(_directory, "test.json"));
        store.Exists("TEST").Should().BeTrue();
        store.Load("Test").Should().BeEquivalentTo(profile);
        Directory.GetFiles(_directory).Should().ContainSingle("the temporary file is moved into place");

        store.Save(profile with { Name = "Second" });
        store.Load("test")!.Name.Should().Be("Second");
    }

    [Fact]
    public void Store_file_names_are_safe()
    {
        var store = new ProfileStore(_directory);

        Path.GetFileName(store.PathFor(" Water/loo..\\x:y ")).Should().Be("water_loo___x_y.json");
        var act = () => store.PathFor(" ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Store_refuses_a_file_for_another_sim()
    {
        var store = new ProfileStore(_directory);
        store.Save(Sample());
        File.Move(store.PathFor("test"), store.PathFor("other"));

        var act = () => store.Load("other");

        act.Should().Throw<ProfileFormatException>().WithMessage("*profile for 'test', not 'other'*");
    }

    [Fact]
    public void Default_directory_is_under_application_data()
    {
        new ProfileStore().Directory.Should().Be(ProfileStore.DefaultDirectory);
        ProfileStore.DefaultDirectory.Should().EndWith(Path.Combine("Stringline", "profiles"));
    }
}
