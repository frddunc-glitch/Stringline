using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Controls;
using Stringline.Core;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>An item of the graph's right-click menu: a heading (no command), an action, or a separator (no header).</summary>
internal sealed record PlanMenuItem(string? Header, IRelayCommand? Command = null)
{
    public static PlanMenuItem Separator { get; } = new(Header: null);

    public bool IsSeparator => Header is null;
}

/// <summary>
/// Re-planning from the shell (S12, RP-1 to RP-7): plan commands sent to the engine with their outcome shown, undo and
/// redo, the detail panel and edit history, drag edits on the graph with a ghost preview, and the right-click menu.
/// </summary>
internal sealed partial class MainViewModel : IGraphEditor
{
    private PlanDrag? _drag;
    private long? _ghostUntil;

    /// <summary>The selected train's schedule.</summary>
    public TrainDetailViewModel Detail { get; private set; } = null!;

    public EditHistoryViewModel History { get; } = new();

    /// <summary>The preview of a drag edit: where the train would run. Null when not dragging.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GraphPath>? Ghosts { get; private set; }

    /// <summary>For the status bar: what a drag edit does or why it cannot, and the last edit made.</summary>
    [ObservableProperty]
    public partial string EditStatus { get; private set; } = string.Empty;

    public bool IsDragging => _drag is not null;

    public string UndoHeader => Snapshot.History.UndoDescription is { } d ? $"_Undo {d}" : "_Undo";

    public string RedoHeader => Snapshot.History.RedoDescription is { } d ? $"_Redo {d}" : "_Redo";

    public string UndoTip => Snapshot.History.UndoDescription is { } d ? $"Undo: {d} (Ctrl+Z)" : "Nothing to undo";

    public string RedoTip => Snapshot.History.RedoDescription is { } d ? $"Redo: {d} (Ctrl+Y)" : "Nothing to redo";

    private bool CanUndo => Snapshot.History.CanUndo && _drag is null;

    private bool CanRedo => Snapshot.History.CanRedo && _drag is null;

    private bool HasSelectedPlan => Detail.HasTrain;

    private bool CanResetSelected => Detail.CanReset;

    private bool CanLinkSelected => Selection?.LiveUid is { } uid && Snapshot.FindByUid(uid) is not null && Snapshot.Timetable is not null;

    /// <summary>Send a plan command to the engine, and show what happened: an error or clamping warnings in the info bar.</summary>
    public async Task<PlanCommandResult> ExecuteAsync(IPlanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var description = command.Describe(Snapshot.Plan);
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

        var now = NowSeconds is { } n ? new SimTime((int)Math.Floor(n)) : (SimTime?)null;
        var drag = PlanDrag.Begin(hit, Snapshot, Navigator.Viewport, at, now, fromVertex, out var refusal);
        if (drag is null)
        {
            if (refusal is not null)
            {
                EditStatus = refusal;
            }

            return false;
        }

        _drag = drag;
        ClearSuggestionPreview();
        _ghostUntil = null;
        EditStatus = drag.Preview.Status;
        Ghosts = null;
        UpdateEditCommands();
        return true;
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
    }

    /// <summary>The drag ends: make the edit previewed (keeping the ghost until the graph shows it), or drop it.</summary>
    public async Task EndEditAsync(bool commit)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        UpdateEditCommands();
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
            items.Add(new PlanMenuItem($"Regulation _list at {Name(location)}", new RelayCommand(() => ShowRegulation(location))));
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
