using Stringline.Core.Timetables;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

public sealed class CorridorGeneratorTests
{
    private static List<string> Codes(Corridor c) => c.Locations.Select(l => l.Tiploc.Code).ToList();

    /// <summary>n Down trains along <paramref name="down"/> and n Up trains back, flagged as SimSig does (Down only).</summary>
    private static IEnumerable<Train> BothWays(string prefix, int n, params string[] down)
    {
        for (var i = 0; i < n; i++)
        {
            yield return Runner($"{prefix}D{i}", $"{10 + i}:00", true, down);
            yield return Runner($"{prefix}U{i}", $"{10 + i}:30", null, [.. down.Reverse()]);
        }
    }

    [Fact]
    public void A_simple_line_is_one_corridor_called_main()
    {
        var tt = Of([.. BothWays("1", 3, "A", "B", "C", "D")], []);

        var candidates = CorridorGenerator.Generate(tt);

        candidates.Should().ContainSingle();
        var c = candidates[0];
        c.Corridor.Id.Should().Be("main");
        c.Corridor.Name.Should().Be("A – D");
        Codes(c.Corridor).Should().Equal("A", "B", "C", "D");
        c.Corridor.Sections.Should().HaveCount(3).And.OnlyContain(s => s.MinRunSeconds == 60 && s.Samples == 6);
        c.Corridor.Locations.Select(l => l.AxisPosition).Should().Equal(0, 1 / 3.0, 2 / 3.0, 1);
        c.Measurement.Trains.Should().Be(6);
        c.Traffic.Should().Be(18);
        c.NewTraffic.Should().Be(18);
    }

    [Fact]
    public void Orients_the_corridor_so_Down_trains_ascend()
    {
        // The same line, with Down flags on the trains running D to A.
        var trains = new List<Train>();
        for (var i = 0; i < 3; i++)
        {
            trains.Add(Runner($"1D{i}", $"{10 + i}:00", true, "D", "C", "B", "A"));
            trains.Add(Runner($"1U{i}", $"{10 + i}:30", null, "A", "B", "C", "D"));
        }

        Codes(CorridorGenerator.Generate(Of([.. trains], []))[0].Corridor).Should().Equal("D", "C", "B", "A");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("D")]
    public void Without_Down_flags_the_busier_end_goes_first(string terminus)
    {
        var trains = new List<Train>
        {
            Runner("1A", "10:00", null, "A", "B", "C", "D"),
            Runner("1B", "11:00", null, "D", "C", "B", "A"),
            Runner("2T", "12:00", null, terminus == "A" ? ["A", "B"] : ["D", "C"]),
            Runner("2U", "13:00", null, terminus == "A" ? ["B", "A"] : ["C", "D"]),
        };

        Codes(CorridorGenerator.Generate(Of([.. trains], []))[0].Corridor)[0].Should().Be(terminus);
    }

    [Fact]
    public void A_branch_follows_its_trains_onto_the_main_line()
    {
        // Main line T–J–M–N (8 trains each way). Branch J–X–Y (3 each way) runs through to T, the terminus.
        var trains = BothWays("1", 8, "T", "J", "M", "N").Concat(BothWays("2", 3, "T", "J", "X", "Y")).ToList();

        var candidates = CorridorGenerator.Generate(Of(trains, []));

        candidates.Should().HaveCount(2);
        Codes(candidates[0].Corridor).Should().Equal("T", "J", "M", "N");
        Codes(candidates[1].Corridor).Should().Equal("T", "J", "X", "Y");
        candidates[1].Corridor.Id.Should().Be("t-y");
        candidates[1].Traffic.Should().Be(22 + 6 + 6);
        candidates[1].NewTraffic.Should().Be(12);
    }

    [Fact]
    public void Stops_where_too_few_trains_carry_on()
    {
        // 8 trains A–B–C each way; only one goes on to D, below the 25% through share.
        var trains = BothWays("1", 4, "A", "B", "C").Concat(BothWays("2", 4, "A", "B", "C")).Append(Runner("5X", "09:00", true, "A", "B", "C", "D")).ToList();

        var candidates = CorridorGenerator.Generate(Of(trains, []));

        Codes(candidates[0].Corridor).Should().Equal("A", "B", "C");
        candidates.Should().ContainSingle("C–D has one train, below the minimum of two");
    }

    [Fact]
    public void Out_and_back_moves_do_not_make_corridors()
    {
        var trains = BothWays("1", 3, "A", "B", "C").Concat(Enumerable.Range(0, 5).Select(i => Runner($"0R{i}", $"{12 + i}:00", null, "C", "SIDING", "C"))).ToList();

        var candidates = CorridorGenerator.Generate(Of(trains, []), new CorridorGeneratorOptions { MinLocations = 2 });

        candidates.Should().ContainSingle();
        Codes(candidates[0].Corridor).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void Drops_short_corridors_and_spurs_that_add_almost_nothing()
    {
        var main = BothWays("1", 10, "A", "B", "C", "D", "E");
        var spur = BothWays("2", 1, "A", "B", "C", "D", "S");        // 2 trains on D–S, against 8 × 2 + … on the rest.
        var stub = BothWays("3", 2, "E", "F");                       // a single section off the end, few trains through.
        var tt = Of([.. main, .. spur, .. stub], []);

        CorridorGenerator.Generate(tt).Should().ContainSingle();

        var lenient = CorridorGenerator.Generate(tt, new CorridorGeneratorOptions { MinNewShare = 0, MinLocations = 2 });
        lenient.Select(c => c.Corridor.Name).Should().Equal("A – E", "E – F", "A – S");
    }

    [Fact]
    public void Respects_the_corridor_limit_and_minimum_trains()
    {
        var trains = BothWays("1", 3, "A", "B", "C").Concat(BothWays("2", 3, "X", "Y", "Z")).Concat(BothWays("3", 1, "P", "Q", "R")).ToList();
        var tt = Of(trains, []);

        CorridorGenerator.Generate(tt).Should().HaveCount(3);
        CorridorGenerator.Generate(tt, new CorridorGeneratorOptions { MaxCorridors = 1 }).Should().ContainSingle();
        CorridorGenerator.Generate(tt, new CorridorGeneratorOptions { MinTrains = 3 }).Should().HaveCount(2);
        CorridorGenerator.Generate(tt, new CorridorGeneratorOptions { MinTrains = 7 }).Should().BeEmpty();
    }

    [Fact]
    public void Corridors_with_the_same_ends_are_told_apart()
    {
        // Two routes from A to D: via B (busier) and via C.
        var trains = BothWays("1", 5, "A", "B", "D").Concat(BothWays("2", 3, "A", "C", "D")).ToList();

        var names = CorridorGenerator.Generate(Of(trains, []), new CorridorGeneratorOptions { MinNewShare = 0 }).Select(c => (c.Corridor.Id, c.Corridor.Name)).ToList();

        names.Should().Equal(("main", "A – D"), ("a-d", "A – D via C"));
    }

    [Fact]
    public void Gives_nothing_for_an_empty_timetable()
    {
        CorridorGenerator.Generate(Of()).Should().BeEmpty();
    }

    [Fact]
    public void Ids_are_unique()
    {
        var trains = BothWays("1", 5, "A", "B", "C", "D").Concat(BothWays("2", 3, "A", "X", "Y", "D")).Concat(BothWays("3", 2, "A", "P", "Q", "D")).ToList();

        var ids = CorridorGenerator.Generate(Of(trains, []), new CorridorGeneratorOptions { MinNewShare = 0 }).Select(c => c.Corridor.Id).ToList();

        ids.Should().Equal("main", "a-d", "a-d-2");
    }
}
