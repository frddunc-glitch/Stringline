using System.Text;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S17 in the shell: the Delays tab (SES-2), CSV export of the delay table and PDF export of the graph (GR-10).</summary>
public sealed class ReportTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static async Task ReplayWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), Fixtures.Path(Waterloo), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Matches.Matched > 100 && vm.Scene?.InLayer(GraphLayer.Actual).Any() == true, "matched trains on the graph");
    }

    private static string[] CsvLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes.Take(3).Should().Equal([0xEF, 0xBB, 0xBF], "UTF-8 with a byte order mark, for Excel");
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task The_delays_tab_lists_the_worst_offenders_and_follows_the_selection()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await ReplayWaterloo(app);
        (await app.Ui.Run(() => vm.Delays.Trains.Count)).Should().Be(0, "the table is only worked out while its tab is shown");

        await app.Ui.Run(() => vm.SideTab = MainViewModel.DelaysTab);
        await app.Ui.WaitUntil(() => vm.Delays.Trains.Count > 20, "trains in the delay table");

        await app.Ui.Run(() =>
        {
            var rows = vm.Delays.Trains.ToList();
            rows.Select(r => r.Report!.WorstDelaySeconds ?? int.MinValue).Should().BeInDescendingOrder("worst offenders first");
            rows.Should().OnlyContain(r => r.Report!.HasActuals);
            vm.Delays.Summary.Should().MatchRegex(@"^\d+ trains seen · \d+ late \(5\+ min\)");
            vm.Delays.Points.Should().BeEmpty();
            vm.Delays.PointsHeading.Should().Be("Pick a train to see its delays at each timing point.");

            // Picking a row selects the train on the graph and shows its points.
            var pick = rows[0];
            vm.Delays.Selected = pick;
            vm.Selection!.Train.Should().Be(pick.Train);
            vm.Delays.Points.Should().HaveCount(pick.Report!.Points.Count);
            vm.Delays.Points.Should().Contain(p => p.Actual.Length > 0);
            vm.Delays.PointsHeading.Should().StartWith(pick.Headcode);

            // Picking a train elsewhere picks its row.
            var other = vm.Trains.Rows.First(r => r.Train is { } id && id != pick.Train && vm.Delays.Find(id) is not null);
            vm.Trains.Selected = other;
            vm.Delays.Selected!.Train.Should().Be(other.Train!.Value);

            var count = rows.Count;
            vm.Delays.Sort = DelaysViewModel.SortOptions.Single(o => o.Sort == DelaySort.Headcode);
            vm.Delays.Trains.Select(r => r.Headcode).Should().BeInAscendingOrder(StringComparer.Ordinal);
            vm.Delays.Selected!.Train.Should().Be(other.Train!.Value, "sorting keeps the selection");

            vm.Delays.ShowAll = true;
            vm.Delays.Trains.Count.Should().Be(vm.Snapshot.Plan.Count).And.BeGreaterThan(count);
            vm.Delays.Summary.Should().Contain("trains in the plan");
        });
    }

    [Fact]
    public async Task Exports_the_delay_table_as_CSV()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await app.Ui.Run(() => vm.ExportDelaysCommand.Execute("points"));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be("There is no timetable to report on yet.");

        await ReplayWaterloo(app);
        var points = Path.Combine(app.Paths.Exports, "delays.csv");
        var trains = Path.Combine(app.Paths.Exports, "trains.csv");
        Directory.CreateDirectory(app.Paths.Exports);
        app.Dialogs.SaveFiles.Enqueue(points);
        app.Dialogs.SaveFiles.Enqueue(trains);

        await app.Ui.Run(() => vm.ExportDelaysCommand.Execute("points"));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be($"Exported the delay table to {points}.");
        await app.Ui.Run(() => vm.ExportDelaysCommand.Execute("trains"));

        app.Dialogs.SaveSuggestions[0].Should().MatchRegex(@"[\\/]exports[\\/]waterloo-delays-\d{4}\.csv$");
        app.Dialogs.SaveSuggestions[1].Should().MatchRegex(@"[\\/]exports[\\/]waterloo-train-delays-\d{4}\.csv$");
        var pointLines = CsvLines(points);
        pointLines[0].Should().MatchRegex(@"^# Waterloo – SX May 2010 \(Updated - Loader\) v1\.0\.1 \(v1\.0\.2\) – as at \d\d:\d\d:\d\d$", "the title line names the sim and timetable (#6)");
        pointLines[1].Should().Be(string.Join(',', DelayReportCsv.PointColumns));
        pointLines.Length.Should().BeGreaterThan(100);
        var trainLines = CsvLines(trains);
        trainLines[0].Should().StartWith("# Waterloo – SX May 2010");
        trainLines[1].Should().Be(string.Join(',', DelayReportCsv.TrainColumns));
        var report = await app.Ui.Run(() => DelayReport.For(vm.Snapshot));
        trainLines.Length.Should().BeInRange(report.Trains.Count - 5 + 2, report.Trains.Count + 5 + 2, "one row per train seen (the replay may have moved on a little)");
    }

    [Fact]
    public async Task Exports_the_graph_as_PDF_on_A3_by_default()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await app.Ui.Run(() => vm.ExportPdfCommand.Execute("a3"));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be("There is no graph to export yet.");

        await ReplayWaterloo(app);
        var a3 = Path.Combine(app.Paths.Exports, "graph.pdf");
        var a4 = Path.Combine(app.Paths.Exports, "graph-a4.pdf");
        Directory.CreateDirectory(app.Paths.Exports);
        app.Dialogs.SaveFiles.Enqueue(a3);
        app.Dialogs.SaveFiles.Enqueue(a4);
        await app.Ui.Run(() => vm.Tick());

        await app.Ui.Run(() => vm.ExportPdfCommand.Execute(null));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be($"Exported the graph (A3 landscape) to {a3}.");
        await app.Ui.Run(() => vm.ExportPdfCommand.Execute("a4"));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be($"Exported the graph (A4 landscape) to {a4}.");

        app.Dialogs.SaveSuggestions[0].Should().MatchRegex(@"[\\/]exports[\\/]waterloo-main-\d{4}\.pdf$");
        Encoding.Latin1.GetString(File.ReadAllBytes(a3)).Should().StartWith("%PDF-").And.Contain("/MediaBox [0 0 1191 842]");
        Encoding.Latin1.GetString(File.ReadAllBytes(a4)).Should().Contain("/MediaBox [0 0 842 595]");
        app.Settings.Load().LastExportFolder.Should().Be(app.Paths.Exports);
    }
}
