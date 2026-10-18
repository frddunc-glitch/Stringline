using Stringline.Core.Timetables;
using static Stringline.Network.Tests.TestTimetable;

namespace Stringline.Network.Tests;

public sealed class CorridorMeasurementTests
{
    private static CorridorMeasurement Measure(WorkingTimetable tt, params string[] order) => CorridorMeasurement.Measure(tt, Order(order));

    [Fact]
    public void Takes_the_fastest_run_each_way_less_allowances()
    {
        var tt = Of(
            Train("1D01", Call("A", null, "10:00"), Call("B", "10:05", "10:06"), Call("C", "10:10", null)),
            Train("1D02", Call("A", null, "11:00", allowance: 60), Pass("B", "11:04"), Call("C", "11:07", null)),
            Train("1U01", Call("C", null, "12:00"), Pass("B", "12:05"), Call("A", "12:11", null)));

        var m = Measure(tt, "A", "B", "C");

        m.Sections[0].Should().Be(new SectionStats(180, 360, 2, 1, 0), "1D02 took 4 min including 1 min allowance");
        m.Sections[1].Should().Be(new SectionStats(180, 300, 2, 1, 0));
        m.Sections[0].MinRunSeconds.Should().Be(180);
        m.Sections[0].Samples.Should().Be(3);
        m.Trains.Should().Be(3);
    }

    [Fact]
    public void Counts_calls_stops_platforms_starts_and_ends()
    {
        var tt = Of(
            Train("1D01", Call("A", null, "10:00", platform: "2"), Pass("B", "10:05"), Call("C", "10:10", null, platform: "1")),
            Train("1D02", Call("A", null, "11:00", platform: " 1 "), Pass("B", "11:04"), Call("C", "11:07", "11:08"), Call("X", "11:20", null)),
            Train("2X01", Call("X", null, "09:00"), Call("A", "09:10", null, platform: "2")));

        var m = Measure(tt, "A", "B", "C");

        m.Locations[0].Should().BeEquivalentTo(new { Calls = 3, Stops = 3, Starts = 2, Terminates = 1 });
        m.Locations[0].Platforms.Should().Equal("1", "2");
        m.Locations[1].Should().BeEquivalentTo(new { Calls = 2, Stops = 0, Starts = 0, Terminates = 0, IsTimingPointOnly = true });
        m.Locations[2].Should().BeEquivalentTo(new { Calls = 2, Stops = 2, Starts = 0, Terminates = 1, IsTimingPointOnly = false });
        m.Locations[1].Platforms.Should().BeEmpty();
        m.Locations[2].Platforms.Should().Equal("1");
        m.Trains.Should().Be(2, "2X01 calls at only one corridor location");
    }

    [Fact]
    public void A_location_no_train_calls_at_is_not_timing_point_only()
    {
        var m = Measure(Of(), "A", "B");

        m.Locations[0].IsTimingPointOnly.Should().BeFalse();
        m.Sections[0].Should().Be(new SectionStats(null, null, 0, 0, 0));
        m.Sections[0].MinRunSeconds.Should().BeNull();
    }

    [Fact]
    public void Ignores_untimed_and_non_positive_runs()
    {
        var tt = Of(
            Train("0Z01", Untimed("A"), Untimed("B")),
            Train("1D01", Call("A", null, "10:05"), Call("B", "10:05", null)),
            Train("1D02", Call("A", null, "10:05"), Call("B", "10:01", null)));

        var m = Measure(tt, "A", "B");

        m.Sections[0].Samples.Should().Be(0);
        m.Trains.Should().Be(3, "they still run over the section");
    }

    [Fact]
    public void Points_in_a_row_at_one_location_are_one_visit_timed_from_the_last_departure()
    {
        var tt = Of(Train("5D01", Call("A", null, "10:00"), Call("A", "10:10", "10:20"), Call("B", "10:24", null)));

        var m = Measure(tt, "A", "B");

        m.Sections[0].Should().Be(new SectionStats(240, null, 1, 0, 0));
        m.Locations[0].Calls.Should().Be(1);
    }

    [Fact]
    public void A_trip_to_a_siding_and_back_is_two_visits_with_no_run_between()
    {
        var tt = Of(Train("5D01", Call("A", null, "10:00"), Untimed("SIDING"), Call("A", "10:10", "10:20"), Call("B", "10:24", null)));

        var m = Measure(tt, "A", "B");

        m.Sections[0].Should().Be(new SectionStats(240, null, 1, 0, 0));
        m.Locations[0].Calls.Should().Be(2);
    }

    [Fact]
    public void A_shunt_out_and_back_on_the_corridor_counts_both_ways()
    {
        var tt = Of(Train("0D01", Call("A", null, "10:00"), Call("B", "10:02", "10:05"), Call("A", "10:08", null)));

        var m = Measure(tt, "A", "B");

        m.Sections[0].Should().Be(new SectionStats(120, 180, 1, 1, 0));
    }

    [Fact]
    public void A_run_past_corridor_locations_is_shared_by_direct_minimums()
    {
        var tt = Of(
            Train("2D01", Call("A", null, "10:00"), Call("B", "10:02", "10:03"), Call("C", "10:09", null)),
            Train("1D01", Call("A", null, "11:00"), Call("C", "11:04", null)));

        var m = Measure(tt, "A", "B", "C");

        // Direct mins 120 and 360 (1:3), so 1D01's 240 s splits 60 + 180.
        m.Sections[0].Should().Be(new SectionStats(60, null, 2, 0, 0));
        m.Sections[1].Should().Be(new SectionStats(180, null, 2, 0, 0));
    }

    [Fact]
    public void A_run_with_points_off_the_corridor_between_non_neighbours_is_not_used()
    {
        var tt = Of(Train("1D01", Call("A", null, "11:00"), Pass("X", "11:01"), Call("C", "11:04", null)));

        var m = Measure(tt, "A", "B", "C");

        m.Sections.Should().OnlyContain(s => s.Samples == 0);
        m.Trains.Should().Be(0);
    }

    [Fact]
    public void Off_corridor_points_between_neighbours_count_in_the_run()
    {
        var tt = Of(Train("1D01", Call("A", null, "11:00", allowance: 30), Pass("X", "11:01", allowance: 30), Call("B", "11:05", null)));

        Measure(tt, "A", "B").Sections[0].MinRunDownSeconds.Should().Be(240);
    }

    [Fact]
    public void Spans_use_default_weights_where_no_train_is_timed_directly()
    {
        var tt = Of(Train("1U01", Call("C", null, "11:00"), Call("A", "11:06", null)));

        var m = Measure(tt, "A", "B", "C");

        m.Sections[0].MinRunUpSeconds.Should().Be(180);
        m.Sections[1].MinRunUpSeconds.Should().Be(180);
    }

    [Fact]
    public void Trains_crossing_at_a_loop_hint_at_single_line()
    {
        // Down and Up trains booked to cross at B every hour.
        var trains = new List<Train>();
        for (var h = 10; h < 13; h++)
        {
            trains.Add(Train($"2D{h}", Call("A", null, $"{h}:00"), Call("B", $"{h}:10", $"{h}:12"), Call("C", $"{h}:22", null)));
            trains.Add(Train($"2U{h}", Call("C", null, $"{h}:00"), Call("B", $"{h}:10", $"{h}:12"), Call("A", $"{h}:22", null)));
        }

        var m = Measure(Of([.. trains], []), "A", "B", "C");

        m.Sections.Should().OnlyContain(s => s.OpposingOverlaps == 0 && s.SingleLineHint);
    }

    [Fact]
    public void Trains_passing_in_a_section_rule_out_single_line()
    {
        var trains = new List<Train>();
        for (var h = 10; h < 13; h++)
        {
            trains.Add(Train($"2D{h}", Call("A", null, $"{h}:00"), Call("B", $"{h}:10", null)));
            trains.Add(Train($"2U{h}", Call("B", null, $"{h}:02"), Call("A", $"{h}:12", null)));
        }

        var section = Measure(Of([.. trains], []), "A", "B").Sections[0];

        section.OpposingOverlaps.Should().Be(3);
        section.SingleLineHint.Should().BeFalse();
    }

    [Fact]
    public void A_minute_of_overlap_is_booking_rounding_not_passing()
    {
        // As Llangollen's 1L02 and 2C03 either side of DEESIDE loop.
        var trains = new List<Train>
        {
            Train("2D1", Call("A", null, "12:17"), Call("B", null, "12:26")),
            Train("2U1", Call("B", null, "12:10"), Call("A", "12:18", null)),
            Train("2D2", Call("A", null, "14:00"), Call("B", "14:08", null)),
            Train("2U2", Call("B", null, "15:00"), Call("A", "15:08", null)),
        };

        Measure(Of([.. trains], []), "A", "B").Sections[0].SingleLineHint.Should().BeTrue();
    }

    [Fact]
    public void A_short_section_uses_half_the_run_as_tolerance()
    {
        var trains = new List<Train>
        {
            Train("2D1", Pass("A", "12:00:00"), Pass("B", "12:01:00")),
            Train("2U1", Pass("B", "12:00:10"), Pass("A", "12:01:10")),
            Train("2D2", Pass("A", "14:00"), Pass("B", "14:01")),
            Train("2U2", Pass("B", "15:00"), Pass("A", "15:01")),
        };

        Measure(Of([.. trains], []), "A", "B").Sections[0].OpposingOverlaps.Should().Be(1, "50 s together on a 60 s section is passing");
    }

    [Fact]
    public void Needs_two_runs_each_way_for_a_hint()
    {
        var tt = Of(Train("2D1", Call("A", null, "10:00"), Call("B", "10:10", null)), Train("2U1", Call("B", null, "11:00"), Call("A", "11:10", null)));

        Measure(tt, "A", "B").Sections[0].SingleLineHint.Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_location_twice()
    {
        var act = () => Measure(Of(), "A", "B", "A");

        act.Should().Throw<ArgumentException>().WithMessage("*A appears twice*");
    }
}
