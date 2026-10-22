using System.CommandLine;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline wtt-save</c> in-process.</summary>
public sealed class WttSaveCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-wttsave-{Guid.NewGuid():N}");

    public WttSaveCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Waterloo => Fixtures.Path("waterloo 2010.WTT");

    private string Out => Path.Combine(_dir, "out.wtt");

    private async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var root = new RootCommand { WttSaveCommand.Create() };
        var exit = await root.Parse(["wtt-save", .. args, "--backup-folder", Path.Combine(_dir, "backups")]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_round_trip_with_no_bump_gives_the_same_bytes()
    {
        var (exit, output, error) = await Run(Waterloo, Out, "--bump", "none");

        exit.Should().Be(0, error);
        output.Should().Contain("(version 1.0.2, 1768 trains)");
        using var original = new System.IO.Compression.ZipArchive(File.OpenRead(Waterloo));
        using var saved = new System.IO.Compression.ZipArchive(File.OpenRead(Out));
        foreach (var name in new[] { "SavedTimetable.xml", "TimetableHeader.xml" })
        {
            using var a = original.GetEntry(name)!.Open();
            using var b = saved.GetEntry(name)!.Open();
            using var bufferA = new MemoryStream();
            using var bufferB = new MemoryStream();
            a.CopyTo(bufferA);
            b.CopyTo(bufferB);
            bufferB.ToArray().Should().Equal(bufferA.ToArray(), name);
        }
    }

    [Fact]
    public async Task A_save_raises_the_build_and_backs_up_the_file_it_replaces()
    {
        (await Run(Waterloo, Out)).Exit.Should().Be(0);

        var (exit, output, _) = await Run(Out, Out, "--bump", "minor");

        exit.Should().Be(0);
        output.Should().Contain("(version 1.1.0, 1768 trains)");
        output.Should().Contain("Backed up the file it replaced to ");
        Directory.GetFiles(Path.Combine(_dir, "backups", "waterloo")).Should().ContainSingle().Which.Should().Contain("out 1.0.3 ");
    }

    [Fact]
    public async Task Shifting_a_train_moves_all_its_times_and_nothing_else()
    {
        var (exit, output, error) = await Run(Waterloo, Out, "--shift-train", "1W51", "+5", "--shift-train", "2P05", "-2H");

        exit.Should().Be(0, error);
        output.Should().Contain("Shifted 1W51 by +5 min (");
        output.Should().Contain("Shifted 2P05 by -2½ min (");
        var before = new WttReader().Read(Waterloo).Timetable;
        var after = new WttReader().Read(Out).Timetable;
        Times(after, "1W51").Should().Equal(Times(before, "1W51").Select(t => t + 300));
        Times(after, "2P05").Should().Equal(Times(before, "2P05").Select(t => t - 150));
        after.Get(new TrainId("1W51")).EntryTime.Should().Be(before.Get(new TrainId("1W51")).EntryTime + 300);
        Times(after, "2P07").Should().Equal(Times(before, "2P07"));
        WttDocument.Load(Out).Header.Version.Should().Be(new WttVersion(1, 0, 3));
    }

    private static List<SimTime> Times(Core.Timetables.WorkingTimetable tt, string id) =>
        tt.Get(new TrainId(id)).Points.SelectMany(p => new[] { p.Arrive, p.Depart }).OfType<SimTime>().ToList();

    [Fact]
    public async Task An_unknown_train_or_amount_is_refused()
    {
        (await Run(Waterloo, Out, "--shift-train", "9Z99", "+5")).Should().Match<(int Exit, string Out, string Error)>(r => r.Exit == 1 && r.Error.Contains("No train 9Z99"));
        (await Run(Waterloo, Out, "--shift-train", "1W51", "five")).Exit.Should().Be(1);
        File.Exists(Out).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_that_is_not_a_timetable_is_refused()
    {
        var bad = Path.Combine(_dir, "bad.wtt");
        await File.WriteAllTextAsync(bad, "not a timetable");

        var (exit, _, error) = await Run(bad, Out);

        exit.Should().Be(2);
        error.Should().Contain("Cannot read");
    }
}
