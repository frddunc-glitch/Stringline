using Microsoft.Extensions.Time.Testing;
using Stringline.Core.Events;
using Stringline.Core.Live;

namespace Stringline.Core.Tests;

public sealed class SimClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private static ClockEvent Clock(double wallSeconds, string clock, int interval = 500, bool paused = false) =>
        new(T0.AddSeconds(wallSeconds), "waterloo", SimTime.Parse(clock), interval, paused);

    private static DateTimeOffset At(double wallSeconds) => T0.AddSeconds(wallSeconds);

    [Fact]
    public void Has_no_time_before_the_first_clock()
    {
        var clock = new SimClock();

        clock.HasTime.Should().BeFalse();
        clock.Speed.Should().Be(0);
        clock.NowAt(At(5)).Should().Be(SimTime.Zero);
        clock.AreaId.Should().BeNull();
    }

    [Fact]
    public void Interpolates_at_the_nominal_speed_until_measured()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00"));

        clock.HasTime.Should().BeTrue();
        clock.AreaId.Should().Be("waterloo");
        clock.MeasuredSpeed.Should().BeNull();
        clock.Speed.Should().Be(1);
        clock.NowAt(At(0)).Should().Be(SimTime.Parse("05:00:00"));
        clock.NowAt(At(12.5)).Should().Be(SimTime.Parse("05:00:12"));
        clock.SecondsAt(At(12.5)).Should().BeApproximately(SimTime.Parse("05:00:12").Seconds + 0.5, 1e-9);
    }

    [Fact]
    public void Nominal_speed_follows_the_interval()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00", interval: 125));

        clock.Speed.Should().Be(4);
        clock.NowAt(At(10)).Should().Be(SimTime.Parse("05:00:40"));
    }

    [Fact]
    public void Does_not_run_past_the_end_of_the_minute()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:30"));

        clock.NowAt(At(29)).Should().Be(SimTime.Parse("05:00:59"));
        clock.NowAt(At(300)).Should().Be(SimTime.Parse("05:00:59"), "the next clock_msg is due at the minute");
    }

    [Fact]
    public void Never_goes_back_before_the_last_clock()
    {
        var clock = new SimClock();
        clock.Observe(Clock(10, "05:00:00"));

        clock.NowAt(At(0)).Should().Be(SimTime.Parse("05:00:00"));
    }

    [Fact]
    public void Freezes_while_paused()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00", paused: true));

        clock.Paused.Should().BeTrue();
        clock.Speed.Should().Be(0);
        clock.NowAt(At(30)).Should().Be(SimTime.Parse("05:00:00"));
    }

    [Fact]
    public void Measures_the_speed_from_clock_arrivals()
    {
        // A recording made at 1× replayed at 8×: the interval still says 500.
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00"));
        clock.Observe(Clock(7.5, "05:01:00"));

        clock.MeasuredSpeed.Should().BeApproximately(8, 1e-9);
        clock.Speed.Should().BeApproximately(8, 1e-9);
        clock.NowAt(At(7.5 + 5)).Should().Be(SimTime.Parse("05:01:40"));
    }

    [Fact]
    public void Flat_out_holds_until_measured()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00", interval: 1));

        clock.Speed.Should().Be(0, "an interval of 1 says nothing about the real speed");
        clock.NowAt(At(1)).Should().Be(SimTime.Parse("05:00:00"));

        clock.Observe(Clock(1.875, "05:01:00", interval: 1));

        clock.MeasuredSpeed.Should().BeApproximately(32, 1e-9);
        clock.NowAt(At(1.875 + 1)).Should().Be(SimTime.Parse("05:01:32"));
    }

    [Fact]
    public void A_speed_change_drops_the_measurement()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00"));
        clock.Observe(Clock(60, "05:01:00"));
        clock.MeasuredSpeed.Should().BeApproximately(1, 1e-9);

        clock.Observe(Clock(70, "05:01:10", interval: 125));

        clock.MeasuredSpeed.Should().BeNull();
        clock.Speed.Should().Be(4);
        clock.IntervalMs.Should().Be(125);
    }

    [Fact]
    public void Short_gaps_accumulate_into_one_measurement()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:50"));
        clock.Observe(Clock(10, "05:01:00"));

        clock.MeasuredSpeed.Should().BeNull("10 sim seconds is too short to measure");

        clock.Observe(Clock(70, "05:02:00"));

        clock.MeasuredSpeed.Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void A_pause_does_not_count_towards_the_measurement()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00", interval: 125));
        clock.Observe(Clock(5, "05:00:20", interval: 125, paused: true));
        clock.Observe(Clock(500, "05:00:20", interval: 125));
        clock.Observe(Clock(510, "05:01:00", interval: 125));

        clock.MeasuredSpeed.Should().BeApproximately(4, 1e-9);
    }

    [Fact]
    public void Ignores_an_implausible_measurement()
    {
        // A stale idrequest reply, then the real tick a moment later.
        var clock = new SimClock();
        clock.Observe(Clock(0, "04:30:00"));
        clock.Observe(Clock(0.5, "09:00:00"));

        clock.MeasuredSpeed.Should().BeNull();
        clock.Speed.Should().Be(1);
    }

    [Fact]
    public void A_clock_going_back_starts_again()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "09:00:00"));
        clock.Observe(Clock(7.5, "09:01:00"));

        clock.Observe(Clock(8, "06:00:00"));

        clock.MeasuredSpeed.Should().BeNull();
        clock.LastReported.Should().Be(SimTime.Parse("06:00:00"));
        clock.NowAt(At(18)).Should().Be(SimTime.Parse("06:00:10"));
    }

    [Fact]
    public void Train_event_times_move_the_clock_on()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00"));

        clock.ObserveEventTime(SimTime.Parse("05:00:30"), At(2));
        clock.NowAt(At(2)).Should().Be(SimTime.Parse("05:00:30"));
        clock.NowAt(At(4)).Should().Be(SimTime.Parse("05:00:32"));

        clock.ObserveEventTime(SimTime.Parse("05:00:10"), At(5));
        clock.NowAt(At(5)).Should().Be(SimTime.Parse("05:00:33"), "an earlier event never moves it back");
    }

    [Fact]
    public void A_train_event_past_the_minute_extends_the_limit()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00"));

        clock.ObserveEventTime(SimTime.Parse("05:01:05"), At(3));

        clock.NowAt(At(100)).Should().Be(SimTime.Parse("05:01:05"));
    }

    [Fact]
    public void Train_event_times_are_ignored_before_any_clock()
    {
        var clock = new SimClock();

        clock.ObserveEventTime(SimTime.Parse("05:00:30"), At(0));

        clock.HasTime.Should().BeFalse();
    }

    [Fact]
    public void Now_reads_the_time_provider()
    {
        var time = new FakeTimeProvider(T0);
        var clock = new SimClock(time);
        clock.Observe(Clock(0, "05:00:00"));

        time.Advance(TimeSpan.FromSeconds(20));

        clock.Now.Should().Be(SimTime.Parse("05:00:20"));
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var clock = new SimClock();
        clock.Observe(Clock(0, "05:00:00"));
        clock.Observe(Clock(60, "05:01:00"));

        clock.Reset();

        clock.HasTime.Should().BeFalse();
        clock.MeasuredSpeed.Should().BeNull();
        clock.IntervalMs.Should().Be(0);
        clock.NowAt(At(61)).Should().Be(SimTime.Zero);
    }
}
