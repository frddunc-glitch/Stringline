using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Network.Profiles;

namespace Stringline.Network.Tests;

/// <summary>S39: section headways from the learned signal blocks (<see cref="BlockHeadways"/>).</summary>
public sealed class BlockHeadwaysTests
{
    private static readonly Tiploc A = new("A");
    private static readonly Tiploc B = new("B");
    private static readonly Tiploc C = new("C");

    private static readonly Corridor Main = new(
        "main",
        "Main",
        [new CorridorLocation(A, 0), new CorridorLocation(B, 0.5), new CorridorLocation(C, 1)],
        [new Section(A, B, Section.DoubleLine, 180, 120) { BookedHeadwaySeconds = 150, BookedHeadwayPairs = 20 }, new Section(B, C, Section.DoubleLine, 180, 120)]);

    /// <summary>
    /// Down from A: S1–S5 between A and B, S6 and S7 between B and C, each passed next after the one before, with block
    /// times 20, 22, 24, 26, 30 and 30 s; track circuit T4 starts S4's block and clears 12 s after a train enters it.
    /// </summary>
    private static SignallingPlaces Places(params int[] aspects)
    {
        (string Id, Tiploc From, Tiploc To, double Fraction, string? Next, int? Seconds)[] signals =
        [
            ("S1", A, B, 0.1, "S2", 20), ("S2", A, B, 0.3, "S3", 22), ("S3", A, B, 0.5, "S4", 24), ("S4", A, B, 0.7, "S5", 26),
            ("S5", A, B, 0.9, "S6", 30), ("S6", B, C, 0.2, "S7", 30), ("S7", B, C, 0.5, null, null),
        ];
        var samples = new List<PlaceSample>();
        for (var i = 0; i < 3; i++)
        {
            var at = SimTime.Parse("06:00") + (i * 600);
            foreach (var (id, from, to, fraction, next, seconds) in signals)
            {
                samples.Add(new PlaceSample(SignallingKind.Signal, id, from, to, fraction, at, Next: next, NextSeconds: seconds + i, Aspect: aspects[i % aspects.Length]));
            }

            samples.Add(new PlaceSample(SignallingKind.TrackCircuit, "T4", A, B, 0.72, at, 0.8, Signal: "S4", ClearSeconds: 12 + i));
        }

        return SignallingPlaces.Empty.Learn(samples, "one", SimTime.Parse("07:00"), [Main], out _);
    }

    private static StringlineProfile Profile(SignallingPlaces places, Corridor? corridor = null) =>
        new StringlineProfile { AreaId = "test", Corridors = [corridor ?? Main] }.WithPlaces(places).WithBlockHeadways();

    [Fact]
    public void Four_aspect_needs_three_blocks_and_the_overlap_clear()
    {
        var places = Places(SignalAspects.Green, SignalAspects.DoubleYellow);
        var blocks = BlockHeadways.Measure([Main], UsablePlaces.Of(places));

        var ab = blocks.Find(B, A)!;
        ab.Aspects.Should().Be(4, "a double yellow was seen on the corridor");
        var signals = ab.SignalsFrom(A);
        signals.Select(s => s.Id).Should().Equal("S1", "S2", "S3", "S4", "S5");
        signals[0].ShareFor(RunKind.Through).Should().BeApproximately(0.1, 1e-9);
        signals[0].HeadwaySeconds.Should().Be(20 + 22 + 24 + 12, "S1 to S4, then T4 past S4 clearing behind the train (the fastest times)");
        signals[1].HeadwaySeconds.Should().Be(22 + 24 + 26 + BlockHeadways.OverlapSeconds, "no track circuit is learned past S5");
        signals[3].HeadwaySeconds.Should().Be(26 + 30 + 30 + BlockHeadways.OverlapSeconds, "the blocks go on past B");
        signals[4].HeadwaySeconds.Should().BeNull("no signal is placed three ahead of S5");
        ab.SignalsFrom(B).Should().BeEmpty("no train ran Up");
        ab.HeadwaySeconds.Should().Be(101, "the largest at its signals (Fred)");
        ab.Signals.Should().Be(4);

        var section = Profile(places).Corridors[0].Sections[0];
        section.BlockHeadwaySeconds.Should().Be(101);
        section.BlockSignals.Should().Be(4);
        section.BlockAspects.Should().Be(4);
        section.HeadwaySource.Should().Be(HeadwaySource.Blocks);
        section.DefaultHeadwaySeconds.Should().Be(101, "the blocks come before the WTT's 2:30");
    }

    [Fact]
    public void Three_aspect_needs_two_blocks()
    {
        var places = Places(SignalAspects.Green, SignalAspects.SingleYellow, SignalAspects.Red);
        var ab = BlockHeadways.Measure([Main], UsablePlaces.Of(places)).Find(A, B)!;

        ab.Aspects.Should().Be(3, "no double yellow was seen");
        ab.SignalsFrom(A)[0].HeadwaySeconds.Should().Be(20 + 22 + BlockHeadways.OverlapSeconds);
        ab.SignalsFrom(A)[1].HeadwaySeconds.Should().Be(22 + 24 + 12, "T4 past S4");
        ab.HeadwaySeconds.Should().Be(30 + 30 + BlockHeadways.OverlapSeconds, "S5: to S7 past B");
    }

    [Fact]
    public void Aspects_set_in_the_profile_win_and_none_seen_is_four_aspect()
    {
        var places = Places(SignalAspects.Green, SignalAspects.DoubleYellow);
        var three = Main with { Sections = [Main.Sections[0] with { Aspects = 3 }, Main.Sections[1]] };
        BlockHeadways.Measure([three], UsablePlaces.Of(places)).Find(A, B)!.HeadwaySeconds.Should().Be(75);
        Profile(places, three).Corridors[0].Sections[0].BlockAspects.Should().Be(3);

        var edited = Profile(places).WithAspects("main", 0, 3);
        edited.Corridors[0].Sections[0].Aspects.Should().Be(3);
        edited.Corridors[0].Sections[0].DefaultHeadwaySeconds.Should().Be(75, "the blocks are measured again");
        edited.WithAspects("main", 0, null).Corridors[0].Sections[0].DefaultHeadwaySeconds.Should().Be(101, "back to what play showed");
        FluentActions.Invoking(() => edited.WithAspects("main", 0, 6)).Should().Throw<ProfileEditException>();

        var unknown = places with { Signals = [.. places.Signals.Select(p => p with { Aspects = null })] };
        BlockHeadways.Measure([Main], UsablePlaces.Of(unknown)).Find(A, B)!.Aspects.Should().Be(BlockHeadways.DefaultAspects, "a place learned before S39 has no aspects");
    }

    [Fact]
    public void Headways_go_hand_set_then_blocks_then_wtt_then_default()
    {
        var places = Places(SignalAspects.DoubleYellow);
        var profile = Profile(places);
        profile.Corridors[0].Sections[1].HeadwaySource.Should().Be(HeadwaySource.Default, "B–C has signals but none measured, and no WTT figure");
        profile.Corridors[0].Sections[1].BlockHeadwaySeconds.Should().BeNull();

        var low = profile.WithGlobalHeadway(90).Corridors[0].Sections[0];
        low.HeadwaySource.Should().Be(HeadwaySource.Blocks, "blocks are used above the default too");
        low.DefaultHeadwaySeconds.Should().Be(101);

        var hand = profile.WithHeadway("main", 0, 200).WithBlockHeadways().Corridors[0].Sections[0];
        hand.HeadwaySource.Should().Be(HeadwaySource.Hand, "measuring the blocks again keeps a hand-set headway");
        hand.DefaultHeadwaySeconds.Should().Be(200);
        hand.BlockHeadwaySeconds.Should().Be(101, "still measured, for the editor");

        var none = profile.WithPlaces(SignallingPlaces.Empty).WithBlockHeadways().Corridors[0].Sections[0];
        none.HeadwaySource.Should().Be(HeadwaySource.Wtt, "without places it goes back to the WTT's");
        none.BlockHeadwaySeconds.Should().BeNull();
        none.DefaultHeadwaySeconds.Should().Be(150);
    }

    [Fact]
    public void A_short_block_headway_is_floored()
    {
        var places = Places(SignalAspects.Green);
        var fast = places with { Signals = [.. places.Signals.Select(p => p with { NextSeconds = p.NextSeconds?.ToDictionary(n => n.Key, _ => 5) })], TrackCircuits = [] };
        BlockHeadways.Measure([Main], UsablePlaces.Of(fast)).Find(A, B)!.HeadwaySeconds.Should().Be(BlockHeadways.FloorSeconds);
    }

    [Fact]
    public void Learning_keeps_the_fastest_block_and_clearing_times_and_the_aspects_seen()
    {
        var places = Places(SignalAspects.Green, SignalAspects.DoubleYellow, SignalAspects.Red);
        var s1 = places.Find(SignallingKind.Signal, "S1", A, B)!;
        s1.NextSeconds.Should().BeEquivalentTo(new Dictionary<string, int> { ["S2"] = 20 });
        s1.Aspects.Should().Equal(SignalAspects.Red, SignalAspects.DoubleYellow, SignalAspects.Green);
        places.Find(SignallingKind.TrackCircuit, "T4", A, B)!.ClearSeconds.Should().Be(12);
        places.Find(SignallingKind.Signal, "S5", A, B)!.Next.Should().ContainKey("S6", "the first signal past B counts as next");

        var faster = SignallingPlaces.Empty.Learn(
            [new PlaceSample(SignallingKind.Signal, "S1", A, B, 0.1, SimTime.Parse("08:00"), Next: "S2", NextSeconds: 15, Aspect: 1)],
            "two",
            SimTime.Parse("08:00"),
            [Main],
            out _);
        var pooled = places.Pool(faster).Find(SignallingKind.Signal, "S1", A, B)!;
        pooled.NextSeconds!["S2"].Should().Be(15);
        pooled.Aspects.Should().Equal(0, 1, 4, 6);
    }

    [Fact]
    public void Json_round_trips_the_aspects_and_timings()
    {
        var places = Places(SignalAspects.Green, SignalAspects.DoubleYellow);
        var profile = Profile(places, Main with { Sections = [Main.Sections[0] with { Aspects = 4 }, Main.Sections[1]] });

        var json = ProfileJson.Serialize(profile);
        var back = ProfileJson.Deserialize(json);

        back.Corridors[0].Sections[0].Aspects.Should().Be(4);
        back.Corridors[0].Sections[0].BlockHeadwaySeconds.Should().Be(101);
        back.Corridors[0].Sections[0].HeadwaySource.Should().Be(HeadwaySource.Blocks);
        back.Corridors[0].Sections[1].Aspects.Should().BeNull();
        back.Places.Should().BeEquivalentTo(places);
        json.Should().Contain("\"aspects\": 4").And.Contain("\"nextSeconds\"").And.Contain("\"clearSeconds\": 12").And.Contain("\"headwaySource\": \"Blocks\"");
        back.Version.Should().Be(2, "an older Stringline ignores what is new");
    }
}
