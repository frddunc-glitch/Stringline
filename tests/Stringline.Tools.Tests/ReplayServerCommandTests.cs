using System.Collections.Concurrent;
using System.CommandLine;
using System.Globalization;
using System.Net;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline replay-server</c> in-process with a real <see cref="GatewayClient"/> connecting to it.</summary>
public sealed class ReplayServerCommandTests : IDisposable
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

    public void Dispose() => _timeout.Dispose();

    private static string Recording => Fixtures.Path(Fixtures.Waterloo4x);

    private Task<int> Run(params string[] args)
    {
        var root = new RootCommand { ReplayServerCommand.Create() };
        return root.Parse(["replay-server", .. args]).InvokeAsync(null, _timeout.Token);
    }

    [Fact]
    public async Task Plays_a_recording_to_a_client_from_the_start_time_and_exits_at_the_end()
    {
        var port = FakeStompServer.FreePort();
        var run = Run(Recording, "--port", port.ToString(CultureInfo.InvariantCulture), "--speed", "0", "--start", "07:30", "--exit-at-end");
        await using var client = new GatewayClient(new GatewayClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            MinReconnectDelay = TimeSpan.FromMilliseconds(50),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
        });
        var source = new LiveGatewaySource(client, new MessageParser());
        var events = new ConcurrentQueue<GatewayEvent>();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
        var read = Task.Run(async () =>
        {
            await foreach (var e in source.ReadAllAsync(stop.Token))
            {
                events.Enqueue(e);
            }
        }, CancellationToken.None);

        (await run).Should().Be(0);

        // The server flushed everything before exiting; give the client a moment to read it.
        var lastClock = new Core.SimTime(29111);
        for (var i = 0; i < 200 && !events.OfType<ClockEvent>().Any(c => c.Clock == lastClock); i++)
        {
            await Task.Delay(50, _timeout.Token);
        }

        await stop.CancelAsync();
        await read.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);

        events.Should().NotBeEmpty();
        events.OfType<ClockEvent>().First().Clock.Should().BeLessThan(Core.SimTime.Parse("07:30", null));
        events.OfType<ClockEvent>().Last().Clock.Should().Be(lastClock);
        events.OfType<UnknownEvent>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("--start", "nonsense")]
    [InlineData("--host", "not-an-address")]
    [InlineData("--speed", "-1")]
    [InlineData("--port", "70000")]
    public async Task Bad_options_exit_with_2(string option, string value)
    {
        (await Run(Recording, $"{option}={value}", "--exit-at-end")).Should().Be(2);
    }

    [Fact]
    public async Task A_missing_or_unreadable_recording_exits_with_2()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"stringline-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var junk = Path.Combine(dir, "junk.sslog");
            await File.WriteAllTextAsync(junk, "not a recording");

            (await Run(Path.Combine(dir, "missing.sslog"))).Should().Be(2);
            (await Run(junk)).Should().Be(2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_busy_port_exits_with_2()
    {
        await using var busy = ReplayServer.Start(SslogRecording.Load(Recording), new ReplayServerOptions { Port = 0 });

        (await Run(Recording, "--port", busy.Port.ToString(CultureInfo.InvariantCulture))).Should().Be(2);
    }

    [Theory]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData(" ANY ", "0.0.0.0")]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("::1", "::1")]
    public void Parses_listen_addresses(string host, string expected) =>
        ReplayServerCommand.ParseAddress(host).Should().Be(IPAddress.Parse(expected));

    [Fact]
    public void Rejects_host_names() => ReplayServerCommand.ParseAddress("simsig.example").Should().BeNull();

    [Theory]
    [InlineData(1, "1×")]
    [InlineData(0.5, "0.5×")]
    [InlineData(double.PositiveInfinity, "max speed")]
    public void Formats_speeds(double speed, string expected) => ReplayServerCommand.FormatSpeed(speed).Should().Be(expected);

    [Fact]
    public void Describes_a_recording()
    {
        var text = ReplayServerCommand.Describe(SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo1x)), "w.sslog");

        text.Should().Contain("w.sslog: sim waterloo, 04:30:00 to 05:52:31, 8437 frames over 01:12:58")
            .And.Contain("/topic/TRAIN_MVT_SUMMARY");
    }
}
