using System.CommandLine;
using SkiaSharp;
using Stringline.Core;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline render-png</c> in-process against the Waterloo and Llangollen fixtures.</summary>
public sealed class RenderPngCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("stringline-render-");

    private static string Waterloo => Fixtures.Path("waterloo 2010.WTT");

    private string Out(string name = "graph.png") => Path.Combine(_dir.FullName, name);

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { RenderPngCommand.Create() };
        var exit = await root.Parse(["render-png", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task The_forecast_can_be_left_out()
    {
        var (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "07:00", "--no-forecast", Out());

        exit.Should().Be(0, error);
        output.Should().Contain(" 0 forecast in all");

        (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "07:00", "--no-knock-on", Out());
        exit.Should().Be(0, error);
        output.Should().NotContain(" 0 forecast in all");
    }

    [Fact]
    public async Task Live_heads_are_drawn_only_when_asked()
    {
        var (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "07:00", Out());
        exit.Should().Be(0, error);
        output.Should().NotContain("live heads", "an export leaves them out by default (S36)");

        (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "07:00", "--live-head", Out("heads.png"));
        exit.Should().Be(0, error);
        output.Should().MatchRegex(@" [1-9]\d* live heads\.\r?\n?$");
        File.ReadAllBytes(Out("heads.png")).Should().NotEqual(File.ReadAllBytes(Out()));
    }

    [Fact]
    public async Task Draws_learned_signals_as_ticks_unless_told_otherwise()
    {
        var profile = Out("learned.json");
        var root = new RootCommand { LearnCommand.Create() };
        (await root.Parse(["learn", "--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--out", profile]).InvokeAsync(new InvocationConfiguration { Output = new StringWriter(), Error = new StringWriter() })).Should().Be(0);

        var (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--profile", profile, "--at", "05:30", Out("ticks.png"));
        exit.Should().Be(0, error);
        (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--profile", profile, "--at", "05:30", "--signals", "off", Out("off.png"));
        exit.Should().Be(0, error);
        (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--profile", profile, "--at", "05:30", "--signals", "rows", Out("rows.png"));
        exit.Should().Be(0, error);

        File.ReadAllBytes(Out("ticks.png")).Should().NotEqual(File.ReadAllBytes(Out("off.png")));
        File.ReadAllBytes(Out("rows.png")).Should().NotEqual(File.ReadAllBytes(Out("ticks.png")));
    }

    [Fact]
    public async Task Draws_wtt_and_actuals_around_the_time_given()
    {
        var (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--corridor", "main", "--at", "07:00", "--hours", "3", Out());

        exit.Should().Be(0, error);
        error.Should().BeEmpty();
        output.Should().StartWith("Wrote graph.png: main (WLOO – WIMB), 06:00:00–09:00:00, now 07:00:00, ");
        output.Should().MatchRegex(@"\d+ paths in view \(\d+ actual, [1-9]\d* forecast in all\), \d+ of \d+ conflicts on this corridor\.");
        using var bitmap = SKBitmap.Decode(Out());
        (bitmap.Width, bitmap.Height).Should().Be((1600, 900));
    }

    [Fact]
    public async Task Draws_vertically_and_by_delay_band()
    {
        var (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "07:00", "--hours", "2",
            "--vertical", "--colour", "delay", "--width", "700", "--height", "1000", Out());

        exit.Should().Be(0, error);
        output.Should().StartWith("Wrote graph.png: main (WLOO – WIMB), 06:20:00–08:20:00, now 07:00:00, ");
        using var bitmap = SKBitmap.Decode(Out());
        (bitmap.Width, bitmap.Height).Should().Be((700, 1000));

        // The now line runs across, ⅓ of the way down the plot, whose top margin fits the location names (#3).
        var margins = Graph.GraphMargins.DefaultVertical;
        var width = 700 - margins.Left - margins.Right;
        var y = Enumerable.Range(0, 1000).Single(y =>
            Enumerable.Range((int)margins.Left + 5, (int)width - 10).Count(x => bitmap.GetPixel(x, y) is { Red: > 180, Green: < 120 }) > width * 0.8);
        var bottom = 1000 - margins.Bottom;
        var top = ((3 * (y + 0.5)) - bottom) / 2;
        top.Should().BeInRange(
            Graph.GraphLabels.MarginsFor(Graph.GraphOrientation.Vertical, Graph.GraphLabels.MinRoom).Top - 2,
            Graph.GraphLabels.MarginsFor(Graph.GraphOrientation.Vertical, Graph.GraphLabels.MaxRoom).Top + 2);
    }

    [Fact]
    public async Task Without_a_log_or_time_starts_at_the_timetable_start()
    {
        var (exit, output, _) = await Run("--wtt", Fixtures.Path("llangollen 2012 B.WTT"), "--width", "800", "--height", "400", "--now-at", "0", Out());

        exit.Should().Be(0);
        var start = new WttReader().Read(Fixtures.Path("llangollen 2012 B.WTT")).Timetable.Header.StartTime;
        output.Should().Contain($"main (LLANGOLLEN – CARROG), {start}–{start + (3 * 3600)}, now {start}, ");
        output.Should().Contain("(0 actual, 0 forecast in all)");
        using var bitmap = SKBitmap.Decode(Out());
        (bitmap.Width, bitmap.Height).Should().Be((800, 400));
    }

    [Fact]
    public async Task Without_a_time_the_now_line_is_the_end_of_the_recording()
    {
        var (exit, output, _) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo1x), "--hours", "1", Out());

        exit.Should().Be(0);
        var last = SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo1x)).LastSim!.Value;
        output.Should().Contain($"now {last},");
    }

    [Fact]
    public async Task Uses_a_profile_and_its_corridor_ids()
    {
        var tt = new WttReader().Read(Waterloo).Timetable;
        var profile = StringlineProfile.Create(tt);
        var renamed = profile with { Corridors = [profile.Corridors[1] with { Id = "windsor" }] };
        var path = Out("profile.json");
        await File.WriteAllTextAsync(path, ProfileJson.Serialize(renamed));

        var (exit, output, _) = await Run("--wtt", Waterloo, "--profile", path, "--corridor", "windsor", "--at", "07:00", "--no-wtt", Out());

        exit.Should().Be(0);
        output.Should().Contain("windsor (WLOO – BARNES)").And.Contain(" 0 paths in view");
    }

    [Fact]
    public async Task Reports_an_unknown_corridor()
    {
        var (exit, _, error) = await Run("--wtt", Waterloo, "--corridor", "nowhere", Out());

        exit.Should().Be(2);
        error.Should().Contain("No corridor 'nowhere' in the profile (corridors: main, wloo-barnes).");
        File.Exists(Out()).Should().BeFalse();
    }

    [Theory]
    [InlineData("--at", "noon")]
    [InlineData("--hours", "0")]
    [InlineData("--hours", "49")]
    [InlineData("--now-at", "1.5")]
    [InlineData("--width", "10")]
    [InlineData("--height", "99999")]
    public async Task Refuses_bad_arguments(string option, string value)
    {
        var (exit, _, error) = await Run("--wtt", Waterloo, option, value, Out());

        exit.Should().Be(2);
        error.Should().NotBeEmpty();
        File.Exists(Out()).Should().BeFalse();
    }

    [Fact]
    public async Task Reports_unreadable_inputs()
    {
        var (exit, _, error) = await Run("--wtt", Out("missing.wtt"), Out());
        exit.Should().Be(2);
        error.Should().StartWith("Cannot read input:");

        var badProfile = Out("bad.json");
        await File.WriteAllTextAsync(badProfile, "{ not json");
        (exit, _, error) = await Run("--wtt", Waterloo, "--profile", badProfile, Out());
        exit.Should().Be(2);
        error.Should().StartWith("Cannot read input:");
    }

    [Fact]
    public async Task Reports_an_unwritable_output()
    {
        var (exit, _, error) = await Run("--wtt", Fixtures.Path("llangollen 2012 B.WTT"), Path.Combine(_dir.FullName, "no-such-dir", "x.png"));

        exit.Should().Be(2);
        error.Should().StartWith("Cannot write ");
    }

    [Fact]
    public void Play_stops_at_the_first_event_after_the_time()
    {
        var tt = new WttReader().Read(Waterloo).Timetable;
        var recording = SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo4x));
        using var loggers = ConsoleLogging.Create(verbose: false);

        var state = RenderPngCommand.Play(recording, tt, SimTime.FromClock(6, 30), loggers, CancellationToken.None);

        state.Clock.LastReported.Should().BeLessThanOrEqualTo(SimTime.FromClock(6, 30));
        state.Trains.SelectMany(t => t.Actuals).Should().OnlyContain(a => a.Time <= SimTime.FromClock(6, 30));
        state.Trains.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Writes_a_PDF_when_the_output_ends_in_pdf()
    {
        var (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "07:00", Out("graph.pdf"));

        exit.Should().Be(0, error);
        output.Should().StartWith("Wrote graph.pdf (A3 landscape): main ");
        var pdf = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Out("graph.pdf")));
        pdf.Should().StartWith("%PDF-").And.Contain("/MediaBox [0 0 1191 842]");

        (exit, output, error) = await Run("--wtt", Waterloo, "--page", "a4", Out("a4.pdf"));
        exit.Should().Be(0, error);
        output.Should().StartWith("Wrote a4.pdf (A4 landscape): ");

        (exit, _, error) = await Run("--wtt", Waterloo, "--page", "letter", Out("bad.pdf"));
        exit.Should().Be(2);
        error.Should().Contain("--page 'letter' is not a page");
    }

    [Fact]
    public async Task Plots_a_time_range_over_pages_time_down_on_A3_portrait()
    {
        var (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--plot", "--from", "06:00", "--to", "09:30", Out("plot.pdf"));

        exit.Should().Be(0, error);
        output.Should().StartWith("Wrote plot.pdf (A3 portrait, time down): main ").And.Contain("06:00:00–09:30:00, 2 pages of 120 min.");
        var pdf = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Out("plot.pdf")));
        pdf.Should().StartWith("%PDF-").And.Contain("/MediaBox [0 0 842 1191]");
        System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type\s*/Page\b(?!s)").Should().HaveCount(2);

        (exit, output, error) = await Run("--wtt", Waterloo, "--plot", "--horizontal", "--per-page", "60", "--from", "07:00", "--hours", "2", Out("across.pdf"));
        exit.Should().Be(0, error);
        output.Should().StartWith("Wrote across.pdf (A3 landscape, time across): ").And.Contain("2 pages of 60 min.");

        (exit, _, error) = await Run("--wtt", Waterloo, "--plot", Out("plot.png"));
        exit.Should().Be(2);
        error.Should().Contain("--plot writes a PDF");

        (exit, _, error) = await Run("--wtt", Waterloo, "--plot", "--from", "09:00", "--to", "08:00", Out("bad.pdf"));
        exit.Should().Be(2);
        error.Should().Contain("must end after it starts");
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
