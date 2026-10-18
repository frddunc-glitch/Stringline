using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Live;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// The forecaster's rules for one train at a time (<c>06-…md</c> §2 steps 1–3, 5) on the A–B–C–D timetable of
/// <see cref="WorkingPlanTests"/>: 1A01 runs at the minimums (A–B 5 min, B–C 4 min, C–D 5 min); 2B01 has a minute's
/// slack on A–B and C–D.
/// </summary>
public sealed class ForecasterTests
{
    private static readonly TrainId Fast = new("1A01");
    private static readonly TrainId Slow = new("2B01");

    private static WorkingPlan Plan() => WorkingPlanTests.Plan();

    private static WorkingPlan Applied(WorkingPlan plan, IPlanCommand command)
    {
        var (after, result) = plan.Apply(command);
        result.Applied.Should().BeTrue(result.Error);
        return after;
    }

    [Fact]
    public void With_nothing_running_the_forecast_is_the_plan_and_nothing_is_drawn()
    {
        var forecast = Run(Plan());

        forecast.Trains.Select(t => t.Train).Should().Equal(Fast, Slow);
        forecast.Trains.Should().OnlyContain(t => t.Status == TrainForecastStatus.NotStarted && !t.Deviates && t.KnockOnSeconds == 0);
        Times(forecast.Find(Slow)!).Should().Equal("A -/07:00:00 F", "B 07:06:00/07:07:00 F", "C pass 07:11:00 F", "D 07:17:00/- F");
        forecast.Find(Slow)!.FinalDelaySeconds.Should().Be(0);
        forecast.Drawn.Should().BeEmpty();
        forecast.Now.Should().BeNull();
    }

    [Fact]
    public void A_late_train_runs_at_the_minimums_until_it_is_back_on_time()
    {
        var forecast = Run(Plan(), "07:03:30", live: Running("10", "2B01", Dep("A", "07:03")));

        var slow = forecast.Find(Slow)!;
        slow.Status.Should().Be(TrainForecastStatus.Running);
        slow.LiveUid.Should().Be("10");
        Times(slow).Should().Equal("A -/07:03:00 A", "B 07:08:00/07:08:30 F", "C pass 07:12:30 F", "D 07:17:30/- F");
        slow.Points.Select(p => p.DelaySeconds).Should().Equal(180, 90, 90, 30);
        slow.FinalDelaySeconds.Should().Be(30);
        slow.FirstForecast.Should().Be(1);
        forecast.Drawn.Select(t => t.Train).Should().Contain(Slow);
    }

    [Fact]
    public void A_train_waiting_at_a_stop_leaves_after_the_dwell_and_not_before_its_time()
    {
        var onTime = Run(Plan(), "06:05:10", live: Running("10", "1A01", Dep("A", "06:00"), Arr("B", "06:05")));
        Times(onTime.Find(Fast)!).Should().Equal("A -/06:00:00 A", "B 06:05:00/06:06:00 R", "C pass 06:10:00 F", "D 06:15:00/- F");
        onTime.Find(Fast)!.Deviates.Should().BeFalse();
        onTime.Drawn.Select(t => t.Train).Should().Equal([Fast], "a running train is drawn even on time");

        var late = Run(Plan(), "06:07:10", live: Running("10", "1A01", Dep("A", "06:02"), Arr("B", "06:07")));
        Times(late.Find(Fast)!).Should().Equal("A -/06:02:00 A", "B 06:07:00/06:07:30 R", "C pass 06:11:30 F", "D 06:16:30/- F");
    }

    [Fact]
    public void A_train_that_left_a_stop_early_keeps_running_early_where_it_passes()
    {
        var forecast = Run(Plan(), "06:05:40", live: Running("10", "1A01", Dep("A", "06:00"), Arr("B", "06:05"), Dep("B", "06:05:30")));

        Times(forecast.Find(Fast)!).Should().Equal("A -/06:00:00 A", "B 06:05:00/06:05:30 A", "C pass 06:09:30 F", "D 06:14:30/- F");
        forecast.Find(Fast)!.FinalDelaySeconds.Should().Be(-30);
    }

    [Fact]
    public void Reported_passes_keep_their_one_time()
    {
        var forecast = Run(Plan(), "07:11:10", live: Running("10", "2B01", Dep("A", "07:00"), Arr("B", "07:06"), Dep("B", "07:07"), Passed("C", "07:11")));

        Times(forecast.Find(Slow)!).Should().Equal("A -/07:00:00 A", "B 07:06:00/07:07:00 A", "C pass 07:11:00 A", "D 07:17:00/- F");
    }

    [Fact]
    public void Reports_at_locations_the_plan_does_not_book_are_skipped()
    {
        var forecast = Run(Plan(), "07:03:30", live: Running("10", "2B01", Dep("A", "07:00"), Passed("X", "07:02")));

        Times(forecast.Find(Slow)!)[0].Should().Be("A -/07:00:00 A");
        forecast.Find(Slow)!.FinalDelaySeconds.Should().Be(0);
    }

    [Fact]
    public void No_forecast_time_is_before_now_and_a_long_overdue_train_is_possibly_stopped()
    {
        var overdue = Run(Plan(), "07:12", live: Running("10", "2B01", Dep("A", "07:00")));
        var slow = overdue.Find(Slow)!;
        Times(slow)[1].Should().Be("B 07:12:00/07:12:30 F");
        slow.PossiblyStopped.Should().BeTrue("it was due at B at 07:06, six minutes ago");

        var late = Run(Plan(), "07:10", live: Running("10", "2B01", Dep("A", "07:00")));
        Times(late.Find(Slow)!)[1].Should().Be("B 07:10:00/07:10:30 F");
        late.Find(Slow)!.PossiblyStopped.Should().BeFalse("four minutes overdue is not yet five");
    }

    [Fact]
    public void A_report_at_a_signal_shows_the_train_is_moving()
    {
        var live = Running("10", "2B01", Dep("A", "07:00")) is var t
            ? t with { Actuals = t.Actuals.Add(new ActualEvent(new Tiploc("S123"), Core.Events.TrainLocationAction.Pass, SimTime.Parse("07:10"), "", false, 0)) }
            : null!;

        var forecast = Run(Plan(), "07:12", live: live);

        forecast.Find(Slow)!.PossiblyStopped.Should().BeFalse("it passed a signal two minutes ago");
    }

    [Fact]
    public void A_train_not_seen_after_its_start_time_starts_now_and_trains_over_before_the_session_are_left_out()
    {
        var forecast = Forecaster.Run(new ForecastRequest(Plan()) { Now = SimTime.Parse("07:02"), SessionStart = SimTime.Parse("06:30") });

        forecast.Trains.Select(t => t.Train).Should().Equal([Slow], "1A01 was over by 06:15");
        var slow = forecast.Find(Slow)!;
        slow.Status.Should().Be(TrainForecastStatus.NotStarted);
        Times(slow).Should().Equal("A -/07:02:00 F", "B 07:07:00/07:07:30 F", "C pass 07:11:30 F", "D 07:17:00/- F");
        forecast.Drawn.Select(t => t.Train).Should().Equal([Slow], "it deviates from the plan");

        var noSession = Forecaster.Run(new ForecastRequest(Plan()) { Now = SimTime.Parse("07:02") });
        Times(noSession.Find(Fast)!)[0].Should().Be("A -/07:02:00 F");
    }

    [Fact]
    public void The_sims_entry_delay_moves_a_train_not_yet_entered()
    {
        var forecast = Forecaster.Run(new ForecastRequest(Plan())
        {
            Now = SimTime.Parse("06:00"),
            EntryDelays = new Dictionary<Headcode, int> { [new Headcode("2B01")] = 120 },
        });

        Times(forecast.Find(Slow)!)[0].Should().Be("A -/07:02:00 F");
        Times(forecast.Find(Fast)!)[0].Should().Be("A -/06:00:00 F");
    }

    [Fact]
    public void Cancelled_trains_are_left_out_and_a_train_terminated_short_ends_there()
    {
        var plan = Applied(Applied(Plan(), new CancelTrain(Fast)), new TerminateAt(Slow, 2));

        var forecast = Run(plan);

        forecast.Trains.Select(t => t.Train).Should().Equal(Slow);
        Times(forecast.Find(Slow)!).Should().Equal("A -/07:00:00 F", "B 07:06:00/07:07:00 F", "C 07:11:00/- F");
    }

    [Fact]
    public void A_speed_restriction_in_force_lengthens_the_run()
    {
        var plan = Applied(Plan(), new AddDisruption(new Disruption("SR1", DisruptionKind.SpeedRestriction, new Tiploc("A"), new Tiploc("B"), null,
            SimTime.Parse("06:30"), SimTime.Parse("08:00"), RunTimeFactor: 2)));

        var forecast = Run(plan);

        Times(forecast.Find(Slow)!).Should().Equal("A -/07:00:00 F", "B 07:10:00/07:10:30 F", "C pass 07:14:30 F", "D 07:19:30/- F");
        Times(forecast.Find(Fast)!)[1].Should().Be("B 06:05:00/06:06:00 F", "06:00 is before the restriction");
    }

    [Fact]
    public void A_block_holds_the_train_until_it_ends()
    {
        var plan = Applied(Plan(), new AddDisruption(new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), null,
            SimTime.Parse("07:00"), SimTime.Parse("07:20"))));

        var forecast = Run(plan);

        Times(forecast.Find(Slow)!).Should().Equal("A -/07:20:00 F", "B 07:25:00/07:25:30 F", "C pass 07:29:30 F", "D 07:34:30/- F");
        Times(forecast.Find(Fast)!)[0].Should().Be("A -/06:00:00 F");
    }

    [Fact]
    public void A_block_on_another_line_does_not_hold_the_train()
    {
        var plan = Applied(Plan(), new AddDisruption(new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), "Up",
            SimTime.Parse("07:00"), SimTime.Parse("07:20"))));

        Times(Run(plan).Find(Slow)!)[0].Should().Be("A -/07:00:00 F");
    }

    // Trains formed from others

    private static WorkingPlan TurnPlan() => WorkingPlan.FromTimetable(Of(
        Train("1X01", points: [Call("A", null, "08:00"), Call("D", "08:15", null, Next("1Y01"))]),
        Train("1Y01", formedFrom: new TrainId("1X01"), points: [Call("D", null, "08:20"), Call("A", "08:35", null)])));

    [Fact]
    public void A_next_working_waits_for_its_train_to_arrive_and_turn_round()
    {
        var onTime = Run(TurnPlan());
        Times(onTime.Find(new TrainId("1Y01"))!)[0].Should().Be("D -/08:20:00 F");

        var late = Run(TurnPlan(), "08:10", live: Running("10", "1X01", Dep("A", "08:10")));
        Times(late.Find(new TrainId("1X01"))!)[1].Should().Be("D 08:25:00/- F");
        Times(late.Find(new TrainId("1Y01"))!).Should().Equal("D -/08:25:30 F", "A 08:40:30/- F");
        late.Drawn.Select(t => t.Headcode.Value).Should().Equal("1X01", "1Y01");
    }

    [Fact]
    public void A_next_working_of_a_train_already_arrived_leaves_after_the_turnaround()
    {
        var forecast = Run(TurnPlan(), "08:22:10", live: Running("10", "1X01", Dep("A", "08:07"), Arr("D", "08:22")));

        forecast.Find(new TrainId("1X01"))!.Status.Should().Be(TrainForecastStatus.Finished);
        Times(forecast.Find(new TrainId("1Y01"))!)[0].Should().Be("D -/08:22:30 F");
    }

    [Fact]
    public void A_train_that_has_become_its_next_working_is_finished()
    {
        var live = new LiveTrain("10",
            [
                new LiveWorking(new Headcode("1X01"), 0, MatchStatus.Matched, new TrainId("1X01")),
                new LiveWorking(new Headcode("1Y01"), 1, MatchStatus.Matched, new TrainId("1Y01")),
            ],
            [
                new ActualEvent(new Tiploc("A"), Core.Events.TrainLocationAction.Depart, SimTime.Parse("08:00"), "", true, 0),
                new ActualEvent(new Tiploc("D"), Core.Events.TrainLocationAction.Depart, SimTime.Parse("08:21"), "", true, 1),
            ],
            false);

        var forecast = Run(TurnPlan(), "08:21:10", live: live);

        var x = forecast.Find(new TrainId("1X01"))!;
        x.Status.Should().Be(TrainForecastStatus.Finished);
        Times(x).Should().Equal("A -/08:00:00 A", "D -/- -");
        Times(forecast.Find(new TrainId("1Y01"))!).Should().Equal("D -/08:21:00 A", "A 08:36:00/- F");
    }

    [Fact]
    public void Trains_at_their_last_point_are_finished_and_not_drawn()
    {
        var forecast = Run(Plan(), "06:15:10", live: Running("10", "1A01", Dep("A", "06:00"), Arr("B", "06:05"), Dep("B", "06:06"), Passed("C", "06:10"), Arr("D", "06:15")));

        forecast.Find(Fast)!.Status.Should().Be(TrainForecastStatus.Finished);
        forecast.Drawn.Should().NotContain(t => t.Train == Fast);
    }

    [Fact]
    public void The_same_inputs_give_the_same_forecast()
    {
        var live = Running("10", "2B01", Dep("A", "07:03"));
        var a = Run(Plan(), "07:03:30", live: live);
        var b = Run(Plan(), "07:03:30", live: live);

        a.Trains.Select(Times).Should().BeEquivalentTo(b.Trains.Select(Times), o => o.WithStrictOrdering());
    }

    [Fact]
    public void The_snapshot_request_takes_the_plan_live_trains_clock_and_entry_delays()
    {
        var snapshot = new PlanSnapshot(1, null, [Running("10", "2B01", Dep("A", "07:03"))],
            SimClockReading.None with { HasTime = true, LastReported = SimTime.Parse("07:03:30") },
            new Timetable.Live.MatchSummary(1, 1, 1, 0, 0, new Dictionary<MatchMethod, int>()), 1, SimTime.Parse("06:30"), Plan(),
            EntryDelays: new Dictionary<Headcode, int> { [new Headcode("1A01")] = 60 }.ToImmutableDictionary());

        var request = ForecastRequest.For(snapshot);

        request.Now.Should().Be(SimTime.Parse("07:03:30"));
        request.SessionStart.Should().Be(SimTime.Parse("06:30"));
        request.LiveTrains.Should().ContainSingle();
        request.EntryDelays.Should().ContainKey(new Headcode("1A01"));
        Forecaster.Run(request).Find(Slow)!.FinalDelaySeconds.Should().Be(30);
    }
}
