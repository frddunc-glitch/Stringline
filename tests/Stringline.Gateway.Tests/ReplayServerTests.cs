using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;
using Stringline.Gateway.Stomp;

namespace Stringline.Gateway.Tests;

/// <summary>The replay server end to end: real <see cref="GatewayClient"/>s and raw STOMP sockets against it.</summary>
public sealed class ReplayServerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private static readonly Lazy<SslogRecording> Fast = new(() => SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo4x)));
    private static readonly Lazy<SslogRecording> WithTd = new(() => SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo1x)));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

    public void Dispose() => _timeout.Dispose();

    private static ReplayServer Start(SslogRecording recording, ReplayOptions? replay = null, bool waitForClient = true) =>
        ReplayServer.Start(recording, new ReplayServerOptions
        {
            Port = 0,
            Replay = replay ?? new ReplayOptions { Speed = double.PositiveInfinity },
            WaitForClient = waitForClient,
        });

    private static GatewayClient Client(ReplayServer server, IReadOnlyList<string>? topics = null) => new(new GatewayClientOptions
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Topics = topics ?? GatewayTopics.Default,
        Reconnect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });

    /// <summary>Collects a client's messages in the background.</summary>
    private sealed class Collector : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly Task _task;

        public Collector(GatewayClient client, CancellationToken token)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _task = Task.Run(async () =>
            {
                await foreach (var m in client.ReadAllAsync(_stop.Token))
                {
                    Messages.Enqueue(m);
                }
            }, CancellationToken.None);
        }

        public ConcurrentQueue<GatewayMessage> Messages { get; } = new();

        public async Task WaitUntil(Func<IReadOnlyCollection<GatewayMessage>, bool> condition)
        {
            using var timeout = new CancellationTokenSource(Wait);
            while (!condition(Messages))
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task A_real_client_receives_every_recorded_frame_in_order()
    {
        var recording = Fast.Value;
        await using var server = Start(recording);
        await using var client = Client(server);
        await using var collector = new Collector(client, _timeout.Token);

        await collector.WaitUntil(m => m.Count >= recording.Messages.Count);
        await server.Completion.WaitAsync(Wait);
        await Task.Delay(100);

        collector.Messages.Select(m => (m.Topic, m.BodyText)).Should().Equal(recording.Messages.Select(m => (m.Topic, m.BodyText)));
        server.MessagesSent.Should().Be(recording.Messages.Count);
        server.ClientCount.Should().Be(1);
    }

    [Fact]
    public async Task Waits_for_a_client_before_playing()
    {
        await using var server = Start(Fast.Value);

        await Task.Delay(200);

        server.Timeline.NextIndex.Should().Be(0);
        server.Completion.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Without_waiting_it_plays_to_the_end_with_no_clients()
    {
        await using var server = Start(Fast.Value, waitForClient: false);

        await server.Completion.WaitAsync(Wait);

        server.Timeline.IsFinished.Should().BeTrue();
        server.MessagesSent.Should().Be(0);
    }

    [Fact]
    public async Task Start_time_pre_rolls_state_so_idrequest_gets_the_clock()
    {
        var recording = Fast.Value;
        var start = SimTime.Parse("07:00", null);
        await using var server = Start(recording, new ReplayOptions { Speed = double.PositiveInfinity, StartAt = start });
        await using var client = Client(server);
        await using var collector = new Collector(client, _timeout.Token);
        var expected = recording.Messages.Count - recording.IndexOf(start) + 1;

        await collector.WaitUntil(m => m.Count >= expected);

        var parser = new MessageParser();
        var events = collector.Messages.SelectMany(parser.Parse).ToList();
        events[0].Should().BeOfType<ClockEvent>().Which.Clock.Should().BeLessThan(start).And.BeGreaterThan(SimTime.Parse("06:55", null));
        events.OfType<TrainLocationEvent>().First().Time.Should().BeGreaterThan(SimTime.Parse("06:55", null));
        collector.Messages.Should().HaveCount(expected);
    }

    [Fact]
    public async Task Answers_snapshot_and_ttsummary_from_the_state_so_far()
    {
        var recording = WithTd.Value;
        var start = SimTime.Parse("05:00", null);
        var parser = new MessageParser();
        var objectsSoFar = recording.Messages.Take(recording.IndexOf(start))
            .SelectMany(m => parser.Parse(m.ToGatewayMessage()))
            .OfType<SignallingStateEvent>()
            .Select(e => e.ObjectId)
            .Distinct()
            .Count();
        await using var server = Start(recording, new ReplayOptions { Speed = 1, StartAt = start });
        await using var client = Client(server, [.. GatewayTopics.WithTrainDescriber, GatewayTopics.TrainMovementSummary]);
        client.ConnectionChanged += (_, c) =>
        {
            if (c.State == GatewayConnectionState.Connected)
            {
                _ = client.SendAsync(GatewayCommands.TimetableSummary());
            }
        };
        await using var collector = new Collector(client, _timeout.Token);

        await collector.WaitUntil(m => m.Any(x => x.Topic == GatewayTopics.TrainMovementSummary) && m.Any(x => x.Body.Length > 100_000));

        var snapshot = collector.Messages.First(m => m.Body.Length > 100_000);
        var signalling = parser.Parse(snapshot).Cast<SignallingStateEvent>().ToList();
        signalling.Should().HaveCount(objectsSoFar);
        signalling.Select(s => s.ObjectId).Should().Contain("T1620");
        var summary = parser.Parse(collector.Messages.First(m => m.Topic == GatewayTopics.TrainMovementSummary));
        summary.Should().ContainSingle().Which.Should().BeOfType<TimetableSummaryEvent>().Which.Trains.Should().HaveCount(1779);
        collector.Messages.First().Topic.Should().Be(GatewayTopics.SimSig);
    }

    [Fact]
    public async Task Every_subscriber_gets_the_stream_and_unsubscribing_stops_it()
    {
        await using var server = Start(Fast.Value, new ReplayOptions { Speed = double.PositiveInfinity, StartPaused = true });
        using var a = await RawClient.ConnectAsync(server.Port);
        using var b = await RawClient.ConnectAsync(server.Port);
        await a.SendAsync(Subscribe(GatewayTopics.TrainMovements, "7"));
        await b.SendAsync(Subscribe(GatewayTopics.TrainMovements, "1"));
        await b.SendAsync(new StompFrame(StompCommands.Unsubscribe, [new("id", "1")]));
        await b.SendAsync(new StompFrame(StompCommands.Subscribe, [new("destination", GatewayTopics.SimSig), new("id", "2"), new("receipt", "b-ready")]));
        await a.SendAsync(new StompFrame(StompCommands.Send, [new("destination", GatewayTopics.SimSig), new("receipt", "a-ready")], GatewayCommands.IdRequest().Body));
        (await a.ReadAsync()).GetHeader("receipt-id").Should().Be("a-ready");
        (await b.ReadAsync()).GetHeader("receipt-id").Should().Be("b-ready");

        server.Timeline.Resume();
        var fromA = await a.ReadAsync();
        var fromB = await b.ReadAsync();

        fromA.Command.Should().Be(StompCommands.Message);
        fromA.GetHeader("destination").Should().Be(GatewayTopics.TrainMovements);
        fromA.GetHeader("subscription").Should().Be("7");
        fromA.GetHeader("content-length").Should().Be(fromA.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fromB.GetHeader("destination").Should().Be(GatewayTopics.SimSig);
        fromB.GetHeader("subscription").Should().Be("2");
    }

    [Fact]
    public async Task Frames_before_connect_get_an_error()
    {
        await using var server = Start(Fast.Value);
        using var raw = await RawClient.ConnectAsync(server.Port, connect: false);

        await raw.SendAsync(Subscribe(GatewayTopics.SimSig, "0"));
        var reply = await raw.ReadAsync();

        reply.Command.Should().Be(StompCommands.Error);
        (await raw.ReadOrNullAsync()).Should().BeNull("the server closes the connection");
    }

    [Fact]
    public async Task Malformed_and_unknown_commands_are_ignored_and_disconnect_gets_a_receipt()
    {
        await using var server = Start(Fast.Value);
        using var raw = await RawClient.ConnectAsync(server.Port);

        await raw.SendAsync(new StompFrame(StompCommands.Send, [new("destination", GatewayTopics.TrainDescriber)], Encoding.UTF8.GetBytes("{\"routerequest\":")));
        await raw.SendAsync(new StompFrame(StompCommands.Send, [new("destination", GatewayTopics.TrainDescriber)], Encoding.UTF8.GetBytes("{\"a\":{}} trailing")));
        await raw.SendAsync(Command(GatewayCommands.RouteRequest("RHJ1AM")));
        await raw.SendAsync(Command(GatewayCommands.Snapshot()));
        await raw.SendAsync(new StompFrame(StompCommands.Disconnect, [new("receipt", "bye")]));

        var reply = await raw.ReadAsync();
        reply.Command.Should().Be(StompCommands.Receipt);
        reply.GetHeader("receipt-id").Should().Be("bye");
    }

    [Fact]
    public async Task A_busy_port_throws()
    {
        await using var first = Start(Fast.Value);

        var second = () => ReplayServer.Start(Fast.Value, new ReplayServerOptions { Port = first.Port });

        second.Should().Throw<SocketException>();
    }

    [Fact]
    public async Task A_client_that_goes_away_does_not_stop_playback()
    {
        await using var server = Start(Fast.Value, new ReplayOptions { Speed = double.PositiveInfinity });
        using (var raw = await RawClient.ConnectAsync(server.Port))
        {
            await raw.SendAsync(Subscribe(GatewayTopics.TrainMovements, "0"));
            await raw.SendAsync(Command(GatewayCommands.IdRequest()));
            await raw.ReadAsync();
        }

        await server.Completion.WaitAsync(Wait);
        await WaitFor(() => server.ClientCount == 0);
    }

    private static StompFrame Subscribe(string topic, string id) =>
        new(StompCommands.Subscribe, [new("destination", topic), new("id", id), new("ack", "auto")]);

    private static StompFrame Command(GatewayCommand command) =>
        new(StompCommands.Send, [new("destination", command.Topic)], command.Body);

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class RawClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StompFrameReader _reader;

        private RawClient(TcpClient tcp)
        {
            _tcp = tcp;
            _reader = new StompFrameReader(tcp.GetStream());
        }

        public static async Task<RawClient> ConnectAsync(int port, bool connect = true)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var client = new RawClient(tcp);
            if (connect)
            {
                await client.SendAsync(new StompFrame(StompCommands.Connect, [new("accept-version", "1.1"), new("host", "127.0.0.1")]));
                (await client.ReadAsync()).Command.Should().Be(StompCommands.Connected);
            }

            return client;
        }

        public async Task SendAsync(StompFrame frame) => await _tcp.GetStream().WriteAsync(StompFrameWriter.Encode(frame));

        public async Task<StompFrame> ReadAsync() => await ReadOrNullAsync() ?? throw new EndOfStreamException("The server closed the connection.");

        public async Task<StompFrame?> ReadOrNullAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            try
            {
                return await _reader.ReadFrameAsync(timeout.Token);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public void Dispose() => _tcp.Dispose();
    }
}
