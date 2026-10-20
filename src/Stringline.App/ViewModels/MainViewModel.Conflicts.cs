using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Conflicts in the shell (S14, FC-3 and FC-4): the conflicts panel, the count in the status bar, the rings' tooltips,
/// and focusing the graph on a conflict picked in the list or clicked on the graph.
/// </summary>
internal sealed partial class MainViewModel
{
    /// <summary>The conflicts panel (a tab beside the edit history; Fred).</summary>
    public ConflictsViewModel ConflictList { get; } = new();

    /// <summary>Mark conflicts on the graph. Not remembered, like the other layers.</summary>
    [ObservableProperty]
    public partial bool ShowConflicts { get; set; } = true;

    /// <summary>The status bar's count: "2 conflicts"; empty with no timetable.</summary>
    [ObservableProperty]
    public partial string ConflictsText { get; private set; } = string.Empty;

    partial void OnShowConflictsChanged(bool value) => RequestScene();

    private void InitConflicts() => ConflictList.Picked += (_, conflict) => FocusConflict(conflict);

    /// <summary>A new snapshot: refresh the list and the count.</summary>
    private void UpdateConflicts()
    {
        ConflictList.Update(Snapshot.Conflicts, Profile);
        ConflictsText = Snapshot.Plan.Count == 0 ? string.Empty : ConflictList.Summary;
    }

    /// <summary>A click on the graph at a point: see <see cref="ClickConflict"/>.</summary>
    public bool ClickConflictAt(ScreenPoint at) =>
        ClickConflict(ActiveBoard.Scene is { } scene ? GraphHitTester.TargetAt(scene, ActiveBoard.Navigator.Viewport, at) : null);

    /// <summary>
    /// A click on the graph: a conflict's ring, or its highlight away from any line (#12), picks it (true); otherwise
    /// the click is the train line's.
    /// </summary>
    public bool ClickConflict(GraphTarget? target)
    {
        if (target is not { Kind: GraphTargetKind.ConflictRing or GraphTargetKind.Conflict, Conflict: { } conflict })
        {
            return false;
        }

        ConflictList.Select(conflict.Id);
        SelectConflict(conflict.Conflict);
        return true;
    }

    /// <summary>The best suggestion for a conflict, worded as the suggestions panel shows it, if it has been worked out.</summary>
    private string? TopSuggestionFor(Conflict conflict) =>
        SuggestionPanel.ConflictId == conflict.Id && SuggestionPanel.Rows.Count > 0 ? SuggestionPanel.Rows[0].Title : null;

    /// <summary>
    /// Show a conflict (FC-4): on the board last used if its corridor meets it, else another board that shows it
    /// (S21), else the main graph switches to the first corridor that does. The view stops following and centres on
    /// where and when the train that must give way meets it (every board moves in time with it), and that train is
    /// selected.
    /// </summary>
    public void FocusConflict(Conflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        var board = Shows(ActiveBoard, conflict) ? ActiveBoard : Boards.FirstOrDefault(b => Shows(b, conflict));
        if (board is null)
        {
            board = MainBoard;
            if (Corridors.FirstOrDefault(c => GraphConflicts.Shows(c.Corridor, conflict)) is { } other)
            {
                SelectedCorridor = other;
            }
            else
            {
                ShowInfo(InfoSeverity.Info, $"{conflict.Summary} at {Profile?.DisplayName(conflict.Where) ?? conflict.Where.Code}: not on any corridor's graph.");
            }
        }

        var axis = board.Corridor?.Corridor.PositionOf(conflict.Where);
        if (axis is { } a)
        {
            board.Navigator.CentreOn(conflict.When.Seconds, a);
        }
        else
        {
            board.Navigator.CentreOn(conflict.When.Seconds);
        }

        SelectConflict(conflict);
    }

    private static bool Shows(GraphBoardViewModel board, Conflict conflict) =>
        board.Corridor is { } tab && GraphConflicts.Shows(tab.Corridor, conflict);

    private void SelectConflict(Conflict conflict) =>
        SetSelection(conflict.A, Snapshot.FindByTrain(conflict.A)?.Train.SimUid, conflict.Id);
}
