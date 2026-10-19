using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Timetables;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>Plan commands, undo and redo through the engine loop.</summary>
public sealed class PlanningEnginePlanTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TrainId Fast = new("1A01");
    private static readonly TrainId Slow = new("2B01");

    private readonly PlanningEngine _engine = new(new PlanningEngineOptions { PublishInterval = TimeSpan.Zero });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public PlanningEnginePlanTests() => _loop = _engine.RunAsync(_stop.Token);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }

    /// <summary>Waits for a snapshot that satisfies <paramref name="until"/> (the current one, or a later one).</summary>
    private async Task<PlanSnapshot> Snapshot(Func<PlanSnapshot, bool> until)
    {
        var found = new TaskCompletionSource<PlanSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(object? sender, PlanSnapshot s)
        {
            if (until(s))
            {
                found.TrySetResult(s);
            }
        }

        _engine.SnapshotPublished += OnPublished;
        try
        {
            // Read the current snapshot once: the engine may publish another between a check and a second read, which
            // made Clear_starts_a_new_session_with_the_booked_plan fail now and then (S12, fixed in S13).
            var current = _engine.Current;
            if (until(current))
            {
                return current;
            }

            return await found.Task.WaitAsync(Timeout);
        }
        finally
        {
            _engine.SnapshotPublished -= OnPublished;
        }
    }

    private async Task<PlanSnapshot> Loaded(WorkingTimetable? timetable = null)
    {
        _engine.SetTimetable(timetable ?? WorkingPlanTests.Timetable());
        return await Snapshot(s => s.Plan.Count == 2);
    }

    private async Task<PlanCommandResult> Run(IPlanCommand command) => await _engine.Execute(command).WaitAsync(Timeout);

    [Fact]
    public async Task A_timetable_gives_its_plan_as_booked()
    {
        var snapshot = await Loaded();

        snapshot.Plan.Timetable.Should().BeSameAs(snapshot.Timetable);
        snapshot.Plan.Modified.Should().BeEmpty();
        snapshot.History.CanUndo.Should().BeFalse();
        snapshot.History.CanRedo.Should().BeFalse();
        PlanSnapshot.Empty.Plan.Should().BeSameAs(WorkingPlan.Empty);
    }

    [Fact]
    public async Task A_command_is_applied_and_published_with_its_history()
    {
        var loaded = await Loaded();

        var result = await Run(new CancelTrain(Fast));
        var snapshot = await Snapshot(s => s.PlanVersion > loaded.PlanVersion);

        result.Applied.Should().BeTrue();
        snapshot.Plan.Get(Fast).Cancelled.Should().BeTrue();
        snapshot.History.Done.Should().ContainSingle().Which.Description.Should().Be("Cancel 1A01");
        snapshot.History.UndoDescription.Should().Be("Cancel 1A01");
        snapshot.History.CanRedo.Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_command_changes_nothing()
    {
        var loaded = await Loaded();

        var result = await Run(new SkipStop(Fast, 2));

        result.Applied.Should().BeFalse();
        result.Error.Should().Be("1A01 does not stop at C.");
        _engine.Current.PlanVersion.Should().Be(loaded.PlanVersion);
        _engine.Current.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task A_command_that_throws_is_refused_and_the_engine_carries_on()
    {
        await Loaded();

        var result = await Run(new Throwing());
        var next = await Run(new CancelTrain(Fast));

        result.Applied.Should().BeFalse();
        result.Error.Should().Be("The command could not be applied: boom");
        next.Applied.Should().BeTrue();
    }

    [Fact]
    public async Task Warnings_are_returned_and_kept_in_the_history()
    {
        await Loaded();

        var result = await Run(new SetDwell(Slow, 1, SimTime.Parse("07:09")));
        var snapshot = await Snapshot(s => s.History.CanUndo);

        result.Warnings.Should().HaveCount(2);
        snapshot.History.Done[0].Warnings.Should().BeEquivalentTo(result.Warnings);
    }

    [Fact]
    public async Task Undo_and_redo_step_through_the_history()
    {
        var booked = (await Loaded()).Plan;
        await Run(new ShiftTrain(Fast, 0, 300));
        await Run(new CancelTrain(Slow));
        var edited = (await Snapshot(s => s.History.Done.Count == 2)).Plan;

        (await _engine.Undo().WaitAsync(Timeout)).Should().BeTrue();
        var once = await Snapshot(s => s.History.Done.Count == 1);
        once.Plan.Get(Slow).Cancelled.Should().BeFalse();
        once.Plan.Get(Fast).IsModified.Should().BeTrue();
        once.History.RedoDescription.Should().Be("Cancel 2B01");

        (await _engine.Undo().WaitAsync(Timeout)).Should().BeTrue();
        var twice = await Snapshot(s => s.History.Done.Count == 0);
        twice.Plan.Trains.Should().BeEquivalentTo(booked.Trains);
        twice.History.Undone.Select(e => e.Description).Should().Equal("Shift 1A01 by +5 min from A", "Cancel 2B01");
        (await _engine.Undo().WaitAsync(Timeout)).Should().BeFalse();

        (await _engine.Redo().WaitAsync(Timeout)).Should().BeTrue();
        (await _engine.Redo().WaitAsync(Timeout)).Should().BeTrue();
        (await _engine.Redo().WaitAsync(Timeout)).Should().BeFalse();
        var redone = await Snapshot(s => s.History.Done.Count == 2);
        redone.Plan.Trains.Should().BeEquivalentTo(edited.Trains);
        redone.History.CanRedo.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_command_clears_the_redo_list()
    {
        await Loaded();
        await Run(new CancelTrain(Fast));
        await _engine.Undo().WaitAsync(Timeout);

        await Run(new CancelTrain(Slow));
        var snapshot = await Snapshot(s => s.Plan.Get(Slow).Cancelled);

        snapshot.History.CanRedo.Should().BeFalse();
        snapshot.History.Done.Select(e => e.Description).Should().Equal("Cancel 2B01");
        (await _engine.Redo().WaitAsync(Timeout)).Should().BeFalse();
    }

    [Fact]
    public async Task Reloading_the_same_timetable_keeps_the_edits()
    {
        await Loaded();
        await Run(new CancelTrain(Fast));
        await Run(new CloneTrain(Fast, new Headcode("1A51"), 3600, new TrainId("1A51#c1")));

        _engine.SetTimetable(WorkingPlanTests.Timetable());
        var snapshot = await Snapshot(s => s.History.Done.Count == 2 && s.Plan.Count == 3 && s.PlanVersion >= 4);

        snapshot.Plan.Get(Fast).Cancelled.Should().BeTrue();
        snapshot.Plan.Find(new TrainId("1A51#c1")).Should().NotBeNull();
    }

    [Fact]
    public async Task Reloading_drops_edits_that_no_longer_apply()
    {
        await Loaded();
        await Run(new CancelTrain(Fast));
        await Run(new CancelTrain(Slow));

        _engine.SetTimetable(Of(Train("1A01", points: [Call("A", null, "06:00"), Call("B", "06:05", null)])) );
        var snapshot = await Snapshot(s => s.Plan.Count == 1);

        snapshot.History.Done.Select(e => e.Description).Should().Equal("Cancel 1A01");
        snapshot.Plan.Get(Fast).Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task A_different_timetable_starts_a_new_history()
    {
        await Loaded();
        await Run(new CancelTrain(Fast));

        var other = new WorkingTimetable(WorkingPlanTests.Timetable().Header with { Name = "Other" }, [], WorkingPlanTests.Timetable().Trains, []);
        _engine.SetTimetable(other);
        var snapshot = await Snapshot(s => s.Timetable == other);

        snapshot.History.CanUndo.Should().BeFalse();
        snapshot.Plan.Modified.Should().BeEmpty();
    }

    [Fact]
    public async Task Clear_starts_a_new_session_with_the_booked_plan()
    {
        await Loaded();
        await Run(new CancelTrain(Fast));

        _engine.Clear();
        var snapshot = await Snapshot(s => !s.History.CanUndo);

        snapshot.Plan.Modified.Should().BeEmpty();
        snapshot.Plan.Count.Should().Be(2);
    }

    [Fact]
    public async Task A_live_link_rematches_the_train_and_undoes()
    {
        await Loaded();
        GatewayEvent[] events =
        [
            Clock("05:59"),
            Location("10", "1A01", "A", "06:00:30", TrainLocationAction.Depart),
        ];
        Array.ForEach(events, _engine.Post);
        var matched = await Snapshot(s => s.FindByTrain(Fast) is not null);
        matched.Matches.Matched.Should().Be(1);

        (await Run(new LinkLiveTrain("10", new Headcode("1A01"), null))).Applied.Should().BeTrue();
        var unlinked = await Snapshot(s => s.FindByTrain(Fast) is null);
        unlinked.Trains.Should().ContainSingle().Which.Workings[0].IsMatched.Should().BeFalse();
        unlinked.Plan.Links.Should().ContainSingle();

        await _engine.Undo().WaitAsync(Timeout);
        var back = await Snapshot(s => s.FindByTrain(Fast) is not null);
        back.Plan.Links.Should().BeEmpty();
        back.Trains.Should().ContainSingle();
    }

    [Fact]
    public async Task A_link_to_another_train_moves_the_match()
    {
        await Loaded();
        Array.ForEach<GatewayEvent>([Clock("05:59"), Location("10", "1A01", "A", "06:00:30", TrainLocationAction.Depart)], _engine.Post);
        await Snapshot(s => s.FindByTrain(Fast) is not null);

        await Run(new LinkLiveTrain("10", new Headcode("1A01"), Slow));
        var snapshot = await Snapshot(s => s.FindByTrain(Slow) is not null);

        snapshot.FindByTrain(Fast).Should().BeNull();
        snapshot.FindByTrain(Slow)!.Value.Train.SimUid.Should().Be("10");
    }

    private sealed record Throwing : IPlanCommand
    {
        public PlanCommandResult Apply(WorkingPlanBuilder b) => throw new InvalidOperationException("boom");

        public string Describe(WorkingPlan plan) => "Throw";
    }
}
