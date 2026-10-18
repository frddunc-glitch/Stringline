using System.CommandLine;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline match</c> in-process against the Waterloo fixtures.</summary>
public sealed class MatchCommandTests
{
    private static string Wtt => Fixtures.Path("waterloo 2010.WTT");

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { MatchCommand.Create() };
        var exit = await root.Parse(["match", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Prints_a_summary()
    {
        var (exit, output, error) = await Run("--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo4x));

        exit.Should().Be(0);
        error.Should().BeEmpty();
        var lines = output.Split(Environment.NewLine);
        lines[0].Should().Be("waterloo-4x.sslog against waterloo 2010.WTT: sim waterloo, 05:52:31 to 08:05:11.");
        lines[1].Should().Be("128 live trains, 205 workings: 205 matched (100.0 %), 0 pending, 0 unmatched.");
        lines[2].Should().Be("Matched by: headcode+time 127, next working 77, formed 1.");
        lines.Should().NotContain(l => l.StartsWith("Uid", StringComparison.Ordinal), "nothing is unmatched");
    }

    [Fact]
    public async Task Lists_every_working_with_all()
    {
        var (exit, output, _) = await Run("--wtt", Wtt, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--all");

        exit.Should().Be(0);
        var lines = output.Split(Environment.NewLine);
        lines.Should().Contain(l => l.StartsWith("Uid ", StringComparison.Ordinal) && l.Contains("Headcode") && l.EndsWith("Note", StringComparison.Ordinal));
        lines.Should().ContainMatch("15 *2H01 *Matched *2H01 *next working *depart WLOO *");
        lines.Should().ContainMatch("13 *2P05 *Matched *2P05 *formed *1 *depart WLOO 05:00:01*");
        lines.Count(l => l.Contains(" Matched ", StringComparison.Ordinal)).Should().Be(35);
    }

    [Fact]
    public async Task Lists_unmatched_workings()
    {
        // Against the wrong timetable nothing matches.
        var (exit, output, _) = await Run("--wtt", Fixtures.Path("llangollen 2012 B.WTT"), "--log", Fixtures.Path(Fixtures.Waterloo1x));

        exit.Should().Be(0);
        output.Should().Contain("0 matched (0.0 %)");
        output.Should().MatchRegex(@"\n2 +1W51 +Unmatched +arrive WLOO 04:30:10 +No train 1W51 in the timetable");
    }

    [Fact]
    public async Task Reports_a_missing_file()
    {
        var (exit, _, error) = await Run("--wtt", Wtt, "--log", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.sslog"));

        exit.Should().Be(2);
        error.Should().StartWith("Cannot read input:");
    }
}
