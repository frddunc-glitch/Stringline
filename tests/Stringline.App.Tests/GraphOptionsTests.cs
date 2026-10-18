using SkiaSharp;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;

namespace Stringline.App.Tests;

/// <summary>S9's view options in the shell: colour by delay, the vertical graph, and PNG export.</summary>
public sealed class GraphOptionsTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static async Task ReplayWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), Fixtures.Path(Waterloo), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Matches.Matched > 100 && vm.Scene?.InLayer(GraphLayer.Actual).Any() == true, "matched trains on the graph");
    }

    [Fact]
    public async Task View_options_start_as_remembered_and_are_saved_when_changed()
    {
        await using var app = new TestApp(s => s with { ColourByDelay = true, VerticalGraph = true });
        var vm = app.ViewModel;

        await app.Ui.Run(() =>
        {
            vm.ColourMode.Should().Be(GraphColourMode.Delay);
            vm.ColourByDelay.Should().BeTrue();
            vm.IsVertical.Should().BeTrue();
            vm.Navigator.Viewport.Orientation.Should().Be(GraphOrientation.Vertical);
            vm.Trains.ColourMode.Should().Be(GraphColourMode.Delay);

            vm.ColourByDelay = false;
            vm.IsVertical = false;
            vm.ColourMode.Should().Be(GraphColourMode.Category);
            vm.Navigator.Viewport.Orientation.Should().Be(GraphOrientation.Horizontal);
        });

        var saved = app.Settings.Load();
        saved.ColourByDelay.Should().BeFalse();
        saved.VerticalGraph.Should().BeFalse();
    }

    [Fact]
    public async Task Colour_by_delay_recolours_the_graph_and_the_train_list()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await ReplayWaterloo(app);
        var bands = new[] { DelayBands.RightTime, DelayBands.Slight, DelayBands.Late, DelayBands.VeryLate, DelayBands.Unknown };

        await app.Ui.Run(() => vm.ColourMode = GraphColourMode.Delay);
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Actual).All(p => bands.Contains(p.Colour)) == true, "actuals in band colours");

        await app.Ui.Run(() =>
        {
            var bandHex = bands.Select(b => b.ToString()).ToHashSet();
            vm.Trains.Rows.Should().OnlyContain(r => bandHex.Contains(r.Colour));
            vm.Trains.Rows.Where(r => r.DelayMinutes is <= 0).Should().OnlyContain(r => r.Colour == DelayBands.RightTime.ToString() || r.IsUnmatched);
        });
    }

    [Fact]
    public async Task Exports_the_current_view_to_the_file_chosen()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;

        await app.Ui.Run(() => vm.ExportPngCommand.Execute("1"));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be("There is no graph to export yet.");
        app.Dialogs.SaveSuggestions.Should().BeEmpty();

        await ReplayWaterloo(app);
        await app.Ui.Run(() => vm.IsVertical = true);
        var target = Path.Combine(app.Paths.Exports, "elsewhere", "graph.png");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        app.Dialogs.SaveFiles.Enqueue(target);
        await app.Ui.Run(() => vm.Tick());
        await app.Ui.Run(() => vm.ExportPngCommand.Execute("2"));

        app.Dialogs.SaveSuggestions.Should().ContainSingle()
            .Which.Should().MatchRegex(@"[\\/]exports[\\/][a-z0-9_]+-main-\d{4}\.png$");
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be($"Exported the graph to {target}.");
        var viewport = await app.Ui.Run(() => vm.Navigator.Viewport);
        using (var bitmap = SKBitmap.Decode(target))
        {
            (bitmap.Width, bitmap.Height).Should().Be((viewport.Width * 2, viewport.Height * 2));
        }

        app.Settings.Load().LastExportFolder.Should().Be(Path.GetDirectoryName(target));

        // The next export starts where the last went; cancelling writes nothing.
        await app.Ui.Run(() => vm.ExportPngCommand.Execute("1"));
        app.Dialogs.SaveSuggestions[^1].Should().StartWith(Path.GetDirectoryName(target));
        Directory.GetFiles(Path.GetDirectoryName(target)!).Should().ContainSingle();
    }

    [Fact]
    public async Task An_export_that_cannot_be_written_is_reported()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await ReplayWaterloo(app);
        app.Dialogs.SaveFiles.Enqueue(Path.Combine(app.Paths.Exports, "no-such-folder", "graph.png"));

        await app.Ui.Run(() => vm.ExportPngCommand.Execute(null));

        (await app.Ui.Run(() => vm.InfoMessage)).Should().StartWith("Cannot export the graph: ");
        (await app.Ui.Run(() => vm.InfoSeverity)).Should().Be(InfoSeverity.Error);
    }
}
