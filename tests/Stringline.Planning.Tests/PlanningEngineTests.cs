using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Timetables;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

public sealed class PlanningEngineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static WorkingTimetable Timetable() => Of(
        Train("1A01", points: [Call("WLOO", null, "06:00"), Pass("VAUX", "06:04")]),
        Train("2X01", points: [Call("WLOO", null, "07:00"), Pass("VAUX", "07:04")]));

    private static readonly GatewayEvent[] Departures =
    [
        Clock("05:59:00"),
        Location("10", "1A01", "WLOO", "06:00:30", TrainLocationAction.Depart),
        Location("10", "1A01", "VAUX", "06:04:30"),
        Location("11", "2X01", "WLOO", "07:01", TrainLocationAction.Depart),
        Location("11", "2X01", "VAUX", "07:05"),
    ];

    private static PlanningEngine Engine(TimeProvider? time = null, TimeSpan? interval = null) =>
        new(new PlanningEngineOptions { PublishInterval = interval ?? TimeSpan.Zero }, time);

    /// <summary>Runs the engine while <paramref name="act"/> posts, and returns the first snapshot that satisfies <paramref name="until"/>.</summary>
    private static async Task<PlanSnapshot> RunUntil(PlanningEngine engine, Func<PlanSnapshot, bool> until, Action act)
    {
        var found = new TaskCompletionSource<PlanSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPublished(object? sender, PlanSnapshot s)
        {
            if (until(s))
            {
                found.TrySetResult(s);
            }
        }

        engine.SnapshotPublished += OnPublished;
        using var cts = new CancellationTokenSource();
        var loop = engine.RunAsync(cts.Token);
        try
        {
            act();
            return await found.Task.WaitAsync(Timeout);
        }
        finally
        {
            engine.SnapshotPublished -= OnPublished;
            await cts.CancelAsync();
            await loop;
        }
    }

    [Fact]
    public void Starts_with_the_empty_snapshot()
    {
        var engine = Engine();

        engine.Current.Should().BeSameAs(PlanSnapshot.Empty);
        engine.Current.Trains.Should().BeEmpty();
        engine.Current.Timetable.Should().BeNull();
        engine.Current.AreaId.Should().BeNull();
    }

    [Fact]
    public async Task Publishes_signals_and_track_circuits_and_keeps_them_through_a_rebuild()
    {
        var engine = Engine();
        engine.SetTimetable(Timetable());
        SignallingStateEvent Track(string id, bool clear) =>
            new(T0, "test", SignallingObjectType.Track, "track", id, SimTime.Parse("06:01"), new Dictionary<string, string> { ["clear"] = clear ? "True" : "False" });

        var snapshot = await RunUntil(engine, s => s.LineSide.Track("T1") is { Clear: false }, () =>
        {
            Array.ForEach(Departures, engine.Post);
            engine.Post(Track("T1", false));
        });
        snapshot.LineSide.Track("T1")!.Value.LastOccupied.Should().Be(SimTime.Parse("06:01"));

        // A new timetable rebuilds the live state from the events kept: the line side comes back too.
        snapshot = await RunUntil(engine, s => s.Timetable?.Trains.Count == 1 && s.LineSide.Track("T1") is not null, () => engine.SetTimetable(Of(Train("1A01", points: [Call("WLOO", null, "06:00"), Pass("VAUX", "06:04")]))));
        snapshot.LineSide.Track("T1")!.Value.Clear.Should().BeFalse();

        PlanningEngine.IsKept(Track("T1", true)).Should().BeTrue();
        PlanningEngine.IsKept(new SignallingStateEvent(T0, "test", SignallingObjectType.Route, "route", "R1A", null, new Dictionary<string, string>())).Should().BeFalse("only signals and track circuits are kept");
    }

    [Fact]
    public async Task A_change_only_to_the_line_side_publishes_at_most_once_a_second()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var engine = new PlanningEngine(new PlanningEngineOptions { PublishInterval = TimeSpan.Zero, LineSideInterval = TimeSpan.FromSeconds(1) }, time);
        engine.SetTimetable(Timetable());
        SignallingStateEvent Signal(int aspect) =>
            new(T0, "test", SignallingObjectType.Signal, "signal", "S1", SimTime.Parse("06:01"), new Dictionary<string, string> { ["aspect"] = aspect.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        await RunUntil(engine, s => s.Trains.Count == 2, () => Array.ForEach(Departures, engine.Post));
        var published = 0;
        engine.SnapshotPublished += (_, _) => published++;

        await RunUntil(engine, s => s.LineSide.Signal("S1") is not null, () =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            engine.Post(Signal(0));
        });
        var after = published;
        using var cts = new CancellationTokenSource();
        var loop = engine.RunAsync(cts.Token);
        engine.Post(Signal(6));
        engine.Post(Signal(2));
        await Task.Delay(200);
        published.Should().Be(after, "less than a second since the last publish");
        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(200);
        await cts.CancelAsync();
        await loop;
        published.Should().Be(after + 1, "both changes go into one snapshot a second on");
        engine.Current.LineSide.Signal("S1")!.Value.Aspect.Should().Be(2);
    }

    [Fact]
    public async Task Publishes_the_live_trains_and_clock()
    {
        var engine = Engine();
        engine.SetTimetable(Timetable());

        var snapshot = await RunUntil(engine, s => s.Trains.Count == 2, () => Array.ForEach(Departures, engine.Post));

        snapshot.Clock.HasTime.Should().BeTrue();
        snapshot.AreaId.Should().Be("test");
        snapshot.SessionStart.Should().Be(SimTime.Parse("05:59"));
        snapshot.Matches.Matched.Should().Be(2);
        snapshot.FindByTrain(new TrainId("2X01"))!.Value.Train.SimUid.Should().Be("11");
        snapshot.FindByUid("10")!.Headcode.Should().Be(new Headcode("1A01"));
        snapshot.FindByUid("99").Should().BeNull();
        snapshot.FindByTrain(new TrainId("9Z99")).Should().BeNull();
        engine.Current.Should().BeSameAs(snapshot);
    }

    [Fact]
    public async Task Without_a_timetable_every_working_is_unmatched_until_one_is_set()
    {
        var engine = Engine();

        var before = await RunUntil(engine, s => s.Trains.Count == 2, () => Array.ForEach(Departures, engine.Post));
        before.Timetable.Should().BeNull();
        before.Matches.Unmatched.Should().Be(2);

        var after = await RunUntil(engine, s => s.Timetable is not null, () => engine.SetTimetable(Timetable()));
        after.Matches.Matched.Should().Be(2, "the session's events are replayed against the new timetable");
        after.Trains.Should().HaveCount(2);
        after.Sequence.Should().BeGreaterThan(before.Sequence);
        after.Clock.LastReported.Should().Be(SimTime.Parse("05:59"));
    }

    [Fact]
    public async Task A_rewind_forgets_the_events_before_it()
    {
        var engine = Engine();
        await RunUntil(engine, s => s.Trains.Count == 2, () => Array.ForEach(Departures, engine.Post));

        var rewound = await RunUntil(engine, s => s.Trains.Count == 1, () =>
        {
            engine.Post(new ReplayRewoundEvent(T0, SimTime.Parse("06:00")));
            engine.Post(Departures[1]);
        });
        rewound.Clock.HasTime.Should().BeFalse();

        var rebuilt = await RunUntil(engine, s => s.Timetable is not null, () => engine.SetTimetable(Timetable()));
        rebuilt.Trains.Should().ContainSingle().Which.SimUid.Should().Be("10");
    }

    [Fact]
    public async Task Clear_forgets_the_session_but_keeps_the_timetable()
    {
        var engine = Engine();
        engine.SetTimetable(Timetable());
        await RunUntil(engine, s => s.Trains.Count == 2, () => Array.ForEach(Departures, engine.Post));

        var cleared = await RunUntil(engine, s => s.Trains.Count == 0, engine.Clear);

        cleared.Timetable.Should().NotBeNull();
        cleared.Clock.HasTime.Should().BeFalse();
    }

    [Fact]
    public async Task Ignores_events_the_live_state_does_not_use()
    {
        var engine = Engine();
        var published = 0;
        engine.SnapshotPublished += (_, _) => published++;

        await RunUntil(engine, s => s.Trains.Count == 1, () =>
        {
            engine.Post(new UnknownEvent(T0, "/topic/x", "odd", "{}", "test"));
            engine.Post(new SignallingStateEvent(T0, "test", SignallingObjectType.Track, "track", "T1", null, new Dictionary<string, string>()));
            engine.Post(Departures[1]);
        });

        published.Should().Be(1, "only the movement changed anything");
    }

    [Fact]
    public async Task Publishes_the_signalling_names_seen_and_keeps_them_through_a_rewind()
    {
        var engine = Engine();
        var named = await RunUntil(engine, s => s.Signalling.Berths.Count == 1, () =>
        {
            engine.Post(Signalling(SignallingObjectType.Route, "RWL1A"));
            engine.Post(Signalling(SignallingObjectType.Signal, "SWL1"));
            engine.Post(new TdStepEvent(T0, TdStepKind.Interpose, "test", string.Empty, "0123", "1A01", null));
        });

        named.Signalling.Routes.Should().Equal("RWL1A");
        named.Signalling.Signals.Should().Equal("SWL1");
        named.Signalling.Descriptions.Should().Contain("0123", "1A01");
        named.Trains.Should().BeEmpty("a name alone is no train");

        var rewound = await RunUntil(engine, s => s.Trains.Count == 1, () =>
        {
            engine.Post(new ReplayRewoundEvent(T0, SimTime.Parse("06:00")));
            engine.Post(Departures[1]);
        });
        rewound.Signalling.Should().BeSameAs(named.Signalling, "names are not state");

        var cleared = await RunUntil(engine, s => s.Trains.Count == 0, engine.Clear);
        cleared.Signalling.IsEmpty.Should().BeTrue();

        static SignallingStateEvent Signalling(SignallingObjectType type, string id) =>
            new(T0, "test", type, type.ToString().ToLowerInvariant(), id, null, new Dictionary<string, string>());
    }

    [Fact]
    public async Task Coalesces_events_into_one_snapshot_per_interval()
    {
        var time = new FakeTimeProvider(T0);
        var engine = Engine(time, TimeSpan.FromMilliseconds(100));
        var snapshots = new List<PlanSnapshot>();
        var published = new SemaphoreSlim(0);
        engine.SnapshotPublished += (_, s) =>
        {
            lock (snapshots)
            {
                snapshots.Add(s);
            }

            published.Release();
        };

        using var cts = new CancellationTokenSource();
        var loop = engine.RunAsync(cts.Token);

        engine.Post(Departures[0]);
        (await published.WaitAsync(Timeout)).Should().BeTrue("the first snapshot goes straight out");

        engine.Post(Departures[1]);
        engine.Post(Departures[2]);
        engine.Post(Departures[3]);
        (await published.WaitAsync(TimeSpan.FromMilliseconds(200))).Should().BeFalse("the interval has not passed");

        time.Advance(TimeSpan.FromMilliseconds(100));
        (await published.WaitAsync(Timeout)).Should().BeTrue();

        await cts.CancelAsync();
        await loop;
        snapshots.Should().HaveCount(2);
        snapshots[1].Trains.Should().HaveCount(2, "all three movements are in one snapshot");
    }

    [Fact]
    public async Task Pumps_a_source_into_the_queue()
    {
        var engine = Engine();
        engine.SetTimetable(Timetable());

        var snapshot = await RunUntil(engine, s => s.Trains.Count == 2, () => _ = engine.PumpAsync(new ListSource(Departures), CancellationToken.None));

        snapshot.Matches.Matched.Should().Be(2);
    }

    [Fact]
    public async Task Only_one_loop_may_run()
    {
        var engine = Engine();
        using var cts = new CancellationTokenSource();
        var loop = engine.RunAsync(cts.Token);

        var second = () => engine.RunAsync(CancellationToken.None);
        await second.Should().ThrowAsync<InvalidOperationException>();

        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task A_failing_handler_does_not_stop_the_engine()
    {
        var engine = Engine();
        engine.SnapshotPublished += (_, _) => throw new InvalidOperationException("boom");

        var snapshot = await RunUntil(engine, s => s.Trains.Count == 2, () => Array.ForEach(Departures, engine.Post));

        snapshot.Trains.Should().HaveCount(2);
    }

    private sealed class ListSource(IEnumerable<GatewayEvent> events) : ISimEventSource
    {
        public async IAsyncEnumerable<GatewayEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var e in events)
            {
                await Task.Yield();
                yield return e;
            }
        }
    }
}
