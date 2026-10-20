using System.Windows;
using System.Windows.Input;
using Stringline.App.Services;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>
/// A board window (S21): one corridor's graph in a window of its own, e.g. on another monitor. It opens where it was
/// last time (on a screen that is still there), keeps the board told where it is, closes when the board asks, and
/// tells the board when the user closes it. Undo, redo and the graph exports work here as in the main window.
/// </summary>
public partial class BoardWindow : Window
{
    private readonly GraphBoardViewModel _board;
    private bool _closed;

    internal BoardWindow(GraphBoardViewModel board)
    {
        InitializeComponent();
        _board = board;
        DataContext = board;
        Place(board.Placement);
        board.CloseRequested += OnCloseRequested;
        LocationChanged += (_, _) => Remember();
        SizeChanged += (_, _) => Remember();
        StateChanged += (_, _) => Remember();
        Loaded += (_, _) =>
        {
            AddShortcuts();
            Remember();
            Board.GraphControl.Focus();
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        _board.CloseRequested -= OnCloseRequested;
        _board.CloseCommand.Execute(null); // the main view model lets the board go
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        if (!_closed)
        {
            Close();
        }
    }

    /// <summary>The main window's undo, redo and graph exports, which act on the board last used (this one, once clicked).</summary>
    private void AddShortcuts()
    {
        if (Owner?.DataContext is not MainViewModel main)
        {
            return;
        }

        InputBindings.Add(new KeyBinding(main.UndoCommand, Key.Z, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(main.RedoCommand, Key.Y, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(main.RedoCommand, Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(main.ExportPngCommand, Key.E, ModifierKeys.Control) { CommandParameter = "1" });
        InputBindings.Add(new KeyBinding(main.ExportPdfCommand, Key.P, ModifierKeys.Control) { CommandParameter = "a3" });
    }

    private void Place(WindowPlacement? saved)
    {
        if (saved is null)
        {
            return;
        }

        var placed = saved.OnScreen(MainWindow.WorkAreas());
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (placed.Left, placed.Top, Math.Max(MinWidth, placed.Width), Math.Max(MinHeight, placed.Height));
        if (placed.Maximised)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void Remember()
    {
        if (!IsLoaded)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _board.Placement = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized);
        }
    }
}
