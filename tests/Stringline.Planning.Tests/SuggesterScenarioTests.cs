using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network.Profiles;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// Golden scenarios for the suggestion engine (<c>06-…md</c> §4, §6): each kind of resolution on the hand-written
/// corridors of the conflict scenarios, with the hold and its cost worked out by hand.
/// </summary>
public sealed class SuggesterScenarioTests
{
    private static readonly TrainId Fast = new("1F01");
    private static readonly TrainId Stopper = new("2S01");

    private sealed record Case(WorkingPlan Plan, ForecastRequest Request, ConflictSet Conflicts)
    {
        public IReadOnlyList<Suggestion> Suggest(Conflict conflict, SuggestionOptions? options = null) =>
            Suggester.Suggest(new SuggestionRequest(Request, conflict) { Options = options ?? new SuggestionOptions() });

        /// <summary>The conflicts once a suggestion is applied.</summary>
        public ConflictSet After(Suggestion suggestion)
        {
            var (plan, result) = Plan.Apply(suggestion.ToCommand());
            result.Applied.Should().BeTrue(result.Error);
            var forecast = Forecaster.Run(Request with { Plan = plan });
            return ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = Request.Network });
        }
    }

    private static Case Make(WorkingPlan plan, ForecastNetwork? network = null, string? now = null, params LiveTrain[] live)
    {
        var request = new ForecastRequest(plan)
        {
            LiveTrains = live,
            Now = now is null ? null : SimTime.Parse(now),
            Network = network ?? ForecastNetwork.Empty,
        };
        var forecast = Forecaster.Run(request);
        return new Case(plan, request, ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = request.Network }));
    }

    private static WorkingPlan Applied(WorkingPlan plan, IPlanCommand command)
    {
        var (next, result) = plan.Apply(command);
        result.Applied.Should().BeTrue(result.Error);
        return next;
    }

    // ---- Double line with a loop at B (headway 3 min) ----

    private static WorkingTimetable LoopTimetable(string? fastCategory = null, string? stopperCategory = null) => Of(
        Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]) with { CategoryId = fastCategory },
        Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]) with { CategoryId = stopperCategory });

    private static ForecastNetwork LoopNetwork(WorkingTimetable timetable, IReadOnlyDictionary<string, int>? priorities = null) =>
        ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C")], timetable, priorities);

    [Fact]
    public void Headway_hold_the_late_train_at_its_origin()
    {
        // 1F01 has not started at 08:05, so it leaves A now, 5 min late, and is 2 min too close behind 2S01 out of
        // the loop (as in the conflict scenario). Held at A until 08:07 it follows at the headway. The forecast's
        // knock-on already has it 2 min later at C, so planning the hold costs nothing more.
        var timetable = LoopTimetable();
        var test = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));
        var conflict = test.Conflicts.All.Should().ContainSingle().Subject;
        (conflict.Kind, conflict.A, conflict.SeveritySeconds).Should().Be((ConflictKind.Headway, Fast, 120));

        var suggestions = test.Suggest(conflict);

        var hold = suggestions[0];
        (hold.Kind, hold.Train, hold.Location, hold.Index).Should().Be((SuggestionKind.Hold, Fast, new Tiploc("A"), 0));
        hold.Commands.Should().Equal(new ShiftTrain(Fast, 0, 7 * 60));
        (hold.Until, hold.HoldSeconds).Should().Be((SimTime.Parse("08:07"), 120));
        hold.Resolves.Should().BeTrue();
        (hold.Cleared, hold.Added).Should().Be((1, 0));
        hold.WeightedDelaySeconds.Should().Be(0);
        hold.Changed.Should().Equal(Fast);
        hold.Describe().Should().Be("Hold 1F01 at A until 08:07 (+2 min)");
        test.After(hold).Count.Should().Be(0);

        // Or 2S01 waits in the loop for 1F01 to go by (B 08:09, C 08:13). Late, it runs B–C at the fastest (4 min),
        // and must reach C the headway after 1F01 (08:16), so it leaves B at 08:12. That costs 2S01 4 min at C and
        // gains 1F01 its 2 min knock-on: +2 min.
        var swap = suggestions.Should().ContainSingle(s => s.Kind == SuggestionKind.Swap).Subject;
        (swap.Train, swap.Location, swap.Until, swap.HoldSeconds).Should().Be((Stopper, new Tiploc("B"), SimTime.Parse("08:12"), 360));
        swap.Commands.Should().Equal(new SetDwell(Stopper, 1, SimTime.Parse("08:12")));
        swap.Resolves.Should().BeTrue();
        swap.Impacts.Select(i => (i.Headcode.Value, i.DelaySeconds)).Should().BeEquivalentTo([("2S01", 240), ("1F01", -120)]);
        swap.WeightedDelaySeconds.Should().Be(120);
        swap.Describe().Should().Be("Hold 2S01 at B until 08:12 (+6 min) so 1F01 goes first");
        test.After(swap).Count.Should().Be(0);
    }

    [Fact]
    public void Headway_a_train_already_on_its_way_cannot_be_held_so_the_other_waits()
    {
        // 1F01 passed A 5 min late: it cannot wait anywhere before B–C (it passes B), so only the swap is left.
        var timetable = LoopTimetable();
        var test = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05",
            Running("10", "1F01", Passed("A", "08:05")), Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));

        var suggestion = test.Suggest(test.Conflicts.All.Single()).Should().ContainSingle().Subject;

        (suggestion.Kind, suggestion.Train, suggestion.Until).Should().Be((SuggestionKind.Swap, Stopper, SimTime.Parse("08:12")));
        suggestion.Resolves.Should().BeTrue();
    }

    [Fact]
    public void Headway_a_train_already_on_its_way_is_held_at_a_signal_before_the_loop()
    {
        // S33: as above, but with signal S12 placed three quarters of the way from A to B (hand-set, so used). 1F01
        // gets there at 08:08; held until 08:10 it passes B at 08:11 and follows 2S01 out of the loop at the headway.
        // The knock-on pass already has it 2 min later at C, so the hold costs nothing more than the swap's +2 min.
        var timetable = LoopTimetable();
        var places = SignallingPlaces.Empty.SetByHand(SignallingKind.Signal, "S12", new Tiploc("A"), new Tiploc("B"), 0.75);
        var network = LoopNetwork(timetable).WithPlaces(UsablePlaces.Of(places));
        var test = Make(WorkingPlan.FromTimetable(timetable), network, "08:05",
            Running("10", "1F01", Passed("A", "08:05")), Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));

        var suggestions = test.Suggest(test.Conflicts.All.Single());

        var hold = suggestions[0];
        (hold.Kind, hold.Train, hold.Signal, hold.Location, hold.Index).Should().Be((SuggestionKind.Hold, Fast, "S12", new Tiploc("B"), 0));
        hold.Commands.Should().Equal(new HoldAtSignal(Fast, new SignalHold(0, "S12", new Tiploc("A"), new Tiploc("B"), 0.75, 0.75, SimTime.Parse("08:10"))));
        (hold.Until, hold.HoldSeconds).Should().Be((SimTime.Parse("08:10"), 120));
        hold.Resolves.Should().BeTrue();
        hold.WeightedDelaySeconds.Should().Be(0);
        hold.Describe().Should().Be("Hold 1F01 at S12 before B until 08:10 (+2 min)");
        hold.Id.Should().EndWith("|Hold:1F01:0:S12");
        test.After(hold).Count.Should().Be(0);
        suggestions.Should().Contain(s => s.Kind == SuggestionKind.Swap);

        // A signal still learning (one sample) is not offered.
        var learning = places.With(SignallingKind.Signal, [places.Signals[0] with { HandSet = false, Samples = [0.75], SampleCount = 1 }]);
        var unsure = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable).WithPlaces(UsablePlaces.Of(learning)), "08:05",
            Running("10", "1F01", Passed("A", "08:05")), Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));
        unsure.Suggest(unsure.Conflicts.All.Single()).Should().ContainSingle().Which.Kind.Should().Be(SuggestionKind.Swap);
    }

    [Fact]
    public void Priorities_weight_the_delay()
    {
        Suggester.WeightOf(90).Should().Be(1.8);
        Suggester.WeightOf(50).Should().Be(1);
        Suggester.WeightOf(0).Should().Be(0.1);

        // 2S01 is an express here (priority 90): the swap's 4 min to it weighs 1.8 a minute.
        var timetable = LoopTimetable(fastCategory: "slow", stopperCategory: "express");
        var network = LoopNetwork(timetable, new Dictionary<string, int> { ["express"] = 90, ["slow"] = 25 });
        var test = Make(WorkingPlan.FromTimetable(timetable), network, "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));

        var swap = test.Suggest(test.Conflicts.All.Single()).Single(s => s.Kind == SuggestionKind.Swap);
        swap.Impacts.Select(i => (i.Headcode.Value, i.Weight)).Should().BeEquivalentTo([("2S01", 1.8), ("1F01", 0.5)]);
        swap.WeightedDelaySeconds.Should().Be((240 * 1.8) - (120 * 0.5));
    }

    // ---- Single line A–B–C with a crossing loop at B (headway 2 min) ----

    [Fact]
    public void Single_line_hold_the_opposing_train_in_the_crossing_loop()
    {
        var timetable = Of(
            Train("2D01", points: [Call("A", null, "09:00"), Call("B", "09:10", "09:11"), Call("C", "09:21", null)]),
            Train("2U01", points: [Call("C", null, "09:00"), Call("B", "09:10", "09:11"), Call("A", "09:21", null)]));
        var network = ForecastNetwork.Build([Line("main", 120, single: true, "A", "B", "C")], timetable);
        var test = Make(WorkingPlan.FromTimetable(timetable), network, "09:10", Running("10", "2D01", Dep("A", "09:10")), Running("11", "2U01", Dep("C", "09:00")));
        var conflict = test.Conflicts.All.Single();

        // 2U01 waits in the loop until 2D01 is in, plus the booked minute: 10 min, as the conflict says. 2D01 is on
        // its way into the section, so it cannot be the one to wait.
        var hold = test.Suggest(conflict).Should().ContainSingle().Subject;
        (hold.Kind, hold.Train, hold.Location).Should().Be((SuggestionKind.Hold, new TrainId("2U01"), new Tiploc("B")));
        hold.Commands.Should().Equal(new SetDwell(new TrainId("2U01"), 1, SimTime.Parse("09:21")));
        hold.Resolves.Should().BeTrue();
        hold.WeightedDelaySeconds.Should().Be(0, "the knock-on forecast already has it waiting");
        test.After(hold).Count.Should().Be(0);
    }

    // ---- Platforms ----

    private static TimingPoint At(TimingPoint point, string platform) => point with { Platform = platform };

    [Fact]
    public void Platform_another_free_platform()
    {
        // 2A01 is still in platform 1 at S when 2B01 is due (the conflict scenario). Neither can wait before S, as
        // both have left A; platform 2 (used later by 2C01) is free, and 3 is busy then.
        var timetable = Of(
            Train("2A01", points: [At(Call("A", null, "10:00"), "1"), At(Call("S", "10:05", "10:10"), "1"), Call("C", "10:20", null)]),
            Train("2B01", points: [At(Call("A", null, "10:02"), "2"), At(Call("S", "10:12", "10:14"), "1"), Call("C", "10:24", null)]),
            Train("2C01", points: [Call("C", null, "10:50"), At(Call("S", "11:00", "11:02"), "2"), Call("A", "11:07", null)]),
            Train("2E01", points: [Call("C", null, "09:50"), At(Call("S", "10:00", "10:20"), "3"), Call("A", "10:27", null)]));
        var test = Make(WorkingPlan.FromTimetable(timetable), now: "10:06:30", live:
            [Running("30", "2A01", Dep("A", "10:06:30")), Running("31", "2B01", Dep("A", "10:02")), Running("32", "2E01", Dep("C", "09:50"))]);
        var conflict = test.Conflicts.All.Single();
        (conflict.Kind, conflict.A).Should().Be((ConflictKind.Platform, new TrainId("2B01")));

        var change = test.Suggest(conflict).Should().ContainSingle().Subject;

        (change.Kind, change.Platform, change.Location).Should().Be((SuggestionKind.Platform, "2", new Tiploc("S")));
        change.Commands.Should().Equal(new ChangePlatform(new TrainId("2B01"), 1, "2"));
        change.Penalty.Should().Be(120);
        change.Score.Should().Be(120);
        change.Resolves.Should().BeTrue();
        change.Describe().Should().Be("2B01 into platform 2 at S");
        test.After(change).Count.Should().Be(0);
    }

    [Fact]
    public void Platform_hold_before_the_platform_and_its_next_working_moves_with_a_platform_change()
    {
        // 2Z01 is booked into platform 1 at S at 10:22, but 2A02 (formed from 2A01 there) is held in it to 10:22.
        var timetable = Of(
            Train("2A01", points: [Call("A", null, "10:00"), At(Call("S", "10:05", null, Next("2A02")), "1")]),
            Train("2A02", formedFrom: new TrainId("2A01"), points: [At(Call("S", null, "10:20"), "1"), Call("A", "10:25", null)]),
            Train("2Z01", points: [Call("C", null, "10:15"), At(Call("S", "10:22", "10:23"), "1"), Call("A", "10:28", null)]),
            Train("2Y01", points: [Call("C", null, "11:00"), At(Call("S", "11:05", "11:06"), "2"), Call("A", "11:12", null)]));
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new ShiftTrain(new TrainId("2A02"), 0, 120));
        var test = Make(plan);
        var conflict = test.Conflicts.All.Single();
        (conflict.A, conflict.B).Should().Be((new TrainId("2Z01"), (TrainId?)new TrainId("2A02")));

        var suggestions = test.Suggest(conflict);

        // 2Z01 waits at its origin C for the platform: a minute after 2A02 leaves at 10:22, so it leaves C at 10:16.
        var hold = suggestions.Single(s => s.Kind == SuggestionKind.Hold);
        hold.Commands.Should().Equal(new ShiftTrain(new TrainId("2Z01"), 0, 60));
        test.After(hold).Count.Should().Be(0);

        // Swapping would hold 2A02, but it is in the platform already (formed there).
        var swap = suggestions.SingleOrDefault(s => s.Kind == SuggestionKind.Swap);
        swap.Should().BeNull("2A02 is formed in the platform and cannot be held before it");

        // A platform change of 2Z01 into 2 is the other way round: 2Z01 alone moves.
        var change = suggestions.Single(s => s.Kind == SuggestionKind.Platform);
        change.Commands.Should().Equal(new ChangePlatform(new TrainId("2Z01"), 1, "2"));
        suggestions[0].Should().BeSameAs(hold, "the hold costs a minute; the platform change costs two");
    }

    [Fact]
    public void Platform_change_takes_the_next_working_formed_in_the_platform_with_it()
    {
        // 2A01 is due into platform 1 at S, where it forms 2A02, while 2Z01 is still there.
        var timetable = Of(
            Train("2Z01", points: [Call("C", null, "09:55"), At(Call("S", "10:00", "10:10"), "1"), Call("A", "10:15", null)]),
            Train("2A01", points: [Call("A", null, "10:00"), At(Call("S", "10:12", null, Next("2A02")), "1")]),
            Train("2A02", formedFrom: new TrainId("2A01"), points: [At(Call("S", null, "10:30"), "1"), Call("A", "10:35", null)]),
            Train("2Y01", points: [Call("C", null, "11:00"), At(Call("S", "11:05", "11:06"), "2"), Call("A", "11:12", null)]));
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new SetDwell(new TrainId("2Z01"), 1, SimTime.Parse("10:15")));
        var test = Make(plan);
        var conflict = test.Conflicts.All.Single(c => c.A == new TrainId("2A01"));

        var change = test.Suggest(conflict).Single(s => s.Kind == SuggestionKind.Platform);

        change.Commands.Should().Equal(new ChangePlatform(new TrainId("2A01"), 1, "2"), new ChangePlatform(new TrainId("2A02"), 0, "2"));
        change.Changed.Should().BeEquivalentTo([new TrainId("2A01"), new TrainId("2A02")]);
        test.After(change).Count.Should().Be(0);
    }

    [Fact]
    public void Platform_change_of_a_train_formed_there_takes_the_train_it_is_formed_from_with_it()
    {
        // 2A02 is formed in platform 1 at S from 2A01, which arrives while 2Z01 is still there. The conflict is the
        // next working's (its stay starts when 2A01 arrives); moving it means bringing 2A01 into the other platform.
        var timetable = Of(
            Train("2Z01", points: [Call("C", null, "09:55"), At(Call("S", "10:00", "10:10"), "1"), Call("A", "10:15", null)]),
            Train("2A01", points: [Call("A", null, "10:00"), At(Call("S", "10:12", null, Next("2A02")), "1")]),
            Train("2A02", formedFrom: new TrainId("2A01"), points: [At(Call("S", null, "10:30"), "1"), Call("A", "10:35", null)]),
            Train("2Y01", points: [Call("C", null, "11:00"), At(Call("S", "11:05", "11:06"), "2"), Call("A", "11:12", null)]));
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new SetDwell(new TrainId("2Z01"), 1, SimTime.Parse("10:15")));
        var test = Make(plan);
        var conflict = test.Conflicts.All.Single(c => c.A == new TrainId("2A02"));

        var change = test.Suggest(conflict).Single(s => s.Kind == SuggestionKind.Platform);

        change.Commands.Should().Equal(new ChangePlatform(new TrainId("2A01"), 1, "2"), new ChangePlatform(new TrainId("2A02"), 0, "2"));
        change.Resolves.Should().BeTrue();
        test.After(change).Count.Should().Be(0);
    }

    // ---- Blocks ----

    private static Disruption Block(string from, string to, string start, string end, string? track = null) =>
        new("B1", DisruptionKind.Block, new Tiploc(from), new Tiploc(to), track, SimTime.Parse(start), SimTime.Parse(end));

    [Fact]
    public void Block_plan_the_hold_and_terminate_or_cancel_only_if_allowed()
    {
        var plan = Applied(WorkingPlan.FromTimetable(LoopTimetable()), new AddDisruption(Block("B", "C", "08:00", "08:30")));
        var test = Make(plan);
        var stopper = test.Conflicts.All.Single(c => c.A == Stopper);

        // 2S01 waits in the loop until the block ends: planning that clears the conflict at no further cost.
        var hold = test.Suggest(stopper).Should().ContainSingle().Subject;
        hold.Commands.Should().Equal(new SetDwell(Stopper, 1, SimTime.Parse("08:30")));
        (hold.Resolves, hold.WeightedDelaySeconds).Should().Be((true, 0));
        hold.Describe().Should().Be("Hold 2S01 at B until 08:30 (+24 min)");

        var allowed = test.Suggest(stopper, new SuggestionOptions { AllowCancellation = true });
        allowed.Select(s => s.Kind).Should().Equal(SuggestionKind.Hold, SuggestionKind.TerminateShort, SuggestionKind.Cancel);
        allowed[1].Commands.Should().Equal(new TerminateAt(Stopper, 1));
        allowed[1].Describe().Should().Be("Terminate 2S01 short at B");
        allowed[2].Commands.Should().Equal(new CancelTrain(Stopper));

        // 1F01 passes B, so it waits at its origin.
        var fast = test.Suggest(test.Conflicts.All.Single(c => c.A == Fast)).Single();
        fast.Commands.Should().Equal(new ShiftTrain(Fast, 0, 26 * 60));
        test.After(fast).All.Should().ContainSingle(c => c.A == Stopper);
    }

    [Fact]
    public void Block_on_one_line_another_line_out_of_the_location()
    {
        // The block is on the UF line B–C; 1F01 is planned onto it, and the US line is used out of B too.
        var timetable = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04") with { Line = "UF" }, Pass("C", "08:08")]),
            Train("2S01", points: [Call("A", null, "07:00"), Call("B", "07:04", "07:05") with { Line = "US" }, Call("C", "07:10", null)]));
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new AddDisruption(Block("B", "C", "08:00", "08:30", track: "UF")));
        var test = Make(plan);

        var suggestions = test.Suggest(test.Conflicts.All.Single());

        var line = suggestions.Should().ContainSingle(s => s.Kind == SuggestionKind.Line).Subject;
        line.Commands.Should().Equal(new ChangeLine(Fast, 1, "US"));
        (line.Line, line.Penalty, line.Resolves).Should().Be(("US", 120.0, true));
        line.Describe().Should().Be("1F01 on the US line out of B");
        suggestions[0].Should().BeSameAs(line, "it costs 2 weighted minutes; holding 1F01 costs 26");
        test.After(line).Count.Should().Be(0);
    }

    // ---- Run times ----

    [Fact]
    public void Unrealistic_retime_from_the_end_of_the_run()
    {
        var restriction = new Disruption("SR1", DisruptionKind.SpeedRestriction, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("07:00"), SimTime.Parse("09:00"), RunTimeFactor: 2);
        var plan = Applied(WorkingPlan.FromTimetable(LoopTimetable()), new AddDisruption(restriction));
        var test = Make(plan);

        var retime = test.Suggest(test.Conflicts.All.Single(c => c.A == Fast)).Should().ContainSingle().Subject;

        retime.Commands.Should().Equal(new ShiftTrain(Fast, 1, 240));
        retime.Describe().Should().Be("Retime 1F01 from B by +4 min");
        retime.Resolves.Should().BeTrue();
        test.After(retime).All.Should().NotContain(c => c.A == Fast);
    }

    // ---- Applying ----

    [Fact]
    public void Accepting_a_suggestion_is_one_edit_described_by_it()
    {
        var timetable = LoopTimetable();
        var test = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));
        var hold = test.Suggest(test.Conflicts.All.Single())[0];

        var command = hold.ToCommand();
        command.SuggestionId.Should().Be(hold.Id);
        command.Describe(test.Plan).Should().Be("Suggestion: Hold 1F01 at A until 08:07 (+2 min)");
        var (plan, result) = test.Plan.Apply(command);
        result.Applied.Should().BeTrue();
        plan.Get(Fast).SameAs(hold.Plan!.Get(Fast)).Should().BeTrue("the preview is what is applied");

        var json = PlanCommandJson.Serialize(command);
        PlanCommandJson.Deserialize(json).Should().BeEquivalentTo(command, o => o.RespectingRuntimeTypes());
    }

    [Fact]
    public void Suggestions_are_deterministic_and_best_first()
    {
        var timetable = LoopTimetable();
        var test = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));
        var conflict = test.Conflicts.All.Single();

        var a = test.Suggest(conflict);
        var b = test.Suggest(conflict);

        a.Select(s => (s.Id, s.Score)).Should().Equal(b.Select(s => (s.Id, s.Score)));
        a.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        a.Select(s => s.Score).Should().BeInAscendingOrder();
        test.Suggest(conflict, new SuggestionOptions { MaxSuggestions = 1 }).Should().ContainSingle().Which.Id.Should().Be(a[0].Id);
    }
}
