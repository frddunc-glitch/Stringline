using Serilog.Extensions.Logging;
using Stringline.App.Services;
using Stringline.Gateway.Tests;

namespace Stringline.App.Tests;

/// <summary>Each sim's own log files, so a bug report can attach just the one sim's log.</summary>
public sealed class AppLoggingTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-logging-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Cleaned up eventually.
        }
    }

    [Theory]
    [InlineData("waterloo", "waterloo")]
    [InlineData(" Moss Vale ", "moss-vale")]
    [InlineData("Liverpool St/Shenfield", "liverpool-st-shenfield")]
    [InlineData("north_wales", "north_wales")]
    [InlineData("..\\evil", "evil")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("///", null)]
    [InlineData(null, null)]
    public void A_sims_area_ID_goes_into_the_file_name_safely(string? area, string? key)
    {
        AppLogging.FileKey(area).Should().Be(key);
        AppLogging.FilePatternFor(area).Should().Be(key is null ? "stringline-.log" : $"stringline-{key}-.log");
    }

    [Fact]
    public void Lines_go_to_the_general_file_until_a_sim_is_named_then_to_that_sims_file()
    {
        var logging = new AppLogging(_root.FullName, detailed: false);
        using (var logger = logging.CreateLogger(retentionDays: 3))
        {
            logger.Information("before any sim");
            logging.UseSim("").Should().BeFalse("a blank area names no sim");
            logging.UseSim("Waterloo").Should().BeTrue();
            logging.Sim.Should().Be("waterloo");
            logger.Information("on waterloo");
            logging.UseSim("waterloo").Should().BeFalse("it is the same sim");
            logging.UseSim(null).Should().BeFalse("the log stays with the last sim");
            logger.Information("still waterloo");
            logging.UseSim("brightonmainline").Should().BeTrue();
            logger.Information("on brighton");
        }

        Read("stringline-2*.log").Should().Contain("before any sim").And.NotContain("waterloo");
        Read("stringline-waterloo-*.log").Should().Contain("on waterloo").And.Contain("still waterloo").And.NotContain("brighton");
        Read("stringline-brightonmainline-*.log").Should().Contain("on brighton");
    }

    [Fact]
    public async Task Loading_a_timetable_moves_the_log_to_its_sims_file_which_starts_with_what_is_running()
    {
        var logging = new AppLogging(_root.FullName, detailed: false);
        using (var loggerFactory = new SerilogLoggerFactory(logging.CreateLogger(retentionDays: 1), dispose: true))
        {
            await using var app = new TestApp(root: _root.FullName, logging: logging, loggerFactory: loggerFactory);
            await app.Ui.RunAsync(() => app.ViewModel.LoadTimetableAsync(Fixtures.Path("waterloo 2010.WTT")));
            logging.Sim.Should().Be("waterloo");
        }

        Read("stringline-2*.log").Should().Contain("The sim is waterloo; logging to stringline-waterloo-.log");
        var sim = Read("stringline-waterloo-*.log");
        sim.Should().Contain($"Logging for the sim waterloo: Stringline {AboutInfo.Version}");
    }

    private string Read(string pattern)
    {
        var files = Directory.GetFiles(_root.FullName, pattern, SearchOption.AllDirectories);
        files.Should().ContainSingle(pattern);
        return File.ReadAllText(files[0]);
    }
}
