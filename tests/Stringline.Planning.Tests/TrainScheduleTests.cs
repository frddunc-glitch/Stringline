using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Timetable.Live;
using static Stringline.Planning.Tests.ForecastTesting;

namespace Stringline.Planning.Tests;

/// <summary>The detail panel's schedule (RP-1): WTT, plan, actual and forecast side by side.</summary>
public sealed class TrainScheduleTests
{
    private static readonly TrainId Fast = new("1A01");

    private static SimTime S(string clock) => SimTime.Parse(clock);

    private static PlanSnapshot Snapshot(WorkingPlan plan, string now, params LiveTrain[] live)
    {
        var forecast = Run(plan, now, live: live);
        var clock = SimClockReading.None with { HasTime = true, LastReported = S(now) };
        return new PlanSnapshot(1, plan.Timetable, live, clock, new MatchSummary(live.Length, live.Length, live.Length, 0, 0, new Dictionary<MatchMethod, int>()),
            1, S("05:30"), plan, Forecast: forecast);
    }

    [Fact]
    public void A_train_as_booked_shows_the_WTT_in_the_plan_column()
    {
        var schedule = TrainSchedule.For(Snapshot(WorkingPlanTests.Plan(), "05:30"), Fast)!;

        schedule.Wtt!.Id.Should().Be(Fast);
        schedule.Live.Should().BeNull();
        schedule.Rows.Select(r => r.Location.ToString()).Should().Equal("A", "B", "C", "D");
        var b = schedule.Row(1)!;
        (b.WttArrive, b.WttDepart, b.PlanArrive, b.PlanDepart).Should().Be((S("06:05"), S("06:06"), S("06:05"), S("06:06")));
        b.IsCall.Should().BeTrue();
        b.TimesChanged.Should().BeFalse();
        var c = schedule.Row(2)!;
        c.IsPass.Should().BeTrue();
        (c.WttArrive, c.WttDepart).Should().Be(((SimTime?)null, S("06:10")));
        schedule.Rows.Should().OnlyContain(r => r.Runs && r.ActualArrive == null && r.ActualDepart == null);
        (schedule.Row(3)!.ForecastArrive, schedule.Row(3)!.DelaySeconds).Should().Be((S("06:15"), 0), "a train not started is forecast to run as planned");
    }

    [Fact]
    public void Edits_are_marked_and_points_not_run_after_a_short_termination()
    {
        var (plan, _) = WorkingPlanTests.Plan().ApplyAll([new ShiftTrain(Fast, 1, 120), new ChangePlatform(Fast, 1, "3"), new TerminateAt(Fast, 2)]);

        var schedule = TrainSchedule.For(Snapshot(plan, "05:30"), Fast)!;

        var b = schedule.Row(1)!;
        b.TimesChanged.Should().BeTrue();
        b.PlatformChanged.Should().BeTrue();
        b.Platform.Should().Be("3");
        (b.PlanArrive, b.PlanDepart).Should().Be((S("06:07"), S("06:08")));
        schedule.Row(0)!.TimesChanged.Should().BeFalse();
        var c = schedule.Row(2)!;
        c.TimesChanged.Should().BeTrue("it now terminates there");
        (c.PlanArrive, c.PlanDepart, c.IsPass).Should().Be((S("06:12"), (SimTime?)null, false));
        schedule.Row(3)!.Runs.Should().BeFalse();
    }

    [Fact]
    public void A_running_train_shows_actuals_then_the_forecast()
    {
        var live = Running("901", "1A01", Dep("A", "06:02"), Arr("B", "06:07"));

        var schedule = TrainSchedule.For(Snapshot(WorkingPlanTests.Plan(), "06:07:20", live), Fast)!;

        schedule.Live!.SimUid.Should().Be("901");
        var a = schedule.Row(0)!;
        (a.ActualDepart, a.DelaySeconds).Should().Be((S("06:02"), 120));
        var b = schedule.Row(1)!;
        (b.ActualArrive, b.ActualDepart, b.ForecastDepart).Should().Be((S("06:07"), (SimTime?)null, S("06:07:30")), "it is at B: the departure is forecast after the 30 s minimum dwell");
        var d = schedule.Row(3)!;
        d.ActualArrive.Should().BeNull();
        d.ForecastArrive.Should().Be(S("06:16:30"));
        d.DelaySeconds.Should().Be(90);
    }

    [Fact]
    public void A_clone_has_no_booking_and_unknown_trains_no_schedule()
    {
        var (plan, _) = WorkingPlanTests.Plan().Apply(new CloneTrain(Fast, new Headcode("1A51"), 3600, new TrainId("1A51#c1")));
        var snapshot = Snapshot(plan, "05:30");

        var clone = TrainSchedule.For(snapshot, new TrainId("1A51#c1"))!;
        clone.Wtt.Should().BeNull();
        clone.Rows.Should().HaveCount(4).And.OnlyContain(r => r.WttDepart == null && r.WttArrive == null && r.TimesChanged);
        TrainSchedule.For(snapshot, new TrainId("9Z99")).Should().BeNull();
    }
}
