using System.CommandLine;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline simdata</c> in-process on the free sims in <c>tests/fixtures</c>.</summary>
public sealed class SimdataCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-simdata-{Guid.NewGuid():N}");

    public SimdataCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string FixtureFolder => Path.GetDirectoryName(Fixtures.Path("waterloo 2010.WTT"))!;

    private async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { SimdataCommand.Create() };
        var exit = await root.Parse(["simdata", "--folder", FixtureFolder, "--no-sessions", "--no-profile", "--cache-folder", _dir, .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task It_builds_then_reads_from_the_cache()
    {
        var (exit, output, error) = await Run("--area", "llangollen");

        exit.Should().Be(0, error);
        output.Should().StartWith("llangollen: 1 timetables (24 trains), 0 recordings or sessions, no profile; built in ")
            .And.Contain("Locations: 18 (timing 15, stations 11, key 2, reversing 6, entry 2, seed 1, reported 0).");
        File.Exists(Path.Combine(_dir, "llangollen.simdata.json")).Should().BeTrue();
        (await Run("--area", "llangollen")).Out.Should().Contain("; from the cache in ");
        (await Run("--area", "llangollen", "--rebuild")).Out.Should().Contain("; built in ");
    }

    [Fact]
    public async Task It_prints_a_location_and_a_link()
    {
        var (exit, output, _) = await Run("--area", "llangollen", "--location", "carrog", "--link", "LLANGOLLEN", "BERWYN");

        exit.Should().Be(0);
        output.Should().Contain("CARROG: Timing, Station, Key, Reversing")
            .And.Contain("  Platforms: 1 20")
            .And.Contain("LLANGOLLEN → BERWYN: never booked.")
            .And.Contain("  Booked through: LLANGOLLEN GOODSJNC BERWYN");
    }

    [Fact]
    public async Task It_learns_from_a_recording_and_names_a_signal()
    {
        var (exit, output, _) = await Run("--area", "waterloo", "--log", Fixtures.Path(Fixtures.Waterloo1x), "--location", "S4304", "--link", "WLOO", "VAUX");

        exit.Should().Be(0);
        output.Should().Contain("waterloo: 1 timetables (1768 trains), 1 recordings or sessions")
            .And.Contain("Signal S4304: reported")
            .And.Contain("WLOO → VAUX: booked 452 times")
            .And.Contain("    91-100         3:00 / 3:30  (124)");
    }

    [Fact]
    public async Task A_bad_tiploc_is_refused()
    {
        var (exit, _, error) = await Run("--area", "waterloo", "--location", " ");

        exit.Should().Be(2);
        error.Should().Contain("TIPLOC");
    }
}
