using System.CommandLine;
using System.IO.Compression;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline wtt-dump</c> in-process and checks what it prints.</summary>
public sealed class WttDumpCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-wtt-{Guid.NewGuid():N}");

    public WttDumpCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Waterloo => Fixtures.Path("waterloo 2010.WTT");

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { WttDumpCommand.Create() };
        var exit = await root.Parse(["wtt-dump", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    private string WriteWtt(string xml)
    {
        var path = Path.Combine(_dir, "test.wtt");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("SavedTimetable.xml").Open());
        writer.Write(xml);
        return path;
    }

    [Fact]
    public async Task Prints_a_summary_and_a_table_of_trains()
    {
        var (exit, output, error) = await Run(Waterloo);

        exit.Should().Be(0);
        error.Should().BeEmpty();
        var lines = output.Split(Environment.NewLine);
        lines[0].Should().Be("waterloo 2010.WTT: sim waterloo, \"SX May 2010 (Updated - Loader) v1.0.1\" (saved by SimSig 4.1.2, timetable version 1.0.2)");
        lines[1].Should().Be("Runs 04:30:00 to 01:30:00+1. 1768 trains: 898 enter, 11 seeded, 859 start in the sim.");
        lines[2].Should().Be("7834 timing points at 20 locations; 24 categories; 338 rules.");
        lines.Should().Contain("Activities: Next 823, DivideRear 34, DivideFront 2, Join 58.");
        lines.Should().Contain("Import: 0 errors, 0 warnings, 0 notes.");
        lines.Should().Contain(l => l.StartsWith("ID ", StringComparison.Ordinal) && l.Contains("Headcode") && l.EndsWith("Category", StringComparison.Ordinal));
        lines.Should().ContainMatch("1W51 *1W51 *seed S21 *05:30:00 *05:43:00 *WLOO *WIMB *3 *[Electric - DC] 444 - 10 cars");
        lines.Should().ContainMatch("2P05 *2P05 *from 1W51 *05:00:00*");
        lines.Should().ContainMatch("0Z94-1 *0Z94-1 *enter ESHEEP *11:15:00 *11:15:00 *CJWIN *CJWIN *1 *0Z95 *[Diesel] Class 47");
    }

    [Fact]
    public async Task Prints_the_points_of_chosen_trains()
    {
        var (exit, output, _) = await Run(Waterloo, "--summary", "--train", "1W51", "--train", "NOPE");

        exit.Should().Be(0);
        output.Should().NotContain("Headcode  Start", "--summary leaves out the table");
        var lines = output.Split(Environment.NewLine);
        lines.Should().Contain("1W51 (1W51) 05:30 London Waterloo - Weymouth (5 444)");
        lines.Should().Contain("  seeded at S21, [Electric - DC] 444 - 10 cars, 100 mph, 232 m");
        lines.Should().ContainMatch("1 *WLOO *05:30:00 *11 *DMF *DivideFront 2P05");
        lines.Should().ContainMatch("3 *WIMB *05:42:30 *05:43:00");
        lines.Should().Contain("No train NOPE in the timetable.");
    }

    [Fact]
    public async Task Shows_passes_allowances_unresolved_links_and_warnings()
    {
        var xml = """
            <SimSigTimetable ID="t" Version="5">
              <Timetables>
                <Timetable><ID>1A01</ID><UID>U1</UID><IsFreight>-1</IsFreight><AsRequired>-1</AsRequired><Category>X</Category><Trips>
                  <Trip><Location>AAA</Location><DepPassTime>100</DepPassTime><IsPassTime>-1</IsPassTime><EngAllowance>3</EngAllowance><PathAllowance>1</PathAllowance><PerfAllowance>2</PerfAllowance></Trip>
                  <Trip><Location>BBB</Location><ArrTime>200</ArrTime><Activities><Activity><Activity>0</Activity><AssociatedTrain>9Z99</AssociatedTrain></Activity></Activities></Trip>
                </Trips></Timetable>
                <Timetable><ID></ID></Timetable>
              </Timetables>
            </SimSigTimetable>
            """;

        var (exit, output, _) = await Run(WriteWtt(xml), "--train", "U1", "--warnings");

        exit.Should().Be(0);
        var lines = output.Split(Environment.NewLine);
        lines.Should().Contain("Runs 00:00:00 to 00:00:00. 1 trains: 0 enter, 0 seeded, 1 start in the sim; 1 skipped.");
        lines.Should().Contain("1 distinct headcodes; 1 trains have a WTT UID.");
        lines.Should().Contain("Activities: Next 1 (1 name a train that is not in the timetable).");
        lines.Should().Contain("Import: 0 errors, 2 warnings, 1 notes.");
        lines.Should().Contain("  Warning: U1: Next at BBB names 9Z99, which is not in the timetable; the link is lost.");
        lines.Should().Contain("  UID U1, starts in the sim, freight, as required");
        lines.Should().ContainMatch("1 *AAA *00:01:40 pass *E1½ P½ R1*");
        lines.Should().ContainMatch("2 *BBB *00:03:20 *Next 9Z99?");
    }

    [Fact]
    public async Task Damaged_timetables_exit_with_1_and_unreadable_files_with_2()
    {
        var damaged = WriteWtt("<SimSigTimetable ID=\"t\"><Timetables><Timetable><ID>1A01</ID>");
        (await Run(damaged)).Exit.Should().Be(1);

        var (exit, _, error) = await Run(Path.Combine(_dir, "missing.wtt"));
        exit.Should().Be(2);
        error.Should().StartWith("Cannot read ");

        var notWtt = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(notWtt, "hello");
        (await Run(notWtt)).Exit.Should().Be(2);
    }
}
