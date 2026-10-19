using Stringline.Core;
using Stringline.Core.Events;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>The forecast in the engine's snapshots: made on each change, with the network and options set on the engine.</summary>
public sealed class PlanningEngineForecastTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TrainId Slow = new("2B01");

    private readonly PlanningEngine _engine = new(new PlanningEngineOptions { PublishInterval = TimeSpan.Zero });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public PlanningEngineForecastTests() => _loop = _engine.RunAsync(_stop.Token);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }

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
            // Read the current snapshot once: the engine may publish another between a check and a second read.
            var current = _engine.Current;
            return until(current) ? current : await found.Task.WaitAsync(Timeout);
        }
        finally
        {
            _engine.SnapshotPublished -= OnPublished;
        }
    }

    [Fact]
    public async Task Snapshots_carry_the_forecast_of_the_plan_and_live_trains()
    {
        _engine.SetTimetable(WorkingPlanTests.Timetable());
        var loaded = await Snapshot(s => s.Plan.Count == 2);
        loaded.Forecast.Trains.Should().HaveCount(2);
        loaded.Forecast.Find(Slow)!.Status.Should().Be(TrainForecastStatus.NotStarted);

        _engine.Post(Clock("07:03:30"));
        _engine.Post(Location("10", "2B01", "A", "07:03", TrainLocationAction.Depart));
        var running = await Snapshot(s => s.Forecast.Find(Slow)?.Status == TrainForecastStatus.Running);

        running.Forecast.Now.Should().Be(SimTime.Parse("07:03:30"));
        Times(running.Forecast.Find(Slow)!).Should().Equal("A -/07:03:00 A", "B 07:08:00/07:08:30 F", "C pass 07:12:30 F", "D 07:17:30/- F");

        var result = await _engine.Execute(new CancelTrain(Slow)).WaitAsync(Timeout);
        result.Applied.Should().BeTrue();
        var cancelled = await Snapshot(s => s.PlanVersion > running.PlanVersion);
        cancelled.Forecast.Find(Slow).Should().BeNull("a cancelled train has no forecast");
    }

    [Fact]
    public async Task The_network_and_options_are_set_on_the_engine()
    {
        _engine.SetTimetable(WorkingPlanTests.Timetable());
        var loaded = await Snapshot(s => s.Plan.Count == 2);
        loaded.Forecast.Interaction.Should().BeFalse("there are no sections yet");

        _engine.SetForecast(ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C", "D")], WorkingPlanTests.Timetable()));
        var withNetwork = await Snapshot(s => s.Forecast.Interaction);
        withNetwork.Sequence.Should().BeGreaterThan(loaded.Sequence);

        _engine.SetForecast(options: new ForecastOptions { Interaction = false });
        var off = await Snapshot(s => s.Sequence > withNetwork.Sequence);
        off.Forecast.Interaction.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshots_carry_the_conflicts_of_the_plan()
    {
        _engine.SetTimetable(WorkingPlanTests.Timetable());
        var loaded = await Snapshot(s => s.Plan.Count == 2);
        loaded.Conflicts.Count.Should().Be(0);

        var block = new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("06:00"), SimTime.Parse("07:30"));
        (await _engine.Execute(new AddDisruption(block)).WaitAsync(Timeout)).Applied.Should().BeTrue();
        var blocked = await Snapshot(s => s.PlanVersion > loaded.PlanVersion);

        var c = blocked.Conflicts.All.Should().ContainSingle(c => c.A == Slow).Subject;
        (c.Kind, c.DisruptionId, c.Where).Should().Be((ConflictKind.Disruption, "B1", new Tiploc("A")));
        c.SeveritySeconds.Should().Be(30 * 60);

        await _engine.Undo().WaitAsync(Timeout);
        var undone = await Snapshot(s => s.PlanVersion > blocked.PlanVersion);
        undone.Conflicts.Count.Should().Be(0);
    }

    [Fact]
    public async Task Conflict_options_can_be_changed_while_running()
    {
        _engine.SetTimetable(WorkingPlanTests.Timetable());
        var loaded = await Snapshot(s => s.Plan.Count == 2);
        var block = new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("06:00"), SimTime.Parse("07:30"));
        (await _engine.Execute(new AddDisruption(block)).WaitAsync(Timeout)).Applied.Should().BeTrue();
        var blocked = await Snapshot(s => s.PlanVersion > loaded.PlanVersion && s.Conflicts.Count > 0);
        blocked.Settings.Conflicts.MinSeveritySeconds.Should().Be(30);

        // A 30-minute hold is under a 2-hour threshold, so nothing is reported.
        _engine.SetForecast(conflicts: new ConflictOptions { MinSeveritySeconds = 2 * 60 * 60 });
        var raised = await Snapshot(s => s.Settings.Conflicts.MinSeveritySeconds == 2 * 60 * 60);
        raised.Conflicts.Count.Should().Be(0);
        raised.Settings.Forecast.Should().Be(blocked.Settings.Forecast, "the forecast options are kept");
    }

    [Fact]
    public async Task Snapshots_carry_what_the_forecast_was_made_with_so_suggestions_can_be_made_from_them()
    {
        var network = ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C", "D")], WorkingPlanTests.Timetable());
        _engine.SetTimetable(WorkingPlanTests.Timetable());
        _engine.SetForecast(network, new ForecastOptions { DefaultDwellSeconds = 45 });
        var loaded = await Snapshot(s => s.Plan.Count == 2 && s.Settings.Network == network);
        loaded.Settings.Forecast.DefaultDwellSeconds.Should().Be(45);

        var block = new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("06:00"), SimTime.Parse("07:30"));
        (await _engine.Execute(new AddDisruption(block)).WaitAsync(Timeout)).Applied.Should().BeTrue();
        var blocked = await Snapshot(s => s.PlanVersion > loaded.PlanVersion);
        // The block holds both trains (and then 2B01 behind 1A01); suggest for 2B01's hold.
        var conflict = blocked.Conflicts.All.Single(c => c.A == Slow && c.Kind == ConflictKind.Disruption);

        var request = SuggestionRequest.For(blocked, conflict);
        (request.Forecast.Network, request.Forecast.Options, request.Baseline, request.BaselineConflicts).Should().Be((network, blocked.Settings.Forecast, blocked.Forecast, blocked.Conflicts));
        var hold = Suggester.Suggest(request)[0];
        hold.Commands.Should().Equal(new ShiftTrain(Slow, 0, 30 * 60));

        (await _engine.Execute(hold.ToCommand()).WaitAsync(Timeout)).Applied.Should().BeTrue();
        var applied = await Snapshot(s => s.PlanVersion > blocked.PlanVersion);
        applied.Conflicts.All.Should().NotContain(c => c.A == Slow && c.Kind == ConflictKind.Disruption);
        applied.History.UndoDescription.Should().Be("Suggestion: Hold 2B01 at A until 07:30 (+30 min)");
    }

    [Fact]
    public async Task Entry_delays_reach_the_snapshot_and_the_forecast()
    {
        _engine.SetTimetable(WorkingPlanTests.Timetable());
        await Snapshot(s => s.Plan.Count == 2);

        _engine.Post(new TrainDelayEvent(T0, new Headcode("2B01"), "", 120));
        var snapshot = await Snapshot(s => s.EntryDelays.Count == 1);

        snapshot.EntryDelays[new Headcode("2B01")].Should().Be(120);
        Times(snapshot.Forecast.Find(Slow)!)[0].Should().Be("A -/07:02:00 F");
    }
}
