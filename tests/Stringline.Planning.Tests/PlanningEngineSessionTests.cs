using Stringline.Core;
using Stringline.Core.Events;

namespace Stringline.Planning.Tests;

/// <summary>S16: the engine reports its plan steps for the session store, and restores a history from them.</summary>
public sealed class PlanningEngineSessionTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TrainId Fast = new("1A01");
    private static readonly TrainId Slow = new("2B01");
    private static readonly DateTimeOffset Wall = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly PlanningEngine _engine = new(new PlanningEngineOptions { PublishInterval = TimeSpan.Zero });
    private readonly CancellationTokenSource _stop = new();
    private readonly List<PlanStep> _steps = [];
    private readonly Task _loop;

    public PlanningEngineSessionTests()
    {
        _engine.PlanStepped += (_, step) =>
        {
            lock (_steps)
            {
                _steps.Add(step);
            }
        };
        _loop = _engine.RunAsync(_stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }

    private List<PlanStep> Steps()
    {
        lock (_steps)
        {
            return [.. _steps];
        }
    }

    private async Task<PlanSnapshot> Snapshot(Func<PlanSnapshot, bool> until)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var current = _engine.Current;
            if (until(current))
            {
                return current;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No such snapshot.");
            }

            await Task.Delay(5);
        }
    }

    private async Task Loaded()
    {
        _engine.SetTimetable(WorkingPlanTests.Timetable());
        await Snapshot(s => s.Plan.Count == 2);
    }

    private async Task Run(IPlanCommand command) => (await _engine.Execute(command).WaitAsync(Timeout)).Applied.Should().BeTrue();

    [Fact]
    public async Task Every_change_to_the_history_is_reported_in_order_with_the_sim_time()
    {
        await Loaded();
        _engine.Post(new ClockEvent(Wall, "test", SimTime.Parse("07:01"), 500, false));
        await Snapshot(s => s.Clock.HasTime);

        await Run(new ShiftTrain(Fast, 0, 300));
        await Run(new CancelTrain(Slow));
        (await _engine.Undo().WaitAsync(Timeout)).Should().BeTrue();
        (await _engine.Redo().WaitAsync(Timeout)).Should().BeTrue();
        (await _engine.Undo().WaitAsync(Timeout)).Should().BeTrue();
        (await _engine.Undo().WaitAsync(Timeout)).Should().BeTrue();
        _engine.Clear();
        await Snapshot(s => !s.History.CanUndo && !s.History.CanRedo);

        var steps = Steps();
        steps.Select(s => s.Kind).Should().Equal(
            PlanStepKind.Reset, // the first timetable
            PlanStepKind.Execute,
            PlanStepKind.Execute,
            PlanStepKind.Undo,
            PlanStepKind.Redo,
            PlanStepKind.Undo,
            PlanStepKind.Undo,
            PlanStepKind.Reset);
        steps[1].Command.Should().Be(new ShiftTrain(Fast, 0, 300));
        steps[1].Description.Should().StartWith("Shift 1A01");
        steps[3].Description.Should().Be("Cancel 2B01");
        steps.Skip(1).Take(6).Should().OnlyContain(s => s.SimTime == SimTime.Parse("07:01") && s.At != default);
    }

    [Fact]
    public async Task Refused_commands_and_steps_with_nothing_to_do_are_not_reported()
    {
        await Loaded();
        var before = Steps().Count;

        (await _engine.Execute(new SkipStop(Fast, 2)).WaitAsync(Timeout)).Applied.Should().BeFalse();
        (await _engine.Undo().WaitAsync(Timeout)).Should().BeFalse();
        (await _engine.Redo().WaitAsync(Timeout)).Should().BeFalse();

        Steps().Should().HaveCount(before);
    }

    [Fact]
    public async Task Restoring_rebuilds_the_plan_and_the_redo_list_without_reporting()
    {
        await Loaded();
        await Run(new ShiftTrain(Fast, 0, 300));
        await Run(new CancelTrain(Slow));
        await _engine.Undo().WaitAsync(Timeout);
        var original = await Snapshot(s => s.History.Done.Count == 1 && s.History.CanRedo);
        var steps = Steps();

        _engine.Clear();
        await Snapshot(s => !s.History.CanUndo && !s.History.CanRedo);
        var reported = Steps().Count;
        await _engine.RestorePlan(steps).WaitAsync(Timeout);
        var restored = await Snapshot(s => s.History.CanUndo);

        Steps().Should().HaveCount(reported);
        restored.History.Done.Select(e => e.Description).Should().Equal(original.History.Done.Select(e => e.Description));
        restored.History.Undone.Select(e => e.Description).Should().Equal("Cancel 2B01");
        restored.Plan.Get(Fast).Points[0].Depart.Should().Be(original.Plan.Get(Fast).Points[0].Depart);
        restored.Plan.Get(Slow).Cancelled.Should().BeFalse();

        (await _engine.Redo().WaitAsync(Timeout)).Should().BeTrue();
        (await Snapshot(s => s.History.Done.Count == 2)).Plan.Get(Slow).Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Restoring_drops_commands_that_no_longer_apply()
    {
        await Loaded();

        await _engine.RestorePlan(
        [
            PlanStep.Execute(new CancelTrain(Fast), "Cancel 1A01"),
            PlanStep.Execute(new SkipStop(Fast, 2), "Skip C"),
            PlanStep.Execute(new CancelTrain(new TrainId("9Z99")), "Cancel 9Z99"),
        ]).WaitAsync(Timeout);
        var restored = await Snapshot(s => s.History.CanUndo);

        restored.History.Done.Should().ContainSingle().Which.Description.Should().Be("Cancel 1A01");
        restored.Plan.Get(Fast).Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task Restoring_an_earlier_part_of_the_steps_shows_the_plan_as_it_was()
    {
        await Loaded();
        PlanStep[] steps =
        [
            PlanStep.Execute(new CancelTrain(Fast), "Cancel 1A01", SimTime.Parse("07:00")),
            PlanStep.Execute(new CancelTrain(Slow), "Cancel 2B01", SimTime.Parse("08:00")),
        ];

        await _engine.RestorePlan(PlanLog.By(steps, SimTime.Parse("07:30"))).WaitAsync(Timeout);
        var early = await Snapshot(s => s.History.Done.Count == 1);
        await _engine.RestorePlan(PlanLog.By(steps, SimTime.Parse("08:00"))).WaitAsync(Timeout);
        var late = await Snapshot(s => s.History.Done.Count == 2);

        early.Plan.Get(Fast).Cancelled.Should().BeTrue();
        early.Plan.Get(Slow).Cancelled.Should().BeFalse();
        late.Plan.Get(Slow).Cancelled.Should().BeTrue();
    }
}

public sealed class PlanLogTests
{
    private static readonly IPlanCommand A = new CancelTrain(new TrainId("1A01"));
    private static readonly IPlanCommand B = new CancelTrain(new TrainId("2B01"));
    private static readonly IPlanCommand C = new ReinstateTrain(new TrainId("1A01"));

    [Fact]
    public void Folding_follows_undo_redo_and_a_new_edit_clearing_the_redo_list()
    {
        var (done, undone) = PlanLog.Fold(
        [
            PlanStep.Execute(A), PlanStep.Execute(B), PlanStep.Execute(C),
            new(PlanStepKind.Undo), new(PlanStepKind.Undo), new(PlanStepKind.Redo),
        ]);
        done.Select(s => s.Command).Should().Equal(A, B);
        undone.Select(s => s.Command).Should().Equal(C);

        PlanLog.Fold([PlanStep.Execute(A), PlanStep.Execute(B), new(PlanStepKind.Undo), PlanStep.Execute(C)])
            .Undone.Should().BeEmpty();
    }

    [Fact]
    public void Only_steps_after_the_last_reset_count_and_extra_undos_are_ignored()
    {
        var (done, undone) = PlanLog.Fold(
        [
            PlanStep.Execute(A), new(PlanStepKind.Reset), new(PlanStepKind.Undo), new(PlanStepKind.Redo), PlanStep.Execute(B),
        ]);

        done.Select(s => s.Command).Should().Equal(B);
        undone.Should().BeEmpty();
    }

    [Fact]
    public void By_keeps_the_steps_made_by_then_and_those_before_the_clock()
    {
        PlanStep[] steps =
        [
            PlanStep.Execute(A),
            PlanStep.Execute(B, simTime: SimTime.Parse("07:00")),
            PlanStep.Execute(C, simTime: SimTime.Parse("08:00")),
        ];

        PlanLog.By(steps, null).Should().Equal(steps[0]);
        PlanLog.By(steps, SimTime.Parse("07:00")).Should().Equal(steps[0], steps[1]);
        PlanLog.By(steps, SimTime.Parse("09:00")).Should().Equal(steps);
    }
}
