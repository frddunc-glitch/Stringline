using System.Text;
using System.Text.RegularExpressions;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>
/// S21's boards on Waterloo (Fred: split panes and board windows, locked in time, full editing on each), and the
/// multi-page plot.
/// </summary>
public sealed partial class BoardTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static async Task<MainViewModel> LoadWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true && vm.Snapshot.Plan.Count > 0, "the WTT on the graph");
        await app.Ui.Run(() => vm.Tick());
        return vm;
    }

    private static Task WaitForScene(TestApp app, GraphBoardViewModel board, string corridorId) =>
        app.Ui.WaitUntil(() => board.Scene?.CorridorId == corridorId && board.Scene.InLayer(GraphLayer.Wtt).Any(), $"the {corridorId} scene on the board");

    private static (double, double) TimeOf(GraphBoardViewModel board) => (board.Navigator.Viewport.TimeStart, board.Navigator.Viewport.TimeEnd);

    [Fact]
    public async Task The_split_view_shows_another_corridor_on_the_same_clock_and_comes_back_with_it()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (main, other) = await app.Ui.Run(() => (vm.Corridors[0], vm.Corridors[1]));

        var split = await app.Ui.Run(() =>
        {
            vm.SplitBoard.Should().BeNull("the split view is off at first");
            vm.Layout.SetSplitCommand.Execute("Stacked");
            vm.Layout.IsSplitStacked.Should().BeTrue();
            return vm.SplitBoard!;
        });
        await WaitForScene(app, split, other.Id);

        await app.Ui.Run(() =>
        {
            split.Place.Should().Be(BoardPlace.Split);
            split.IsSplit.Should().BeTrue();
            split.Corridor.Should().Be(other, "a new board shows the first corridor no other board does");
            vm.Boards.Should().Equal(vm.MainBoard, split);
            vm.SelectedCorridor.Should().Be(main, "the main graph keeps its corridor");

            TimeOf(split).Should().Be(TimeOf(vm.MainBoard), "the boards are locked in time");
            vm.ZoomCommand.Execute("1");
            split.Navigator.Viewport.TimeSpan.Should().Be(3600);
            split.Navigator.Wheel(120, 400, 300);
            TimeOf(vm.MainBoard).Should().Be(TimeOf(split), "zooming either moves both");
            split.Navigator.BeginDrag(new ScreenPoint(400, 300));
            split.Navigator.DragTo(new ScreenPoint(500, 300));
            split.Navigator.EndDrag();
            vm.Navigator.IsFollowing.Should().BeFalse("panning a board in time stops them all following");
            TimeOf(vm.MainBoard).Should().Be(TimeOf(split));

            vm.IsVertical = true;
            split.Navigator.Viewport.IsVertical.Should().BeTrue("orientation is the same on every board");
            vm.IsVertical = false;

            split.Corridor = main;
        });
        await WaitForScene(app, split, main.Id);

        await app.Ui.Run(() =>
        {
            split.CloseCommand.Execute(null);
            vm.Layout.Split.Should().Be(BoardSplit.None, "the split's ✕ turns the split view off");
            vm.SplitBoard.Should().BeNull();
            vm.Boards.Should().Equal(vm.MainBoard);

            vm.Layout.SetSplitCommand.Execute("SideBySide");
            vm.SplitBoard!.Corridor.Should().Be(main, "the split comes back with the corridor it last showed");
        });
    }

    [Fact]
    public async Task Board_windows_open_share_the_selection_and_are_remembered()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var other = await app.Ui.Run(() => vm.Corridors[1]);

        var board = await app.Ui.Run(() =>
        {
            vm.CloseBoardWindowsCommand.CanExecute(null).Should().BeFalse();
            vm.NewBoardWindowCommand.Execute(null);
            vm.CloseBoardWindowsCommand.CanExecute(null).Should().BeTrue();
            return app.Dialogs.BoardsShown.Should().ContainSingle().Subject;
        });
        await WaitForScene(app, board, other.Id);

        await app.Ui.Run(() =>
        {
            board.Place.Should().Be(BoardPlace.Window);
            board.Title.Should().Be($"Stringline board — {other.Name}");
            vm.BoardWindows.Should().Equal(board);

            // A click on a line on the board selects the train everywhere.
            var path = board.Scene!.InLayer(GraphLayer.Wtt).First();
            board.PathClicker(new GraphHit(path, 0, 0, 0, path.Vertices[0].Time));
            vm.Selection!.Train.Should().Be(path.Train);
            board.Selection.Should().Be(vm.Selection);
            vm.MainBoard.Selection.Should().Be(vm.Selection);
            vm.ActiveBoard.Should().Be(board, "the board clicked is the one in use");
            board.ClearSelectionCommand.Execute(null);
            vm.Selection.Should().BeNull();
            board.Selection.Should().BeNull();

            board.Placement = new WindowPlacement(100, 120, 900, 600);
            vm.SaveLayout();
        });

        var stored = app.Settings.Load().Layout;
        stored.Boards.Should().Equal(new BoardLayout(other.Id, new WindowPlacement(100, 120, 900, 600)));

        await app.Ui.Run(() =>
        {
            vm.CloseBoardWindowsCommand.Execute(null);
            vm.BoardWindows.Should().BeEmpty();
            vm.ActiveBoard.Should().Be(vm.MainBoard, "the board in use went with its window");
        });

        // The next start opens the board windows that were open when the app closed.
        await using var again = new TestApp(root: app.Root);
        await again.Ui.Run(() => again.ViewModel.RestoreBoards());
        var reopened = again.Dialogs.BoardsShown.Should().ContainSingle().Subject;
        (await again.Ui.Run(() => (reopened.PreferredCorridorId, reopened.Placement, reopened.Corridor)))
            .Should().Be((other.Id, new WindowPlacement(100, 120, 900, 600), null), "its corridor comes once a timetable is loaded");
        await LoadWaterloo(again);
        await WaitForScene(again, reopened, other.Id);
    }

    [Fact]
    public async Task A_train_can_be_dragged_on_any_board_and_its_ghost_shows_on_every_board_with_its_corridor()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var main = await app.Ui.Run(() =>
        {
            vm.Layout.Split = BoardSplit.SideBySide;
            vm.SplitBoard!.Corridor = vm.SelectedCorridor;
            return vm.SelectedCorridor!;
        });
        var split = await app.Ui.Run(() => vm.SplitBoard!);
        await WaitForScene(app, split, main.Id);

        var grip = await app.Ui.Run(() =>
        {
            var view = split.Navigator.Viewport;
            foreach (var path in split.Scene!.InLayer(GraphLayer.Wtt))
            {
                for (var v = 1; v < path.Vertices.Count; v++)
                {
                    var at = view.ToScreen(path.Vertices[v].Time, path.Vertices[v].Axis);
                    if (!view.IsInPlot(at) || at.X > view.PlotRight - 150 || at.X < view.PlotLeft + 50)
                    {
                        continue;
                    }

                    split.PathClicker(new GraphHit(path, v - 1, 0, v, path.Vertices[v].Time));
                    var found = split.Grips?.InView(view).FirstOrDefault(g => g.Kind == GraphGripKind.Point && !g.IsLocked && g.Index > 0
                        && view.ToScreen(g.At.Time, g.At.Axis).X < view.PlotRight - 150);
                    if (found is not null && split.Editor.GripAt(view.ToScreen(found.At.Time, found.At.Axis)) is { } hit && hit.SameAs(found))
                    {
                        return found;
                    }
                }
            }

            throw new InvalidOperationException("No train with a grip on the split board.");
        });

        await app.Ui.Run(() =>
        {
            vm.MainBoard.Grips!.Train.Should().Be(grip.Train, "the selected train has its grips on every board");
            var view = split.Navigator.Viewport;
            var at = view.ToScreen(grip.At.Time, grip.At.Axis);
            split.Editor.BeginGrip(grip, at).Should().BeTrue();
            vm.ActiveBoard.Should().Be(split);
            split.Grips!.Drag!.Grip.Should().Be(grip, "the grip is dragged on the board it was pressed on");
            vm.MainBoard.Grips!.Drag.Should().BeNull();

            split.Editor.MoveEdit(new ScreenPoint(at.X + (180 * view.PixelsPerSecond), at.Y), fine: false);
            split.Ghosts.Should().NotBeNullOrEmpty();
            vm.MainBoard.Ghosts.Should().NotBeNullOrEmpty("the ghost shows on every board with the train's corridor");
        });

        await app.Ui.RunAsync(() => split.Editor.EndEditAsync(commit: true));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 1, "the edit");
        (await app.Ui.Run(() => vm.Snapshot.History.Done[0].Command)).Should().Be(new ShiftTrain(grip.Train, grip.Index, 180));
        await app.Ui.WaitUntil(() => vm.Boards.All(b => b.Ghosts is null), "the ghosts dropped once the boards draw the edit");
    }

    [Fact]
    public async Task Exports_and_whole_corridor_act_on_the_board_last_used()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var other = await app.Ui.Run(() =>
        {
            vm.Layout.Split = BoardSplit.Stacked;
            return vm.Corridors[1];
        });
        await WaitForScene(app, vm.SplitBoard!, other.Id);

        await app.Ui.Run(() =>
        {
            var split = vm.SplitBoard!;
            split.Navigator.Wheel(240, 400, 300, GraphModifiers.Control);
            split.Activated();
            vm.ActiveBoard.Should().Be(split);
            vm.ShowFullCorridorCommand.Execute(null);
            split.Navigator.Viewport.ShowsFullAxis.Should().BeTrue();

            vm.ExportPngCommand.Execute("1");
            app.Dialogs.SaveSuggestions.Should().ContainSingle().Which.Should().Contain($"-{other.Id}-", "the board in use is exported");
        });
    }

    [Fact]
    public async Task A_plot_writes_a_page_for_each_stretch_of_time_and_remembers_the_choices()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var path = Path.Combine(app.Root, "plot.pdf");
        app.Dialogs.SaveFiles.Enqueue(path);
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields.Select(f => f.Label).Should().Equal("Corridor", "From", "To", "Minutes a page", "Paper", "Time runs");
            p.Fields[4].Value.Should().Be("A3 portrait", "time down on A3 portrait by default");
            p.Fields[5].Value.Should().Be("Down the page");
            p.Fields[1].Value = "06:00";
            p.Fields[2].Value = "08:30";
            p.Fields[3].Value = "60";
            return true;
        });

        await app.Ui.RunAsync(() => vm.ExportPlotCommand.ExecuteAsync(null));

        var pdf = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(path));
        PagePattern().Matches(pdf).Should().HaveCount(3, "06:00–08:30 at an hour a page");
        pdf.Should().Contain("/MediaBox [0 0 842 1191]", "A3 portrait");
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Contain("the plot (3 pages, A3 portrait)");
        app.Settings.Load().Plot.Should().Be(new PlotSettings { Hours = 2.5, MinutesPerPage = 60, Paper = "A3 portrait", Across = false });

        // A plot that cannot be made is refused with the reason, and nothing is written.
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[3].Value = "1";
            p.TryAccept().Should().BeFalse();
            p.Error.Should().Contain("at least 5 minutes");
            return false;
        });
        await app.Ui.RunAsync(() => vm.ExportPlotCommand.ExecuteAsync(null));
        app.Dialogs.SaveSuggestions.Should().ContainSingle();
    }

    [GeneratedRegex(@"/Type\s*/Page\b(?!s)")]
    private static partial Regex PagePattern();
}
