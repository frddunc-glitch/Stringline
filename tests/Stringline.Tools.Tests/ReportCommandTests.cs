using System.CommandLine;
using System.Text;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Persistence;
using Stringline.Planning;
using Stringline.Timetable;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline report</c> in-process against the Waterloo fixtures (SES-2, S17).</summary>
public sealed class ReportCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("stringline-report-");

    private static string Waterloo => Fixtures.Path("waterloo 2010.WTT");

    private string Out(string name = "delays.csv") => Path.Combine(_dir.FullName, name);

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { ReportCommand.Create() };
        var exit = await root.Parse(["report", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    private string[] Lines(string name = "delays.csv")
    {
        var bytes = File.ReadAllBytes(Out(name));
        bytes.Take(3).Should().Equal([0xEF, 0xBB, 0xBF], "UTF-8 with a byte order mark, for Excel");
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Writes_a_row_per_train_and_timing_point_from_a_recording()
    {
        var (exit, output, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "07:00", Out());

        exit.Should().Be(0, error);
        output.Should().MatchRegex(@"^Wrote delays\.csv: \d+ trains, \d+ rows, as at 07:00:00; \d+ late at some point, worst \w+ -?\d+ min at \w+\.");
        var lines = Lines();
        lines[0].Should().Be("# Waterloo – SX May 2010 (Updated - Loader) v1.0.1 (v1.0.2) – as at 07:00:00", "the title line names the sim and timetable (#6)");
        lines[1].Should().Be(string.Join(',', DelayReportCsv.PointColumns));
        lines.Length.Should().BeGreaterThan(100);
        lines.Skip(2).Should().OnlyContain(l => l.Split(',').Length == DelayReportCsv.PointColumns.Length);
        lines.Should().Contain(l => l.EndsWith(",actual", StringComparison.Ordinal)).And.Contain(l => l.EndsWith(",forecast", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writes_a_row_per_train_and_every_train_when_asked()
    {
        var (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--by-train", Out("seen.csv"));
        exit.Should().Be(0, error);
        (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--by-train", "--all", Out("all.csv"));
        exit.Should().Be(0, error);

        var seen = Lines("seen.csv");
        var all = Lines("all.csv");
        seen[0].Should().StartWith("# Waterloo – ");
        seen[1].Should().Be(string.Join(',', DelayReportCsv.TrainColumns));
        seen.Skip(2).Should().OnlyContain(l => l.Contains(",running,", StringComparison.Ordinal) || l.Contains(",finished,", StringComparison.Ordinal));
        all.Length.Should().Be(2 + new WttReader().Read(Waterloo).Timetable.Trains.Count);
        all.Should().Contain(l => l.Contains(",not started,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_brings_its_edits_as_they_stood_at_the_time()
    {
        var tt = new WttReader().Read(Waterloo).Timetable;
        var later = tt.Trains.Where(t => t.Points.FirstOrDefault(p => p.Depart is not null)?.Depart > SimTime.Parse("09:00")).Take(2).ToList();
        string path;
        using (var writer = SessionWriter.Create(_dir.FullName, new SessionInfo
        {
            Kind = SavedSessionKind.Replay,
            RecordingPath = Fixtures.Path(Fixtures.Waterloo4x),
            TimetablePath = Waterloo,
            AreaId = "waterloo",
        }, new SessionWriterOptions { AutosaveInterval = TimeSpan.Zero }))
        {
            writer.OnStep(PlanStep.Execute(new CancelTrain(later[0].Id), "Cancel", SimTime.Parse("06:50")));
            writer.OnStep(PlanStep.Execute(new CancelTrain(later[1].Id), "Cancel", SimTime.Parse("07:30")));
            writer.SetPosition(SimTime.Parse("07:00"));
            writer.Flush();
            path = writer.Path!;
        }

        var (exit, output, error) = await Run("--session", path, "--by-train", "--all", Out());

        exit.Should().Be(0, error);
        output.Should().Contain("as at 07:00:00, 1 edit;", "a replay session reports where it had got to, with the edits made by then");
        var lines = Lines();
        lines.Should().ContainSingle(l => l.StartsWith($"{later[0].Headcode},{later[0].Id},", StringComparison.Ordinal)).Which.Should().Contain(",cancelled,yes,");
        lines.Should().ContainSingle(l => l.StartsWith($"{later[1].Headcode},{later[1].Id},", StringComparison.Ordinal)).Which.Should().Contain(",not started,no,");
    }

    [Fact]
    public async Task Bad_arguments_are_refused()
    {
        (await Run("--wtt", Waterloo, Out())).Exit.Should().Be(2);
        var (exit, _, error) = await Run("--wtt", Waterloo, "--log", Fixtures.Path(Fixtures.Waterloo4x), "--at", "soon", Out());
        exit.Should().Be(2);
        error.Should().Contain("--at 'soon' is not a time");
        (exit, _, error) = await Run("--session", Out("missing.stringline"), Out());
        exit.Should().Be(2);
        error.Should().StartWith("Cannot read input: ");
    }

    public void Dispose() => _dir.Delete(recursive: true);
}
