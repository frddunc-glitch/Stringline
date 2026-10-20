using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

/// <summary>S27 (#7): sim-aware headways, derived from the WTT, with where each comes from.</summary>
public sealed class HeadwayTests
{
    private static readonly Tiploc A = new("A"), B = new("B");

    /// <summary>Runs A → B taking 2 min, entering at these clock times.</summary>
    private static WorkingTimetable Followers(params string[] departures) =>
        Of([.. departures.Select((d, i) => Train($"1D{i:00}", Pass("A", d), Pass("B", SimTime.Parse(d).AddSeconds(120).ToString())))], []);

    /// <summary>Every <paramref name="step"/> seconds from 07:00, <paramref name="count"/> times.</summary>
    private static string[] Every(int step, int count) =>
        [.. Enumerable.Range(0, count).Select(i => SimTime.FromClock(7, 0).AddSeconds(i * step).ToString())];

    private static Section Measured(WorkingTimetable timetable, int fallback = Section.StandardHeadwaySeconds) =>
        CorridorBuilder.Create("main", "A – B", Order("A", "B"), timetable, fallback).Sections[0];

    [Fact]
    public void Trains_booked_two_minutes_apart_give_a_two_minute_headway()
    {
        var h = BookedHeadway.Derive(Enumerable.Range(0, 20).Select(i => (i * 120, (i * 120) + 120)));

        h.Should().Be(new BookedHeadway(120, 19, 120, 120));
    }

    [Fact]
    public void One_tight_pair_does_not_set_it()
    {
        // 60 trains 2:30 apart, and one squeezed in 1:10 behind another (making two close pairs): the 5th percentile is still 2:30.
        var runs = Enumerable.Range(0, 60).Select(i => (i * 150, (i * 150) + 100)).Append((10 * 150 + 70, (10 * 150) + 170)).ToList();

        var h = BookedHeadway.Derive(runs);

        h.Seconds.Should().Be(150);
        h.ClosestSeconds.Should().Be(70);
    }

    [Fact]
    public void Too_few_pairs_give_none()
    {
        var h = BookedHeadway.Derive(Enumerable.Range(0, BookedHeadway.MinPairs).Select(i => (i * 120, (i * 120) + 120)));

        h.Pairs.Should().Be(BookedHeadway.MinPairs - 1);
        h.Seconds.Should().BeNull();
        h.SpacingSeconds.Should().Be(120);
    }

    [Fact]
    public void Trains_side_by_side_are_on_different_tracks()
    {
        // Pairs leaving together every 4 min (a four-track line): each track's trains are 4 min apart, not 0.
        var runs = Enumerable.Range(0, 20).SelectMany(i => new[] { (i * 240, (i * 240) + 120), ((i * 240) + 10, (i * 240) + 130) });

        BookedHeadway.Derive(runs).Seconds.Should().Be(240);
    }

    [Fact]
    public void Spacings_closer_than_the_floor_are_the_floor_and_wide_gaps_are_not_pairs()
    {
        BookedHeadway.Derive(Enumerable.Range(0, 20).Select(i => (i * 60, (i * 60) + 60))).Seconds.Should().Be(BookedHeadway.FloorSeconds);
        BookedHeadway.Derive(Enumerable.Range(0, 20).Select(i => (i * 3600, (i * 3600) + 60))).Pairs.Should().Be(0, "an hour apart is not following");
    }

    [Fact]
    public void Both_directions_count()
    {
        var down = Enumerable.Range(0, 6).Select(i => (i * 120, (i * 120) + 60));
        var up = Enumerable.Range(0, 6).Select(i => (i * 100, (i * 100) + 60));

        BookedHeadway.Derive(down, up).Should().Match<BookedHeadway>(h => h.Pairs == 10 && h.Seconds == 100);
    }

    [Theory]
    [InlineData(HeadwaySource.Hand, 240, 90, 180, 240, HeadwaySource.Hand)]
    [InlineData(HeadwaySource.Default, 180, 90, 180, 90, HeadwaySource.Wtt)]
    [InlineData(HeadwaySource.Wtt, 90, 200, 180, 180, HeadwaySource.Default)]
    [InlineData(HeadwaySource.Wtt, 90, null, 120, 120, HeadwaySource.Default)]
    [InlineData(HeadwaySource.Default, 180, 150, 120, 120, HeadwaySource.Default)]
    public void A_hand_set_headway_wins_then_the_wtt_if_less_than_the_default(HeadwaySource source, int headway, int? booked, int fallback, int expected, HeadwaySource expectedSource)
    {
        var section = new Section(A, B, Section.DoubleLine, headway, 60, BookedHeadwaySeconds: booked, HeadwaySource: source);

        section.ResolveHeadway(fallback).Should().Match<Section>(s => s.DefaultHeadwaySeconds == expected && s.HeadwaySource == expectedSource);
    }

    [Fact]
    public void A_corridor_takes_the_wtt_headway()
    {
        var section = Measured(Followers(Every(150, 15)));

        section.Should().Match<Section>(s => s.BookedHeadwaySeconds == 150 && s.BookedHeadwayPairs == 14 && s.DefaultHeadwaySeconds == 150 && s.HeadwaySource == HeadwaySource.Wtt);
        Measured(Followers(Every(150, 15)), fallback: 120).Should().Match<Section>(s => s.DefaultHeadwaySeconds == 120 && s.HeadwaySource == HeadwaySource.Default);
        Measured(Followers(Every(150, 5))).Should().Match<Section>(s => s.BookedHeadwaySeconds == null && s.DefaultHeadwaySeconds == 180 && s.HeadwaySource == HeadwaySource.Default);
    }

    [Fact]
    public void Refreshing_keeps_a_hand_set_headway_and_derives_the_rest_again()
    {
        var timetable = Followers(Every(150, 15));
        var corridor = CorridorBuilder.Create("main", "A – B", Order("A", "B"), timetable);
        var hand = corridor with { Sections = [corridor.Sections[0] with { DefaultHeadwaySeconds = 200, HeadwaySource = HeadwaySource.Hand }] };
        var stale = corridor with { Sections = [corridor.Sections[0] with { DefaultHeadwaySeconds = 999, BookedHeadwaySeconds = 999 }] };

        CorridorBuilder.Refresh(hand, timetable).Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 200 && s.HeadwaySource == HeadwaySource.Hand && s.BookedHeadwaySeconds == 150);
        CorridorBuilder.Refresh(stale, timetable).Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 150 && s.HeadwaySource == HeadwaySource.Wtt);
    }

    [Fact]
    public void Clearing_a_hand_set_headway_goes_back_to_the_wtt()
    {
        var profile = StringlineProfile.Create(Followers(Every(150, 15)), [CorridorBuilder.Create("main", "A – B", Order("A", "B"), Followers(Every(150, 15)))]);

        var hand = profile.WithHeadway("main", 0, 240);
        var cleared = hand.WithHeadway("main", 0, null);

        hand.Corridors[0].Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 240 && s.HeadwaySource == HeadwaySource.Hand);
        cleared.Corridors[0].Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 150 && s.HeadwaySource == HeadwaySource.Wtt);
        FluentActions.Invoking(() => profile.WithHeadway("main", 0, -1)).Should().Throw<ProfileEditException>();
    }

    [Fact]
    public void The_profile_default_then_the_global_one_apply_where_the_wtt_gives_no_less()
    {
        var timetable = Followers(Every(150, 15));
        var profile = StringlineProfile.Create(timetable, [CorridorBuilder.Create("main", "A – B", Order("A", "B"), timetable)]).WithHeadway("main", 0, null);

        var tighter = profile.WithDefaultHeadway(120);
        var global = profile.WithGlobalHeadway(100);
        var both = tighter.WithGlobalHeadway(100);
        var back = tighter.WithDefaultHeadway(null);

        tighter.Corridors[0].Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 120 && s.HeadwaySource == HeadwaySource.Default);
        global.Corridors[0].Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 100 && s.HeadwaySource == HeadwaySource.Default);
        both.Corridors[0].Sections[0].DefaultHeadwaySeconds.Should().Be(120, "the profile's default comes before the global one");
        back.Corridors[0].Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 150 && s.HeadwaySource == HeadwaySource.Wtt);
        FluentActions.Invoking(() => profile.WithDefaultHeadway(-1)).Should().Throw<ProfileEditException>();
    }

    [Fact]
    public void Only_a_hand_set_headway_makes_a_corridor_hand_tuned()
    {
        var timetable = Followers(Every(150, 15));
        var profile = StringlineProfile.Create(timetable);

        profile.WithDefaultHeadway(120).HandTuned(timetable).Should().BeEmpty("headways not set by hand are not settings");
        var hand = profile.WithHeadway(profile.Corridors[0].Id, 0, 240);
        hand.HandTuned(timetable).Should().ContainSingle();
        hand.RegenerateCorridors(timetable, keepHandTuned: true).Corridors[0].Sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 240 && s.HeadwaySource == HeadwaySource.Hand);
        hand.RegenerateCorridors(timetable, keepHandTuned: false).Corridors[0].Sections[0].HeadwaySource.Should().Be(HeadwaySource.Wtt);
    }

    [Fact]
    public void Json_keeps_the_source_and_the_wtt_figure()
    {
        var timetable = Followers(Every(150, 15));
        var profile = StringlineProfile.Create(timetable).WithHeadway("main", 0, 200) with { DefaultHeadwaySeconds = 150 };

        var json = ProfileJson.Serialize(profile);
        var read = ProfileJson.Deserialize(json);

        json.Should().Contain("\"headwaySource\": \"Hand\"").And.Contain("\"bookedHeadwaySeconds\": 150").And.Contain("\"defaultHeadwaySeconds\": 150");
        json.Should().NotContain("globalHeadwaySeconds");
        read.Corridors.Should().BeEquivalentTo(profile.Corridors);
        read.DefaultHeadwaySeconds.Should().Be(150);
    }

    [Fact]
    public void A_version_1_profile_keeps_headways_set_by_hand()
    {
        const string json = """
            {
              "version": 1,
              "areaId": "x",
              "corridors": [
                {
                  "id": "c",
                  "name": "A – C",
                  "locations": [ { "tiploc": "A", "axisPosition": 0 }, { "tiploc": "B", "axisPosition": 0.5 }, { "tiploc": "C", "axisPosition": 1 } ],
                  "sections": [
                    { "from": "A", "to": "B", "tracks": [ { "name": "Up", "direction": "Up" } ], "defaultHeadwaySeconds": 180, "minRunSeconds": 90 },
                    { "from": "B", "to": "C", "tracks": [ { "name": "Up", "direction": "Up" } ], "defaultHeadwaySeconds": 120, "minRunSeconds": 90 }
                  ]
                }
              ]
            }
            """;

        var profile = ProfileJson.Deserialize(json);

        profile.Version.Should().Be(StringlineProfile.CurrentVersion);
        profile.Corridors[0].Sections.Select(s => s.HeadwaySource).Should().Equal(HeadwaySource.Default, HeadwaySource.Hand);
        profile.Corridors[0].Sections[1].DefaultHeadwaySeconds.Should().Be(120);
    }

    [Fact]
    public void Waterloo_takes_the_wtt_headway_where_trains_are_booked_closer_than_three_minutes()
    {
        var waterloo = new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable;

        var main = StringlineProfile.Create(waterloo).FindCorridor("main")!;

        main.Sections.Select(s => (s.DefaultHeadwaySeconds, s.HeadwaySource)).Should().Equal(
            (174, HeadwaySource.Wtt), (151, HeadwaySource.Wtt), (134, HeadwaySource.Wtt), (120, HeadwaySource.Wtt),
            (120, HeadwaySource.Wtt), (150, HeadwaySource.Wtt), (90, HeadwaySource.Wtt));
        StringlineProfile.Create(waterloo).FindCorridor("wloo-barnes")!.Sections.Skip(6).Should().OnlyContain(
            s => s.HeadwaySource == HeadwaySource.Default && s.DefaultHeadwaySeconds == Section.StandardHeadwaySeconds,
            "the Windsor lines beyond Clapham are booked no closer than 3 min");
    }

    [Fact]
    public void Llangollen_keeps_the_default_on_its_single_line()
    {
        var llangollen = new WttReader().Read(Fixtures.Path("llangollen 2012 B.WTT")).Timetable;

        StringlineProfile.Create(llangollen).Corridors.SelectMany(c => c.Sections).Should().OnlyContain(
            s => s.HeadwaySource == HeadwaySource.Default && s.DefaultHeadwaySeconds == Section.StandardHeadwaySeconds && s.BookedHeadwaySeconds == null);
    }
}
