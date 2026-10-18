using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Planning.Tests;

/// <summary>A real recording through the engine, with the WTT loaded part way through as the app does.</summary>
public sealed class LiveEngineFixtureTests
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

        var engine = new LiveEngine(new LiveEngineOptions { PublishInterval = TimeSpan.Zero });
        var done = new TaskCompletionSource<LiveSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
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
}
