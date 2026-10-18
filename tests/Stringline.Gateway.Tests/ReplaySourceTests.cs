using Microsoft.Extensions.Time.Testing;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;

namespace Stringline.Gateway.Tests;

public sealed class ReplaySourceTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _timeout.Dispose();

    [Fact]
    public async Task Replays_a_recording_as_the_same_events_the_parser_gives()
    {
        var recording = SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo4x));
        var parser = new MessageParser();
        var expected = recording.Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        var source = new ReplaySource(recording, new ReplayOptions { Speed = double.PositiveInfinity, PreserveTimestamps = true });

        var events = new List<GatewayEvent>();
        source.Should().BeAssignableTo<ISimEventSource>();
        await foreach (var e in source.ReadAllAsync(_timeout.Token))
        {
            events.Add(e);
        }

        events.Should().HaveCount(expected.Count);
        events.OfType<TrainLocationEvent>().Should().Equal(expected.OfType<TrainLocationEvent>());
        events.OfType<ClockEvent>().Should().Equal(expected.OfType<ClockEvent>());
    }

    [Fact]
    public async Task Stamps_events_with_the_replay_time_by_default()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var source = ReplaySource.Open(Fixtures.Path(Fixtures.Waterloo4x), new ReplayOptions { Speed = double.PositiveInfinity }, timeProvider: time);

        await using var events = source.ReadAllAsync(_timeout.Token).GetAsyncEnumerator(_timeout.Token);
        (await events.MoveNextAsync()).Should().BeTrue();

        events.Current.ReceivedAt.Should().Be(time.GetUtcNow());
        source.Recording.Header.Topics.Should().Equal(GatewayTopics.TrainMovements, GatewayTopics.SimSig);
    }

    [Fact]
    public async Task Seeking_back_announces_a_rewind_then_replays_from_the_start()
    {
        var source = ReplaySource.Open(Fixtures.Path(Fixtures.Waterloo4x), new ReplayOptions { Speed = double.PositiveInfinity, StartAt = SimTime.Parse("07:00", null) });
        var first = source.Recording.Messages[0];
        var events = new List<GatewayEvent>();

        await foreach (var e in source.ReadAllAsync(_timeout.Token))
        {
            events.Add(e);
            if (e is TrainLocationEvent { Time.Seconds: >= 25200 } && !events.OfType<ReplayRewoundEvent>().Any())
            {
                source.SeekTo(SimTime.Parse("06:00", null));
            }

            if (e is ReplayRewoundEvent)
            {
                break;
            }
        }

        events.OfType<ReplayRewoundEvent>().Should().ContainSingle().Which.Target.Should().Be(SimTime.Parse("06:00", null));
        source.Timeline.NextIndex.Should().Be(1, "the rewind comes with the first frame again");
        new MessageParser().Parse(first.ToGatewayMessage()).Should().ContainSingle().Which.Should().BeOfType<ClockEvent>();
    }
}
