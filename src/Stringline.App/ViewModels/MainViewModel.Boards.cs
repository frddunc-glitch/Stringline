using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Controls;
using Stringline.App.Services;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>A train's plan drawn as a preview (a ghost) on every board that shows its corridor.</summary>
internal sealed record GhostPlan(TrainPlan Plan, string Key);

/// <summary>
/// Several boards (S21, GR-8; Fred: split panes and board windows, locked in time, full editing on each): the main
/// graph, a second graph beside or under it (View ▸ Split view), and board windows of their own (View ▸ New board
/// window), each with its own corridor. They share the plan, selection, layers and colours, and one clock: panning,
/// zooming or following on any moves them all in time (<see cref="GraphTimeLink"/>), while each keeps its corridor
/// range. The board used last is <see cref="ActiveBoard"/>: drag edits, B+drag, the right-click menu, exports and Whole
/// corridor act on it.
/// </summary>
internal sealed partial class MainViewModel
{
    private readonly GraphTimeLink _timeLink = new();
    private IReadOnlyList<GhostPlan>? _ghostPlans;

    /// <summary>The main window's graph, whose corridor the toolbar's tabs pick.</summary>
    public GraphBoardViewModel MainBoard { get; private set; } = null!;

    /// <summary>Every board: the main one first, then the split view's and the board windows', in the order opened.</summary>
    public ObservableCollection<GraphBoardViewModel> Boards { get; } = [];

    /// <summary>The board used last (a press, the wheel or a key on it); the main board at first.</summary>
    public GraphBoardViewModel ActiveBoard { get; private set; } = null!;

    /// <summary>The split view's second graph, while View ▸ Split view is on.</summary>
    [ObservableProperty]
    public partial GraphBoardViewModel? SplitBoard { get; private set; }

    /// <summary>The board windows open.</summary>
    public IEnumerable<GraphBoardViewModel> BoardWindows => Boards.Where(b => b.Place == BoardPlace.Window);

    /// <summary>The main graph's preview of a drag or a suggestion (each board has its own, on its corridor).</summary>
    public IReadOnlyList<GraphPath>? Ghosts => MainBoard.Ghosts;

    /// <summary>The selected train's grips on the main graph (each board has its own).</summary>
    public GraphGrips? Grips => MainBoard.Grips;

    /// <summary>A block being drawn with B+drag on the main graph.</summary>
    public GraphDisruption? DraftDisruption => MainBoard.Draft;

    private void InitBoards()
    {
        MainBoard = AddBoard(BoardPlace.Main, null);
        ActiveBoard = MainBoard;
    }

    /// <summary>The split view (and the boards' layout) once the settings are read.</summary>
    private void InitSplit()
    {
        Layout.PropertyChanged += OnLayoutChanged;
        UpdateSplit(Layout.Current.SplitCorridor);
    }

    /// <summary>Open the board windows that were open when the app last closed; the main window calls this once it is shown.</summary>
    public void RestoreBoards()
    {
        foreach (var saved in Layout.Current.Boards)
        {
            OpenBoardWindow(saved.CorridorId, saved.Window);
        }
    }

    /// <summary>View ▸ New board window: a graph in a window of its own, e.g. for another monitor.</summary>
    [RelayCommand]
    private void NewBoardWindow() => OpenBoardWindow(null, null);

    /// <summary>View ▸ Close board windows.</summary>
    [RelayCommand(CanExecute = nameof(HasBoardWindows))]
    private void CloseBoardWindows()
    {
        foreach (var board in BoardWindows.ToList())
        {
            board.RequestClose();
        }
    }

    public bool HasBoardWindows => BoardWindows.Any();

    /// <summary>A new board window showing a corridor (by ID; the first not shown on another board if null) at a place, if given.</summary>
    public GraphBoardViewModel OpenBoardWindow(string? corridorId, WindowPlacement? placement)
    {
        var board = AddBoard(BoardPlace.Window, corridorId);
        board.Placement = placement;
        _dialogs.ShowBoard(board);
        return board;
    }

    /// <summary>Remember what the boards show for next time: the split view's corridor and the board windows.</summary>
    private WindowLayout BoardsLayout(WindowLayout layout) => layout with
    {
        SplitCorridor = SplitBoard?.PreferredCorridorId ?? layout.SplitCorridor,
        Boards = [.. BoardWindows.Select(b => new BoardLayout(b.PreferredCorridorId, b.Placement))],
    };

    private GraphBoardViewModel AddBoard(BoardPlace place, string? corridorId)
    {
        var board = new GraphBoardViewModel(place, Corridors) { PreferredCorridorId = corridorId };
        board.Editor = new BoardEditor(this, board);
        board.Describer = target => Describe(board, target);
        board.ConflictClicker = target =>
        {
            ActiveBoard = board;
            return ClickConflict(target);
        };
        board.PathClicker = hit =>
        {
            ActiveBoard = board;
            SelectPath(hit);
        };
        board.Activated = () => ActiveBoard = board;
        board.ClearSelectionAction = () => SetSelection(null, null);
        board.Selection = Selection;
        board.Navigator.SetOrientation(IsVertical ? GraphOrientation.Vertical : GraphOrientation.Horizontal);
        if (place != BoardPlace.Main)
        {
            board.Corridor = CorridorFor(corridorId);
        }

        _timeLink.Add(board.Navigator);
        board.PropertyChanged += OnBoardChanged;
        board.CloseRequested += OnBoardCloseRequested;
        Boards.Add(board);
        if (place != BoardPlace.Main)
        {
            board.Ghosts = GhostsFor(board);
            RequestScene(board);
            UpdatePlaceholder();
            CloseBoardWindowsCommand.NotifyCanExecuteChanged();
        }

        return board;
    }

    private void RemoveBoard(GraphBoardViewModel board)
    {
        if (board.IsMain || !Boards.Remove(board))
        {
            return;
        }

        board.PropertyChanged -= OnBoardChanged;
        board.CloseRequested -= OnBoardCloseRequested;
        _timeLink.Remove(board.Navigator);
        if (ReferenceEquals(ActiveBoard, board))
        {
            ActiveBoard = MainBoard;
        }

        if (ReferenceEquals(SplitBoard, board))
        {
            SplitBoard = null;
        }

        CloseBoardWindowsCommand.NotifyCanExecuteChanged();
    }

    private void OnBoardCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not GraphBoardViewModel board)
        {
            return;
        }

        if (board.Place == BoardPlace.Split)
        {
            Layout.Split = BoardSplit.None;
        }
        else
        {
            RemoveBoard(board);
        }
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayoutViewModel.Split))
        {
            UpdateSplit(SplitBoard?.PreferredCorridorId ?? Layout.Current.SplitCorridor);
        }
    }

    /// <summary>Open or close the split view's board to match <see cref="LayoutViewModel.Split"/>.</summary>
    private void UpdateSplit(string? corridorId)
    {
        if (Layout.Split == BoardSplit.None)
        {
            if (SplitBoard is { } split)
            {
                Layout.RememberSplitCorridor(split.PreferredCorridorId);
                RemoveBoard(split);
            }
        }
        else if (SplitBoard is null)
        {
            SplitBoard = AddBoard(BoardPlace.Split, corridorId);
        }
    }

    /// <summary>
    /// A new board's corridor: the one asked for, else the first that no board shows, else the main graph's; null
    /// before a timetable is loaded (the ID is kept for when one is).
    /// </summary>
    private CorridorTab? CorridorFor(string? id) =>
        (id is null ? null : Corridors.FirstOrDefault(c => c.Id == id))
        ?? Corridors.FirstOrDefault(c => !Boards.Any(b => b.Corridor?.Id == c.Id))
        ?? SelectedCorridor
        ?? Corridors.FirstOrDefault();

    /// <summary>The corridor tabs were made again (a timetable or the profile): every other board shows its corridor again, by ID.</summary>
    private void RefreshBoardCorridors()
    {
        foreach (var board in Boards.Where(b => !b.IsMain))
        {
            board.Corridor = CorridorFor(board.PreferredCorridorId);
        }
    }

    private void OnBoardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GraphBoardViewModel board)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(GraphBoardViewModel.Corridor):
                board.Ghosts = GhostsFor(board);
                if (!board.IsMain)
                {
                    // The main board's scene is asked for by the tabs (OnSelectedCorridorChanged).
                    RequestScene(board);
                    UpdatePlaceholder();
                }

                break;
            case nameof(GraphBoardViewModel.Scene):
                UpdateGrips(board);
                if (board.IsMain)
                {
                    OnPropertyChanged(nameof(Scene));
                }

                break;
            case nameof(GraphBoardViewModel.Ghosts) when board.IsMain:
                OnPropertyChanged(nameof(Ghosts));
                break;
            case nameof(GraphBoardViewModel.Grips) when board.IsMain:
                OnPropertyChanged(nameof(Grips));
                break;
            case nameof(GraphBoardViewModel.Draft) when board.IsMain:
                OnPropertyChanged(nameof(DraftDisruption));
                break;
        }
    }

    partial void OnSelectionChanged(GraphSelection? value)
    {
        foreach (var board in Boards)
        {
            board.Selection = value;
        }
    }

    // ---- Ghosts and grips on every board ------------------------------------------------------

    /// <summary>Show these plans as ghosts on every board that shows their corridor; null clears them.</summary>
    private void SetGhosts(IReadOnlyList<GhostPlan>? plans)
    {
        _ghostPlans = plans;
        foreach (var board in Boards)
        {
            board.Ghosts = GhostsFor(board);
        }
    }

    /// <summary>The ghost plans' paths on a board's corridor.</summary>
    private List<GraphPath>? GhostsFor(GraphBoardViewModel board)
    {
        if (_ghostPlans is not { } plans || board.Corridor is not { } tab)
        {
            return null;
        }

        var paths = new List<GraphPath>();
        foreach (var ghost in plans)
        {
            var colour = Profile?.StyleFor(ghost.Plan.CategoryId).Colour ?? CategoryStyle.Fallback.Colour;
            paths.AddRange(GraphSceneBuilder.PlanPaths(tab.Corridor, ghost.Plan, colour, ghost.Key));
        }

        return paths;
    }

    /// <summary>The grips for the selected train on every board.</summary>
    private void UpdateGrips()
    {
        foreach (var board in Boards)
        {
            UpdateGrips(board);
        }
    }

    /// <summary>The grips for the selected train in a board's scene now, keeping the grip picked, and the one dragged.</summary>
    private void UpdateGrips(GraphBoardViewModel board)
    {
        var grips = board.Scene is { } scene ? GraphGrips.For(scene, Snapshot, Selection) : null;
        if (grips is not null && board.Grips is { } old && old.Train == grips.Train)
        {
            grips = grips.WithSelected(old.Selected is { } picked ? grips.Find(picked) : null).WithDrag(old.Drag);
        }

        board.Grips = grips;
    }

    /// <summary>
    /// The graph control's editor for one board: it marks the board as the one in use, then hands the call to the
    /// main view model, whose editing works on <see cref="ActiveBoard"/>.
    /// </summary>
    private sealed class BoardEditor(MainViewModel main, GraphBoardViewModel board) : IGraphEditor
    {
        private MainViewModel Use()
        {
            main.ActiveBoard = board;
            return main;
        }

        public bool BeginEdit(GraphHit hit, ScreenPoint at, bool fromVertex) => Use().BeginEdit(hit, at, fromVertex);

        public GraphGrip? GripAt(ScreenPoint at) => board.Grips?.HitTest(board.Navigator.Viewport, at);

        public GraphHit? SelectedLineAt(ScreenPoint at) => board.Grips?.LineAt(board.Navigator.Viewport, at);

        public void HoverGrip(GraphGrip? grip, bool overLine) => Use().HoverGrip(grip, overLine);

        public HoverInfo DescribeGrip(GraphGrip grip) => GraphGrips.Describe(grip, board.Navigator.Viewport.IsVertical);

        public bool BeginGrip(GraphGrip grip, ScreenPoint at) => Use().BeginGrip(grip, at);

        public void SelectGrip(GraphGrip grip) => Use().SelectGrip(grip);

        public bool Nudge(int seconds) => Use().Nudge(seconds);

        public void MoveEdit(ScreenPoint at, bool fine) => Use().MoveEdit(at, fine);

        public Task EndEditAsync(bool commit) => Use().EndEditAsync(commit);

        public bool BeginBlock(ScreenPoint at) => Use().BeginBlock(at);

        public void MoveBlock(ScreenPoint at) => Use().MoveBlock(at);

        public Task EndBlockAsync(bool commit) => Use().EndBlockAsync(commit);

        public IReadOnlyList<PlanMenuItem> MenuFor(GraphHit? hit, ScreenPoint at) => Use().MenuFor(hit, at);
    }
}
