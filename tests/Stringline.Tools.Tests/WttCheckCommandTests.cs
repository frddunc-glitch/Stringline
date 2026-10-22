using System.CommandLine;
using Stringline.Gateway.Tests;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline wtt-check</c> in-process on the free sims in <c>tests/fixtures</c>.</summary>
public sealed class WttCheckCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-wttcheck-{Guid.NewGuid():N}");

    public WttCheckCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string FixtureFolder => Path.GetDirectoryName(Fixtures.Path("waterloo 2010.WTT"))!;

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { WttCheckCommand.Create() };
        var exit = await root.Parse(["wtt-check", .. args, "--knowledge-folder", FixtureFolder, "--no-profile"])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Llangollen_has_no_errors_and_lists_its_info()
    {
        var (exit, output, error) = await Run(Fixtures.Path("llangollen 2012 B.WTT"), "--include-self");

        exit.Should().Be(0, error);
        output.Should().StartWith("Knowledge of llangollen: 1 timetables (24 trains), 0 recordings or sessions, no profile; built in ")
            .And.Contain("(llangollen), 24 trains, ")
            .And.Contain("0 errors, 0 warnings, ")
            .And.Contain("Terminates at PENTREFELIN with no onward working")
            .And.Contain("  terminates-no-onward ");
    }

    [Fact]
    public async Task By_default_the_checked_file_is_left_out_of_the_knowledge()
    {
        var (exit, output, _) = await Run(Fixtures.Path("llangollen 2012 B.WTT"), "--severity", "warning", "--summary");

        exit.Should().Be(0);
        output.Should().Contain("Knowledge of llangollen: 0 timetables")
            .And.Contain("No other timetable of the sim was found, so the sim-knowledge checks are left out.")
            .And.Contain("0 errors, 0 warnings, 0 info.");
    }

    [Fact]
    public async Task Errors_give_exit_code_1_with_fixes_and_a_train_filter()
    {
        var path = Path.Combine(_dir, "broken.wtt");
        var document = Timetable.Editing.WttDocument.Load(Fixtures.Path("llangollen 2012 B.WTT"));
        var (train, trip) = document.Trains
            .SelectMany(t => t.Trips.Skip(1).Select(p => (Train: t, Trip: p)))
            .First(x => x.Trip is { IsPassTime: false, ArrTime: not null, DepPassTime: not null });
        document.Apply(Timetable.Editing.TimetableEdit.Change(trip, t => t.DepPassTime = t.ArrTime!.Value - 60));
        Timetable.Editing.WttWriter.Save(document, path, new Timetable.Editing.WttSaveOptions(Timetable.Editing.VersionBump.None, Backup: false));

        var (exit, output, _) = await Run(path, "--train", train.Headcode!, "--severity", "error");

        exit.Should().Be(1);
        output.Should().Contain($"Error   {train.Headcode}").And.Contain("earlier than arrival time").And.Contain("1 errors, 0 warnings, 0 info.");
        (await Run(Path.Combine(_dir, "missing.wtt"))).Exit.Should().Be(2);
    }
}
