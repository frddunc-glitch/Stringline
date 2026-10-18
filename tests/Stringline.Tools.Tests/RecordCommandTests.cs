using System.CommandLine;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Stomp;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline record</c> in-process against the fake gateway; cancelling stands in for Ctrl+C.</summary>
public sealed class RecordCommandTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-tools-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
    private FakeStompServer _server = null!;

    private string OutPath => Path.Combine(_dir, "sub", "rec.sslog");

    public Task InitializeAsync()
    {
        _server = FakeStompServer.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    public void Dispose()
    {
        _timeout.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private Task<int> Run(CancellationToken cancellationToken, params string[] args)
    {
        var root = new RootCommand { RecordCommand.Create() };
        return root.Parse(["record", "--host", "127.0.0.1", "--port", _server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), .. args])
            .InvokeAsync(null, cancellationToken);
    }

    [Fact]
    public async Task Records_until_cancelled_then_exits_cleanly()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
        var run = Run(stop.Token, "--out", OutPath);

        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);
        await _server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"testsim","clock":36000,"interval":500,"paused":false}}""");
        await _server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"2A02","uid":"3","action":"arrive","location":"WLOO","platform":"1","time":36030}}""");
        await WaitForMessages(2);
        await stop.CancelAsync();

        (await run).Should().Be(0);
        await _server.WaitForFrameAsync(StompCommands.Disconnect);
        using var reader = SslogReader.Open(OutPath);
        reader.Header.Port.Should().Be(_server.Port);
        var messages = reader.ReadEntries().OfType<SslogMessage>().ToList();
        messages.Should().HaveCount(2);
        messages[1].Sim.Should().Be(new SimTime(36000));
        _server.ReceivedFrames().Should().NotContain(f => f.GetHeader("login") != null);
    }

    [Fact]
    public async Task Refuses_to_overwrite_without_force()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutPath)!);
        await File.WriteAllTextAsync(OutPath, "keep me");

        (await Run(_timeout.Token, "--out", OutPath)).Should().Be(2);
        (await File.ReadAllTextAsync(OutPath)).Should().Be("keep me");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
        var run = Run(stop.Token, "--out", OutPath, "--force");
        await _server.WaitForSubscribersAsync(GatewayTopics.SimSig);
        await stop.CancelAsync();
        (await run).Should().Be(0);
        (await File.ReadAllTextAsync(OutPath)).Should().StartWith("{\"sslog\":1");
    }

    [Fact]
    public async Task Uses_the_password_from_the_environment_and_reports_rejection()
    {
        _server.OnConnect = _ => new StompFrame(StompCommands.Error, [new("message", "Login failed")]);
        Environment.SetEnvironmentVariable(RecordCommand.PasswordVariable, "hunter2");
        try
        {
            (await Run(_timeout.Token, "--out", OutPath, "--user", " fred ")).Should().Be(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RecordCommand.PasswordVariable, null);
        }

        var connect = _server.ReceivedFrames().First(f => f.Command == StompCommands.Connect);
        connect.GetHeader("login").Should().Be("fred");
        connect.GetHeader("passcode").Should().Be("hunter2");
        (await File.ReadAllTextAsync(OutPath)).Should().Contain("Login failed").And.NotContain("hunter2");
    }

    [Fact]
    public async Task TD_and_ttsummary_options_add_topics_and_requests()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_timeout.Token);
        var run = Run(stop.Token, "--out", OutPath, "--td", "--ttsummary", "--verbose");

        await _server.WaitForSubscribersAsync(GatewayTopics.TrainMovementSummary);
        await _server.WaitForFrameAsync(f => f.BodyText == """{"ttsummary":{}}""");
        var frames = _server.ReceivedFrames();
        await stop.CancelAsync();
        (await run).Should().Be(0);

        frames.Where(f => f.Command == StompCommands.Subscribe).Select(f => f.GetHeader("destination")).Should().Equal(
            GatewayTopics.TrainMovements, GatewayTopics.SimSig, GatewayTopics.TrainDescriber, GatewayTopics.TrainMovementSummary);
        frames.Should().Contain(f => f.BodyText == """{"snapshot":{}}""");
    }

    [Fact]
    public async Task Out_is_required()
    {
        var root = new RootCommand { RecordCommand.Create() };

        (await root.Parse(["record"]).InvokeAsync(null, _timeout.Token)).Should().NotBe(0);
    }

    [Fact]
    public void Summary_lists_counts_by_kind()
    {
        var stats = new RecordingStats(DateTimeOffset.UtcNow.AddMinutes(-1));

        var text = RecordCommand.Summary(stats, 0, "x.sslog");

        text.Should().Contain("Recorded 0 messages").And.Contain("x.sslog").And.Contain("Sim: ?");
    }

    private async Task WaitForMessages(int count)
    {
        while (true)
        {
            if (File.Exists(OutPath))
            {
                using var stream = new FileStream(OutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var text = new StreamReader(stream);
                var lines = (await text.ReadToEndAsync(_timeout.Token)).Split('\n').Count(l => l.Contains("\"topic\"", StringComparison.Ordinal));
                if (lines >= count)
                {
                    return;
                }
            }

            await Task.Delay(50, _timeout.Token);
        }
    }
}
