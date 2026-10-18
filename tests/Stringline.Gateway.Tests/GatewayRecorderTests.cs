using System.Text;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway.Recording;

namespace Stringline.Gateway.Tests;

public class GatewayRecorderTests
{
    [Fact]
    public async Task Records_frames_and_connection_changes_end_to_end()
    {
        await using var server = FakeStompServer.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stream = new MemoryStream();
        var options = new GatewayClientOptions { Host = "127.0.0.1", Port = server.Port, MinReconnectDelay = TimeSpan.FromMilliseconds(20) };
        var header = SslogWriter.LiveHeader(DateTimeOffset.UtcNow, options.Host, options.Port, options.Topics);
        var parsed = new List<GatewayEvent>();

        using (var writer = new SslogWriter(stream, header, ownsStream: false))
        {
            await using var client = new GatewayClient(options);
            var recorder = new GatewayRecorder(client, writer, new MessageParser());
            recorder.EventParsed += (_, e) =>
            {
                lock (parsed)
                {
                    parsed.Add(e);
                }
            };
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var run = recorder.RunAsync(stop.Token);

            await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
            await server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"1A01","uid":"1","action":"depart","location":"WLOO","time":15600}}""");
            await server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"testsim","clock":15660,"interval":125,"paused":false}}""");
            await server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"1A01","uid":"1","action":"pass","location":"VAUXHLL","time":15700}}""");
            await server.PublishAsync(GatewayTopics.TrainMovements, """{"mystery":{}}""");
            await server.PublishAsync(GatewayTopics.TrainMovements, "not json");

            while (recorder.Stats.Messages < 5)
            {
                await Task.Delay(10, timeout.Token);
            }

            server.DropConnections();
            await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
            while (recorder.Stats.Connections < 2)
            {
                await Task.Delay(10, timeout.Token);
            }

            await stop.CancelAsync();
            await run;

            var stats = recorder.Stats;
            stats.Messages.Should().Be(5);
            stats.Bytes.Should().BeGreaterThan(100);
            stats.AreaId.Should().Be("testsim");
            stats.LastClock.Should().Be(new SimTime(15660));
            stats.SpeedRatio.Should().Be(4);
            stats.Paused.Should().BeFalse();
            stats.UnknownEvents.Should().Be(2);
            stats.Connections.Should().Be(2);
            stats.State.Should().Be(GatewayConnectionState.Disconnected);
            stats.ByKind.Should().BeEquivalentTo(new Dictionary<string, long>
            {
                ["train_location"] = 2,
                ["clock_msg"] = 1,
                ["unknown:mystery"] = 1,
                ["unknown:(malformed)"] = 1,
            });
            writer.MessagesWritten.Should().Be(5);
        }

        parsed.Should().HaveCount(5);

        stream.Position = 0;
        using var reader = new SslogReader(stream);
        reader.Header.Topics.Should().Equal(options.Topics);
        var entries = reader.ReadEntries().ToList();
        reader.SkippedLines.Should().BeEmpty();

        var messages = entries.OfType<SslogMessage>().ToList();
        messages.Select(m => m.Sim).Should().Equal(null, new SimTime(15660), new SimTime(15660), new SimTime(15660), new SimTime(15660));
        messages[^1].BodyText.Should().Be("not json");

        var states = entries.OfType<SslogConnection>().Select(c => c.State).ToList();
        states.Should().StartWith([GatewayConnectionState.Connecting, GatewayConnectionState.Connected]);
        states.Should().EndWith(GatewayConnectionState.Disconnected);
        states.Count(s => s == GatewayConnectionState.Connected).Should().Be(2);
        entries.OfType<SslogConnection>().First(c => c.State == GatewayConnectionState.Connected).Headers
            .Should().Contain(new KeyValuePair<string, string>("server", "FakeSimSig/1.0"));

        // Replaying the recorded bodies through the parser reproduces the live events.
        var parser = new MessageParser();
        var replayed = messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        replayed.Select(e => e.GetType()).Should().Equal(parsed.Select(e => e.GetType()));
        // .sslog times have millisecond precision, so compare everything but ReceivedAt exactly.
        replayed.OfType<TrainLocationEvent>().Select(e => e with { ReceivedAt = default })
            .Should().Equal(parsed.OfType<TrainLocationEvent>().Select(e => e with { ReceivedAt = default }));
        replayed[0].ReceivedAt.Should().BeCloseTo(parsed[0].ReceivedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Rejected_connection_propagates_and_is_recorded()
    {
        await using var server = FakeStompServer.Start();
        server.OnConnect = _ => new Stomp.StompFrame(Stomp.StompCommands.Error, [new("message", "Bad credentials")]);
        using var stream = new MemoryStream();
        var options = new GatewayClientOptions { Host = "127.0.0.1", Port = server.Port, Login = "fred", Passcode = "pw" };

        using (var writer = new SslogWriter(stream, SslogWriter.LiveHeader(DateTimeOffset.UtcNow, options.Host, options.Port, options.Topics), ownsStream: false))
        {
            await using var client = new GatewayClient(options);
            var recorder = new GatewayRecorder(client, writer, new MessageParser());

            await recorder.Invoking(r => r.RunAsync(CancellationToken.None)).Should().ThrowAsync<GatewayConnectException>();
        }

        var text = Encoding.UTF8.GetString(stream.ToArray());
        text.Should().Contain("\"conn\":\"disconnected\"").And.Contain("Bad credentials").And.NotContain("\"pw\"").And.NotContain("passcode=pw");
    }

    [Fact]
    public void Validates_arguments()
    {
        var client = new GatewayClient(new GatewayClientOptions());
        using var writer = new SslogWriter(new MemoryStream(), new SslogHeader(1, "x", DateTimeOffset.UtcNow, null, null, []));

        FluentActions.Invoking(() => new GatewayRecorder(null!, writer, new MessageParser())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new GatewayRecorder(client, null!, new MessageParser())).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new GatewayRecorder(client, writer, null!)).Should().Throw<ArgumentNullException>();
    }
}
