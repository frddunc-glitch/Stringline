using System.CommandLine;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Persistence;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline learn</c> (S37) in-process against the Waterloo fixtures.</summary>
public sealed class LearnCommandTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("stringline-learn-").FullName;

    private static string Wtt => Fixtures.Path("waterloo 2010.WTT");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { LearnCommand.Create() };
        var exit = await root.Parse(["learn", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Learns_from_two_recordings_compares_them_and_writes_the_profile()
    {
        var target = Path.Combine(_folder, "waterloo.json");

        var (exit, output, error) = await Run("--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--log", Fixtures.Path(Fixtures.WaterlooFailure), "--report", "--out", target);

        exit.Should().Be(0, error);
        var lines = output.Split(Environment.NewLine);
        lines[0].Should().Be("waterloo-1x.sslog (04:30:00 to 05:52:31): 139 runs between timing locations, 117 usable; samples: 297 signal, 521 track circuit, 306 berth (1124 new).");
        lines[1].Should().StartWith("  Track circuits occupied: 1014; put to one train 680 (67.1 %)");
        lines.Should().Contain("Signals passed by 3 or more trains: 113, used 113 (100.0 %).");
        lines.Should().ContainMatch("waterloo-1x.sslog against waterloo-failure.sslog: 21 signals used in both; places move median 0.000*0 move more than their spread*");
        lines.Should().Contain("CJML → EARLSFD");
        lines.Should().ContainMatch("  Sig S197    -  0.295 * 5 samples  spread 0.064  used");
        lines.Should().ContainMatch("  TC  T1061   -  0.095–0.419 * used  signal S191");
        lines.Should().Contain("Wrote waterloo.json.");

        var profile = ProfileJson.Deserialize(await File.ReadAllTextAsync(target));
        profile.Places.Sessions.Should().HaveCount(2);
        profile.Places.Find(SignallingKind.Signal, "S197", new("CJML"), new("EARLSFD"))!.SampleCount.Should().Be(5);
    }

    [Fact]
    public async Task Learning_into_a_profile_again_adds_nothing()
    {
        var target = Path.Combine(_folder, "waterloo.json");
        (await Run("--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--out", target)).Exit.Should().Be(0);

        var (exit, output, _) = await Run("--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--profile", target);

        exit.Should().Be(0);
        output.Should().Contain("(0 new)");
    }

    [Fact]
    public async Task Learns_from_a_saved_live_session_as_from_its_recording()
    {
        // A live session keeps the signal and track circuit states too (S38), so it teaches what the recording does.
        string path;
        var parser = new MessageParser();
        using (var writer = SessionWriter.Create(_folder, new SessionInfo { Kind = SavedSessionKind.Live, Host = "localhost", Port = 51515 }, new SessionWriterOptions { AutosaveInterval = TimeSpan.Zero }))
        {
            foreach (var frame in SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo1x)).Messages)
            {
                var message = frame.ToGatewayMessage();
                writer.OnMessage(message, parser.Parse(message));
            }

            writer.Flush();
            path = writer.Path!;
        }

        var (exit, output, error) = await Run("--wtt", Wtt, "--log", path);

        exit.Should().Be(0, error);
        output.Split(Environment.NewLine)[0].Should().Be($"{Path.GetFileName(path)} (04:30:00 to 05:52:31): 139 runs between timing locations, 117 usable; samples: 297 signal, 521 track circuit, 306 berth (1124 new).");
    }

    [Fact]
    public async Task Needs_a_recording()
    {
        var (exit, _, error) = await Run("--wtt", Wtt);

        exit.Should().NotBe(0);
        error.Should().Contain("--log");
    }
}
