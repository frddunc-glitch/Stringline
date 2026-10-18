using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

public sealed class TrainDelaysTests
{
    private static readonly Core.Timetables.Train Stopper = Train("2A01", points:
    [
        Call("WLOO", null, "06:00"),
        Pass("VAUX", "06:04"),
        Call("CLPHMJM", "06:08", "06:09"),
    ]);

    private static readonly Core.Timetables.Train OutAndBack = Train("2B01", points:
    [
        Call("WLOO", null, "06:00"),
        Call("VAUX", "06:05", "06:06"),
        Call("WLOO", "06:20", null),
    ]);

    [Theory]
    [InlineData("CLPHMJM", TrainLocationAction.Arrive, "06:08")]
    [InlineData("CLPHMJM", TrainLocationAction.Depart, "06:09")]
    [InlineData("VAUX", TrainLocationAction.Pass, "06:04")]
    [InlineData("VAUX", TrainLocationAction.Arrive, "06:04")]
    [InlineData("WLOO", TrainLocationAction.Depart, "06:00")]
    [InlineData("WLOO", TrainLocationAction.Arrive, "06:00")]
    public void Compares_an_arrival_with_the_booked_arrival_and_anything_else_with_the_departure(string location, TrainLocationAction action, string booked)
    {
        TrainDelays.BookedTime(Actual(location, "06:30", action), Stopper).Should().Be(SimTime.Parse(booked));
    }

    [Fact]
    public void Is_not_booked_where_the_train_does_not_call()
    {
        TrainDelays.BookedTime(Actual("WIMB", "06:30"), Stopper).Should().BeNull();
        TrainDelays.At(Actual("WIMB", "06:30"), Stopper).Should().BeNull();
    }

    [Fact]
    public void Uses_the_nearest_call_at_a_location_visited_twice()
    {
        TrainDelays.BookedTime(Actual("WLOO", "06:22", TrainLocationAction.Arrive), OutAndBack).Should().Be(SimTime.Parse("06:20"));
        TrainDelays.BookedTime(Actual("WLOO", "06:01", TrainLocationAction.Depart), OutAndBack).Should().Be(SimTime.Parse("06:00"));
    }

    [Fact]
    public void A_delay_is_actual_minus_booked_in_whole_minutes_towards_zero()
    {
        var late = TrainDelays.At(Actual("CLPHMJM", "06:13:59", TrainLocationAction.Depart), Stopper)!;
        late.Seconds.Should().Be(299);
        late.Minutes.Should().Be(4);
        late.Booked.Should().Be(SimTime.Parse("06:09"));
        late.Location.Should().Be(new Tiploc("CLPHMJM"));

        var early = TrainDelays.At(Actual("VAUX", "06:02:30"), Stopper)!;
        early.Seconds.Should().Be(-90);
        early.Minutes.Should().Be(-1);
    }

    [Fact]
    public void The_current_delay_is_at_the_latest_booked_timing_point_of_the_current_working()
    {
        var tt = Of(Stopper);
        var live = Live(
            new LiveWorking(new Headcode("2A01"), 0, MatchStatus.Matched, new TrainId("2A01")),
            Actual("WLOO", "06:01", TrainLocationAction.Depart),
            Actual("VAUX", "06:07"),
            Actual("WL123", "06:08", atTimingPoint: false),
            Actual("WIMB", "06:09"));

        var delay = TrainDelays.Current(live, tt)!;
        delay.Location.Should().Be(new Tiploc("VAUX"), "signals and points the train is not booked at do not count");
        delay.Seconds.Should().Be(180);
    }

    [Fact]
    public void There_is_no_current_delay_for_an_unmatched_train_or_without_a_timetable()
    {
        var unmatched = Live(new LiveWorking(new Headcode("2A01"), 0, MatchStatus.Unmatched), Actual("WLOO", "06:01", TrainLocationAction.Depart));
        var matched = Live(new LiveWorking(new Headcode("2A01"), 0, MatchStatus.Matched, new TrainId("2A01")), Actual("WLOO", "06:01", TrainLocationAction.Depart));

        TrainDelays.Current(unmatched, Of(Stopper)).Should().BeNull();
        TrainDelays.Current(matched, null).Should().BeNull();
        TrainDelays.Current(matched, Of(OutAndBack)).Should().BeNull("2A01 is not in that timetable");
    }

    private static LiveTrain Live(LiveWorking working, params ActualEvent[] actuals) =>
        new("100", [working], actuals.ToImmutableList(), false);
}
