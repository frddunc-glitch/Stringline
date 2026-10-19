using System.Text.Json;
using Stringline.Core;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>The minimum run table, disruption coverage, command JSON and determinism.</summary>
public sealed class PlanModelTests
{
    private static readonly TrainId Fast = new("1A01");
    private static readonly TrainId Slow = new("2B01");

    private static PlannedPoint[] Points(params string[] locations) =>
        locations.Select((l, i) => new PlannedPoint(i, new Tiploc(l), null, SimTime.Parse("06:00") + (i * 60), false, null, null)).ToArray();

    // MinRunTable

    [Fact]
    public void Minimum_runs_are_the_fastest_booked_either_way_less_allowances()
    {
        var timetable = Of(
            Train("1A01", points: [Call("A", null, "06:00"), Call("B", "06:05", null)]),
            Train("1A02", points: [Call("B", null, "07:00"), Call("A", "07:04", null)]),
            Train("1A03", points: [Call("A", null, "08:00") with { PerformanceAllowanceSeconds = 90 }, Call("B", "08:05", null)]));

        var table = MinRunTable.Build(timetable);

        table.Between(new Tiploc("A"), new Tiploc("B")).Should().Be(210, "05:00 less a 1½ min allowance beats 4 min the other way");
        table.Between(new Tiploc("B"), new Tiploc("A")).Should().Be(210);
        table.Between(new Tiploc("A"), new Tiploc("A")).Should().Be(0);
        table.Between(new Tiploc("A"), new Tiploc("C")).Should().BeNull();
        table.Count.Should().Be(1);
    }

    [Fact]
    public void Runs_with_no_time_or_no_length_are_ignored()
    {
        var timetable = Of(
            Train("1A01", points: [Call("A", null, "06:00"), Call("X", null, null), Call("B", "06:05", null)]),
            Train("1A02", points: [Call("A", null, "06:00"), Pass("B", "06:00")]));

        MinRunTable.Build(timetable).Count.Should().Be(0);
    }

    [Fact]
    public void A_minimum_along_a_train_sums_its_steps_or_falls_back_to_the_direct_pair()
    {
        var timetable = Of(
            Train("1A01", points: [Call("A", null, "06:00"), Pass("B", "06:02"), Pass("C", "06:05")]),
            Train("1A02", points: [Call("A", null, "07:00"), Pass("D", "07:01"), Pass("C", "07:04")]));
        var table = MinRunTable.Build(timetable);

        table.Along(Points("A", "B", "C"), 0, 2).Should().Be(300);
        table.Along(Points("A", "B", "C"), 1, 2).Should().Be(180);
        table.Along(Points("A", "X", "C"), 0, 2).Should().BeNull();
        table.Along(Points("A", "D", "C"), 0, 2).Should().Be(240);
    }

    // Disruption coverage

    [Theory]
    [InlineData("A,B,C,D", 0, 1, true)]
    [InlineData("A,B,C,D", 1, 2, true)]
    [InlineData("A,B,C,D", 2, 3, false)]
    [InlineData("D,C,B,A", 1, 2, true)]
    [InlineData("D,C,B,A", 0, 1, false)]
    [InlineData("A,B,A,B", 1, 2, false)]
    [InlineData("X,B,Y", 0, 1, false)]
    public void A_disruption_covers_runs_between_calls_at_its_ends(string route, int from, int to, bool covered)
    {
        var d = new Disruption("D1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("C"), null, SimTime.Parse("06:00"), SimTime.Parse("07:00"));

        d.Covers(Points(route.Split(',')), from, to).Should().Be(covered);
    }

    [Fact]
    public void A_disruption_at_one_location_covers_runs_into_and_out_of_it()
    {
        var d = new Disruption("D1", DisruptionKind.SpeedRestriction, new Tiploc("B"), new Tiploc("B"), null, SimTime.Parse("06:00"), SimTime.Parse("07:00"), 2);
        var points = Points("A", "B", "C", "D");

        d.Covers(points, 0, 1).Should().BeTrue();
        d.Covers(points, 1, 2).Should().BeTrue();
        d.Covers(points, 2, 3).Should().BeFalse();
        d.Lengthen(61).Should().Be(122);
        d.IsActiveAt(SimTime.Parse("07:00")).Should().BeFalse();
        d.IsActiveAt(SimTime.Parse("06:00")).Should().BeTrue();
    }

    // JSON

    internal static IPlanCommand[] EveryCommand() =>
    [
        new ShiftTrain(Fast, 1, -90),
        new SetDwell(Fast, 1, SimTime.Parse("06:08:30")),
        new ChangePlatform(Fast, 1, "3"),
        new ChangeLine(Fast, 1, "UF"),
        new SkipStop(Fast, 1),
        new AddStop(Slow, 2, 45),
        new TerminateAt(Slow, 2),
        new CancelTrain(Slow),
        new ReinstateTrain(Slow),
        new ResetTrain(Slow),
        new CloneTrain(Fast, new Headcode("1A51"), 3600, new TrainId("1A51#c1")),
        new AddDisruption(new Disruption("SR1", DisruptionKind.SpeedRestriction, new Tiploc("A"), new Tiploc("B"), "Down", SimTime.Parse("06:00"), SimTime.Parse("26:00"), 1.25, 30, "Track defect")),
        new EditDisruption(new Disruption("SR1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("06:00"), SimTime.Parse("26:00"))),
        new RemoveDisruption("SR1"),
        new AddDisruption(new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("C"), "Up", SimTime.Parse("06:00"), SimTime.Parse("07:00"), Direction: Network.TrackDirection.Up)),
        new AddNote(new PlanNote("N1", new Tiploc("B"), SimTime.Parse("06:05"), "Driver relieved")),
        new EditNote(new PlanNote("N1", new Tiploc("C"), SimTime.Parse("06:10"), "Driver relieved\nat C")),
        new RemoveNote("N1"),
        new LinkLiveTrain("10", new Headcode("1A01"), Fast),
        new LinkLiveTrain("11", new Headcode("2B01"), null),
        new UnlinkLiveTrain("11", new Headcode("2B01")),
        new AcceptSuggestion("S1", [new ShiftTrain(Slow, 0, 120), new ChangePlatform(Slow, 1, "1")]),
    ];

    [Fact]
    public void Every_command_round_trips_through_JSON()
    {
        foreach (var command in EveryCommand())
        {
            var json = PlanCommandJson.Serialize(command);
            var back = PlanCommandJson.Deserialize(json);

            back.Should().BeOfType(command.GetType());
            back.Should().BeEquivalentTo(command, o => o.RespectingRuntimeTypes(), json);
            PlanCommandJson.Serialize(back).Should().Be(json);
        }
    }

    [Fact]
    public void Command_JSON_is_readable()
    {
        PlanCommandJson.Serialize(new ShiftTrain(Fast, 1, -90)).Should().Be("""{"type":"shiftTrain","train":"1A01","fromIndex":1,"deltaSeconds":-90}""");
        PlanCommandJson.Serialize(new LinkLiveTrain("11", new Headcode("2B01"), null)).Should().Be("""{"type":"linkLiveTrain","simUid":"11","headcode":"2B01","train":null}""");
        PlanCommandJson.Serialize(new AddDisruption(new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("06:00"), SimTime.Parse("07:00"))))
            .Should().Contain("\"kind\":\"Block\"");
    }

    [Fact]
    public void Unknown_command_JSON_is_rejected()
    {
        var act = () => PlanCommandJson.Deserialize("""{"type":"launchMissiles"}""");

        act.Should().Throw<JsonException>();
    }

    // Determinism

    [Fact]
    public void The_same_commands_always_give_the_same_plan()
    {
        var commands = EveryCommand();
        var base1 = WorkingPlanTests.Plan();
        var base2 = WorkingPlanTests.Plan();

        var (plan1, results1) = base1.ApplyAll(commands);
        var (plan2, results2) = base2.ApplyAll(commands.Select(c => PlanCommandJson.Deserialize(PlanCommandJson.Serialize(c))));

        results1.Should().BeEquivalentTo(results2);
        results1.Should().OnlyContain(r => r.Applied, string.Join("; ", results1.Where(r => !r.Applied).Select(r => r.Error)));
        Comparable(plan1).Should().BeEquivalentTo(Comparable(plan2), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Applying_one_by_one_gives_the_same_plan_as_applying_all()
    {
        var commands = EveryCommand();
        var plan = WorkingPlanTests.Plan();
        foreach (var command in commands)
        {
            (plan, _) = plan.Apply(command);
        }

        var (all, _) = WorkingPlanTests.Plan().ApplyAll(commands);

        Comparable(plan).Should().BeEquivalentTo(Comparable(all), o => o.WithStrictOrdering());
    }

    [Fact]
    public void Failed_commands_are_skipped_by_apply_all()
    {
        var (plan, results) = WorkingPlanTests.Plan().ApplyAll([new CancelTrain(Fast), new CancelTrain(Fast), new SkipStop(Slow, 1)]);

        results.Select(r => r.Applied).Should().Equal(true, false, true);
        plan.Get(Fast).Cancelled.Should().BeTrue();
        plan.Get(Slow).Points[1].IsPass.Should().BeTrue();
    }

    private static object Comparable(WorkingPlan plan) => new
    {
        plan.Trains,
        plan.Disruptions,
        plan.Links,
    };
}
