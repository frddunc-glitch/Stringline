using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Controls;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Editing.Drafts;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Planning;
using Stringline.Timetable.Editing;
using Stringline.Timetable.Live;

namespace Stringline.App.ViewModels;

/// <summary>
/// What the draft board needs from the app (S44), worked out in the background as the draft opens: the sim's profile
/// (its corridors and colours), the network the forecast runs on, the original timetable for ghosts and minimum runs,
/// and the planning settings, so the draft's conflicts show as the session's would.
/// </summary>
/// <param name="Original">The timetable the draft was made from (its source file), else the draft as it was opened.</param>
internal sealed record DraftBoardSetup(StringlineProfile Profile, ForecastNetwork Network, WorkingTimetable Original, ForecastOptions Forecast, ConflictOptions Conflicts)
{
    /// <summary>Why the corridors are not the sim's saved ones (none was found), if so.</summary>
    public string? Note { get; init; }

    public SignalDisplay Signals { get; init; } = SignalDisplay.Ticks;

    public bool Vertical { get; init; }

    /// <summary>The least runs the original timetable books, which a drag falls back to without the sim's knowledge.</summary>
    public MinRunTable OriginalMinRuns { get; } = MinRunTable.Build(Original);
}

/// <summary>Where exports go and the Plot dialog's choices: the main window's, shared with the draft board (S44).</summary>
internal sealed record DraftExports(Func<string> Folder, Action<string> Exported, Func<PlotSettings> Plot, Action<PlotSettings> SavePlot);

/// <summary>
/// The draft board (S44, <c>08-…md</c> §7): a train graph of the draft in the editor window, over the profile's
/// corridors (generated from the draft when the sim has no profile, Fred). It shows the booking only, with the booked
/// conflicts the forecaster and detector find with no live data, and lists the trains no corridor draws. The selection
/// is the trip grid's: a train picked in either picks it in the other, and a row picks its point's grip. Grips edit the
/// draft (<see cref="DraftDragTarget"/>): a drag or nudge is one timetable edit, in half minutes, held at the least run
/// known (Shift goes past it). View ▸ Draft ghosts adds the original timetable's lines of changed trains and a
/// recording's actuals. No WPF types.
/// </summary>
internal sealed partial class DraftBoardViewModel : ObservableObject, IGraphEditor
{
    private readonly TimetableEditorViewModel _editor;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly ILogger _logger;
    private readonly DraftExports? _exports;
    private DraftBoardSetup? _setup;
    private PlanSnapshot? _plan;
    private int _planSequence = -1;
    private DraftDragTarget? _target;
    private int _targetSequence = -1;
    private IReadOnlyList<LiveTrain> _actuals = [];
    private IReadOnlyList<GraphPath>? _dragGhost;
    private int? _ghostUntil;
    private PlanDrag? _drag;
    private GraphGrip? _dragGrip;
    private DraftTrainViewModel? _train;
    private bool _syncing;
    private bool _building;
    private bool _dirty;

    public DraftBoardViewModel(TimetableEditorViewModel editor, IDialogService dialogs, IUiDispatcher ui, ILogger logger, DraftExports? exports = null)
    {
        _editor = editor;
        _dialogs = dialogs;
        _ui = ui;
        _logger = logger;
        _exports = exports;
        Board = new GraphBoardViewModel(BoardPlace.Window, Corridors)
        {
            Editor = this,
            Placeholder = "Reading the sim's corridors…",
        };
        Board.Describer = Describe;
        Board.ConflictClicker = ClickConflict;
        Board.PathClicker = ClickPath;
        Board.ClearSelectionAction = () => SelectTrain(null);
        Board.PropertyChanged += OnBoardChanged;
        editor.PropertyChanged += OnEditorChanged;
    }

    /// <summary>The graph: its corridor, navigator, scene, selection, grips and ghosts.</summary>
    public GraphBoardViewModel Board { get; }

    /// <summary>The profile's corridors, to pick from.</summary>
    public ObservableCollection<CorridorTab> Corridors { get; } = [];

    /// <summary>The draft's plan as the board last drew it: its booking, forecast with no live data and conflicts.</summary>
    public PlanSnapshot? Plan => _plan;

    public bool IsReady => _setup is not null;

    /// <summary>View ▸ Draft ghosts ▸ Original timetable: the original's lines of the trains the draft changed.</summary>
    [ObservableProperty]
    public partial bool ShowOriginal { get; set; } = true;

    /// <summary>The recording whose actuals are drawn as ghosts, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecording))]
    [NotifyCanExecuteChangedFor(nameof(ClearRecordingCommand))]
    public partial string? RecordingName { get; private set; }

    public bool HasRecording => RecordingName is not null;

    /// <summary>The board's status line: conflicts, the trains no corridor draws, and the profile's note.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>What a drag or the grip under the mouse does, or why it stopped.</summary>
    [ObservableProperty]
    public partial string? EditStatus { get; private set; }

    /// <summary>The trains no corridor draws (the status line names the first few).</summary>
    public IReadOnlyList<Headcode> OffCorridor { get; private set; } = [];

    public bool IsDragging => _drag is not null;

    /// <summary>The app's profile and settings for the board: it draws once they are in.</summary>
    public void SetUp(DraftBoardSetup setup)
    {
        _setup = setup;
        _planSequence = -1;
        Board.Navigator.SetOrientation(setup.Vertical ? GraphOrientation.Vertical : GraphOrientation.Horizontal);
        Corridors.Clear();
        foreach (var corridor in setup.Profile.Corridors)
        {
            Corridors.Add(new CorridorTab(corridor));
        }

        // No now line on a draft: the view starts at the timetable's start, and stays where it is put.
        Board.Navigator.SetFollowing(false);
        var start = _editor.Snapshot.Timetable.Header.StartTime;
        Board.Navigator.CentreOn(start.Seconds + (Board.Navigator.Viewport.TimeSpan / 2));
        Board.Corridor = Corridors.FirstOrDefault();
        Board.Placeholder = Corridors.Count == 0 ? "The sim has no corridors to draw yet: add trains, or make a profile (Tools ▸ Edit profile…)." : null;
        OnPropertyChanged(nameof(IsReady));
        SyncFromGrid();
        Rebuild();
    }

    // ---- Building the scene ---------------------------------------------------------------------

    /// <summary>The draft changed, or what is drawn: build the plan and scene again off the UI thread (latest wins).</summary>
    private void Rebuild()
    {
        if (_setup is not { } setup)
        {
            return;
        }

        if (_building)
        {
            _dirty = true;
            return;
        }

        var snapshot = _editor.Snapshot;
        var plan = _planSequence == snapshot.Sequence ? _plan : null;
        var corridor = Board.Corridor?.Corridor;
        var showOriginal = ShowOriginal;
        var actuals = _actuals;
        var title = Title(snapshot);
        _building = true;
        _dirty = false;
        _ = Task.Run(() =>
        {
            var tt = snapshot.Timetable;
            plan ??= DraftBoard.Plan(tt, setup.Network, setup.Original, setup.OriginalMinRuns, setup.Forecast, setup.Conflicts, snapshot.Sequence);
            var originals = showOriginal ? DraftBoard.Changed(setup.Original, tt) : [];
            var scene = corridor is null ? null : DraftBoard.Build(new DraftBoardRequest(setup.Profile, corridor, plan, title)
            {
                Originals = originals,
                Actuals = actuals,
                Signals = setup.Signals,
            });
            var off = DraftBoard.OffCorridors(setup.Profile.Corridors, tt);
            return (Plan: plan, Scene: scene, Off: off, Room: scene is null ? 0 : GraphLabels.RoomFor(scene.Locations));
        }).ContinueWith(
            t => _ui.Post(() =>
            {
                _building = false;
                if (_editor.IsClosed)
                {
                    return;
                }

                if (t.IsCompletedSuccessfully)
                {
                    var (built, scene, off, room) = t.Result;
                    _plan = built;
                    _planSequence = snapshot.Sequence;
                    if (scene is not null)
                    {
                        Board.Navigator.FitLabels(room);
                    }

                    Board.Scene = scene;
                    OffCorridor = [.. off.Select(o => o.Headcode)];
                    if (_ghostUntil is { } until && snapshot.Sequence >= until && _drag is null)
                    {
                        (_ghostUntil, _dragGhost) = (null, null);
                        UpdateGhosts();
                    }

                    UpdateGrips();
                    UpdateStatus();
                }
                else if (t.Exception is { } ex)
                {
                    LogBoardFailed(ex.GetBaseException());
                    Status = $"The draft could not be drawn: {ex.GetBaseException().Message}";
                }

                if (_dirty)
                {
                    Rebuild();
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>"Draft – <i>name</i> <i>version</i>", as the window, the graph's title and exports have it.</summary>
    private string Title(DraftSnapshot snapshot) =>
        $"Draft – {(snapshot.Document.Header.Name is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(_editor.DraftPath))} {snapshot.Version?.ToString() ?? string.Empty}".TrimEnd();

    private void UpdateStatus()
    {
        var parts = new List<string>();
        if (_plan is { } plan)
        {
            var count = plan.Conflicts.Count;
            var here = Board.Scene?.Conflicts.Count ?? 0;
            parts.Add(count == 0 ? "No booked conflicts" : string.Create(CultureInfo.InvariantCulture, $"{count} booked conflict{(count == 1 ? string.Empty : "s")} ({here} on this corridor)"));
        }

        if (OffCorridor.Count > 0)
        {
            var named = string.Join(", ", OffCorridor.Take(8));
            parts.Add($"Off every corridor: {named}{(OffCorridor.Count > 8 ? string.Create(CultureInfo.InvariantCulture, $" and {OffCorridor.Count - 8} more") : string.Empty)}");
        }

        if (_setup?.Note is { } note)
        {
            parts.Add(note);
        }

        Status = string.Join(". ", parts) + (parts.Count > 0 ? "." : string.Empty);
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TimetableEditorViewModel.Snapshot):
                Rebuild();
                break;
            case nameof(TimetableEditorViewModel.Train):
                if (_train is not null)
                {
                    _train.PropertyChanged -= OnTrainChanged;
                }

                _train = _editor.Train;
                if (_train is not null)
                {
                    _train.PropertyChanged += OnTrainChanged;
                }

                SyncFromGrid();
                break;
        }
    }

    private void OnTrainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DraftTrainViewModel.CurrentTrip))
        {
            SyncPointFromGrid();
        }
    }

    private void OnBoardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphBoardViewModel.Corridor))
        {
            UpdateGhosts();
            Rebuild();
        }
    }

    partial void OnShowOriginalChanged(bool value) => Rebuild();

    /// <summary>View ▸ Draft ghosts ▸ Original timetable.</summary>
    [RelayCommand]
    private void ToggleOriginal() => ShowOriginal = !ShowOriginal;

    // ---- Shared selection ----------------------------------------------------------------------

    /// <summary>The train picked in the grid is picked on the board, and its current row's point.</summary>
    private void SyncFromGrid()
    {
        if (_syncing)
        {
            return;
        }

        var id = _editor.Train is { } train ? TrainIdOf(train.Element) : null;
        if (Board.Selection?.Train != id)
        {
            Board.Selection = id is null ? null : new GraphSelection(id, null);
            UpdateGrips();
            BringIntoView(id);
        }

        SyncPointFromGrid();
    }

    private void SyncPointFromGrid()
    {
        if (_syncing || Board.Grips is not { } grips || _editor.Train?.CurrentTrip is not { } row)
        {
            return;
        }

        var snapshot = _editor.Snapshot;
        if (snapshot.Import.TrainOf(new TrainNode(snapshot.Document, _editor.Train.Element)) is not { } train || snapshot.Import.PointOf(train, row.Element) is not { } index)
        {
            return;
        }

        var grip = grips.Grips.FirstOrDefault(g => g.Kind == GraphGripKind.Point && g.Index == index)
            ?? grips.Grips.FirstOrDefault(g => g.Index == index);
        if (grip is not null && !grip.SameAs(grips.Selected))
        {
            Board.Grips = grips.WithSelected(grip);
        }
    }

    /// <summary>A train picked on the board (or none): the grid goes to it, and to the point if a grip was picked.</summary>
    private void SelectTrain(TrainId? id, int? point = null)
    {
        Board.Selection = id is null ? null : new GraphSelection(id, null);
        UpdateGrips();
        if (id is null)
        {
            return;
        }

        var snapshot = _editor.Snapshot;
        if (snapshot.Timetable.Find(id.Value) is not { } train)
        {
            return;
        }

        var element = snapshot.Import.NodeOf(train).Element;
        var trip = point is { } k && k < train.Points.Count ? snapshot.Import.TripOf(train, k).Element : null;
        _syncing = true;
        try
        {
            _editor.GoTo(element, trip);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The train's ID in the draft's import, if the importer read it.</summary>
    private TrainId? TrainIdOf(XElement element)
    {
        var snapshot = _editor.Snapshot;
        return element.Document == snapshot.Document.Xml ? snapshot.Import.TrainOf(new TrainNode(snapshot.Document, element))?.Id : null;
    }

    /// <summary>Moves the view in time to a train picked in the grid if none of it is in view on this corridor.</summary>
    private void BringIntoView(TrainId? id)
    {
        if (id is null || Board.Scene is not { } scene)
        {
            return;
        }

        var paths = scene.Paths.Where(p => p.Train == id && p.Layer == GraphLayer.Plan).ToList();
        var viewport = Board.Navigator.Viewport;
        if (paths.Count == 0 || paths.Any(p => viewport.Overlaps(p.TimeMin, p.TimeMax, p.AxisMin, p.AxisMax)))
        {
            return;
        }

        Board.Navigator.SetFollowing(false);
        Board.Navigator.CentreOn((paths.Min(p => p.TimeMin) + paths.Max(p => p.TimeMax)) / 2);
    }

    private void ClickPath(GraphHit? hit)
    {
        if (hit?.Path.Train is { } id)
        {
            SelectTrain(id, hit.Path.PointAt(hit.NearestVertex));
        }
        else if (hit is null)
        {
            SelectTrain(null);
        }
    }

    private bool ClickConflict(GraphTarget? target)
    {
        if (target is not { Kind: GraphTargetKind.ConflictRing or GraphTargetKind.Conflict, Conflict: { } conflict })
        {
            return false;
        }

        SelectTrain(conflict.Conflict.A, conflict.Conflict.PointIndex);
        Board.Selection = new GraphSelection(conflict.Conflict.A, null) { ConflictId = conflict.Conflict.Id };
        EditStatus = $"{GraphConflicts.KindName(conflict.Conflict.Kind)}: {conflict.Conflict.Summary}";
        return true;
    }

    private HoverInfo? Describe(GraphTarget target) =>
        Board.Scene is { } scene && _plan is { } plan ? GraphHover.Describe(target, scene, plan) : null;

    // ---- Grips and drags (IGraphEditor) -----------------------------------------------------------

    /// <summary>The drag target for the draft as it is now (cheap: the booking, with no forecast).</summary>
    private DraftDragTarget? Target()
    {
        if (_setup is not { } setup)
        {
            return null;
        }

        var snapshot = _editor.Snapshot;
        if (_target is null || _targetSequence != snapshot.Sequence)
        {
            var tt = snapshot.Timetable;
            var plan = new PlanSnapshot(snapshot.Sequence, tt, [], SimClockReading.None, PlanSnapshot.Empty.Matches, 0, null,
                WorkingPlan.FromTimetable(tt, setup.OriginalMinRuns), PlanVersion: snapshot.Sequence);
            _target = new DraftDragTarget(plan, snapshot.Import, DraftMinRuns.For(_editor.Editor.Knowledge, setup.OriginalMinRuns, tt));
            _targetSequence = snapshot.Sequence;
        }

        return _target;
    }

    private void UpdateGrips()
    {
        var grips = Board.Scene is { } scene && _plan is { } plan ? GraphGrips.For(scene, plan, Board.Selection, PlanDragRules.Draft) : null;
        if (grips is not null && Board.Grips is { } old && old.Train == grips.Train)
        {
            grips = grips.WithSelected(old.Selected is { } picked ? grips.Find(picked) : null).WithDrag(old.Drag);
        }

        Board.Grips = grips;
        if (grips is not null && grips.Selected is null)
        {
            SyncPointFromGrid();
        }
    }

    private void UpdateGhosts()
    {
        if (_dragGhost is null || Board.Corridor is null)
        {
            Board.Ghosts = null;
            return;
        }

        Board.Ghosts = _dragGhost;
    }

    public int NudgeSeconds => PlanDragRules.Draft.NudgeSeconds;

    public bool BeginEdit(GraphHit hit, ScreenPoint at, bool fromVertex)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (Board.Selection is not { Train: { } train } || hit.Path.Train != train || hit.Path.Layer != GraphLayer.Plan || _drag is not null || Target() is not { } target)
        {
            return false;
        }

        if (target.Begin(hit, Board.Navigator.Viewport, at, fromVertex, out var refusal) is not { } drag)
        {
            EditStatus = refusal ?? EditStatus;
            return false;
        }

        var (time, axis) = Board.Navigator.Viewport.ToWorld(at);
        var kind = drag.Kind switch
        {
            PlanDragKind.Dwell => GraphGripKind.Dwell,
            PlanDragKind.ShiftFrom => GraphGripKind.Point,
            _ => GraphGripKind.Body,
        };
        var plan = target.Snapshot.Plan.Get(drag.Train);
        StartDrag(drag, new GraphGrip(kind, drag.Train, plan.Headcode, drag.Index, drag.Location, Name(drag.Location), new GraphVertex(time, axis)));
        return true;
    }

    public GraphGrip? GripAt(ScreenPoint at) => Board.Grips?.HitTest(Board.Navigator.Viewport, at);

    public GraphHit? SelectedLineAt(ScreenPoint at) => Board.Grips?.LineAt(Board.Navigator.Viewport, at);

    public void HoverGrip(GraphGrip? grip, bool overLine)
    {
        if (Board.Grips is not { } grips || _drag is not null)
        {
            return;
        }

        Board.Grips = grips.WithHot(grip);
        EditStatus = grip is { LockReason: { } reason } ? $"Locked: {reason}"
            : grip is not null ? $"{grip.Action}, in half minutes. Shift: past the least run known; Esc cancels."
            : overLine ? "Drag to retime the rest of the run, in half minutes. Shift: past the least run known; Esc cancels."
            : EditStatus;
    }

    public HoverInfo DescribeGrip(GraphGrip grip) => GraphGrips.Describe(grip, Board.Navigator.Viewport.IsVertical, NudgeSeconds);

    public bool BeginGrip(GraphGrip grip, ScreenPoint at)
    {
        ArgumentNullException.ThrowIfNull(grip);
        if (_drag is not null || Target() is not { } target)
        {
            return false;
        }

        if (target.Begin(grip, Board.Navigator.Viewport, at, out var refusal) is not { } drag)
        {
            EditStatus = refusal ?? "It cannot be moved.";
            return false;
        }

        StartDrag(drag, grip);
        return true;
    }

    public void SelectGrip(GraphGrip grip)
    {
        ArgumentNullException.ThrowIfNull(grip);
        if (Board.Grips is not { } grips)
        {
            return;
        }

        Board.Grips = grips.WithSelected(grip.IsLocked ? null : grip);
        EditStatus = grip.LockReason is { } reason ? $"Locked: {reason}"
            : $"{grip.Action}, or nudge it with {(Board.Navigator.Viewport.IsVertical ? "↑/↓" : "←/→")} ({NudgeSeconds} s; Shift: 1 min).";
        if (grip.Kind != GraphGripKind.Body)
        {
            SelectTrain(grip.Train, grip.Index);
        }
    }

    /// <summary>Nudges the picked grip: one edit of the draft, on the draft as it is now.</summary>
    public bool Nudge(int seconds)
    {
        if (Board.Grips?.Selected is not { IsLocked: false } grip || _drag is not null || Target() is not { } target)
        {
            return false;
        }

        var drag = target.Start(grip.Train, grip.DragKind, grip.Index, out var refusal);
        var preview = drag?.Nudge(seconds);
        if (preview is null || target.EditFor(preview, out var why) is not { } edit)
        {
            EditStatus = refusal ?? preview?.Status ?? "It cannot be moved.";
            return true;
        }

        if (_editor.TryApply(edit))
        {
            EditStatus = $"Done: {edit.Description}" + (preview.Limited ? " — " + preview.Status[(preview.Status.IndexOf('—', StringComparison.Ordinal) + 1)..].Trim() : string.Empty);
        }
        else
        {
            EditStatus = _editor.Error ?? why;
        }

        return true;
    }

    private void StartDrag(PlanDrag drag, GraphGrip grip)
    {
        _drag = drag;
        _dragGrip = grip;
        _ghostUntil = null;
        _dragGhost = null;
        EditStatus = drag.Preview.Status;
        UpdateGhosts();
        Board.Grips = Board.Grips?.WithHot(null).WithDrag(new GraphGripDrag(grip, grip.At, drag.Preview.Label, false));
    }

    public void MoveEdit(ScreenPoint at, bool fine)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var preview = drag.Update(Board.Navigator.Viewport, at, fine);
        EditStatus = preview.Status;
        _dragGhost = preview.Command is null || Board.Corridor is not { } tab || _setup is not { } setup
            ? null
            : GraphSceneBuilder.PlanPaths(tab.Corridor, preview.Train, setup.Profile.StyleFor(preview.Train.CategoryId).Colour, "ghost");
        UpdateGhosts();
        if (_dragGrip is { } grip)
        {
            Board.Grips = Board.Grips?.WithDrag(new GraphGripDrag(grip, grip.At with { Time = grip.At.Time + preview.DeltaSeconds }, preview.Label, preview.Limited));
        }
    }

    /// <summary>The drag ends: the edit it previews is made on the draft (the ghost stays until the board shows it), or dropped.</summary>
    public Task EndEditAsync(bool commit)
    {
        if (_drag is not { } drag)
        {
            return Task.CompletedTask;
        }

        _drag = null;
        var grip = _dragGrip;
        _dragGrip = null;
        Board.Grips = Board.Grips?.WithDrag(null);
        if (!commit || drag.Preview.Command is null || _target is not { } target)
        {
            _dragGhost = null;
            UpdateGhosts();
            EditStatus = commit || drag.Preview.Command is null ? null : "Edit cancelled.";
            return Task.CompletedTask;
        }

        if (target.EditFor(drag.Preview, out var refusal) is not { } edit)
        {
            _dragGhost = null;
            UpdateGhosts();
            EditStatus = refusal;
            return Task.CompletedTask;
        }

        if (_editor.TryApply(edit))
        {
            EditStatus = $"Done: {edit.Description}" + (drag.Preview.Status.Contains(" — ", StringComparison.Ordinal) ? drag.Preview.Status[drag.Preview.Status.IndexOf(" — ", StringComparison.Ordinal)..] : string.Empty);

            // Keep the ghost until a scene drawn from the edited draft replaces it, so the line does not flick back.
            _ghostUntil = _editor.Snapshot.Sequence;
            if (grip is not null && grip.Kind != GraphGripKind.Body)
            {
                SelectTrain(grip.Train, grip.Index);
            }
        }
        else
        {
            _dragGhost = null;
            UpdateGhosts();
            EditStatus = _editor.Error;
        }

        return Task.CompletedTask;
    }

    // Blocks are a session's: a draft board has none.
    public bool BeginBlock(ScreenPoint at) => false;

    public void MoveBlock(ScreenPoint at)
    {
    }

    public Task EndBlockAsync(bool commit) => Task.CompletedTask;

    /// <summary>The right-click menu on a train's line: go to it in the trip grid.</summary>
    public IReadOnlyList<PlanMenuItem> MenuFor(GraphHit? hit, ScreenPoint at)
    {
        if (hit?.Path.Train is not { } id || _drag is not null || _editor.Snapshot.Timetable.Find(id) is not { } train)
        {
            return [];
        }

        var point = hit.Path.PointAt(hit.NearestVertex);
        var where = point is { } k && k < train.Points.Count ? $" at {MenuText.Escape(Name(train.Points[k].Location))}" : string.Empty;
        return
        [
            new PlanMenuItem($"{train.Headcode}{where}"),
            new PlanMenuItem("_Show in the trip grid", new RelayCommand(() => SelectTrain(id, point))),
        ];
    }

    private string Name(Tiploc tiploc) => _setup?.Profile.DisplayName(tiploc) ?? tiploc.ToString();

    // ---- Ghosts ------------------------------------------------------------------------------------

    /// <summary>View ▸ Draft ghosts ▸ Recording…: a recording's (or saved session's) actuals, matched against the draft, as ghosts.</summary>
    [RelayCommand]
    private async Task LoadRecordingAsync()
    {
        if (_dialogs.PickFile("Draw a recording's actuals on the draft", RecordingFilter, PlayFiles.DefaultSessionsFolder + Path.DirectorySeparatorChar) is not { } path)
        {
            return;
        }

        var timetable = _editor.Snapshot.Timetable;
        try
        {
            var trains = await Task.Run(() =>
            {
                var state = new LiveState(timetable);
                foreach (var e in PlayFiles.Read(path))
                {
                    state.Apply(e);
                }

                return state.Trains.ToList();
            });
            _actuals = trains;
            RecordingName = Path.GetFileName(path);
            EditStatus = string.Create(CultureInfo.InvariantCulture, $"Drawing {trains.Count} trains of {RecordingName} as ghosts.");
            Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogRecordingFailed(ex, path);
            _dialogs.ShowError("Draft ghosts", $"{path} could not be read: {ex.Message}");
        }
    }

    /// <summary>Draws the actuals of a recording already read (tests, and a recording opened elsewhere).</summary>
    internal void SetActuals(IReadOnlyList<LiveTrain> trains, string name)
    {
        _actuals = trains;
        RecordingName = name;
        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(HasRecording))]
    private void ClearRecording()
    {
        _actuals = [];
        RecordingName = null;
        Rebuild();
    }

    private const string RecordingFilter = "Recordings and sessions (*.sslog;*.stringline)|*.sslog;*.stringline|All files (*.*)|*.*";

    // ---- Exports -----------------------------------------------------------------------------------

    /// <summary>Draft ▸ Export ▸ PNG: the board as shown.</summary>
    [RelayCommand]
    private void ExportPng()
    {
        if (Board.Scene is not { } scene)
        {
            return;
        }

        var viewport = Board.Navigator.Viewport;
        var selection = Board.Selection;
        SaveExport("Export the draft's graph as PNG", FileFilters.Png, ExportName(scene.CorridorId, ".png"), path =>
        {
            using var renderer = new SkiaGraphRenderer();
            File.WriteAllBytes(path, renderer.RenderPng(scene, viewport, selection));
        });
    }

    /// <summary>Draft ▸ Export ▸ PDF: the board as shown, on A3 landscape.</summary>
    [RelayCommand]
    private void ExportPdf()
    {
        if (Board.Scene is not { } scene)
        {
            return;
        }

        var viewport = Board.Navigator.Viewport;
        var selection = Board.Selection;
        SaveExport("Export the draft's graph as PDF", FileFilters.Pdf, ExportName(scene.CorridorId, ".pdf"), path =>
        {
            using var renderer = new SkiaGraphRenderer();
            File.WriteAllBytes(path, renderer.RenderPdf(scene, viewport, selection, PdfPage.A3Landscape, scene.Title));
        });
    }

    /// <summary>Draft ▸ Export ▸ Plot…: a corridor of the draft over a stretch of time, as File ▸ Export ▸ Plot does.</summary>
    [RelayCommand]
    private async Task ExportPlotAsync()
    {
        if (_setup is not { } setup || _plan is not { } plan || Corridors.Count == 0)
        {
            return;
        }

        var start = _editor.Snapshot.Timetable.Header.StartTime;
        var now = new SimTime((int)Math.Round(Board.Navigator.Viewport.TimeStart)) is var shown && shown > start ? shown : start;
        if (PlotPrompt.Ask(_dialogs, Corridors, Board.Corridor ?? Corridors[0], now + SimTime.SecondsPerHour, _exports?.Plot() ?? new PlotSettings()) is not var (options, corridor, remember))
        {
            return;
        }

        _exports?.SavePlot(remember);
        var request = new DraftBoardRequest(setup.Profile, corridor.Corridor, plan, Title(_editor.Snapshot))
        {
            Originals = ShowOriginal ? DraftBoard.Changed(setup.Original, _editor.Snapshot.Timetable) : [],
            Actuals = _actuals,
            Signals = setup.Signals,
        };
        var scene = await Task.Run(() => DraftBoard.Build(request));
        var title = $"{scene.Title} — plot {PlanInput.Format(options.From)}–{PlanInput.Format(options.To)}";
        SaveExport("Export a plot of the draft as PDF", FileFilters.Pdf, ExportName($"{corridor.Id}-plot", ".pdf"), path =>
        {
            using var renderer = new SkiaGraphRenderer();
            File.WriteAllBytes(path, renderer.RenderPlot(scene, options, title));
        });
    }

    private void SaveExport(string title, string filter, string name, Action<string> write)
    {
        var folder = _exports?.Folder() is { Length: > 0 } f && Directory.Exists(f) ? f : Path.GetDirectoryName(_editor.DraftPath)!;
        if (_dialogs.PickSaveFile(title, filter, Path.Combine(folder, name)) is not { } path)
        {
            return;
        }

        try
        {
            write(path);
            _exports?.Exported(path);
            EditStatus = $"Exported to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Export", $"Cannot export to {path}: {ex.Message}");
        }
    }

    private string ExportName(string what, string extension)
    {
        var area = _editor.Snapshot.Document.Header.AreaId is { Length: > 0 } a ? a : "draft";
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. $"{area}-draft-{what}".Select(c => invalid.Contains(c) || c == ' ' ? '_' : c)]) + extension;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not draw the draft board")]
    private partial void LogBoardFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the recording {Path} for the draft's ghosts")]
    private partial void LogRecordingFailed(Exception ex, string path);
}
