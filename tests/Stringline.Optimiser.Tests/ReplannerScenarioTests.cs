using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Planning;
using Xunit.Abstractions;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Optimiser.Tests;

/// <summary>
/// Golden scenarios for re-planning a horizon (<c>06-…md</c> §5): the optimiser and the rules on the hand-written
/// corridors of the suggestion scenarios.
/// </summary>
public sealed class ReplannerScenarioTests(ITestOutputHelper output)
{
    private static readonly TrainId Fast = new("1F01");
    private static readonly TrainId Stopper = new("2S01");

    private static readonly ReplanOptions Deterministic = new() { Workers = 1, TimeLimitSeconds = 10 };

    private sealed record Case(WorkingPlan Plan, ForecastRequest Request, ConflictSet Conflicts)
    {
        public ReplanResult Replan(IReplanner replanner, ReplanOptions? options = null) =>
            replanner.Replan(new ReplanRequest(Request) { Options = options ?? Deterministic });

        /// <summary>The conflicts once a re-plan is applied.</summary>
        public ConflictSet After(ReplanResult result)
        {
            var (plan, applied) = Plan.Apply(result.ToCommand());
            applied.Applied.Should().BeTrue(applied.Error);
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

    private void Show(ReplanResult result)
    {
        output.WriteLine(result.ToString());
        foreach (var s in result.Steps)
        {
            output.WriteLine("  " + s);
        }
    }

    // ---- Double line with a loop at B (headway 3 min) ----

    private static WorkingTimetable LoopTimetable(string? fastCategory = null, string? stopperCategory = null) => Of(
        Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]) with { CategoryId = fastCategory },
        Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]) with { CategoryId = stopperCategory });

    private static ForecastNetwork LoopNetwork(WorkingTimetable timetable, IReadOnlyDictionary<string, int>? priorities = null) =>
        ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C")], timetable, priorities);

    [Fact]
    public void Optimiser_holds_the_late_train_at_its_origin()
    {
        // 1F01 has not started at 08:05 and is 2 min too close behind 2S01 out of the loop. Holding it at A until
        // 08:07 costs it 2 min; holding 2S01 in the loop instead costs 2S01 4 min.
        var timetable = LoopTimetable();
        var test = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));
        test.Conflicts.All.Should().ContainSingle();

        var result = test.Replan(new CpSatReplanner());
        Show(result);

        result.Outcome.Should().Be(ReplanOutcome.Improved);
        result.Commands.Should().Equal(new ShiftTrain(Fast, 0, 7 * 60));
        var step = result.Steps.Should().ContainSingle().Subject;
        (step.Kind, step.Train, step.Location, step.Until, step.HoldSeconds).Should().Be((SuggestionKind.Hold, Fast, new Tiploc("A"), SimTime.Parse("08:07"), 120));
        (result.Before.Count, result.After.Count, result.Cleared, result.Added).Should().Be((1, 0, 1, 0));
        result.WeightedDelaySeconds.Should().Be(0, "the knock-on forecast already has 1F01 2 min late");
        result.Changed.Should().Equal(Fast);
        result.Describe().Should().Be("Re-plan to 09:05: 1 hold");
        result.Optimal.Should().BeTrue();
        test.After(result).Count.Should().Be(0);
    }

    [Fact]
    public void Rules_hold_the_late_train_at_its_origin_too()
    {
        var timetable = LoopTimetable();
        var test = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));

        var result = test.Replan(new RuleReplanner());
        Show(result);

        result.Outcome.Should().Be(ReplanOutcome.Improved);
        result.Method.Should().Be("rules");
        result.Commands.Should().Equal(new ShiftTrain(Fast, 0, 7 * 60));
        result.Steps.Should().ContainSingle().Which.Describe().Should().Be("Hold 1F01 at A until 08:07 (+2 min)");
        result.After.Should().BeEmpty();
        test.After(result).Count.Should().Be(0);
    }

    public static TheoryData<string> Methods => new() { "optimiser", "rules" };

    private static IReplanner Replanner(string method) => method == "rules" ? new RuleReplanner() : new CpSatReplanner();

    [Theory]
    [MemberData(nameof(Methods))]
    public void Priorities_hold_the_slow_train_in_the_loop_instead(string method)
    {
        // 1F01 is an express (priority 90) and 2S01 a slow train (10): 2S01 waits in the loop for 1F01 to go by
        // (4 min to it at C, weighing 0.2 a minute) rather than 1F01 waiting 2 min at A (1.8 a minute).
        var timetable = LoopTimetable(fastCategory: "express", stopperCategory: "slow");
        var network = LoopNetwork(timetable, new Dictionary<string, int> { ["express"] = 90, ["slow"] = 10 });
        var test = Make(WorkingPlan.FromTimetable(timetable), network, "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));

        var result = test.Replan(Replanner(method));
        Show(result);

        result.Outcome.Should().Be(ReplanOutcome.Improved);
        result.Commands.Should().Equal(new SetDwell(Stopper, 1, SimTime.Parse("08:12")));
        result.Steps.Should().ContainSingle().Which.Train.Should().Be(Stopper);
        result.Impacts.Select(i => (i.Headcode.Value, i.DelaySeconds)).Should().BeEquivalentTo([("2S01", 240), ("1F01", -120)]);
        result.WeightedDelaySeconds.Should().Be((240 * 0.2) - (120 * 1.8));
        test.After(result).Count.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void Single_line_the_opposing_train_waits_in_the_crossing_loop(string method)
    {
        // 2D01 left A 10 min late and is on its way to B; 2U01 must wait in the loop at B for it, plus the minute
        // the WTT books between them.
        var timetable = Of(
            Train("2D01", points: [Call("A", null, "09:00"), Call("B", "09:10", "09:11"), Call("C", "09:21", null)]),
            Train("2U01", points: [Call("C", null, "09:00"), Call("B", "09:10", "09:11"), Call("A", "09:21", null)]));
        var network = ForecastNetwork.Build([Line("main", 120, single: true, "A", "B", "C")], timetable);
        var test = Make(WorkingPlan.FromTimetable(timetable), network, "09:10", Running("10", "2D01", Dep("A", "09:10")), Running("11", "2U01", Dep("C", "09:00")));
        test.Conflicts.All.Should().ContainSingle().Which.Kind.Should().Be(ConflictKind.SingleLine);

        var result = test.Replan(Replanner(method));
        Show(result);

        result.Outcome.Should().Be(ReplanOutcome.Improved);
        result.Commands.Should().Equal(new SetDwell(new TrainId("2U01"), 1, SimTime.Parse("09:21")));
        result.WeightedDelaySeconds.Should().Be(0, "the knock-on forecast already has it waiting");
        test.After(result).Count.Should().Be(0);
    }

    private static TimingPoint At(TimingPoint point, string platform) => point with { Platform = platform };

    private static Case PlatformCase()
    {
        // 2A01 is still in platform 1 at S when 2B01 is due. Neither can wait before S, as both have left A;
        // platform 2 (used later by 2C01) is free, and 3 is busy then.
        var timetable = Of(
            Train("2A01", points: [At(Call("A", null, "10:00"), "1"), At(Call("S", "10:05", "10:10"), "1"), Call("C", "10:20", null)]),
            Train("2B01", points: [At(Call("A", null, "10:02"), "2"), At(Call("S", "10:12", "10:14"), "1"), Call("C", "10:24", null)]),
            Train("2C01", points: [Call("C", null, "10:50"), At(Call("S", "11:00", "11:02"), "2"), Call("A", "11:07", null)]),
            Train("2E01", points: [Call("C", null, "09:50"), At(Call("S", "10:00", "10:20"), "3"), Call("A", "10:27", null)]));
        return Make(WorkingPlan.FromTimetable(timetable), now: "10:06:30", live:
            [Running("30", "2A01", Dep("A", "10:06:30")), Running("31", "2B01", Dep("A", "10:02")), Running("32", "2E01", Dep("C", "09:50"))]);
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void Platform_another_free_platform(string method)
    {
        var test = PlatformCase();
        test.Conflicts.All.Should().ContainSingle().Which.Kind.Should().Be(ConflictKind.Platform);

        var result = test.Replan(Replanner(method));
        Show(result);

        result.Outcome.Should().Be(ReplanOutcome.Improved);
        result.Commands.Should().Equal(new ChangePlatform(new TrainId("2B01"), 1, "2"));
        var step = result.Steps.Should().ContainSingle().Subject;
        (step.Kind, step.Platform).Should().Be((SuggestionKind.Platform, "2"));
        step.Describe().Should().Be("2B01 into platform 2 at S");
        (result.Penalty, result.PlatformChanges, result.Holds).Should().Be((120, 1, 0));
        result.Describe().Should().Be("Re-plan to 11:06½: 1 platform change");
        test.After(result).Count.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void Platform_changes_only_if_allowed(string method)
    {
        var test = PlatformCase();

        var result = test.Replan(Replanner(method), Deterministic with { AllowPlatformChanges = false });

        result.Outcome.Should().Be(ReplanOutcome.NoImprovement, "neither train can wait before the platform");
        result.Commands.Should().BeEmpty();
        result.After.Should().HaveCount(1);
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public void Nothing_to_do_without_conflicts_in_the_horizon(string method)
    {
        var timetable = LoopTimetable();
        var onTime = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "07:50");
        onTime.Conflicts.Count.Should().Be(0);
        onTime.Replan(Replanner(method)).Outcome.Should().Be(ReplanOutcome.NoConflicts);

        // 1F01 planned 5 min later clashes with 2S01 beyond a 10 min horizon from 07:56.
        var plan = WorkingPlan.FromTimetable(timetable).Apply(new ShiftTrain(Fast, 0, 5 * 60)).Plan;
        var late = Make(plan, LoopNetwork(timetable), "07:56", Running("11", "2S01", Dep("A", "07:55")));
        late.Conflicts.All.Should().ContainSingle().Which.When.Should().BeGreaterThan(SimTime.Parse("08:06"));
        var result = late.Replan(Replanner(method), Deterministic with { HorizonSeconds = 10 * 60 });
        (result.Outcome, result.From, result.Until).Should().Be((ReplanOutcome.NoConflicts, SimTime.Parse("07:56"), SimTime.Parse("08:06")));
    }

    [Fact]
    public void Several_conflicts_at_once()
    {
        // 1F01 and 1F02 have not started at 08:05: 1F01 is too close behind 2S01 out of the loop, and 1F02 (booked
        // 3 min behind 1F01) too close behind 1F01 from A. The optimiser clears both, for no more weighted delay
        // than the rules take one conflict at a time.
        var timetable = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]) with { CategoryId = "express" },
            Train("1F02", points: [Pass("A", "08:03"), Pass("B", "08:07"), Pass("C", "08:11")]) with { CategoryId = "express" },
            Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]) with { CategoryId = "slow" });
        var network = LoopNetwork(timetable, new Dictionary<string, int> { ["express"] = 75, ["slow"] = 25 });
        var test = Make(WorkingPlan.FromTimetable(timetable), network, "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));
        output.WriteLine(string.Join(Environment.NewLine, test.Conflicts.All));
        test.Conflicts.Count.Should().BeGreaterThanOrEqualTo(2);

        var optimised = test.Replan(new CpSatReplanner());
        var rules = test.Replan(new RuleReplanner());
        Show(optimised);
        Show(rules);

        optimised.Outcome.Should().Be(ReplanOutcome.Improved);
        optimised.After.Should().BeEmpty();
        test.After(optimised).Count.Should().Be(0);
        (optimised.WeightedDelaySeconds + optimised.Penalty).Should().BeLessThanOrEqualTo(rules.WeightedDelaySeconds + rules.Penalty + 0.5);
    }

    [Fact]
    public void Applying_a_re_plan_is_one_edit_described_by_it()
    {
        var timetable = LoopTimetable();
        var test = Make(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")));
        var result = test.Replan(new CpSatReplanner());

        var command = result.ToCommand();
        command.SuggestionId.Should().Be("replan:optimiser:" + SimTime.Parse("08:05").Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Commands.Should().Equal(result.Commands);
        command.Describe(test.Plan).Should().Be("Suggestion: Re-plan to 09:05: 1 hold");
    }
}
