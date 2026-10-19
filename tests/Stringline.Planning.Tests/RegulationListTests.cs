using Stringline.Core;
using Stringline.Core.Timetables;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>The regulation list (RP-9): movements still to come at a location, in plan order, against the WTT's.</summary>
public sealed class RegulationListTests
{
    private static readonly Tiploc B = new("B");

    private static WorkingTimetable Timetable() => Of(
        Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04") with { Line = "UF" }, Pass("C", "08:08")]),
        Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06") with { Platform = "2" }, Call("C", "08:12", null)]),
        Train("2T01", points: [Call("C", null, "08:20"), Call("B", "08:25", null)]),
        Train("2B01", points: [Call("B", null, "09:00"), Call("A", "09:05", null)]));

    private static WorkingPlan Applied(WorkingPlan plan, params IPlanCommand[] commands)
    {
        foreach (var command in commands)
        {
            var (next, result) = plan.Apply(command);
            result.Applied.Should().BeTrue(result.Error);
            plan = next;
        }

        return plan;
    }

    [Fact]
    public void Movements_in_plan_order_with_what_each_train_does()
    {
        var rows = RegulationList.For(WorkingPlan.FromTimetable(Timetable()), Forecast.Empty, B, null);

        rows.Select(r => (r.Headcode.Value, r.Move, r.Planned.ToString())).Should().Equal(
            ("1F01", RegulationMove.Pass, "08:04:00"),
            ("2S01", RegulationMove.Call, "08:06:00"),
            ("2T01", RegulationMove.Terminate, "08:25:00"),
            ("2B01", RegulationMove.Start, "09:00:00"));
        var call = rows[1];
        (call.PlannedArrive, call.Booked, call.Platform, call.Index).Should().Be((SimTime.Parse("07:59"), (SimTime?)SimTime.Parse("08:06"), "2", 1));
        rows[0].Line.Should().Be("UF");
        rows.Should().OnlyContain(r => !r.OutOfOrder && !r.IsChanged && !r.IsAdded);
    }

    [Fact]
    public void A_train_planned_behind_one_it_is_booked_ahead_of_is_out_of_order()
    {
        // 1F01 is held 5 min at A, so it now passes B at 08:09, after 2S01 leaves the loop.
        var plan = Applied(WorkingPlan.FromTimetable(Timetable()), new ShiftTrain(new TrainId("1F01"), 0, 300));

        var rows = RegulationList.For(plan, Forecast.Empty, B, null);

        rows.Select(r => r.Headcode.Value).Should().StartWith(["2S01", "1F01"]);
        rows[0].Ahead.Should().Equal(new Headcode("1F01"));
        rows[0].Behind.Should().BeEmpty();
        rows[1].Behind.Should().Equal(new Headcode("2S01"));
        rows[1].IsChanged.Should().BeTrue();
        rows[0].IsChanged.Should().BeFalse();
        rows.Skip(2).Should().OnlyContain(r => !r.OutOfOrder);
    }

    [Fact]
    public void Only_what_is_still_to_come_within_the_horizon_with_its_forecast()
    {
        // At 08:05 2S01 is in the loop at B (arrived), and 1F01 has gone by.
        var plan = WorkingPlan.FromTimetable(Timetable());
        var forecast = Run(plan, "08:05", live: [Running("10", "1F01", Passed("A", "08:00"), Passed("B", "08:04")), Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59"))]);

        var rows = RegulationList.For(plan, forecast, B, SimTime.Parse("08:05"), horizonSeconds: 30 * 60);

        rows.Select(r => r.Headcode.Value).Should().Equal("2S01", "2T01");
        rows[0].Forecast.Should().Be(SimTime.Parse("08:06"));
        rows[0].DelaySeconds.Should().Be(0);
        rows[1].Forecast.Should().Be(SimTime.Parse("08:25"));
    }

    [Fact]
    public void Cancelled_trains_are_left_out_and_clones_are_added()
    {
        var plan = Applied(
            WorkingPlan.FromTimetable(Timetable()),
            new CancelTrain(new TrainId("2T01")),
            new CloneTrain(new TrainId("1F01"), new Headcode("1Z99"), 600, new TrainId("1Z99#c1")));

        var rows = RegulationList.For(plan, Forecast.Empty, B, null);

        rows.Select(r => r.Headcode.Value).Should().Equal("1F01", "2S01", "1Z99", "2B01");
        var clone = rows[2];
        (clone.IsAdded, clone.Booked, clone.OutOfOrder).Should().Be((true, (SimTime?)null, false));
    }

    [Fact]
    public void A_train_terminated_short_terminates_there()
    {
        var plan = Applied(WorkingPlan.FromTimetable(Timetable()), new TerminateAt(new TrainId("2S01"), 1));

        var row = RegulationList.For(plan, Forecast.Empty, B, null).Single(r => r.Headcode.Value == "2S01");

        (row.Move, row.Planned, row.Booked).Should().Be((RegulationMove.Terminate, SimTime.Parse("07:59"), (SimTime?)SimTime.Parse("07:59")));
        row.IsChanged.Should().BeTrue("its departure is gone");
    }
}
