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
        ClickConflict(Scene is { } scene ? GraphHitTester.TargetAt(scene, Navigator.Viewport, at) : null);

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
    /// Show a conflict (FC-4): on the corridor shown if it meets it, else the first that does; the view stops following
    /// and centres on where and when the train that must give way meets it, and that train is selected.
    /// </summary>
    public void FocusConflict(Conflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (SelectedCorridor is not { } current || !GraphConflicts.Shows(current.Corridor, conflict))
        {
            if (Corridors.FirstOrDefault(c => GraphConflicts.Shows(c.Corridor, conflict)) is { } other)
            {
                SelectedCorridor = other;
            }
            else
            {
                ShowInfo(InfoSeverity.Info, $"{conflict.Summary} at {Profile?.DisplayName(conflict.Where) ?? conflict.Where.Code}: not on any corridor's graph.");
            }
        }

        var axis = SelectedCorridor?.Corridor.PositionOf(conflict.Where);
        if (axis is { } a)
        {
            Navigator.CentreOn(conflict.When.Seconds, a);
        }
        else
        {
            Navigator.CentreOn(conflict.When.Seconds);
        }

        SelectConflict(conflict);
    }

    private void SelectConflict(Conflict conflict) =>
        SetSelection(conflict.A, Snapshot.FindByTrain(conflict.A)?.Train.SimUid, conflict.Id);
}
