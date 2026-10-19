using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Controls;
using Stringline.Core;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// An item of a menu built in code (the graph's right-click menu, the Corridor menu): a heading (no command), an
/// action, or a separator (no header). <paramref name="Gesture"/> is the shortcut shown beside it.
/// </summary>
internal sealed record PlanMenuItem(string? Header, IRelayCommand? Command = null, string? Gesture = null, bool IsChecked = false)
{
    public static PlanMenuItem Separator { get; } = new(Header: null);

    public bool IsSeparator => Header is null;
}

/// <summary>Access keys in menu headers (S25): <c>_</c> marks the key, and <c>__</c> is a plain underscore.</summary>
internal static class MenuText
{
    /// <summary>Text shown as it is in a menu header: its underscores are not taken as access keys.</summary>
    public static string Escape(string text) => text.Replace("_", "__", StringComparison.Ordinal);

    /// <summary>The header's access key, upper case; null when it has none.</summary>
    public static char? AccessKey(string header)
    {
        for (var i = 0; i < header.Length - 1; i++)
        {
            if (header[i] != '_')
            {
                continue;
            }

            if (header[i + 1] != '_')
            {
                return char.ToUpperInvariant(header[i + 1]);
            }

            i++; // an escaped underscore
        }

        return null;
    }
}

/// <summary>
/// Re-planning from the shell (S12, RP-1 to RP-7): plan commands sent to the engine with their outcome shown, undo and
/// redo, the detail panel and edit history, drag edits on the graph with a ghost preview, and the right-click menu.
/// </summary>
internal sealed partial class MainViewModel : IGraphEditor
{
    private PlanDrag? _drag;
    private GraphGrip? _dragGrip;
    private long? _ghostUntil;
    private int _pendingNudge;
    private bool _nudging;

    /// <summary>The selected train's schedule.</summary>
    public TrainDetailViewModel Detail { get; private set; } = null!;

    public EditHistoryViewModel History { get; } = new();

    /// <summary>The preview of a drag edit: where the train would run. Null when not dragging.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GraphPath>? Ghosts { get; private set; }

    /// <summary>For the status bar: what a drag edit does or why it cannot, and the last edit made.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditStatusText))]
    public partial string EditStatus { get; private set; } = string.Empty;

    /// <summary>The grips on the selected train (S26), with the one under the mouse, the one picked and the one dragged.</summary>
    [ObservableProperty]
    public partial GraphGrips? Grips { get; private set; }

    /// <summary>What the grip or line under the mouse would do (S26); shown in the status bar over <see cref="EditStatus"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditStatusText))]
    public partial string? GripHint { get; private set; }

    /// <summary>The status bar's edit text: the hint for what is under the mouse, else the last edit's outcome.</summary>
    public string EditStatusText => GripHint ?? EditStatus;

    public bool IsDragging => _drag is not null;

    public string UndoHeader => Snapshot.History.UndoDescription is { } d ? $"_Undo {MenuText.Escape(d)}" : "_Undo";

    public string RedoHeader => Snapshot.History.RedoDescription is { } d ? $"_Redo {MenuText.Escape(d)}" : "_Redo";

    public string UndoTip => Snapshot.History.UndoDescription is { } d ? $"Undo: {d} (Ctrl+Z)" : "Nothing to undo";

    public string RedoTip => Snapshot.History.RedoDescription is { } d ? $"Redo: {d} (Ctrl+Y)" : "Nothing to redo";

    private bool CanUndo => Snapshot.History.CanUndo && _drag is null && !IsReviewing;

    private bool CanRedo => Snapshot.History.CanRedo && _drag is null && !IsReviewing;

    private bool HasSelectedPlan => Detail.HasTrain;

    private bool CanResetSelected => Detail.CanReset;

    private bool CanLinkSelected => Selection?.LiveUid is { } uid && Snapshot.FindByUid(uid) is not null && Snapshot.Timetable is not null;

    /// <summary>Send a plan command to the engine, and show what happened: an error or clamping warnings in the info bar.</summary>
    public async Task<PlanCommandResult> ExecuteAsync(IPlanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var description = command.Describe(Snapshot.Plan);
        if (ReviewRefusal is { } refusal)
        {
            ShowInfo(InfoSeverity.Warning, refusal);
            return PlanCommandResult.Fail(refusal);
        }

        PlanCommandResult result;
        try
        {
            result = await _engine.Execute(command);
        }
#pragma warning disable CA1031 // A faulty command is reported, never thrown onto the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogEditFailed(ex, description);
            result = PlanCommandResult.Fail(ex.Message);
        }

        if (!result.Applied)
        {
            ShowInfo(InfoSeverity.Error, $"{description}: not done. {result.Error}");
            EditStatus = string.Empty;
        }
        else if (result.Warnings.Count > 0)
        {
            ShowInfo(InfoSeverity.Warning, $"{description}. " + string.Join(" ", result.Warnings.Select(w => w.Message)));
            EditStatus = $"Done: {description}";
        }
        else
        {
            EditStatus = $"Done: {description}";
        }

        return result;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var description = Snapshot.History.UndoDescription;
        if (await _engine.Undo())
        {
            EditStatus = $"Undone: {description}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        var description = Snapshot.History.RedoDescription;
        if (await _engine.Redo())
        {
            EditStatus = $"Redone: {description}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanResetSelected))]
    private Task ResetSelectedAsync() => Detail.Schedule is { } s ? ExecuteAsync(new ResetTrain(s.Plan.Id)) : Task.CompletedTask;

    /// <summary>Cancel the selected train, or reinstate it if it is cancelled or terminated short.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedPlan))]
    private Task CancelSelectedAsync() => Detail.Schedule is { } s
        ? ExecuteAsync(s.Plan.Cancelled ? new ReinstateTrain(s.Plan.Id) : new CancelTrain(s.Plan.Id))
        : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelectedPlan))]
    private Task CloneSelectedAsync() => Detail.Schedule is { } s ? CloneAsync(s.Plan.Id) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanLinkSelected))]
    private Task LinkSelectedAsync() => Selection?.LiveUid is { } uid && Snapshot.FindByUid(uid) is { } live
        ? LinkAsync(uid, live.CurrentIndex)
        : Task.CompletedTask;

    // ---- Drag edits (IGraphEditor) ---------------------------------------------------------

    /// <summary>
    /// A press on the graph: start a drag edit if it is on the selected train's WTT or plan line (Fred, S12: the
    /// selected train only). Returns false to let the graph pan; a reason the train cannot be edited goes in
    /// <see cref="EditStatus"/>.
    /// </summary>
    public bool BeginEdit(GraphHit hit, ScreenPoint at, bool fromVertex)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (Selection is not { Train: { } train } || hit.Path.Train != train || _drag is not null)
        {
            return false;
        }

        if (ReviewRefusal is { } reviewing)
        {
            EditStatus = reviewing;
            return false;
        }

        var drag = PlanDrag.Begin(hit, Snapshot, Navigator.Viewport, at, NowTime, fromVertex, out var refusal);
        if (drag is null)
        {
            if (refusal is not null)
            {
                EditStatus = refusal;
            }

            return false;
        }

        // The time label goes where the line was pressed, as if a grip were there.
        var (time, axis) = Navigator.Viewport.ToWorld(at);
        var kind = drag.Kind switch
        {
            PlanDragKind.Dwell => GraphGripKind.Dwell,
            PlanDragKind.ShiftFrom => GraphGripKind.Point,
            _ => GraphGripKind.Body,
        };
        var plan = Snapshot.Plan.Get(drag.Train);
        StartDrag(drag, new GraphGrip(kind, drag.Train, plan.Headcode, drag.Index, drag.Location, Name(drag.Location), new GraphVertex(time, axis)));
        return true;
    }

    // ---- Grips (S26, #10) --------------------------------------------------------------------

    public GraphGrip? GripAt(ScreenPoint at) => Grips?.HitTest(Navigator.Viewport, at);

    public GraphHit? SelectedLineAt(ScreenPoint at) => Grips?.LineAt(Navigator.Viewport, at);

    public void HoverGrip(GraphGrip? grip, bool overLine)
    {
        if (Grips is not { } grips || _drag is not null)
        {
            GripHint = null;
            return;
        }

        Grips = grips.WithHot(grip);
        var headcode = Snapshot.Plan.Find(grips.Train)?.Headcode.ToString() ?? grips.Train.ToString();
        GripHint = grip is { LockReason: { } reason } ? $"Locked: {reason}"
            : grip is not null ? $"{grip.Action}. Shift: 1 s steps; Esc cancels."
            : overLine ? $"Drag to retime the rest of {headcode}'s run. Shift: 1 s steps; Esc cancels."
            : null;
    }

    public HoverInfo DescribeGrip(GraphGrip grip) => GraphGrips.Describe(grip, Navigator.Viewport.IsVertical);

    public bool BeginGrip(GraphGrip grip, ScreenPoint at)
    {
        ArgumentNullException.ThrowIfNull(grip);
        if (_drag is not null)
        {
            return false;
        }

        if (ReviewRefusal is { } reviewing)
        {
            EditStatus = reviewing;
            return false;
        }

        if (PlanDrag.Begin(grip, Snapshot, Navigator.Viewport, at, NowTime, out var refusal) is not { } drag)
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
        if (Grips is not { } grips)
        {
            return;
        }

        if (grip.LockReason is { } reason)
        {
            Grips = grips.WithSelected(null);
            EditStatus = $"Locked: {reason}";
            return;
        }

        Grips = grips.WithSelected(grip);
        var keys = Navigator.Viewport.IsVertical ? "↑/↓" : "←/→";
        EditStatus = $"{grip.Action}, or nudge it with {keys} (15 s; Shift: 1 min).";
    }

    /// <summary>
    /// Nudges the picked grip (S26). Each nudge is one edit, made on the engine's latest plan so nudges in quick
    /// succession add up; those pressed while one is being made go together as the next.
    /// </summary>
    public bool Nudge(int seconds)
    {
        if (Grips?.Selected is not { IsLocked: false } || _drag is not null)
        {
            return false;
        }

        if (ReviewRefusal is { } reviewing)
        {
            EditStatus = reviewing;
            return true;
        }

        _pendingNudge += seconds;
        if (!_nudging)
        {
            _ = NudgeAsync();
        }

        return true;
    }

    /// <summary>Makes the nudges waiting, one edit at a time; done when none is left.</summary>
    internal async Task NudgeAsync()
    {
        _nudging = true;
        try
        {
            while (_pendingNudge != 0 && Grips?.Selected is { } grip)
            {
                var seconds = _pendingNudge;
                _pendingNudge = 0;
                var drag = PlanDrag.Start(_engine.Current, grip.Train, grip.DragKind, grip.Index, NowTime, out var refusal);
                var preview = drag?.Nudge(seconds);
                if (preview?.Command is not { } command)
                {
                    EditStatus = refusal ?? preview?.Status ?? "It cannot be moved.";
                    _pendingNudge = 0;
                    break;
                }

                if ((await ExecuteAsync(command)).Applied)
                {
                    EditStatus = $"Done: {preview.Status}";
                }
            }
        }
        finally
        {
            _nudging = false;
        }
    }

    private SimTime? NowTime => NowSeconds is { } n ? new SimTime((int)Math.Floor(n)) : null;

    private void StartDrag(PlanDrag drag, GraphGrip grip)
    {
        _drag = drag;
        _dragGrip = grip;
        _pendingNudge = 0;
        ClearSuggestionPreview();
        _ghostUntil = null;
        GripHint = null;
        EditStatus = drag.Preview.Status;
        Ghosts = null;
        Grips = Grips?.WithHot(null).WithDrag(new GraphGripDrag(grip, grip.At, drag.Preview.Label, false));
        UpdateEditCommands();
    }

    /// <summary>The grips for the selected train in the scene now, keeping the grip picked, and the one dragged.</summary>
    private void UpdateGrips()
    {
        var grips = Scene is { } scene ? GraphGrips.For(scene, Snapshot, Selection) : null;
        if (grips is not null && Grips is { } old && old.Train == grips.Train)
        {
            grips = grips.WithSelected(old.Selected is { } picked ? grips.Find(picked) : null).WithDrag(old.Drag);
        }

        Grips = grips;
    }

    public void MoveEdit(ScreenPoint at, bool fine)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var preview = drag.Update(Navigator.Viewport, at, fine);
        EditStatus = preview.Status;
        Ghosts = preview.Command is null || SelectedCorridor is not { } tab ? null
            : GraphSceneBuilder.PlanPaths(tab.Corridor, preview.Train, Profile?.StyleFor(preview.Train.CategoryId).Colour ?? Network.Profiles.CategoryStyle.Fallback.Colour, "ghost");
        if (_dragGrip is { } grip)
        {
            Grips = Grips?.WithDrag(new GraphGripDrag(grip, grip.At with { Time = grip.At.Time + preview.DeltaSeconds }, preview.Label, preview.Limited));
        }
    }

    /// <summary>The drag ends: make the edit previewed (keeping the ghost until the graph shows it), or drop it.</summary>
    public async Task EndEditAsync(bool commit)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        var grip = _dragGrip;
        _dragGrip = null;
        Grips = Grips?.WithDrag(null);
        UpdateEditCommands();
        if (commit && drag.Preview.Command is not null && grip is not null && Grips?.Find(grip) is { IsLocked: false } kept && grip.Kind != GraphGripKind.Body)
        {
            // The grip dragged stays picked, so the arrow keys can fine-tune it.
            Grips = Grips.WithSelected(kept);
        }

        if (!commit || drag.Preview.Command is not { } command)
        {
            Ghosts = null;
            EditStatus = commit || drag.Preview.Command is null ? string.Empty : "Edit cancelled.";
            return;
        }

        var version = Snapshot.PlanVersion;
        var result = await ExecuteAsync(command);
        if (result.Applied && Ghosts is not null)
        {
            // Keep the ghost until a scene drawn from the edited plan replaces it, so the line does not flick back.
            _ghostUntil = version + 1;
        }
        else
        {
            Ghosts = null;
        }
    }

    // ---- The right-click menu ----------------------------------------------------------------

    /// <summary>
    /// The right-click menu for a train's line (<c>05-…md</c> §3), at the timing point nearest the click. Selects the
    /// train. Empty for none; the background's menu is <see cref="MenuFor(GraphHit?, ScreenPoint)"/>'s.
    /// </summary>
    public IReadOnlyList<PlanMenuItem> MenuFor(GraphHit? hit)
    {
        if (hit is null || _drag is not null)
        {
            return [];
        }

        SelectPath(hit);
        var path = hit.Path;
        var items = new List<PlanMenuItem>();
        var live = path.LiveUid is { } uid ? Snapshot.FindByUid(uid) : null;
        var working = path.Working ?? live?.CurrentIndex;
        if (path.Train is not { } id || Snapshot.Plan.Find(id) is not { } plan)
        {
            if (live is not null && working is { } w)
            {
                items.Add(new PlanMenuItem($"{live.Workings[w].Headcode} (not matched)"));
                items.Add(new PlanMenuItem("_Link to WTT train…", new AsyncRelayCommand(() => LinkAsync(live.SimUid, w), () => Snapshot.Timetable is not null)));
            }

            return items;
        }

        var index = PointIndexOf(hit, plan);
        var point = index is { } i ? plan.Points[i] : null;
        var runs = index is { } k && k <= plan.LastIndex && !plan.Cancelled;
        items.Add(new PlanMenuItem(point is null ? plan.Headcode.ToString() : $"{plan.Headcode} at {Profile?.DisplayName(point.Location) ?? point.Location.ToString()}"));
        if (runs && point is { IsTimed: true } && index < plan.LastIndex)
        {
            var at = index!.Value;
            items.Add(new PlanMenuItem("_Hold here…", new AsyncRelayCommand(() => HoldAsync(id, at))));
            if (point.IsPass)
            {
                items.Add(new PlanMenuItem("_Add stop…", new AsyncRelayCommand(() => AddStopAsync(id, at))));
            }
            else if (point.IsCall && at > 0)
            {
                items.Add(new PlanMenuItem("_Skip stop", new AsyncRelayCommand(() => ExecuteAsync(new SkipStop(id, at)))));
            }
        }

        if (runs && point is not null)
        {
            var at = index!.Value;
            items.Add(new PlanMenuItem("Change _platform…", new AsyncRelayCommand(() => ChangePlatformAsync(id, at))));
            items.Add(new PlanMenuItem("Change li_ne…", new AsyncRelayCommand(() => ChangeLineAsync(id, at))));
            if (at > 0 && at < plan.LastIndex && point.IsTimed)
            {
                items.Add(new PlanMenuItem("_Terminate here", new AsyncRelayCommand(() => ExecuteAsync(new TerminateAt(id, at)))));
            }
        }

        items.Add(PlanMenuItem.Separator);
        if (plan.Cancelled || plan.TerminatesAt is not null)
        {
            items.Add(new PlanMenuItem(plan.Cancelled ? "_Reinstate" : "_Reinstate to run in full", new AsyncRelayCommand(() => ExecuteAsync(new ReinstateTrain(id)))));
        }

        if (!plan.Cancelled)
        {
            items.Add(new PlanMenuItem("_Cancel train", new AsyncRelayCommand(() => ExecuteAsync(new CancelTrain(id)))));
        }

        if (plan.IsModified && plan.Origin == PlanOrigin.Wtt)
        {
            items.Add(new PlanMenuItem("Reset to _WTT", new AsyncRelayCommand(() => ExecuteAsync(new ResetTrain(id)))));
        }

        items.Add(new PlanMenuItem("Cl_one as ad-hoc…", new AsyncRelayCommand(() => CloneAsync(id))));
        if (live is not null && working is { } lw)
        {
            items.Add(PlanMenuItem.Separator);
            items.Add(new PlanMenuItem("_Link to WTT train…", new AsyncRelayCommand(() => LinkAsync(live.SimUid, lw))));
            var headcode = live.Workings[lw].Headcode;
            if (Snapshot.Plan.FindLink(live.SimUid, headcode) is not null)
            {
                items.Add(new PlanMenuItem("_Match automatically", new AsyncRelayCommand(() => ExecuteAsync(new UnlinkLiveTrain(live.SimUid, headcode)))));
            }
        }

        if (point is not null)
        {
            var location = point.Location;
            items.Add(PlanMenuItem.Separator);
            items.Add(new PlanMenuItem($"Re_gulation list at {MenuText.Escape(Name(location))}", new RelayCommand(() => ShowRegulation(location))));
        }

        return items;
    }

    /// <summary>The plan point a hit is at: the vertex's own point on a WTT, plan or forecast line, else the nearest call at that location.</summary>
    private int? PointIndexOf(GraphHit hit, TrainPlan plan)
    {
        if (hit.Path.PointAt(hit.NearestVertex) is { } index)
        {
            return index < plan.Points.Count ? index : null;
        }

        var axis = hit.Vertex.Axis;
        var location = SelectedCorridor?.Corridor.Locations.FirstOrDefault(l => Math.Abs(l.AxisPosition - axis) < 1e-9)?.Tiploc;
        return plan.Points
            .Where(p => p.Location == location && p.FirstTime is not null)
            .OrderBy(p => Math.Abs(p.FirstTime!.Value.Seconds - hit.Vertex.Time))
            .Select(p => (int?)p.Index)
            .FirstOrDefault();
    }

    // ---- Edits that ask for a value ----------------------------------------------------------

    private async Task HoldAsync(TrainId id, int index)
    {
        if (Snapshot.Plan.Find(id) is not { } plan)
        {
            return;
        }

        var point = plan.Points[index];
        var planned = point.Depart ?? point.Arrive ?? SimTime.Zero;
        var from = NowSeconds is { } n && n > planned.Seconds ? new SimTime((int)Math.Floor(n)) : planned;
        var suggested = new SimTime(((from.Seconds + (2 * SimTime.SecondsPerMinute) + 29) / 30) * 30);
        var prompt = new PromptViewModel(
            "Hold here",
            $"Hold {plan.Headcode} at {Name(point.Location)} until:",
            new PromptField("Depart at", suggested.ToString(), hint: $"Planned {(point.IsPass ? "to pass" : "to leave")} at {planned}. Later points move on only as far as they must."))
        {
            Validate = p => !PlanInput.TryParseTime(p[0], planned, out var t) ? "Enter a time such as 10:25 or 10:25:30."
                : t <= (point.IsPass ? planned : point.Arrive ?? planned) ? $"The departure must be after {(point.IsPass ? planned : point.Arrive ?? planned)}."
                : null,
        };
        if (!_dialogs.Prompt(prompt) || !PlanInput.TryParseTime(prompt[0], planned, out var depart))
        {
            return;
        }

        await ExecuteAsync(point.IsPass ? new AddStop(id, index, depart - planned) : new SetDwell(id, index, depart));
    }

    private async Task AddStopAsync(TrainId id, int index)
    {
        if (Snapshot.Plan.Find(id) is not { } plan)
        {
            return;
        }

        var point = plan.Points[index];
        var prompt = new PromptViewModel(
            "Add stop",
            $"Stop {plan.Headcode} at {Name(point.Location)} (it passes at {point.Depart}) for:",
            new PromptField("Dwell (minutes)", "1", hint: "For example 1, 0.5 or 2.5."))
        {
            Validate = p => PlanInput.TryParseMinutes(p[0], out var s) && s > 0 && s <= AddStop.MaxDwellSeconds ? null
                : $"Enter a dwell between 1 s and {PlanText.Duration(AddStop.MaxDwellSeconds)}, in minutes.",
        };
        if (_dialogs.Prompt(prompt) && PlanInput.TryParseMinutes(prompt[0], out var seconds))
        {
            await ExecuteAsync(new AddStop(id, index, seconds));
        }
    }

    private async Task ChangePlatformAsync(TrainId id, int index)
    {
        if (Snapshot.Plan.Find(id) is { } plan &&
            AskText("Change platform", $"Platform for {plan.Headcode} at {Name(plan.Points[index].Location)}:", "Platform", plan.Points[index].Platform) is { } platform)
        {
            await ExecuteAsync(new ChangePlatform(id, index, platform));
        }
    }

    private async Task ChangeLineAsync(TrainId id, int index)
    {
        if (Snapshot.Plan.Find(id) is { } plan &&
            AskText("Change line", $"Line for {plan.Headcode} out of {Name(plan.Points[index].Location)}:", "Line", plan.Points[index].Line) is { } line)
        {
            await ExecuteAsync(new ChangeLine(id, index, line));
        }
    }

    private string? AskText(string title, string heading, string label, string? current)
    {
        var prompt = new PromptViewModel(title, heading, new PromptField(label, current ?? string.Empty, hint: "Leave empty for none."));
        return _dialogs.Prompt(prompt) ? prompt[0] : null;
    }

    private async Task CloneAsync(TrainId id)
    {
        if (Snapshot.Plan.Find(id) is not { } plan)
        {
            return;
        }

        var prompt = new PromptViewModel(
            "Clone as ad-hoc train",
            $"Copy {plan.Headcode}'s plan as a new train:",
            new PromptField("New headcode", plan.Headcode.ToString()),
            new PromptField("Later by (minutes)", "0", hint: "Negative for earlier. The copy can be dragged afterwards."))
        {
            Validate = p => p[0].Length == 0 || p[0].Any(char.IsWhiteSpace) ? "Enter a headcode, such as 1Z99."
                : !PlanInput.TryParseMinutes(p[1], out _) ? "Enter how many minutes later (0 for the same times)."
                : null,
        };
        if (!_dialogs.Prompt(prompt) || !PlanInput.TryParseMinutes(prompt[1], out var delta))
        {
            return;
        }

        var headcode = new Headcode(prompt[0].ToUpperInvariant());
        var newId = Snapshot.Plan.NewCloneId(headcode);
        if ((await ExecuteAsync(new CloneTrain(id, headcode, delta, newId))).Applied)
        {
            // Select the copy so it can be dragged into place straight away.
            SetSelection(newId, null);
        }
    }

    /// <summary>Link a live working to a WTT train chosen from a list, or mark it "do not match".</summary>
    private async Task LinkAsync(string uid, int working)
    {
        if (Snapshot.FindByUid(uid) is not { } live || Snapshot.Timetable is not { } timetable || working >= live.Workings.Count)
        {
            return;
        }

        const string doNotMatch = "(do not match)";
        var headcode = live.Workings[working].Headcode;
        var choices = timetable.Trains
            .OrderBy(t => t.Headcode == headcode ? 0 : 1)
            .ThenBy(t => t.FirstTime?.Seconds ?? int.MaxValue)
            .Select(t => (Text: $"{t.Headcode}  {t.FirstTime?.ToString() ?? "--:--:--"}  {Name(t.Origin)} – {Name(t.Destination)}  [{t.Id}]", t.Id))
            .ToList();
        var current = live.Workings[working].Train is { } matched ? choices.FirstOrDefault(c => c.Id == matched).Text : null;
        var prompt = new PromptViewModel(
            "Link to WTT train",
            $"Which WTT train is live {headcode} (uid {uid})?",
            new PromptField("WTT train", current ?? choices.FirstOrDefault().Text ?? string.Empty, [doNotMatch, .. choices.Select(c => c.Text)], "Type a headcode to find it."))
        {
            Validate = p => p[0] == doNotMatch || choices.Any(c => c.Text == p[0]) ? null : "Choose a train from the list.",
        };
        if (!_dialogs.Prompt(prompt))
        {
            return;
        }

        var train = prompt[0] == doNotMatch ? (TrainId?)null : choices.First(c => c.Text == prompt[0]).Id;
        await ExecuteAsync(new LinkLiveTrain(uid, headcode, train));
    }

    // ---- Editing the detail panel's cells ----------------------------------------------------

    /// <summary>A plan cell typed into: a time retimes from there (an arrival) or sets the dwell (a departure); platform and line change those.</summary>
    private void EditCell(TrainId id, ScheduleRowViewModel row, ScheduleField field, string text)
    {
        if (Snapshot.Plan.Find(id) is not { } plan || row.Index >= plan.Points.Count)
        {
            return;
        }

        var point = plan.Points[row.Index];
        IPlanCommand? command = null;
        string? problem = null;
        switch (field)
        {
            case ScheduleField.Platform:
                command = new ChangePlatform(id, row.Index, text);
                break;
            case ScheduleField.Line:
                command = new ChangeLine(id, row.Index, text);
                break;
            case ScheduleField.Arrive when point.Arrive is { } arrive:
                command = PlanInput.TryParseTime(text, arrive, out var t) ? new ShiftTrain(id, row.Index, t - arrive) : null;
                problem = command is null ? $"'{text}' is not a time; enter one such as 10:25 or 10:25:30." : null;
                break;
            case ScheduleField.Depart when point.Depart is { } depart:
                if (!PlanInput.TryParseTime(text, depart, out var d))
                {
                    problem = $"'{text}' is not a time; enter one such as 10:25 or 10:25:30.";
                }
                else
                {
                    command = point.IsCall && row.Index > 0 ? new SetDwell(id, row.Index, d) : new ShiftTrain(id, row.Index, d - depart);
                }

                break;
            default:
                problem = field == ScheduleField.Arrive
                    ? $"{plan.Headcode} has no arrival at {Name(point.Location)} to change; change the departure or pass time."
                    : $"{plan.Headcode} does not leave {Name(point.Location)}; change the arrival.";
                break;
        }

        if (problem is not null)
        {
            ShowInfo(InfoSeverity.Error, problem);
        }
        else if (command is not null)
        {
            _ = ExecuteAsync(command);
        }
    }

    // ---- Keeping the panels up to date --------------------------------------------------------

    private void InitEditing() => Detail = new TrainDetailViewModel(EditCell);

    /// <summary>A new snapshot or selection: refresh the detail panel, the history and the edit commands.</summary>
    private void UpdateEditing()
    {
        Detail.Update(Snapshot, Selection, Profile);
        History.Update(Snapshot.History);
        UpdateGrips();
        UpdateEditCommands();
    }

    private void UpdateEditCommands()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ResetSelectedCommand.NotifyCanExecuteChanged();
        CancelSelectedCommand.NotifyCanExecuteChanged();
        CloneSelectedCommand.NotifyCanExecuteChanged();
        LinkSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoHeader));
        OnPropertyChanged(nameof(RedoHeader));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        OnPropertyChanged(nameof(CancelSelectedText));
    }

    /// <summary>The detail panel's cancel button: "Cancel train" or "Reinstate".</summary>
    public string CancelSelectedText => Detail.Schedule?.Plan is { } p && (p.Cancelled || p.TerminatesAt is not null) ? "Reinstate" : "Cancel train";

    /// <summary>A scene has been built from a snapshot with this plan version: drop a ghost kept for an edit it now shows.</summary>
    partial void OnSceneChanged(GraphScene? value) => UpdateGrips();

    private void SceneBuiltFor(long planVersion)
    {
        if (_ghostUntil is { } until && planVersion >= until && _drag is null)
        {
            _ghostUntil = null;
            Ghosts = null;
        }
    }

    private string Name(Tiploc tiploc) => Profile?.DisplayName(tiploc) ?? tiploc.ToString();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plan edit '{Description}' failed")]
    private partial void LogEditFailed(Exception ex, string description);
}
