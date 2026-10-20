using System.Collections.Concurrent;
using Stringline.Core.Events;
using Stringline.Gateway.Stomp;

namespace Stringline.Gateway.Tests;

public sealed class GatewayClientTests : IAsyncLifetime, IDisposable
{
    private readonly CancellationTokenSource _testTimeout = new(TimeSpan.FromSeconds(30));
    private FakeStompServer _server = null!;

    public Task InitializeAsync()
    {
        _server = FakeStompServer.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    public void Dispose() => _testTimeout.Dispose();

    private GatewayClientOptions Options(Func<GatewayClientOptions, GatewayClientOptions>? change = null)
    {
        var options = new GatewayClientOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            MinReconnectDelay = TimeSpan.FromMilliseconds(20),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(80),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        return change is null ? options : change(options);
    }

    /// <summary>Reads the client's messages into a queue in the background.</summary>
    private sealed class Pump : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;

        public Pump(GatewayClient client, CancellationToken token)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task = Task.Run(async () =>
            {
                await foreach (var m in client.ReadAllAsync(_stop.Token))
                {
                    Messages.Add(m);
                }
            }, CancellationToken.None);
        }

        public BlockingCollection<GatewayMessage> Messages { get; } = [];

        public Task Task { get; }

        public GatewayMessage Next() =>
            Messages.TryTake(out var m, FakeStompServer.Timeout) ? m : throw new TimeoutException("No message.");

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _stop.Dispose();
        }
    }

    [Fact]
    public async Task Connects_subscribes_requests_identity_and_yields_messages()
    {
        await using var client = new GatewayClient(Options());
        var changes = new ConcurrentQueue<GatewayConnectionChange>();
        client.ConnectionChanged += (_, c) => changes.Enqueue(c);
        await using var pump = new Pump(client, _testTimeout.Token);

        var connect = await _server.WaitForFrameAsync(StompCommands.Connect);
        connect.Headers.Should().Equal(new KeyValuePair<string, string>("accept-version", "1.1"), new KeyValuePair<string, string>("host", "127.0.0.1"));

        var subscribe1 = await _server.WaitForFrameAsync(StompCommands.Subscribe);
        var subscribe2 = await _server.WaitForFrameAsync(StompCommands.Subscribe);
        new[] { subscribe1, subscribe2 }.Select(f => f.GetHeader("destination")).Should().Equal(GatewayTopics.TrainMovements, GatewayTopics.SimSig);
        new[] { subscribe1, subscribe2 }.Select(f => f.GetHeader("id")).Should().Equal("0", "1");
        subscribe1.GetHeader("ack").Should().Be("auto");

        var idRequest = await _server.WaitForFrameAsync(StompCommands.Send);
        idRequest.GetHeader("destination").Should().Be(GatewayTopics.SimSig);
        idRequest.BodyText.Should().Be("""{"idrequest":{}}""");

        await _server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"clock":1,"interval":500}}""");
        await _server.PublishAsync(GatewayTopics.TrainMovements, "{\"a\":1}"u8.ToArray(), withContentLength: false);

        var first = pump.Next();
        first.Topic.Should().Be(GatewayTopics.SimSig);
        first.BodyText.Should().Contain("clock_msg");
        first.ReceivedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        pump.Next().BodyText.Should().Be("{\"a\":1}");

        client.State.Should().Be(GatewayConnectionState.Connected);
        changes.Select(c => c.State).Should().StartWith([GatewayConnectionState.Connecting, GatewayConnectionState.Connected]);
        var connected = changes.First(c => c.State == GatewayConnectionState.Connected);
        connected.Detail.Should().Be("FakeSimSig/1.0");
        connected.ConnectedHeaders.Should().Contain(new KeyValuePair<string, string>("version", "1.1"));
    }

    [Fact]
    public async Task Sends_credentials_but_never_shows_the_passcode()
    {
        var options = Options(o => new GatewayClientOptions
        {
            Host = o.Host, Port = o.Port, Login = "fred", Passcode = "s3cret:with\\chars", SendIdRequestOnConnect = false,
        });
        await using var client = new GatewayClient(options);
        var details = new ConcurrentQueue<string?>();
        client.ConnectionChanged += (_, c) => details.Enqueue(c.Detail);
        await using var pump = new Pump(client, _testTimeout.Token);

        var connect = await _server.WaitForFrameAsync(StompCommands.Connect);

        connect.GetHeader("login").Should().Be("fred");
        connect.GetHeader("passcode").Should().Be("s3cret:with\\chars");
        options.ToString().Should().Contain("login=fred").And.Contain("passcode=***").And.NotContain("s3cret");
        details.Should().NotContain(d => d != null && d.Contains("s3cret", StringComparison.Ordinal));
        new GatewayClientOptions().ToString().Should().Contain("passcode=(none)");
    }

    [Fact]
    public async Task Requests_a_snapshot_when_subscribed_to_TD()
    {
        await using var client = new GatewayClient(Options(o => new GatewayClientOptions
        {
            Host = o.Host, Port = o.Port, Topics = GatewayTopics.WithTrainDescriber, SendIdRequestOnConnect = false,
        }));
        await using var pump = new Pump(client, _testTimeout.Token);

        var send = await _server.WaitForFrameAsync(StompCommands.Send);

        send.GetHeader("destination").Should().Be(GatewayTopics.TrainDescriber);
        send.BodyText.Should().Be("""{"snapshot":{}}""");
        _server.ReceivedFrames().Count(f => f.Command == StompCommands.Subscribe).Should().Be(3);
    }

    [Fact]
    public async Task SendAsync_delivers_commands_and_fails_when_not_connected()
    {
        await using var client = new GatewayClient(Options());
        await client.Invoking(c => c.SendAsync(GatewayCommands.Snapshot())).Should().ThrowAsync<InvalidOperationException>();

        await using var pump = new Pump(client, _testTimeout.Token);
        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);
        await _server.WaitForFrameAsync(f => f.BodyText.Contains("idrequest", StringComparison.Ordinal));

        await client.SendAsync(GatewayCommands.RouteRequest("RHJ1AM"));

        var frame = await _server.WaitForFrameAsync(StompCommands.Send);
        frame.GetHeader("destination").Should().Be(GatewayTopics.TrainDescriber);
        frame.GetHeader("content-length").Should().Be(frame.Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        frame.BodyText.Should().Be("""{"routerequest":{"route":"RHJ1AM"}}""");
    }

    [Fact]
    public async Task Reconnects_and_resubscribes_after_the_server_drops_the_connection()
    {
        await using var client = new GatewayClient(Options());
        var changes = new ConcurrentQueue<GatewayConnectionChange>();
        client.ConnectionChanged += (_, c) => changes.Enqueue(c);
        await using var pump = new Pump(client, _testTimeout.Token);
        await _server.WaitForSubscribersAsync(GatewayTopics.TrainMovements);

        _server.DropConnections();

        await _server.WaitForSubscribersAsync(GatewayTopics.TrainMovements);
        await _server.PublishAsync(GatewayTopics.TrainMovements, "{\"after\":true}");
        pump.Next().BodyText.Should().Be("{\"after\":true}");
        _server.ConnectionCount.Should().Be(2);
        var drop = changes.First(c => c.State == GatewayConnectionState.Disconnected);
        drop.RetryIn.Should().Be(TimeSpan.FromMilliseconds(20));
        changes.Count(c => c.State == GatewayConnectionState.Connected).Should().Be(2);
    }

    [Fact]
    public async Task Retries_with_back_off_until_the_gateway_appears()
    {
        var port = FakeStompServer.FreePort();
        await using var client = new GatewayClient(Options(o => new GatewayClientOptions
        {
            Host = o.Host, Port = port, MinReconnectDelay = TimeSpan.FromMilliseconds(10), MaxReconnectDelay = TimeSpan.FromMilliseconds(40),
        }));
        var retries = new ConcurrentQueue<TimeSpan>();
        client.ConnectionChanged += (_, c) =>
        {
            if (c.RetryIn is { } delay)
            {
                retries.Enqueue(delay);
            }
        };
        await using var pump = new Pump(client, _testTimeout.Token);

        while (retries.Count < 4)
        {
            await Task.Delay(10, _testTimeout.Token);
        }

        await using var late = FakeStompServer.Start(port);
        await late.WaitForSubscribersAsync(GatewayTopics.SimSig);

        retries.Take(4).Select(r => r.TotalMilliseconds).Should().Equal(10, 20, 40, 40);
    }

    [Fact]
    public async Task ERROR_in_reply_to_CONNECT_is_fatal_and_not_retried()
    {
        _server.OnConnect = _ => new StompFrame(StompCommands.Error, [new("message", "Authentication failed")], "Bad password"u8.ToArray());
        await using var client = new GatewayClient(Options());

        var act = async () =>
        {
            await foreach (var _ in client.ReadAllAsync(_testTimeout.Token))
            {
            }
        };

        await act.Should().ThrowAsync<GatewayConnectException>().WithMessage("*Authentication failed — Bad password*");
        _server.ConnectionCount.Should().Be(1);
        client.State.Should().Be(GatewayConnectionState.Disconnected);
    }

    [Theory]
    [InlineData(null, "", "(no details)")]
    [InlineData(null, "body only", "body only")]
    [InlineData("header only", "", "header only")]
    public async Task ERROR_details_come_from_message_header_and_body(string? message, string body, string expected)
    {
        _server.OnConnect = _ => new StompFrame(StompCommands.Error, message is null ? [] : [new("message", message)], System.Text.Encoding.UTF8.GetBytes(body));
        await using var client = new GatewayClient(Options());

        var act = async () =>
        {
            await foreach (var _ in client.ReadAllAsync(_testTimeout.Token))
            {
            }
        };

        await act.Should().ThrowAsync<GatewayConnectException>().WithMessage($"*{expected}");
    }

    [Fact]
    public async Task Without_reconnect_a_refused_connection_ends_with_the_error()
    {
        await using var client = new GatewayClient(Options(o => new GatewayClientOptions { Host = o.Host, Port = FakeStompServer.FreePort(), Reconnect = false }));

        var act = async () =>
        {
            await foreach (var _ in client.ReadAllAsync(_testTimeout.Token))
            {
            }
        };

        await act.Should().ThrowAsync<System.Net.Sockets.SocketException>();
    }

    [Fact]
    public async Task Without_reconnect_a_closed_connection_ends_the_stream()
    {
        await using var client = new GatewayClient(Options(o => new GatewayClientOptions { Host = o.Host, Port = o.Port, Reconnect = false }));
        var read = Task.Run(async () =>
        {
            var count = 0;
            await foreach (var _ in client.ReadAllAsync(_testTimeout.Token))
            {
                count++;
            }

            return count;
        });
        await _server.WaitForSubscribersAsync(GatewayTopics.TrainMovements);
        await _server.PublishAsync(GatewayTopics.TrainMovements, "{}");
        await _server.WaitForFrameAsync(f => f.BodyText.Contains("idrequest", StringComparison.Ordinal));

        // The message is sent before the drop, so it arrives first.
        await Task.Delay(100, _testTimeout.Token);
        _server.DropConnections();

        (await read).Should().Be(1);
    }

    [Fact]
    public async Task Times_out_waiting_for_CONNECTED_and_retries()
    {
        var attempts = 0;
        _server.OnConnect = _ => Interlocked.Increment(ref attempts) == 1
            ? null
            : new StompFrame(StompCommands.Connected, [new("version", "1.1")]);
        await using var client = new GatewayClient(Options(o => new GatewayClientOptions
        {
            Host = o.Host, Port = o.Port, ConnectTimeout = TimeSpan.FromSeconds(1), MinReconnectDelay = TimeSpan.FromMilliseconds(10),
        }));
        var reasons = new ConcurrentQueue<string?>();
        client.ConnectionChanged += (_, c) =>
        {
            if (c.State == GatewayConnectionState.Disconnected)
            {
                reasons.Enqueue(c.Detail);
            }
        };
        await using var pump = new Pump(client, _testTimeout.Token);

        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);

        // Only the first attempt is ignored; a slow runner may time out a later one too.
        attempts.Should().BeGreaterThanOrEqualTo(2);
        reasons.Should().NotBeEmpty().And.OnlyContain(r => r != null && r.Contains("No CONNECTED reply", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unexpected_reply_or_ERROR_after_connecting_causes_a_reconnect()
    {
        var attempts = 0;
        _server.OnConnect = _ => Interlocked.Increment(ref attempts) == 1
            ? new StompFrame(StompCommands.Receipt)
            : new StompFrame(StompCommands.Connected);
        await using var client = new GatewayClient(Options());
        var reasons = new ConcurrentQueue<string?>();
        client.ConnectionChanged += (_, c) =>
        {
            if (c.State == GatewayConnectionState.Disconnected)
            {
                reasons.Enqueue(c.Detail);
            }
        };
        await using var pump = new Pump(client, _testTimeout.Token);
        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);

        await _server.SendRawAsync(StompFrameWriter.Encode(new StompFrame(StompCommands.Receipt, [new("receipt-id", "1")])));
        await _server.SendRawAsync(StompFrameWriter.Encode(new StompFrame(StompCommands.Error, [new("message", "Sim closing")])));

        while (reasons.Count < 2)
        {
            await Task.Delay(10, _testTimeout.Token);
        }

        reasons.Should().SatisfyRespectively(
            r => r.Should().Contain("Expected CONNECTED but got RECEIPT"),
            r => r.Should().Contain("Sim closing"));
    }

    [Fact]
    public async Task Cancelling_sends_DISCONNECT_and_ends_with_OperationCanceled()
    {
        await using var client = new GatewayClient(Options());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_testTimeout.Token);
        var read = Task.Run(async () =>
        {
            await foreach (var _ in client.ReadAllAsync(cts.Token))
            {
            }
        });
        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);

        await cts.CancelAsync();

        await read.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
        await _server.WaitForFrameAsync(StompCommands.Disconnect);
        client.State.Should().Be(GatewayConnectionState.Disconnected);

        // The client can be read again after a stop.
        await using var again = new Pump(client, _testTimeout.Token);
        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);
    }

    [Fact]
    public async Task Only_one_reader_at_a_time_and_none_after_dispose()
    {
        var client = new GatewayClient(Options());
        await using (var pump = new Pump(client, _testTimeout.Token))
        {
            await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);
            var second = async () =>
            {
                await foreach (var _ in client.ReadAllAsync(_testTimeout.Token))
                {
                }
            };

            await second.Should().ThrowAsync<InvalidOperationException>();
        }

        await client.DisposeAsync();
        await client.DisposeAsync();
        var afterDispose = async () =>
        {
            await foreach (var _ in client.ReadAllAsync())
            {
            }
        };
        await afterDispose.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_throwing_ConnectionChanged_handler_does_not_break_the_client()
    {
        await using var client = new GatewayClient(Options());
        client.ConnectionChanged += (_, _) => throw new InvalidOperationException("handler bug");
        await using var pump = new Pump(client, _testTimeout.Token);
        await _server.WaitForSubscribersAsync(GatewayTopics.TrainMovements);

        await _server.PublishAsync(GatewayTopics.TrainMovements, "{}");

        pump.Next().BodyText.Should().Be("{}");
    }

    [Fact]
    public async Task LiveGatewaySource_parses_messages_into_events()
    {
        await using var client = new GatewayClient(Options());
        var source = new LiveGatewaySource(client, new MessageParser());
        var events = new BlockingCollection<GatewayEvent>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_testTimeout.Token);
        var read = Task.Run(async () =>
        {
            await foreach (var e in source.ReadAllAsync(cts.Token))
            {
                events.Add(e);
            }
        });
        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);

        await _server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"test","clock":3600,"interval":250},"mystery":{}}""");

        events.TryTake(out var first, FakeStompServer.Timeout).Should().BeTrue();
        first.Should().BeOfType<ClockEvent>().Which.SpeedRatio.Should().Be(2);
        events.TryTake(out var second, FakeStompServer.Timeout).Should().BeTrue();
        second.Should().BeOfType<UnknownEvent>();
        await cts.CancelAsync();
        await read.Invoking(r => r).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Validates_options()
    {
        FluentActions.Invoking(() => new GatewayClient(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new GatewayClient(new GatewayClientOptions { Host = " " })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new GatewayClient(new GatewayClientOptions { Port = 0 })).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new GatewayClient(new GatewayClientOptions { Port = 70000 })).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new LiveGatewaySource(null!, new MessageParser())).Should().Throw<ArgumentNullException>();
    }
}
