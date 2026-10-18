using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Planning.Tests;

/// <summary>A real recording through the engine, with the WTT loaded part way through as the app does.</summary>
public sealed class PlanningEngineFixtureTests
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private static List<GatewayEvent> Events(string name)
    {
        var parser = new MessageParser();
        return SslogRecording.Load(Fixtures.Path(name)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
    }

    [Fact]
    public async Task A_timetable_loaded_mid_session_matches_as_if_it_had_been_there_from_the_start()
    {
        var events = Events(Fixtures.Waterloo4x);
        var direct = new LiveState(Waterloo.Value);
        events.ForEach(e => direct.Apply(e));

        var engine = new PlanningEngine(new PlanningEngineOptions { PublishInterval = TimeSpan.Zero });
        var done = new TaskCompletionSource<PlanSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.SnapshotPublished += (_, s) =>
        {
            if (s.Timetable is not null && s.Trains.Count == direct.TrainCount && s.StateVersion > 0 && s.Clock.LastReported == direct.Clock.LastReported)
            {
                done.TrySetResult(s);
            }
        };

        using var cts = new CancellationTokenSource();
        var loop = engine.RunAsync(cts.Token);
        var half = events.Count / 2;
        events.Take(half).ToList().ForEach(engine.Post);
        engine.SetTimetable(Waterloo.Value);
        events.Skip(half).ToList().ForEach(engine.Post);

        var snapshot = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();
        await loop;

        snapshot.AreaId.Should().Be("waterloo");
        snapshot.Matches.Should().BeEquivalentTo(direct.Summarise());
        snapshot.Matches.MatchedShare.Should().Be(1);
        snapshot.Trains.Select(t => t.SimUid).Should().Equal(direct.Trains.Select(t => t.SimUid));
        var delays = snapshot.Trains.Select(t => TrainDelays.Current(t, snapshot.Timetable)).Where(d => d is not null).ToList();
        delays.Should().NotBeEmpty();
        delays.Should().OnlyContain(d => Math.Abs(d!.Seconds) < 30 * 60, "delays at Waterloo 4× are minutes, not hours");
    }

    [Fact]
    public void Re_planning_every_Waterloo_train_is_deterministic_and_quick()
    {
        var timetable = Waterloo.Value;
        var booked = WorkingPlan.FromTimetable(timetable);
        booked.MinRuns.Count.Should().BeGreaterThan(20);

        // A mix of edits over every train: some earlier (clamped), some later, some stops skipped or cancelled.
        var commands = new List<IPlanCommand>();
        for (var i = 0; i < timetable.Trains.Count; i++)
        {
            var train = timetable.Trains[i];
            var from = Math.Min(1, train.Points.Count - 1);
            commands.Add((i % 4) switch
            {
                0 => new ShiftTrain(train.Id, from, -240),
                1 => new ShiftTrain(train.Id, 0, 180),
                2 => new CancelTrain(train.Id),
                _ => new ShiftTrain(train.Id, train.Points.Count - 1, -600),
            });
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (plan, results) = booked.ApplyAll(commands);
        var elapsed = watch.Elapsed;
        var (again, _) = WorkingPlan.FromTimetable(timetable).ApplyAll(commands.Select(c => PlanCommandJson.Deserialize(PlanCommandJson.Serialize(c))));

        results.Count(r => r.Applied).Should().BeGreaterThan(timetable.Trains.Count * 9 / 10);
        results.SelectMany(r => r.Warnings).Should().NotBeEmpty("trains shifted earlier must be clamped somewhere");
        again.Trains.Should().BeEquivalentTo(plan.Trains, o => o.WithStrictOrdering());
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "an undo replays the whole history");

        // Clamping never leaves a changed run faster than the minimum.
        foreach (var train in plan.Trains.Where(t => t.IsModified))
        {
            var wtt = TrainPlan.FromWtt(timetable.Get(train.Id));
            for (var i = 1; i < train.Points.Count; i++)
            {
                if (train.Points[i].FirstTime - train.Points[i - 1].LastTime is { } run
                    && wtt.Points[i].FirstTime - wtt.Points[i - 1].LastTime is { } was
                    && run < was
                    && plan.MinRuns.Between(train.Points[i - 1].Location, train.Points[i].Location) is { } min)
                {
                    run.Should().BeGreaterThanOrEqualTo(min, $"{train.Headcode} {train.Points[i - 1].Location}–{train.Points[i].Location}");
                }
            }
        }
    }
}
