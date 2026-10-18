using Stringline.Core;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

public sealed class LocationGraphTests
{
    private static readonly Tiploc A = new("A");
    private static readonly Tiploc B = new("B");
    private static readonly Tiploc C = new("C");
    private static readonly Tiploc D = new("D");

    [Fact]
    public void Location_pairs_are_unordered()
    {
        var pair = new LocationPair(C, A);

        pair.Should().Be(new LocationPair(A, C));
        pair.A.Should().Be(A);
        pair.Other(A).Should().Be(C);
        pair.Other(C).Should().Be(A);
        pair.ToString().Should().Be("A–C");
    }

    [Fact]
    public void Edges_count_each_train_once()
    {
        var tt = Of(
            Runner("1A", "10:00", null, "A", "B", "C"),
            Runner("1B", "11:00", null, "C", "B"),
            Runner("0L", "12:00", null, "A", "B", "A", "B", "A"));

        var g = LocationGraph.Build(tt);

        g.Edges.Should().BeEquivalentTo(new Dictionary<LocationPair, int> { [new(A, B)] = 2, [new(B, C)] = 2 }, "0L shuttles A–B twice but counts once");
        g.Weight(B, A).Should().Be(2);
        g.Weight(A, C).Should().Be(0);
        g.Neighbours(B).Should().Equal(A, C);
        g.Neighbours(new Tiploc("X")).Should().BeEmpty();
        g.Locations.Should().BeEquivalentTo([A, B, C]);
    }

    [Fact]
    public void Counts_through_moves_and_out_and_back_moves()
    {
        var tt = Of(
            Runner("1A", "10:00", null, "A", "B", "C"),
            Runner("1B", "11:00", null, "C", "B", "A"),
            Runner("0L", "12:00", null, "C", "B", "D", "B"));

        var g = LocationGraph.Build(tt);

        g.Through(A, B, C).Should().Be(2);
        g.Through(C, B, A).Should().Be(2);
        g.Through(C, B, D).Should().Be(1);
        g.Through(D, B, D).Should().Be(0);
        g.Reversing(new LocationPair(B, D)).Should().Be(1, "0L only goes to D and straight back");
        g.Reversing(new LocationPair(B, C)).Should().Be(0);
    }

    [Fact]
    public void A_train_booked_past_a_location_is_routed_through_it()
    {
        // 1F is booked A–C, but 2S shows the line is A–B–C.
        var tt = Of(
            Train("1F", Call("A", null, "10:00"), Call("C", "10:05", null)),
            Train("2S", Call("A", null, "11:00"), Call("B", "11:03", "11:04"), Call("C", "11:08", null)));

        var g = LocationGraph.Build(tt);

        g.Expansions.Should().ContainKey(new LocationPair(A, C)).WhoseValue.Should().Equal(A, B, C);
        g.Weight(A, C).Should().Be(0);
        g.Weight(A, B).Should().Be(2);
        g.Through(A, B, C).Should().Be(2);
    }

    [Fact]
    public void Expands_in_rounds_to_the_finest_detail()
    {
        var tt = Of(
            Runner("1F", "10:00", null, "A", "D"),
            Runner("2S", "11:00", null, "A", "B", "D"),
            Runner("2T", "12:00", null, "B", "C", "D"));

        var g = LocationGraph.Build(tt);

        g.Edges.Keys.Should().BeEquivalentTo([new LocationPair(A, B), new LocationPair(B, C), new LocationPair(C, D)]);
        g.Weight(C, D).Should().Be(3);
    }

    [Fact]
    public void A_much_slower_chain_is_another_route_not_a_skip()
    {
        var tt = Of(
            Train("1F", Call("A", null, "10:00"), Call("C", "10:05", null)),
            Train("2S", Call("A", null, "11:00"), Call("B", "11:30", "11:31"), Call("C", "12:00", null)));

        var g = LocationGraph.Build(tt);

        g.Expansions.Should().BeEmpty();
        g.Weight(A, C).Should().Be(1);
    }

    [Fact]
    public void The_most_common_chain_wins()
    {
        var tt = Of(
            Runner("1F", "10:00", null, "A", "D"),
            Runner("2S", "11:00", null, "A", "B", "D"),
            Runner("2T", "12:00", null, "A", "C", "D"),
            Runner("2U", "13:00", null, "A", "C", "D"));

        LocationGraph.Build(tt).Expansions[new LocationPair(A, D)].Should().Equal(A, C, D);
    }

    [Fact]
    public void Contradictory_chains_do_not_expand_each_other()
    {
        // 1X says B is between A and C; 1Y says C is between A and B. Only one order can be accepted.
        var tt = Of(
            Runner("1A", "10:00", null, "A", "C"),
            Runner("1B", "10:10", null, "A", "B"),
            Runner("1X", "11:00", null, "A", "B", "C"),
            Runner("1Y", "12:00", null, "A", "C", "B"),
            Runner("1Z", "13:00", null, "A", "C", "B"));

        var g = LocationGraph.Build(tt);

        g.Expansions.Keys.Should().Equal(new LocationPair(A, B));
        g.Expansions[new LocationPair(A, B)].Should().Equal(A, C, B);
    }

    [Fact]
    public void A_skip_limit_of_zero_expands_nothing()
    {
        var tt = Of(Runner("1F", "10:00", null, "A", "C"), Runner("2S", "11:00", null, "A", "B", "C"));

        LocationGraph.Build(tt, maxSkipped: 0).Expansions.Should().BeEmpty();
    }

    [Fact]
    public void Ignores_trains_at_a_single_location()
    {
        LocationGraph.Build(Of(Train("0A", Untimed("A"), Untimed("A")))).Edges.Should().BeEmpty();
    }
}
