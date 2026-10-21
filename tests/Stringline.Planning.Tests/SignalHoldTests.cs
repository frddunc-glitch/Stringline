using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// S33 (Q12): holding a train at a signal between its timing points. 1A01 leaves A at 06:00, passes C at 06:10 (its
/// 10 min booked run is the minimum) and gets to D at 06:15; signals S2 and S3 are placed half and four fifths of the
/// way from A to C.
/// </summary>
public sealed class SignalHoldTests
{
    private static readonly TrainId Fast = new("1A01");
    private static readonly Tiploc A = new("A");
    private static readonly Tiploc C = new("C");

    private static readonly WorkingTimetable Tt = Of(Train("1A01", points: [Call("A", null, "06:00"), Pass("C", "06:10"), Call("D", "06:15", null)]));

    private static readonly Corridor Main = new(
        "main",
        "Main",
        [new CorridorLocation(A, 0), new CorridorLocation(C, 0.5), new CorridorLocation(new Tiploc("D"), 1)],
        [new Section(A, C, Section.DoubleLine, 180, 600), new Section(C, new Tiploc("D"), Section.DoubleLine, 180, 300)]);

    private static SignallingPlaces Places() => SignallingPlaces.Empty
        .SetByHand(SignallingKind.Signal, "S2", A, C, 0.5, RunKind.Starting)
        .SetByHand(SignallingKind.Signal, "S3", A, C, 0.8, RunKind.Starting);

    private static ForecastNetwork Network(SignallingPlaces? places = null) =>
        ForecastNetwork.Build([Main], Tt).WithPlaces(UsablePlaces.Of(places ?? Places()));

    private static SignalHold Hold(string signal, string until, double share = 0.5) => new(0, signal, A, C, share, share, SimTime.Parse(until));

    private static WorkingPlan Plan(params IPlanCommand[] commands)
    {
        var (plan, results) = WorkingPlan.FromTimetable(Tt).ApplyAll(commands);
        results.Should().OnlyContain(r => r.Applied);
        return plan;
    }

    /// <summary>1A01 (uid 10) with these reports; a location named S… is a signal passed.</summary>
    private static LiveTrain Train1A01(params (string Location, string Time, TrainLocationAction Action)[] actuals) =>
        new("10",
            [new LiveWorking(new Headcode("1A01"), 0, MatchStatus.Matched, Fast, MatchMethod.HeadcodeAndTime)],
            actuals.Select(a => new ActualEvent(new Tiploc(a.Location), a.Action, SimTime.Parse(a.Time), "", !a.Location.StartsWith('S'), 0, a.Location.StartsWith('S') ? 6 : null)).ToImmutableList(),
            false);

    private static Forecast Forecast(WorkingPlan plan, string? now = null, params LiveTrain[] live) =>
        Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = live,
            Now = now is null ? null : SimTime.Parse(now),
            Network = Network(),
            Options = new ForecastOptions { Interaction = false },
        });

    // ---- The command ----

    [Fact]
    public void The_commands_round_trip_as_json()
    {
        IPlanCommand[] commands = [new HoldAtSignal(Fast, Hold("S2", "06:08")), new ReleaseSignalHold(Fast, 0, "S2")];
        foreach (var command in commands)
        {
            var json = PlanCommandJson.Serialize(command);
            PlanCommandJson.Deserialize(json).Should().Be(command, json);
        }

        PlanCommandJson.Serialize(commands[0]).Should().Contain("\"type\":\"holdAtSignal\"").And.Contain("\"signal\":\"S2\",\"from\":\"A\",\"to\":\"C\"");
    }

    [Fact]
    public void A_hold_goes_into_the_plan_and_release_or_reset_takes_it_out()
    {
        var hold = new HoldAtSignal(Fast, Hold("S2", "06:08"));
        var plan = Plan(hold);

        plan.Get(Fast).SignalHolds.Should().Equal(Hold("S2", "06:08"));
        plan.Get(Fast).IsModified.Should().BeTrue();
        hold.Describe(plan).Should().Be("Hold 1A01 at S2 until 06:08 on from A");

        // The same signal again changes its time; another signal adds a hold.
        var later = Plan(hold, new HoldAtSignal(Fast, Hold(" s2 ", "06:09")), new HoldAtSignal(Fast, Hold("S3", "06:11", 0.8)));
        later.Get(Fast).SignalHolds.Select(h => (h.Signal, h.Until.ToString())).Should().Equal(("s2", "06:09:00"), ("S3", "06:11:00"));

        var released = Plan(hold, new ReleaseSignalHold(Fast, 0, "s2"));
        released.Get(Fast).SignalHolds.Should().BeEmpty();
        released.Get(Fast).IsModified.Should().BeFalse();
        new ReleaseSignalHold(Fast, 0, "S2").Describe(plan).Should().Be("Release 1A01 from its hold at S2");

        Plan(hold, new ResetTrain(Fast)).Get(Fast).SignalHolds.Should().BeEmpty();
    }

    [Fact]
    public void A_hold_that_cannot_be_made_is_refused()
    {
        var plan = WorkingPlan.FromTimetable(Tt);
        string? Error(IPlanCommand command) => plan.Apply(command).Result.Error;

        Error(new HoldAtSignal(Fast, Hold("", "06:08"))).Should().Be("A hold at a signal needs the signal's ID.");
        Error(new HoldAtSignal(Fast, Hold("S2", "06:08", share: 0))).Should().Be("S2 is not placed between two of 1A01's points.");
        Error(new HoldAtSignal(Fast, Hold("S2", "06:08") with { Index = 2 })).Should().Be("1A01 does not run on from D to a timed point.");
        Error(new HoldAtSignal(Fast, Hold("S2", "06:08") with { Index = 3 })).Should().Be("1A01 has no point 3.");
        Error(new ReleaseSignalHold(Fast, 0, "S2")).Should().Be("1A01 is not held at S2.");
        Plan(new HoldAtSignal(Fast, Hold("S2", "06:08"))).Apply(new HoldAtSignal(Fast, Hold("S2", "06:08"))).Result.Error.Should().Be("This makes no change to the plan.");
    }

    // ---- The forecast ----

    [Fact]
    public void The_forecast_stops_the_train_at_the_signal_until_its_time()
    {
        // Half way at 06:05, held until 06:08, then the other half (5 min): C at 06:13, and D 3 min late.
        var forecast = Forecast(Plan(new HoldAtSignal(Fast, Hold("S2", "06:08")))).Find(Fast)!;

        Times(forecast).Should().Equal("A -/06:00:00 F", "C pass 06:13:00 F", "D 06:18:00/- F");
        var held = forecast.SignalHolds.Should().ContainSingle().Subject;
        (held.Signal, held.ToIndex, held.Reaches, held.Until, held.Seconds).Should().Be(("S2", 1, SimTime.Parse("06:05"), SimTime.Parse("06:08"), 180));
        held.ToString().Should().Be("held at S2 06:05:00–06:08:00");
    }

    [Fact]
    public void Two_holds_on_a_run_are_timed_one_after_the_other()
    {
        // Held at S2 until 06:08, it gets to S3 3 min later (06:11), and is held there until 06:12: C at 06:14.
        var forecast = Forecast(Plan(new HoldAtSignal(Fast, Hold("S3", "06:12", 0.8)), new HoldAtSignal(Fast, Hold("S2", "06:08")))).Find(Fast)!;

        Times(forecast)[1].Should().Be("C pass 06:14:00 F");
        forecast.SignalHolds.Select(h => (h.Signal, h.Reaches.ToString())).Should().Equal(("S2", "06:05:00"), ("S3", "06:11:00"));
    }

    [Fact]
    public void A_train_on_its_way_is_held_from_where_it_was_last_known()
    {
        // Left A two minutes late and passed S2 at 06:06: C is forecast at 06:11, so it gets to S3 at 06:09 and waits
        // there until 06:12; the last fifth (2 min) brings it to C at 06:14.
        var live = Train1A01(Dep("A", "06:02"), Passed("S2", "06:06"));
        var forecast = Forecast(Plan(new HoldAtSignal(Fast, Hold("S3", "06:12", 0.8))), "06:06:10", live).Find(Fast)!;

        Times(forecast)[1].Should().Be("C pass 06:14:00 F");
        forecast.SignalHolds.Single().Reaches.Should().Be(SimTime.Parse("06:09"));
    }

    [Fact]
    public void A_hold_passed_already_or_reached_after_its_time_changes_nothing()
    {
        var live = Train1A01(Dep("A", "06:02"), Passed("S2", "06:06"));
        var passed = Forecast(Plan(new HoldAtSignal(Fast, Hold("S2", "06:15"))), "06:06:10", live).Find(Fast)!;
        Times(passed)[1].Should().Be("C pass 06:11:00 F");
        passed.SignalHolds.Should().BeEmpty();

        var early = Forecast(Plan(new HoldAtSignal(Fast, Hold("S2", "06:04")))).Find(Fast)!;
        Times(early)[1].Should().Be("C pass 06:10:00 F");
        early.SignalHolds.Should().BeEmpty();
    }

    // ---- Where a train can be held ----

    [Fact]
    public void The_signals_on_a_run_and_those_still_ahead_of_a_train_on_its_way()
    {
        var plan = WorkingPlan.FromTimetable(Tt);
        var network = Network();
        var run = SignalHolds.OnRun(plan.Get(Fast), 0, plan.MinRuns, network);
        run.Select(s => (s.Signal, s.Index, s.ToIndex, s.Share)).Should().Equal(("S2", 0, 1, 0.5), ("S3", 0, 1, 0.8));
        run[1].Hold(SimTime.Parse("06:12")).Should().Be(new SignalHold(0, "S3", A, C, 0.8, 0.8, SimTime.Parse("06:12")));
        SignalHolds.OnRun(plan.Get(Fast), 1, plan.MinRuns, network).Should().BeEmpty("none is placed between C and D");

        var forecast = Forecast(plan, "06:06:10", Train1A01(Dep("A", "06:02"), Passed("S2", "06:06"))).Find(Fast)!;
        SignalHolds.CurrentRun(forecast).Should().Be(0);
        var ahead = SignalHolds.Ahead(plan.Get(Fast), forecast, plan.MinRuns, network).Should().ContainSingle().Subject;
        ahead.Signal.Should().Be("S3");
        SignalHolds.Reaches(forecast, ahead).Should().Be(SimTime.Parse("06:09"));
        SignalHolds.Reaches(forecast, ahead, SimTime.Parse("06:10")).Should().Be(SimTime.Parse("06:10"), "it has not reported passing S3, so it is not there before now");

        SignalHolds.Ahead(plan.Get(Fast), Forecast(plan).Find(Fast)!, plan.MinRuns, network).Should().BeEmpty("it has not started");
    }

    [Fact]
    public void Signals_past_a_timing_location_the_plan_does_not_book_are_found_on_the_trains_own_line()
    {
        // 2B01 runs A–B–C, so B is 0.4 of the way from A to C by minimum runs; 1A01 does not book B. S5 and S6 are
        // learned half way from B to C (0.7 of 1A01's run): S6 is a parallel line's, as trains passing S2 go on to S5.
        var tt = Of(Tt.Trains[0], Train("2B01", points: [Call("A", null, "07:00"), Pass("B", "07:04"), Pass("C", "07:10")]));
        var b = new Tiploc("B");
        var places = Places().SetByHand(SignallingKind.Signal, "S5", b, C, 0.5).SetByHand(SignallingKind.Signal, "S6", b, C, 0.5);
        places = places.With(SignallingKind.Signal, [.. places.Signals.Select(p => p.Id == "S2" ? p with { Next = new Dictionary<string, int> { ["S5"] = 3 } } : p)]);
        var network = ForecastNetwork.Build([Main], tt).WithPlaces(UsablePlaces.Of(places));
        var plan = WorkingPlan.FromTimetable(tt);

        SignalHolds.OnRun(plan.Get(Fast), 0, plan.MinRuns, network).Select(s => (s.Signal, Math.Round(s.Share, 3))).Should().Equal(("S2", 0.5), ("S5", 0.7), ("S6", 0.7), ("S3", 0.8));

        var forecast = Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = [Train1A01(Dep("A", "06:02"), Passed("S2", "06:06"))],
            Now = SimTime.Parse("06:06:10"),
            Network = network,
            Options = new ForecastOptions { Interaction = false },
        }).Find(Fast)!;
        SignalHolds.Ahead(plan.Get(Fast), forecast, plan.MinRuns, network).Select(s => s.Signal).Should().Equal(["S5"], "S2's line goes on to S5 (S3 is not learned after it)");
    }

    [Fact]
    public void A_train_standing_at_the_signal_is_held_there_from_when_it_would_move()
    {
        // As in the S38 test: red at S3, 1A01 is standing there since 06:09, forecast to move now (06:11). Held there
        // until 06:15, the last fifth (2 min) brings it to C at 06:17.
        var places = Places();
        places = places.With(SignallingKind.Signal, [.. places.Signals.Select(p => p.Id == "S2" ? p with { Next = new Dictionary<string, int> { ["S3"] = 5 } } : p)]);
        var network = ForecastNetwork.Build([Main], Tt).WithPlaces(UsablePlaces.Of(places));
        var red = LineSideState.Empty.With(new SignallingStateEvent(DateTimeOffset.UnixEpoch, "test", SignallingObjectType.Signal, "signal", "S3", SimTime.Parse("06:05:30"), new Dictionary<string, string> { ["aspect"] = "0" }));
        Forecast Run(WorkingPlan plan) => Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = [Train1A01(Dep("A", "06:00"), Passed("S2", "06:06"))],
            Now = SimTime.Parse("06:11"),
            Network = network,
            LineSide = red,
            Options = new ForecastOptions { Interaction = false },
        });

        var free = WorkingPlan.FromTimetable(Tt);
        var standing = Run(free).Find(Fast)!;
        standing.Standing!.Signal.Should().Be("S3");
        var s3 = SignalHolds.Ahead(free.Get(Fast), standing, free.MinRuns, network).Should().ContainSingle().Subject;
        SignalHolds.Reaches(standing, s3, SimTime.Parse("06:11")).Should().Be(SimTime.Parse("06:11"));

        var held = Run(Plan(new HoldAtSignal(Fast, s3.Hold(SimTime.Parse("06:15"))))).Find(Fast)!;
        Times(held)[1].Should().Be("C pass 06:17:00 F");
        held.SignalHolds.Single().Reaches.Should().Be(SimTime.Parse("06:09"));
    }

    [Fact]
    public void A_signal_still_learning_is_not_offered()
    {
        // S4 has one sample: not used yet (S37), so no hold is offered there.
        var learning = new SignallingPlace { Id = "S4", From = A, To = C, Run = RunKind.Starting, Fraction = 0.6, Samples = [0.6], SampleCount = 1 };
        var network = Network(Places().With(SignallingKind.Signal, [.. Places().Signals, learning]));
        var plan = WorkingPlan.FromTimetable(Tt);

        SignalHolds.OnRun(plan.Get(Fast), 0, plan.MinRuns, network).Select(s => s.Signal).Should().Equal("S2", "S3");
    }

    [Fact]
    public void The_regulation_list_reminds_of_a_hold_at_the_next_location()
    {
        var plan = Plan(new HoldAtSignal(Fast, Hold("S2", "06:08")));
        var forecast = Forecast(plan);

        var row = RegulationList.For(plan, forecast, C, null).Should().ContainSingle().Subject;
        row.SignalHold.Should().Be(Hold("S2", "06:08"));
        row.IsChanged.Should().BeTrue();
        RegulationList.For(plan, forecast, A, null).Single().SignalHold.Should().BeNull();
    }
}
