using Stringline.Core;
using Stringline.Gateway.Recording;

namespace Stringline.Gateway.Tests;

public sealed class SessionRecorderTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("stringline-rec-");
    private readonly MessageParser _parser = new();
    private FakeStompServer _server = null!;

    public Task InitializeAsync()
    {
        _server = FakeStompServer.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    public void Dispose() => _folder.Delete(recursive: true);

    private void Feed(SessionRecorder recorder, string topic, string body, double seconds = 0)
    {
        var message = new GatewayMessage(T0.AddSeconds(seconds), topic, body);
        recorder.OnMessage(message, _parser.Parse(message));
    }

    [Fact]
    public void Records_only_while_on_and_stamps_frames_with_the_clock_seen_before()
    {
        using var recorder = new SessionRecorder();
        var path = Path.Combine(_folder.FullName, "sub", "a.sslog");
        var changes = 0;
        recorder.StateChanged += (_, _) => changes++;

        Feed(recorder, GatewayTopics.SimSig, """{"clock_msg":{"area_id":"test","clock":21600,"interval":500}}""");
        recorder.IsRecording.Should().BeFalse();

        recorder.Start(path, "localhost", 51515, GatewayTopics.Default);
        recorder.IsRecording.Should().BeTrue();
        recorder.Path.Should().Be(path);
        Feed(recorder, GatewayTopics.TrainMovements, """{"train_location":{"headcode":"1A01","uid":"1","action":"pass","location":"VAUX","time":21610}}""", 1);
        recorder.OnConnection(new GatewayConnectionChange(T0.AddSeconds(2), GatewayConnectionState.Disconnected, "gone"));
        recorder.MessagesWritten.Should().Be(1);
        recorder.Stop();
        recorder.Stop();
        Feed(recorder, GatewayTopics.TrainMovements, """{"train_location":{"headcode":"1A01","uid":"1","action":"pass","location":"WLOO","time":21620}}""", 3);

        changes.Should().Be(2);
        recorder.IsRecording.Should().BeFalse();
        recorder.MessagesWritten.Should().Be(1, "the count of the last recording stays");
        var recording = SslogRecording.Load(path);
        recording.Header.Host.Should().Be("localhost");
        recording.Messages.Should().ContainSingle().Which.Sim.Should().Be(new SimTime(21600));
        recording.Connections.Should().ContainSingle().Which.State.Should().Be(GatewayConnectionState.Disconnected);
    }

    [Fact]
    public void Starting_again_finishes_the_previous_file()
    {
        using var recorder = new SessionRecorder();
        var first = Path.Combine(_folder.FullName, "1.sslog");
        var second = Path.Combine(_folder.FullName, "2.sslog");

        recorder.Start(first, "h", 1, GatewayTopics.Default);
        Feed(recorder, GatewayTopics.SimSig, """{"clock_msg":{"area_id":"test","clock":21600,"interval":500}}""");
        recorder.Start(second, "h", 1, GatewayTopics.Default);
        Feed(recorder, GatewayTopics.SimSig, """{"clock_msg":{"area_id":"test","clock":21660,"interval":500}}""");
        recorder.Dispose();

        SslogRecording.Load(first).Messages.Should().ContainSingle();
        SslogRecording.Load(second).Messages.Should().ContainSingle().Which.Sim.Should().Be(new SimTime(21660));
    }

    [Fact]
    public void Will_not_overwrite_a_recording()
    {
        using var recorder = new SessionRecorder();
        var path = Path.Combine(_folder.FullName, "x.sslog");
        File.WriteAllText(path, "keep");

        var act = () => recorder.Start(path, "h", 1, GatewayTopics.Default);

        act.Should().Throw<IOException>();
        recorder.IsRecording.Should().BeFalse();
        File.ReadAllText(path).Should().Be("keep");
    }

    [Fact]
    public void New_paths_are_named_by_sim_and_time()
    {
        var now = new DateTimeOffset(2026, 10, 19, 14, 5, 9, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 19, 14, 5, 9, DateTimeKind.Local)));

        Path.GetFileName(SessionRecorder.NewPath("r", "waterloo", now)).Should().Be("waterloo-20261019-140509.sslog");
        Path.GetFileName(SessionRecorder.NewPath("r", null, now)).Should().Be("simsig-20261019-140509.sslog");
        Path.GetFileName(SessionRecorder.NewPath("r", "a/b", now)).Should().NotContain("/");
    }

    [Fact]
    public async Task The_live_source_hands_every_frame_to_its_listeners()
    {
        await using var client = new GatewayClient(new GatewayClientOptions { Host = "127.0.0.1", Port = _server.Port, ConnectTimeout = TimeSpan.FromSeconds(5) });
        var source = new LiveGatewaySource(client, _parser);
        source.Client.Should().BeSameAs(client);
        var parsed = new List<ParsedMessage>();
        source.MessageParsed += (_, m) => parsed.Add(m);
        using var cts = new CancellationTokenSource(FakeStompServer.Timeout);

        var reading = Task.Run(async () =>
        {
            await foreach (var e in source.ReadAllAsync(cts.Token))
            {
                return e;
            }

            return null;
        });
        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);
        await _server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"test","clock":3600,"interval":500}}""");

        var first = await reading;
        first.Should().BeOfType<Core.Events.ClockEvent>();
        parsed.Should().ContainSingle().Which.Events.Should().ContainSingle().Which.Should().BeSameAs(first);
        parsed[0].Message.Topic.Should().Be(GatewayTopics.SimSig);
    }
}
