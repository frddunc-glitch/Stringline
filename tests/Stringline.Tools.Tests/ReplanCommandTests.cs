using System.CommandLine;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline replan</c> in-process against the Waterloo fixtures.</summary>
public sealed class ReplanCommandTests
{
    private static string Waterloo => Fixtures.Path("waterloo 2010.WTT");

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { ReplanCommand.Create() };
        var exit = await root.Parse(["replan", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("optimiser")]
    [InlineData("rules")]
    public async Task Re_plans_the_hour_ahead_of_a_point_in_a_recording(string method)
    {
        // Longer than the default 5 s: the rules are deterministic only if they finish, and a slow CI runner may not.
        string[] args = ["--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.WaterlooFailure), "--seconds", "60"];
        var (exit, output, error) = await Run(method == "rules" ? [.. args, "--rules"] : args);

        exit.Should().Be(0, error);
        output.Should().StartWith($"Re-plan by the {method}, 05:57 to 06:57: ");
        output.Should().Contain("Conflicts before (4):").And.Contain("Changes (").And.Contain("Conflicts after (1):");
        output.Should().Contain("Hold 2O05 at ").And.Contain("Worked out in ");
    }

    [Fact]
    public async Task Bad_arguments_are_refused()
    {
        var (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.WaterlooFailure), "--at", "25:99");
        exit.Should().Be(2);
        error.Should().Contain("is not a time");

        (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.WaterlooFailure), "--seconds", "0");
        exit.Should().Be(2);
        error.Should().Contain("--seconds");
    }
}
