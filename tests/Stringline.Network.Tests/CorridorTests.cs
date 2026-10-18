using Stringline.Core;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

public sealed class CorridorTests
{
    private static readonly Corridor Abc = new(
        "c",
        "A – C",
        [new(new Tiploc("A"), 0), new(new Tiploc("B"), 0.4), new(new Tiploc("C"), 1)],
        [
            new(new Tiploc("A"), new Tiploc("B"), Section.DoubleLine, 180, 120, MinRunDownSeconds: 120, MinRunUpSeconds: 150),
            new(new Tiploc("B"), new Tiploc("C"), Section.SingleLine, 240, 180),
        ]);

    [Fact]
    public void Looks_up_locations_and_positions()
    {
        Abc.IndexOf(new Tiploc("B")).Should().Be(1);
        Abc.IndexOf(new Tiploc("X")).Should().Be(-1);
        Abc.Contains(new Tiploc("c")).Should().BeTrue("TIPLOCs are case-insensitive");
        Abc.PositionOf(new Tiploc("B")).Should().Be(0.4);
        Abc.PositionOf(new Tiploc("X")).Should().BeNull();
        Abc.ToString().Should().Be("c (A – C, 3 locations)");
    }

    [Fact]
    public void Finds_a_section_in_either_order_only_between_neighbours()
    {
        Abc.FindSection(new Tiploc("C"), new Tiploc("B"))!.From.Should().Be(new Tiploc("B"));
        Abc.FindSection(new Tiploc("A"), new Tiploc("B"))!.To.Should().Be(new Tiploc("B"));
        Abc.FindSection(new Tiploc("A"), new Tiploc("C")).Should().BeNull();
        Abc.FindSection(new Tiploc("A"), new Tiploc("X")).Should().BeNull();
    }

    [Theory]
    [InlineData("A B C", TrackDirection.Down)]
    [InlineData("C B", TrackDirection.Up)]
    [InlineData("X A Y C Z", TrackDirection.Down)]
    [InlineData("B B C", TrackDirection.Down)]
    [InlineData("B C B", null)]
    [InlineData("A", null)]
    [InlineData("X Y", null)]
    public void Infers_direction_from_corridor_order(string locations, TrackDirection? expected)
    {
        Abc.DirectionOf(Order(locations.Split(' '))).Should().Be(expected);
    }

    [Fact]
    public void Sections_know_their_run_times_and_track_layout()
    {
        var double_ = Abc.Sections[0];
        double_.MinRunFor(TrackDirection.Down).Should().Be(120);
        double_.MinRunFor(TrackDirection.Up).Should().Be(150);
        double_.MinRunFor(TrackDirection.BiDirectional).Should().Be(120);
        double_.IsSingleLine.Should().BeFalse();
        double_.ToString().Should().Be("A–B 120s");

        var single = Abc.Sections[1];
        single.MinRunFor(TrackDirection.Up).Should().Be(180, "no Up time falls back to the overall minimum");
        single.IsSingleLine.Should().BeTrue();
    }
}

public sealed class CorridorAxisTests
{
    [Fact]
    public void Derived_axis_is_cumulative_run_time_normalised()
    {
        Near(CorridorAxis.Positions([null, null, null, null], [60, 180, 60], AxisMode.Derived), 0, 0.2, 0.8, 1);
    }

    [Fact]
    public void A_section_of_zero_seconds_still_separates_its_locations()
    {
        var positions = CorridorAxis.Positions([null, null, null], [0, 100], AxisMode.Derived);

        positions[1].Should().BeGreaterThan(0).And.BeLessThan(0.02);
        positions[2].Should().Be(1);
    }

    [Fact]
    public void Handles_tiny_corridors()
    {
        CorridorAxis.Positions([], [], AxisMode.Derived).Should().BeEmpty();
        Near(CorridorAxis.Positions([5.0], [], AxisMode.Distance), 0);
    }

    [Fact]
    public void Rejects_mismatched_section_counts()
    {
        var act = () => CorridorAxis.Positions([null, null], [60, 60], AxisMode.Derived);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Distance_axis_uses_configured_kilometres()
    {
        Near(CorridorAxis.Positions([10, 12, 20], [600, 60], AxisMode.Distance), 0, 0.2, 1);
    }

    [Fact]
    public void Distances_may_decrease_along_the_corridor()
    {
        Near(CorridorAxis.Positions([20, 18, 10], [60, 60], AxisMode.Distance), 0, 0.2, 1);
    }

    [Fact]
    public void Missing_distances_are_interpolated_and_extrapolated_by_run_time()
    {
        // Known: B = 0 km, D = 4 km, 120 s apart (30 km/h). A is 60 s before B, C halfway, E 120 s after D.
        var positions = CorridorAxis.Positions([null, 0, null, 4, null], [60, 60, 60, 120], AxisMode.Distance);

        // km: A -2, B 0, C 2, D 4, E 8 → span 10.
        Near(positions, 0, 0.2, 0.4, 0.6, 1);
    }

    [Fact]
    public void Distance_axis_needs_two_distinct_distances()
    {
        Near(CorridorAxis.Positions([null, 3, null], [60, 180], AxisMode.Distance), 0, 0.25, 1);
        Near(CorridorAxis.Positions([3, 3, null], [60, 180], AxisMode.Distance), 0, 0.25, 1);
        Near(CorridorAxis.Positions([1, 2, 3], [60, 180], AxisMode.Derived), 0, 0.25, 1);
    }

    [Fact]
    public void Apply_sets_every_position()
    {
        var corridor = new Corridor(
            "c",
            "c",
            [new(new Tiploc("A"), 0.9, 0), new(new Tiploc("B"), 0.9), new(new Tiploc("C"), 0.9, 30)],
            [
                new(new Tiploc("A"), new Tiploc("B"), Section.DoubleLine, 180, 100),
                new(new Tiploc("B"), new Tiploc("C"), Section.DoubleLine, 180, 200),
            ],
            AxisMode.Distance);

        Near(CorridorAxis.Apply(corridor).Locations.Select(l => l.AxisPosition).ToList(), 0, 1 / 3.0, 1);
    }

    private static void Near(IReadOnlyList<double> actual, params double[] expected)
    {
        actual.Should().HaveCount(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            actual[i].Should().BeApproximately(expected[i], 1e-9, $"position {i}");
        }
    }
}
