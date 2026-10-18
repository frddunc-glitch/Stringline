using System.CommandLine;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline corridors</c> in-process and checks what it prints and writes.</summary>
public sealed class CorridorsCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-corridors-{Guid.NewGuid():N}");

    public CorridorsCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Waterloo => Fixtures.Path("waterloo 2010.WTT");

    private static string Llangollen => Fixtures.Path("llangollen 2012 B.WTT");

    private static async Task<(int Exit, string[] Lines, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { CorridorsCommand.Create() };
        var exit = await root.Parse(["corridors", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString().Split(Environment.NewLine), error.ToString());
    }

    [Fact]
    public async Task Prints_each_candidate_with_its_locations_and_sections()
    {
        var (exit, lines, error) = await Run(Waterloo);

        exit.Should().Be(0);
        error.Should().BeEmpty();
        lines[0].Should().Be("waterloo 2010.WTT: sim waterloo, 20 locations, 23 edges (15 skip edges expanded).");
        lines.Should().Contain("1. main: WLOO – WIMB. 8 locations, 1626 trains, 8981 train-sections (8981 not on an earlier corridor).");
        lines.Should().Contain(l => l.StartsWith("2. wloo-barnes: WLOO – BARNES.", StringComparison.Ordinal));
        lines.Should().Contain(l => l.StartsWith("#  Location  Axis ", StringComparison.Ordinal) && l.EndsWith("Hint", StringComparison.Ordinal));
        lines.Should().ContainMatch("1  WLOO      0.000  19    1643   1643   839    817  0:20 *");
        lines.Should().ContainMatch("8  WIMB      1.000 *553*");
        lines.Should().NotContain(l => l.StartsWith("Edges", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shows_the_graph_and_single_line_hints()
    {
        var (exit, lines, _) = await Run(Llangollen, "--edges");

        exit.Should().Be(0);
        lines.Should().Contain("Edges (trains):");
        lines.Should().ContainMatch("  GOODSJNC–LLANGOLLEN *18");
        lines.Should().ContainMatch("1  LLANGOLLEN *3:00 *single line?");
        lines.Should().ContainMatch("6  CARROG      1.000 *");
    }

    [Fact]
    public async Task Lists_skip_edges_with_edges()
    {
        var (_, lines, _) = await Run(Waterloo, "--edges", "--top", "1");

        lines.Should().Contain("Skip edges expanded:");
        lines.Should().ContainMatch("  CJML–WLOO * via VAUX");
        lines.Should().NotContain(l => l.StartsWith("2. ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writes_a_profile()
    {
        var path = Path.Combine(_dir, "waterloo.json");

        var (exit, lines, _) = await Run(Waterloo, "--profile", path);

        exit.Should().Be(0);
        lines.Should().ContainMatch($"Wrote profile {path} (2 corridors, 24 category styles). Save it as waterloo.json in *");
        var profile = ProfileJson.Deserialize(await File.ReadAllTextAsync(path));
        profile.AreaId.Should().Be("waterloo");
        profile.Corridors.Select(c => c.Id).Should().Equal("main", "wloo-barnes");
    }

    [Fact]
    public async Task Will_not_overwrite_a_profile_without_force()
    {
        var path = Path.Combine(_dir, "existing.json");
        await File.WriteAllTextAsync(path, "keep");

        var (exit, _, error) = await Run(Llangollen, "--profile", path);

        exit.Should().Be(2);
        error.Should().Contain("exists; use --force");
        (await File.ReadAllTextAsync(path)).Should().Be("keep");

        (await Run(Llangollen, "--profile", path, "--force")).Exit.Should().Be(0);
        ProfileJson.Deserialize(await File.ReadAllTextAsync(path)).AreaId.Should().Be("llangollen");
    }

    [Fact]
    public async Task Reports_a_profile_it_cannot_write()
    {
        var (exit, lines, error) = await Run(Llangollen, "--profile", Path.Combine(_dir, "missing", "x.json"));

        exit.Should().Be(2);
        error.Should().StartWith("Cannot write ");
        lines.Should().Contain(l => l.StartsWith("1. main:", StringComparison.Ordinal), "the candidates are still printed");
    }

    [Fact]
    public async Task Says_when_nothing_qualifies()
    {
        var (exit, lines, _) = await Run(Llangollen, "--min-trains", "100");

        exit.Should().Be(0);
        lines.Should().Contain("No corridors: no edge has 100 or more trains.");
    }

    [Theory]
    [InlineData("--top", "0")]
    [InlineData("--min-trains", "0")]
    public async Task Rejects_silly_limits(string option, string value)
    {
        var (exit, _, error) = await Run(Llangollen, option, value);

        exit.Should().Be(2);
        error.Should().Contain("at least 1");
    }

    [Fact]
    public async Task Reports_an_unreadable_timetable()
    {
        var (exit, _, error) = await Run(Path.Combine(_dir, "none.wtt"));

        exit.Should().Be(2);
        error.Should().StartWith("Cannot read ");
    }
}
