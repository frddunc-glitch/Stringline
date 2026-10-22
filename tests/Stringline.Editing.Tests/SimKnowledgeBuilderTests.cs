using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Editing.Knowledge;
using Stringline.Network.Profiles;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Editing.Tests;

/// <summary>What <see cref="SimKnowledgeBuilder"/> learns from small timetables, play and a profile.</summary>
public sealed class SimKnowledgeBuilderTests
{
    private static readonly Tiploc A = new("A"), B = new("B"), C = new("C"), D = new("D");

    private static SimKnowledge Build(params Train[] trains)
    {
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Of(trains));
        return builder.Build();
    }

    private static Train Fast(string headcode, params TimingPoint[] points) => Train(headcode, points: points) with { MaxSpeedMph = 100 };

    [Theory]
    [InlineData(null, SpeedBand.Unknown)]
    [InlineData(0, SpeedBand.Unknown)]
    [InlineData(50, SpeedBand.UpTo50)]
    [InlineData(51, SpeedBand.UpTo75)]
    [InlineData(75, SpeedBand.UpTo75)]
    [InlineData(90, SpeedBand.UpTo90)]
    [InlineData(100, SpeedBand.UpTo100)]
    [InlineData(125, SpeedBand.UpTo125)]
    [InlineData(126, SpeedBand.Over125)]
    public void Speeds_fall_in_fixed_bands(int? mph, SpeedBand band) => RunBand.BandOf(mph).Should().Be(band);

    [Fact]
    public void A_band_reads_back_from_its_text()
    {
        foreach (var speed in Enum.GetValues<SpeedBand>())
        {
            foreach (var freight in new[] { false, true })
            {
                RunBand.TryParse(new RunBand(speed, freight).ToString(), out var band).Should().BeTrue();
                band.Should().Be(new RunBand(speed, freight));
            }
        }

        RunBand.TryParse("76-90 passenger", out _).Should().BeFalse();
        RunBand.TryParse("", out _).Should().BeFalse();
    }

    [Fact]
    public void A_link_has_its_codes_direction_and_run_times_net_of_allowances()
    {
        var k = Build(
            Fast("1A01", Call("A", null, "10:00") with { Line = "UF", DownDirection = true, PathingAllowanceSeconds = 60 }, Call("B", "10:05", null) with { Path = "UF" }),
            Fast("1A02", Call("A", null, "11:00") with { Line = "US", DownDirection = true }, Call("B", "11:06", null) with { Path = "UF" }),
            Fast("1A03", Call("A", null, "12:00") with { Line = "UF" }, Call("B", "12:05", null)),
            Train("6A04", points: [Call("A", null, "13:00"), Call("B", "13:10", null)]) with { MaxSpeedMph = 60, IsFreight = true },
            Fast("1A05", Call("A", null, "14:00"), Call("B", "14:00", null)));

        var link = k.Link(A, B)!;
        link.Count.Should().Be(5);
        (link.Down, link.Up).Should().Be((2, 0));
        link.Lines.Should().Equal(new Dictionary<string, int> { ["UF"] = 2, ["US"] = 1 });
        link.Paths.Should().Equal(new Dictionary<string, int> { ["UF"] = 2 });
        k.RunTime(A, B, new RunBand(SpeedBand.UpTo100, false)).Should().Be(new TimeStats(3, 240, 300), "5 min less a minute's allowance, 6 and 5 min; a run of 0 is bad data");
        k.RunTime(A, B, new RunBand(SpeedBand.UpTo75, true)).Should().Be(new TimeStats(1, 600, 600));
        k.Link(B, A).Should().BeNull("links have a direction");
    }

    [Fact]
    public void A_location_counts_its_codes_and_learns_its_roles()
    {
        var k = Build(
            Train("1A01", points: [Call("A", null, "10:00") with { Platform = "1" }, Pass("B", "10:05") with { Line = "F" }, Call("C", "10:10", null, Next("1A02")) with { Platform = "2" }]),
            Train("1A02", formedFrom: new TrainId("1A01"), points: [Call("C", null, "10:20") with { Platform = "2" }, Pass("B", "10:25"), Call("A", "10:30", null)]),
            Train("2A03", start: TrainStart.EntryPoint, entry: "09:59", points: [Pass("B", "10:00"), Call("D", "10:05", "10:06"), Pass("B", "10:10")]),
            Train("5A04", start: TrainStart.Seeded, points: [Call("A", null, "10:00")]));

        k.Location(A)!.Roles.Should().Be(LocationRoles.Timing | LocationRoles.Station);
        k.Location(A)!.Platforms.Should().Equal(new Dictionary<string, int> { ["1"] = 1 });
        k.Location(B)!.Roles.Should().Be(LocationRoles.Timing, "trains only pass B");
        k.Location(B)!.Lines.Should().Equal(new Dictionary<string, int> { ["F"] = 1 });
        k.Location(C)!.Roles.Should().Be(LocationRoles.Timing | LocationRoles.Station | LocationRoles.Key | LocationRoles.Reversing,
            "1A01 has its next working there, which goes back the way it came");
        k.Location(C)!.Platforms.Should().Equal(new Dictionary<string, int> { ["2"] = 2 });
        k.Location(D)!.Roles.Should().Be(LocationRoles.Timing | LocationRoles.Station | LocationRoles.Reversing, "2A03 goes out and back");
        k.Location(new Tiploc("EIN"))!.Roles.Should().Be(LocationRoles.Entry);
        k.Location(new Tiploc("S1"))!.Roles.Should().Be(LocationRoles.Seed);
    }

    [Fact]
    public void Dwells_are_learned_at_intermediate_stops_without_activities()
    {
        var k = Build(
            Fast("1A01", Call("A", null, "10:00"), Call("B", "10:05", "10:06"), Call("C", "10:10", "10:20")),
            Fast("1A02", Call("A", null, "11:00"), Call("B", "11:05", "11:07"), Call("C", "11:10", null)),
            Fast("1A03", Call("A", null, "12:00"), Call("B", "12:05", "12:15", Next("1A04")), Call("C", "12:20", null)),
            Fast("1A04", Call("A", null, "13:00"), Call("B", "13:05", "13:05:30"), Call("C", "13:10", null)));

        var band = new RunBand(SpeedBand.UpTo100, false);
        k.Dwell(B, band).Should().Be(new TimeStats(3, 30, 60), "1A03's dwell is for an activity");
        k.Dwell(C, band).Should().BeNull("a train's last point is no dwell");
        k.Dwell(A, band).Should().BeNull();
    }

    [Fact]
    public void Consecutive_points_at_one_location_are_one_call_on_the_link()
    {
        var k = Build(Fast("1A01", Call("A", null, "10:00"), Call("B", "10:05", null), Call("B", null, "10:08") with { Line = "R" }, Call("C", "10:12", null)));

        k.Link(A, B)!.Count.Should().Be(1);
        k.Link(B, B).Should().BeNull();
        k.Link(B, C)!.Lines.Should().Equal(new Dictionary<string, int> { ["R"] = 1 });
        k.RunTime(B, C, new RunBand(SpeedBand.UpTo100, false))!.LeastSeconds.Should().Be(240, "from the departure, not the arrival");
    }

    [Fact]
    public void The_nearest_band_stands_in_for_one_with_no_run_times()
    {
        var k = Build(
            Train("1A01", points: [Call("A", null, "10:00"), Call("B", "10:05", null)]) with { MaxSpeedMph = 100 },
            Train("2A02", points: [Call("A", null, "10:00"), Call("B", "10:07", null)]) with { MaxSpeedMph = 75 },
            Train("6A03", points: [Call("A", null, "10:00"), Call("B", "10:15", null)]) with { MaxSpeedMph = 60, IsFreight = true });

        k.NearestRunTime(A, B, new RunBand(SpeedBand.UpTo100, false))!.Value.Band.Should().Be(new RunBand(SpeedBand.UpTo100, false));
        k.NearestRunTime(A, B, new RunBand(SpeedBand.UpTo90, false))!.Value.Band.Should().Be(new RunBand(SpeedBand.UpTo75, false), "75 and 100 are as near; the slower is safer");
        k.NearestRunTime(A, B, new RunBand(SpeedBand.UpTo50, true))!.Value.Band.Should().Be(new RunBand(SpeedBand.UpTo75, true), "freight before passenger");
        k.NearestRunTime(A, B, new RunBand(SpeedBand.Over125, true))!.Value.Band.Should().Be(new RunBand(SpeedBand.UpTo75, true));
        k.NearestRunTime(B, A, new RunBand(SpeedBand.UpTo100, false)).Should().BeNull();
    }

    [Fact]
    public void A_chain_is_a_skip_chain_or_the_fewest_booked_links()
    {
        var k = Build(
            Fast("1A01", Call("A", null, "10:00"), Pass("B", "10:02"), Call("C", "10:05", null)),
            Fast("1A02", Call("A", null, "11:00"), Call("C", "11:05", null)),
            Fast("1A03", Call("C", null, "12:00"), Call("D", "12:05", null)));

        k.SkipChains.Should().ContainSingle().Which.Chain.Should().Equal(A, B, C);
        k.Chain(A, C).Should().Equal(A, B, C);
        k.Chain(C, A).Should().Equal([C, B, A], "a skip chain works both ways");
        k.Chain(A, D).Should().Equal([A, C, D], "A → C → D is two links; A → B → C → D is three");
        k.Chain(D, A).Should().BeNull("nothing is booked out of D");
    }

    [Fact]
    public void Play_gives_reported_locations_signals_and_entry_signals()
    {
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Of(
            Train("1A01", start: TrainStart.EntryPoint, entry: "10:00", points: [Pass("A", "10:02"), Call("B", "10:05", null)]),
            Train("1A02", points: [Call("A", null, "09:00"), Call("B", "09:05", null)])));
        builder.AddPlay([
            Clock("09:00") with { AreaId = "test" },
            Location("1", "1A02", "A", "09:00", TrainLocationAction.Depart, "1"),
            Location("1", "1A02", "S12", "09:01"),
            Location("1", "1A02", "B", "09:05", TrainLocationAction.Arrive, "2"),
            Location("2", "1A01", "S10", "10:01") with { AspectPassed = 3 },
            Location("2", "1A01", "A", "10:02"),
            Location("2", "1A01", "QUARRY", "10:03", TrainLocationAction.Arrive),
        ]).Should().BeTrue();

        var k = builder.Build();

        k.Plays.Should().Be(1);
        k.Location(A)!.Reports.Should().Be(2);
        k.Location(A)!.ReportedPlatforms.Should().Equal(new Dictionary<string, int> { ["1"] = 1 });
        k.Location(B)!.Roles.Should().HaveFlag(LocationRoles.Reported);
        k.Location(new Tiploc("QUARRY"))!.Roles.Should().Be(LocationRoles.Reported, "a train stopped there, so it is a timing location no timetable books");
        k.Signals.Select(s => s.Id).Should().Equal("S10", "S12");
        k.Signal("S12")!.Entries.Should().Be(0, "1A02 was there when play began");
        var entered = k.Signal("s10")!;
        (entered.Reports, entered.Entries, entered.Places).Should().Be((1, 1, null));
        entered.EntryPoints.Should().Equal(new Dictionary<string, int> { ["EIN"] = 1 }, "1A01 enters at EIN in the timetable");
    }

    [Fact]
    public void Another_sims_play_is_left_out()
    {
        var builder = new SimKnowledgeBuilder("test");

        builder.AddPlay([Clock("09:00") with { AreaId = "other" }, Location("1", "1A02", "A", "09:00")]).Should().BeFalse();

        builder.Build().Should().BeEquivalentTo(new { Plays = 0, Locations = Array.Empty<LocationKnowledge>() });
    }

    [Fact]
    public void Another_sims_timetable_is_refused()
    {
        var builder = new SimKnowledgeBuilder("waterloo");

        builder.Invoking(b => b.AddTimetable(Of())).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_profile_gives_names_corridors_and_learned_places()
    {
        var timetable = Of(Fast("1A01", Call("A", null, "10:00"), Call("B", "10:05", null)), Fast("1A02", Call("A", null, "11:00"), Call("B", "11:05", null)));
        var profile = StringlineProfile.Create(timetable) with { LocationNames = new Dictionary<Tiploc, string> { [A] = "Aston" } };
        var places = SignallingPlaces.Empty
            .SetByHand(SignallingKind.Signal, "S5", A, B, 0.5)
            .SetByHand(SignallingKind.TrackCircuit, "T5", A, B, 0.4)
            .SetByHand(SignallingKind.Berth, "0005", A, B, 0.5);
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(timetable);
        builder.AddProfile(profile.WithPlaces(places));

        var k = builder.Build();

        k.Location(A)!.Name.Should().Be("Aston");
        k.Location(A)!.Corridors.Should().Equal(profile.Corridors[0].Id);
        k.Signal("S5")!.Places.Should().ContainSingle().Which.Should().Be(new PlaceKnowledge("S5", A, B, RunKind.Through, 0.5, 0, true));
        k.TrackCircuits.Should().ContainSingle().Which.Id.Should().Be("T5");
        k.Berths.Should().ContainSingle().Which.Id.Should().Be("0005");
    }

    [Fact]
    public void Knowledge_reads_back_from_its_json()
    {
        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Of(
            Fast("1A01", Call("A", null, "10:00") with { Platform = "1", Line = "F" }, Call("B", "10:05", "10:06"), Call("C", "10:10", null)),
            Fast("1A02", Call("A", null, "11:00"), Call("C", "11:10", null))));
        builder.AddPlay([Location("1", "1A01", "S1", "10:03") with { AspectPassed = 1 }]);
        var k = builder.Build([new KnowledgeSource(KnowledgeSourceKind.Timetable, "x.wtt", new DateTime(2026, 10, 22, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234), 99)], DateTimeOffset.UnixEpoch);

        var json = SimKnowledgeJson.Serialize(k);
        var back = SimKnowledgeJson.TryDeserialize(json)!;

        SimKnowledgeJson.Serialize(back).Should().Be(json);
        back.Sources[0].SameAs(k.Sources[0]).Should().BeTrue();
        back.RunTime(A, B, new RunBand(SpeedBand.UpTo100, false)).Should().Be(new TimeStats(1, 300, 300));
        SimKnowledgeJson.TryDeserialize(json.Replace("\"format\": 1", "\"format\": 99", StringComparison.Ordinal)).Should().BeNull("another format is rebuilt");
        SimKnowledgeJson.TryDeserialize("{ not json").Should().BeNull();
    }
}
