using System.Windows;
using Stringline.App.Services;
using Stringline.Gateway.Tests;
using Stringline.Graph;

namespace Stringline.App.Tests;

/// <summary>
/// S21's boards in real windows: the split view lays out both ways with its splitter, and a board window shows its
/// graph and lets the board go when closed; no binding errors. With <c>STRINGLINE_WINDOW_SHOTS</c> set, pictures.
/// </summary>
public sealed class BoardWindowTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    [Fact]
    public async Task The_split_view_and_a_board_window_lay_out_with_no_binding_errors()
    {
        await using var app = new TestApp(wpf: true);
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true, "the WTT on the graph");

        var errors = new List<string>();
        await app.Ui.Run(() =>
        {
            using var listener = new ProfileEditorWindowTests.BindingErrorListener(errors);
            var window = MainWindowTests.Show(vm, 1280, 720);
            try
            {
                window.SplitBoardView.Visibility.Should().Be(Visibility.Collapsed, "one graph at first");

                vm.Layout.Split = BoardSplit.Stacked;
                MainWindowTests.Settle(window);
                window.SplitBoardView.IsVisible.Should().BeTrue();
                window.BoardSplitter.IsVisible.Should().BeTrue();
                window.MainBoardRow.ActualHeight.Should().BeGreaterThan(WindowLayout.MinBoardSize - 1);
                window.SplitBoardRow.ActualHeight.Should().BeApproximately(window.MainBoardRow.ActualHeight, 2, "equal shares at first");
                window.SplitBoardView.GraphControl.ActualWidth.Should().BeApproximately(window.Graph.ActualWidth, 1);
                window.SplitBoardView.CorridorPicker.SelectedItem.Should().Be(vm.SplitBoard!.Corridor);

                vm.Layout.Split = BoardSplit.SideBySide;
                MainWindowTests.Settle(window);
                window.SplitBoardRow.ActualHeight.Should().Be(0);
                window.SplitBoardColumn.ActualWidth.Should().BeGreaterThan(WindowLayout.MinBoardSize - 1);
                window.SplitBoardView.GraphControl.ActualHeight.Should().BeGreaterThan(200);

                // The layout keeps the split's shares.
                window.CaptureLayout();
                vm.Layout.Current.Split.Should().Be(BoardSplit.SideBySide);
                vm.Layout.Current.SplitBoardSize.Should().BeApproximately(window.SplitBoardColumn.ActualWidth, 1);

                var board = vm.OpenBoardWindow(null, null);
                var boardWindow = new Views.BoardWindow(board)
                {
                    Owner = window,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 40,
                    Top = 40,
                };
                boardWindow.Show();
                MainWindowTests.Settle(boardWindow);
                boardWindow.Title.Should().StartWith("Stringline board — ");
                boardWindow.Board.GraphControl.ActualWidth.Should().BeGreaterThan(300);
                board.Placement.Should().NotBeNull("the window tells the board where it is");

                boardWindow.Close();
                vm.Boards.Should().NotContain(board, "closing the window lets the board go");

                vm.Layout.Split = BoardSplit.None;
                MainWindowTests.Settle(window);
                window.SplitBoardView.IsVisible.Should().BeFalse();
                window.BoardSplitter.IsVisible.Should().BeFalse();
            }
            finally
            {
                window.Close();
            }
        });

        errors.Should().BeEmpty();
    }
}
