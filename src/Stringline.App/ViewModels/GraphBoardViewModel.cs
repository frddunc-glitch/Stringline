using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Controls;
using Stringline.App.Services;
using Stringline.Graph;

namespace Stringline.App.ViewModels;

/// <summary>Where a board is shown (S21).</summary>
internal enum BoardPlace
{
    /// <summary>The main window's graph, whose corridor the toolbar's tabs pick.</summary>
    Main,

    /// <summary>The second graph of the main window's split view.</summary>
    Split,

    /// <summary>A board window of its own, e.g. on another monitor.</summary>
    Window,
}

/// <summary>
/// One train graph (a "board", S21): its corridor, its navigator (pan, zoom and follow; locked in time to the other
/// boards), the scene built for it, and the editing marks drawn on it (ghosts, grips, a block being drawn). The main
/// window's graph is one; the split view and each board window are others. Plan edits, the selection and the layers
/// are the <see cref="MainViewModel"/>'s, shared by every board. No WPF types.
/// </summary>
internal sealed partial class GraphBoardViewModel : ObservableObject
{
    public GraphBoardViewModel(BoardPlace place, ObservableCollection<CorridorTab> corridors)
    {
        Place = place;
        Corridors = corridors;
    }

    public BoardPlace Place { get; }

    public bool IsMain => Place == BoardPlace.Main;

    /// <summary>The split view's board, which has a button to close the split.</summary>
    public bool IsSplit => Place == BoardPlace.Split;

    /// <summary>The corridors to pick from (the main view model's tabs).</summary>
    public ObservableCollection<CorridorTab> Corridors { get; }

    public GraphNavigator Navigator { get; } = new();

    /// <summary>The corridor drawn; null before a timetable is loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial CorridorTab? Corridor { get; set; }

    [ObservableProperty]
    public partial GraphScene? Scene { get; internal set; }

    /// <summary>A preview over the graph, on this board's corridor: a train being dragged, or a suggestion.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GraphPath>? Ghosts { get; internal set; }

    /// <summary>The selected train's grips on this board (S26), with the one under the mouse, picked and dragged.</summary>
    [ObservableProperty]
    public partial GraphGrips? Grips { get; internal set; }

    /// <summary>The selected train (and conflict), the same on every board.</summary>
    [ObservableProperty]
    public partial GraphSelection? Selection { get; internal set; }

    /// <summary>A block being drawn with B+drag on this board.</summary>
    [ObservableProperty]
    public partial GraphDisruption? Draft { get; internal set; }

    /// <summary>Text over the graph when there is nothing to draw.</summary>
    [ObservableProperty]
    public partial string? Placeholder { get; internal set; }

    /// <summary>
    /// The corridor's ID: the last one shown, or one asked for before a timetable was loaded. It is kept while the tabs
    /// are made again (a picker then shows none for a moment), so the board comes back to it.
    /// </summary>
    public string? PreferredCorridorId { get; set; }

    /// <summary>A board window's place and size, kept as the window moves, so it opens there next time.</summary>
    public WindowPlacement? Placement { get; set; }

    partial void OnCorridorChanged(CorridorTab? value)
    {
        if (value is null)
        {
            return;
        }

        // Another corridor is shown whole; the same one made again (the tabs rebuilt) keeps its view.
        if (!IsMain && value.Id != PreferredCorridorId)
        {
            Navigator.ShowFullCorridor();
        }

        PreferredCorridorId = value.Id;
    }

    /// <summary>A board window's title: "Stringline board — Main line".</summary>
    public string Title => Corridor is { } c ? $"Stringline board — {c.Name}" : "Stringline board";

    /// <summary>Turns the mouse on this board into edits; set by the main view model.</summary>
    internal IGraphEditor Editor { get; set; } = null!;

    /// <summary>Builds the tooltip for what is under the mouse on this board; set by the main view model.</summary>
    internal Func<GraphTarget, HoverInfo?> Describer { get; set; } = _ => null;

    /// <summary>A click on this board: a conflict's ring or highlight picks it (true); set by the main view model.</summary>
    internal Func<GraphTarget?, bool> ConflictClicker { get; set; } = _ => false;

    /// <summary>A click on a line, or the background (null); set by the main view model.</summary>
    internal Action<GraphHit?> PathClicker { get; set; } = _ => { };

    /// <summary>Esc on the board: clear the selection; set by the main view model.</summary>
    internal Action ClearSelectionAction { get; set; } = () => { };

    /// <summary>The board was used (a press, the wheel, a key): menu commands such as exports act on it.</summary>
    internal Action Activated { get; set; } = () => { };

    /// <summary>A scene is being built for this board; another is wanted when it is done.</summary>
    internal bool Building { get; set; }

    internal bool SceneDirty { get; set; }

    /// <summary>The board's window is to close (View ▸ Close board windows, or the app is closing).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The now line moved (sim seconds), on every tick.</summary>
    public event EventHandler<double?>? NowMoved;

    /// <summary>Space: follow the now line, or stop (every board, as they are locked in time).</summary>
    [RelayCommand]
    private void ToggleFollow() => Navigator.ToggleFollow();

    /// <summary>Esc: clear the selection.</summary>
    [RelayCommand]
    private void ClearSelection() => ClearSelectionAction();

    /// <summary>Close this board (its window, or the split view).</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal void MoveNow(double? now) => NowMoved?.Invoke(this, now);

    internal void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
