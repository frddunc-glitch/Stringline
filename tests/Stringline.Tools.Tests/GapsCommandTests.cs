using System.CommandLine;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline gaps</c> (S36) in-process against the Waterloo fixtures.</summary>
public sealed class GapsCommandTests
{
    private static string Wtt => Fixtures.Path("waterloo 2010.WTT");

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { GapsCommand.Create() };
        var exit = await root.Parse(["gaps", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Prints_the_baseline_for_a_recording()
    {
        var (exit, output, error) = await Run("--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo1x));

        exit.Should().Be(0, error);
        var lines = output.Split(Environment.NewLine);
        lines[0].Should().Be("04:30:00 to 05:52:31: 35 matched workings, 2 corridors.");
        lines[1].Should().Be("Timing location reports: 217; not in the train's WTT: 66 (30.4 %), 66 of them on a corridor.");
        lines.Should().ContainMatch("  booked timing locations *68 *122 *347 *371");
        lines.Should().ContainMatch("  all timing locations *133 *83 *137 *150");
        lines.Should().ContainMatch("  with signals *461 *23 *54 *83");
        lines.Should().NotContain(l => l.Contains("placed signals", StringComparison.Ordinal), "the generated profile has no learned places");
    }

    [Fact]
    public async Task With_learned_places_the_gaps_between_placed_signals_are_measured_too()
    {
        var folder = Directory.CreateTempSubdirectory("stringline-gaps-");
        try
        {
            var profile = Path.Combine(folder.FullName, "waterloo.json");
            var root = new RootCommand { LearnCommand.Create() };
            (await root.Parse(["learn", "--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--out", profile]).InvokeAsync(new InvocationConfiguration { Output = new StringWriter(), Error = new StringWriter() })).Should().Be(0);

            var (exit, output, error) = await Run("--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--profile", profile);

            exit.Should().Be(0, error);
            output.Split(Environment.NewLine).Should().ContainMatch("  placed signals (S38) *269 *29 *107 *145", "learned from this one recording, the placed signals bring the median gap down from 83 s");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Needs_a_recording_or_a_session()
    {
        var (exit, _, error) = await Run("--wtt", Wtt);

        exit.Should().Be(2);
        error.Should().Contain("Give either --log or --session.");
    }
}
