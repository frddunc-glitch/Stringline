using System.Windows;
using System.Windows.Controls;
using Stringline.App.ViewModels;

namespace Stringline.App.Controls;

/// <summary>
/// A board (S21) other than the main graph: a corridor picker over a <see cref="GraphView"/>, for the split view and
/// board windows. Its data context is a <see cref="GraphBoardViewModel"/>; the graph is attached to it and moves its
/// now line on the board's ticks.
/// </summary>
public partial class BoardView : UserControl
{
    private GraphBoardViewModel? _board;

    public BoardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>The graph inside, for the window to focus and tests to find.</summary>
    public GraphView GraphControl => Graph;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_board is not null)
        {
            _board.NowMoved -= OnNowMoved;
        }

        _board = e.NewValue as GraphBoardViewModel;
        if (_board is not null)
        {
            Graph.Attach(_board);
            _board.NowMoved += OnNowMoved;
        }
    }

    private void OnNowMoved(object? sender, double? now) => Graph.SetNow(now);
}
