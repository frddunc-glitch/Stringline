using System.Globalization;
using System.IO;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Editing.Drafts;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>What the editor needs from the app to save a draft: names and folders.</summary>
/// <param name="SimName">The sim's name, for new files' names (<see cref="TimetableNames.Suggest"/>).</param>
/// <param name="TimetableFolder">SimSig's timetables folder, Save As's default for a draft with no source.</param>
/// <param name="BackupFolder">Where replaced files are copied first; null for <see cref="WttWriter.DefaultBackupFolder"/>.</param>
internal sealed record DraftContext(string SimName, string TimetableFolder, string? BackupFolder = null)
{
    public Func<DateOnly> Today { get; init; } = () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Where the draft board's exports go, and the Plot dialog's choices (S44): the main window's.</summary>
    public DraftExports? Exports { get; init; }
}

/// <summary>Which issues the Issues panel lists.</summary>
internal enum IssueLevel
{
    All,
    Warnings,
    Errors,
}

/// <summary>
/// The timetable editor workspace (S43, <c>08-…md</c> §7) over one draft: the trains list, the selected train's details
/// and trip grid, the Header tab and the Issues panel. Edits go through <see cref="TimetableEditor"/>, the single
/// writer; every snapshot it publishes refreshes the panels, and the draft is autosaved shortly after (with its undo
/// history) to the drafts folder. Saving follows <c>08-…md</c> §6: the first save of a draft of a SimSig timetable
/// asks whether to replace it (a new file named by the convention is the default), Errors are listed before saving,
/// and closing with unsaved changes asks first. The draft board (<see cref="Board"/>, S44) draws the draft and shares
/// the selection with the trains list and trip grid.
/// </summary>
internal sealed partial class TimetableEditorViewModel : ObservableObject
{
    /// <summary>How long after the last edit the draft is autosaved.</summary>
    public static readonly TimeSpan AutosaveDelay = TimeSpan.FromSeconds(2);

    private readonly DraftContext _context;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<XElement, DraftTrainRow> _rows = [];
    private IReadOnlyList<CodeChoice> _locations = [];
    private int _autosaveGeneration;
    private bool _autosavePending;
    private bool _refreshingIssues;
    private DateTimeOffset _lastAutosave;

    public TimetableEditorViewModel(TimetableEditor editor, string draftPath, DraftContext context, IDialogService dialogs, IUiDispatcher ui, TimeProvider time, ILogger logger)
    {
        Editor = editor;
        DraftPath = draftPath;
        _context = context;
        _dialogs = dialogs;
        _ui = ui;
        _time = time;
        _logger = logger;
        _lastAutosave = time.GetUtcNow();
        Header = new DraftHeaderViewModel(this);
        Rules = new DraftRulesViewModel(this);
        SeedGroups = new DraftSeedGroupsViewModel(this, dialogs);
        Categories = new DraftCategoriesViewModel(this, dialogs);
        Lists = new DraftListsViewModel(this);
        Snapshot = editor.Current;
        Board = new DraftBoardViewModel(this, dialogs, ui, logger, context.Exports);
        _locations = LocationChoices();
        editor.SnapshotPublished += (_, snapshot) => OnSnapshot(snapshot);
        OnSnapshot(editor.Current);
    }

    /// <summary>The user closed the draft (the window closes).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The view should bring the train and point into view (an issue was opened).</summary>
    public event EventHandler? GoToRequested;

    /// <summary>The view should show the Issues panel (Timetable ▸ Check timetable).</summary>
    public event EventHandler? IssuesRequested;

    public TimetableEditor Editor { get; }

    public WttDocument Document => Editor.Document;

    /// <summary>The draft's autosave in the drafts folder.</summary>
    public string DraftPath { get; }

    public bool IsClosed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(UndoHeader), nameof(RedoHeader), nameof(IssueSummary), nameof(FileSummary))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(RedoCommand))]
    public partial DraftSnapshot Snapshot { get; private set; }

    /// <summary>"Draft – <i>name</i> <i>version</i>", with * while there are unsaved changes.</summary>
    public string Title =>
        $"Draft – {(Snapshot.Document.Header.Name is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(DraftPath))} {Snapshot.Version?.ToString() ?? string.Empty}{(Snapshot.IsDirty ? " *" : string.Empty)}".Replace("  ", " ", StringComparison.Ordinal);

    public string UndoHeader => Snapshot.UndoDescription is { } d ? $"_Undo {MenuText.Escape(d)}" : "_Undo";

    public string RedoHeader => Snapshot.RedoDescription is { } d ? $"_Redo {MenuText.Escape(d)}" : "_Redo";

    public string IssueSummary
    {
        get
        {
            var v = Snapshot.Validation;
            return string.Create(CultureInfo.InvariantCulture,
                $"{Plural(v.Count(IssueSeverity.Error), "error")}, {Plural(v.Count(IssueSeverity.Warning), "warning")}, {Plural(v.Count(IssueSeverity.Info), "note")}");
        }
    }

    /// <summary>Where the draft came from and is saved to.</summary>
    public string FileSummary =>
        (Snapshot.TargetPath is { } target ? $"Saves to {target}" : Snapshot.SourcePath is { } source ? $"From {source}; not saved yet" : "Not saved yet")
        + $". Draft kept in {DraftPath}.";

    /// <summary>What the sim-knowledge checks know, or that they are waiting for it.</summary>
    [ObservableProperty]
    public partial string KnowledgeSummary { get; private set; } = "Learning the sim from its other timetables and play…";

    public BulkCollection<DraftTrainRow> Trains { get; } = [];

    [ObservableProperty]
    public partial string TrainFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IssuesOnly { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyTrainCommand))]
    public partial DraftTrainRow? SelectedTrainRow { get; set; }

    /// <summary>The trains selected in the list (set by the view; several for bulk edits).</summary>
    public IReadOnlyList<DraftTrainRow> SelectedTrainRows
    {
        get => field;
        set
        {
            field = value;
            DeleteTrainsCommand.NotifyCanExecuteChanged();
        }
    } = [];

    [ObservableProperty]
    public partial DraftTrainViewModel? Train { get; private set; }

    public DraftHeaderViewModel Header { get; }

    /// <summary>The Rules tab (S45).</summary>
    public DraftRulesViewModel Rules { get; }

    /// <summary>The Seed groups tab (S45).</summary>
    public DraftSeedGroupsViewModel SeedGroups { get; }

    /// <summary>The Categories tab (S45).</summary>
    public DraftCategoriesViewModel Categories { get; }

    /// <summary>The Lists tab (S45): Decisions, Class of Service, scenario options and Driving.</summary>
    public DraftListsViewModel Lists { get; }

    /// <summary>Every train for the pickers, by headcode then time: "1A23  10:15 WLOO".</summary>
    public IReadOnlyList<TrainChoice> TrainChoices => _trainChoices ??= MakeTrainChoices();

    private IReadOnlyList<TrainChoice>? _trainChoices;

    /// <summary>
    /// The train a typed name means: a picker's label, a UID, else a headcode that only one train has. Null if none
    /// or several do (the caller says so).
    /// </summary>
    public TrainNode? FindTrain(string? text)
    {
        var typed = text?.Trim() ?? string.Empty;
        if (typed.Length == 0)
        {
            return null;
        }

        if (TrainChoices.FirstOrDefault(c => c.Label == typed) is { } picked)
        {
            return new TrainNode(Document, picked.Element);
        }

        var trains = Document.Trains.ToList();
        var byUid = trains.Where(t => string.Equals(t.Uid, typed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byUid.Count == 1)
        {
            return byUid[0];
        }

        var byHeadcode = trains.Where(t => string.Equals(t.Headcode, typed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byHeadcode.Count > 1)
        {
            throw new InvalidOperationException($"{byHeadcode.Count} trains are {typed}: pick one from the list, or type its UID.");
        }

        return byHeadcode.SingleOrDefault();
    }

    private List<TrainChoice> MakeTrainChoices() =>
        [.. Snapshot.Document.Trains
            .Select(t => (Node: t, Train: Snapshot.Import.TrainOf(t)))
            .Select(x => new TrainChoice(x.Node.Element, x.Node.Headcode ?? "?",
                $"{x.Node.Headcode}{(x.Node.Uid is { } uid && uid != x.Node.Headcode ? $" ({uid})" : string.Empty)}  {DraftText.Time(x.Train?.FirstTime)} {x.Train?.Origin}"))
            .OrderBy(c => c.Headcode, StringComparer.Ordinal).ThenBy(c => c.Label, StringComparer.Ordinal)];

    /// <summary>The graph of the draft (S44).</summary>
    public DraftBoardViewModel Board { get; }

    public BulkCollection<DraftIssueRow> Issues { get; } = [];

    [ObservableProperty]
    public partial string IssueFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IssueLevel IssueLevel { get; set; } = IssueLevel.Warnings;

    public IReadOnlyList<IssueLevel> IssueLevels { get; } = Enum.GetValues<IssueLevel>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyFixCommand), nameof(ApplyFixesOfKindCommand), nameof(GoToIssueCommand))]
    public partial DraftIssueRow? SelectedIssue { get; set; }

    /// <summary>What the last action did.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Why the last edit was refused.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Every TIPLOC the sim's knowledge and this draft know, for the location cell's completion.</summary>
    public IReadOnlyList<CodeChoice> Locations => _locations;

    /// <summary>Makes an edit; false (and why, in <see cref="Error"/>) if it does not fit.</summary>
    public bool TryApply(TimetableEdit edit, EditOrigin origin = EditOrigin.User)
    {
        try
        {
            Editor.Apply(edit, origin);
            Error = null;
            Status = edit.Description;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            LogEditRefused(ex, edit.Description);
            Error = $"{edit.Description}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Says why an edit cannot be made.</summary>
    public void Refuse(string message) => Error = message;

    /// <summary>The platforms, lines and paths other timetables use at a location, most used first.</summary>
    public (IReadOnlyList<CodeChoice> Platforms, IReadOnlyList<CodeChoice> Lines, IReadOnlyList<CodeChoice> Paths) ChoicesAt(string? location)
    {
        if (Editor.Knowledge is not { } knowledge || !Tiploc.TryCreate(location, out var tiploc) || knowledge.Location(tiploc) is not { } known)
        {
            return ([], [], []);
        }

        static IReadOnlyList<CodeChoice> Of(IReadOnlyDictionary<string, int>? counts) =>
            counts is null ? [] : [.. counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => new CodeChoice(c.Key, $"{c.Key}  ({c.Value}×)"))];

        return (Of(known.Platforms), Of(known.Lines), Of(known.Paths));
    }

    /// <summary>Knowledge of the sim, built in the background after the draft opened: the checks run again with it.</summary>
    public void SetKnowledge(SimKnowledge? knowledge, IReadOnlyList<string>? problems = null)
    {
        Editor.SetKnowledge(knowledge);
        _locations = LocationChoices();
        OnPropertyChanged(nameof(Locations));
        Train?.Load(Snapshot);
        KnowledgeSummary = knowledge is null || knowledge.Timetables == 0
            ? "No other timetable of this sim was found, so the checks against the sim's knowledge are left out."
            : string.Create(CultureInfo.InvariantCulture,
                $"Checked against {Plural(knowledge.Timetables, "timetable")} ({knowledge.Trains:N0} trains) and {Plural(knowledge.Plays, "recording or session", "recordings or sessions")} of the sim.")
              + (problems is { Count: > 0 } ? $" Left out: {string.Join("; ", problems)}" : string.Empty);
    }

    /// <summary>Writes the draft and its history to the drafts folder now.</summary>
    public void AutosaveNow(bool open = true)
    {
        _autosavePending = false;
        _lastAutosave = _time.GetUtcNow();
        try
        {
            DraftStore.Write(DraftPath, Document, Editor.ToState(open));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogAutosaveFailed(ex, DraftPath);
            Error = $"The draft could not be autosaved: {ex.Message}";
        }
    }

    /// <summary>Closes the draft, asking first if it has changes not saved to a timetable file. True if it closed.</summary>
    public bool RequestClose()
    {
        if (IsClosed)
        {
            return true;
        }

        if (Editor.IsDirty)
        {
            var name = Document.Header.Name ?? Path.GetFileNameWithoutExtension(DraftPath);
            var choice = _dialogs.Choose(
                "Close draft",
                $"{name} has changes not saved to a timetable file.\n\nThe draft stays in {Path.GetDirectoryName(DraftPath)} unless you discard it, so you can open it again later (Timetable ▸ Open draft…).",
                ["Save", "Keep as a draft", "Discard the draft", "Cancel"]);
            switch (choice)
            {
                case 0 when Save(VersionBump.Build):
                    break;
                case 1:
                    break;
                case 2:
                    if (_dialogs.Choose("Discard draft", $"Discard the draft of {name} and its undo history? Timetable files already saved are kept.", ["Discard", "Cancel"]) != 0)
                    {
                        return false;
                    }

                    Close(discard: true);
                    return true;
                default:
                    return false;
            }
        }

        Close(discard: false);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var description = Snapshot.UndoDescription;
        Editor.Undo();
        Status = $"Undone: {description}";
    }

    private bool CanUndo() => Snapshot.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var description = Snapshot.RedoDescription;
        Editor.Redo();
        Status = $"Redone: {description}";
    }

    private bool CanRedo() => Snapshot.CanRedo;

    /// <summary>Ctrl+S: saves to the draft's file (asking where the first time), raising the build number.</summary>
    [RelayCommand]
    private void SaveDraft() => Save(VersionBump.Build);

    [RelayCommand]
    private void SaveDraftAs()
    {
        if (PickNewFile() is { } path)
        {
            SaveTo(path, VersionBump.Build);
        }
    }

    /// <summary>Raises the minor or major number, the build back to 0, and saves (<c>08-…md</c> §6).</summary>
    [RelayCommand]
    private void ReleaseVersion()
    {
        var current = Snapshot.Version ?? WttVersion.Unversioned;
        var (minor, major) = (current.Bump(VersionBump.Minor), current.Bump(VersionBump.Major));
        var choice = _dialogs.Choose(
            "Release version",
            $"The timetable is version {current}. Release it as {minor} (an update) or {major} (a new edition)? It is saved at once.",
            [$"Release {minor}", $"Release {major}", "Cancel"]);
        if (choice is 0 or 1)
        {
            Save(choice == 0 ? VersionBump.Minor : VersionBump.Major);
        }
    }

    [RelayCommand]
    private void CloseDraft() => RequestClose();

    /// <summary>A full check (Timetable ▸ Check timetable): the Issues panel shows what it found.</summary>
    [RelayCommand]
    private void CheckAll()
    {
        Editor.CheckAll();
        Status = $"Checked: {IssueSummary}.";
        IssuesRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A new train after the selected one (or at the end), with the headcode asked for.</summary>
    [RelayCommand]
    private void NewTrain()
    {
        if (AskHeadcode("New train", "Headcode of the new train:", "0A00") is not { } headcode)
        {
            return;
        }

        var category = Train?.Node.Category ?? Document.Categories.FirstOrDefault()?.Id;
        AddTrain(DraftDocuments.NewTrain(headcode, category), $"New train {headcode}");
    }

    [RelayCommand(CanExecute = nameof(HasTrain))]
    private void CopyTrain()
    {
        if (Train is not { } train || AskHeadcode("Copy train", $"Headcode of the copy of {train.Node.Headcode} (the copy has no UID):", train.Node.Headcode ?? "0A00") is not { } headcode)
        {
            return;
        }

        AddTrain(DraftDocuments.CopyTrain(train.Node, headcode), $"Copy {train.Node.Headcode} as {headcode}");
    }

    private bool HasTrain() => SelectedTrainRow is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedTrains))]
    private void DeleteTrains()
    {
        var rows = SelectedTrainRows.Count > 0 ? SelectedTrainRows : SelectedTrainRow is { } one ? [one] : [];
        if (rows.Count == 0)
        {
            return;
        }

        var what = rows.Count == 1 ? rows[0].Headcode : $"{rows.Count} trains";
        if (_dialogs.Choose("Delete trains", $"Delete {what}? Activities and rules that name them are left as they are (the checks will show them). Undo brings them back.", ["Delete", "Cancel"]) != 0)
        {
            return;
        }

        TryApply(TimetableEdit.Composite($"Delete {what}", rows.Select(r => TimetableEdit.Remove(r.Element))));
    }

    private bool HasSelectedTrains() => SelectedTrainRows.Count > 0 || SelectedTrainRow is not null;

    /// <summary>Opens the selected issue's train and point (a click in the Issues panel).</summary>
    [RelayCommand(CanExecute = nameof(HasIssue))]
    private void GoToIssue()
    {
        if (SelectedIssue?.Issue is not { TrainElement: { } element } issue)
        {
            return;
        }

        GoTo(element, issue.Element);
    }

    /// <summary>
    /// Selects a train in the list (clearing the filters if it is hidden) and, if given, the row of one of its trips,
    /// and brings them into view: an issue opened, or a train or point picked on the draft board (S44).
    /// </summary>
    public void GoTo(XElement train, XElement? trip)
    {
        if (!_rows.TryGetValue(train, out var row))
        {
            return;
        }

        if (!Trains.Contains(row))
        {
            (TrainFilter, IssuesOnly) = (string.Empty, false);
        }

        SelectedTrainRow = row;
        if (Train is { } selected && trip is not null)
        {
            selected.CurrentTrip = selected.Trips.FirstOrDefault(t => t.Element == trip) ?? selected.CurrentTrip;
        }

        GoToRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool HasIssue() => SelectedIssue is not null;

    [RelayCommand(CanExecute = nameof(HasFix))]
    private void ApplyFix()
    {
        if (SelectedIssue?.Issue is { QuickFix: { } fix } issue && TryApply(fix.Edit, EditOrigin.Stringline))
        {
            ForgetTestIssues([issue]);
        }
    }

    private bool HasFix() => SelectedIssue?.HasFix == true;

    /// <summary>Applies the quick fix of every issue of the selected one's kind, as one edit.</summary>
    [RelayCommand(CanExecute = nameof(HasFix))]
    private void ApplyFixesOfKind()
    {
        if (SelectedIssue is not { } selected)
        {
            return;
        }

        var issues = Snapshot.Validation.Issues.Concat(TestIssues).Where(i => i.Code == selected.Code && i.QuickFix is not null).ToList();
        if (issues.Count > 0 && TryApply(TimetableEdit.Composite($"{selected.Fix} ({issues.Count}×)", issues.Select(i => i.QuickFix!.Edit)), EditOrigin.Stringline))
        {
            ForgetTestIssues(issues);
        }
    }

    /// <summary>A click on an issue goes to its train and point.</summary>
    partial void OnSelectedIssueChanged(DraftIssueRow? value)
    {
        if (!_refreshingIssues && value is not null)
        {
            GoToIssue();
        }
    }

    partial void OnTrainFilterChanged(string value) => RefreshTrainList();

    partial void OnIssuesOnlyChanged(bool value) => RefreshTrainList();

    partial void OnIssueFilterChanged(string value) => RefreshIssues();

    partial void OnIssueLevelChanged(IssueLevel value) => RefreshIssues();

    partial void OnSelectedTrainRowChanged(DraftTrainRow? value)
    {
        if (value is null)
        {
            Train = null;
            Rules.Load(Snapshot);
            return;
        }

        if (Train?.Element != value.Element)
        {
            Train = new DraftTrainViewModel(this, value.Element);
            Train.Load(Snapshot);
        }

        Rules.Load(Snapshot);
    }

    private bool Save(VersionBump bump)
    {
        var path = Editor.TargetPath;
        if (path is null)
        {
            if (Editor.SourcePath is { } source && File.Exists(source))
            {
                var name = Path.GetFileName(source);
                var choice = _dialogs.Choose(
                    "Save timetable",
                    $"This draft was made from {name}. Save it as a new timetable beside it, so {name} stays as it is, or replace {name}?",
                    ["Save as a new file…", $"Replace {name}", "Cancel"]);
                if (choice == 0)
                {
                    path = PickNewFile();
                }
                else if (choice == 1
                    && _dialogs.Choose("Replace timetable", $"Replace {source} with the draft? A copy of it is kept in Stringline's timetable backups first.", ["Replace", "Cancel"]) == 0)
                {
                    path = source;
                }
            }
            else
            {
                path = PickNewFile();
            }
        }

        return path is not null && SaveTo(path, bump);
    }

    /// <summary>Asks for a new file: in the source's folder (else SimSig's), named by the convention.</summary>
    private string? PickNewFile()
    {
        var folder = Editor.SourcePath is { } source && File.Exists(source) ? Path.GetDirectoryName(source)! : _context.TimetableFolder;
        var name = TimetableNames.Suggest(_context.SimName, Editor.SourcePath is { } s ? Path.GetFileName(s) : null, _context.Today());
        var suggested = Path.GetFileName(TimetableNames.Unique(folder, name));
        return _dialogs.PickSaveFile("Save timetable as", FileFilters.Timetable, Path.Combine(folder, suggested));
    }

    private bool SaveTo(string path, VersionBump bump)
    {
        var checkedNow = Editor.CheckAll();
        var errors = checkedNow.Validation.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (checkedNow.Validation.BlocksSave)
        {
            _dialogs.ShowError("Cannot save", "SimSig could not read the timetable:\n\n" + List(errors.Where(i => IssueCatalogue.Of(i.Code).BlocksSave)));
            return false;
        }

        if (errors.Count > 0
            && _dialogs.Choose("Save timetable", $"The timetable has {Plural(errors.Count, "error")} SimSig's Analyser will report:\n\n{List(errors)}\n\nSave anyway?", ["Save anyway", "Cancel"]) != 0)
        {
            return false;
        }

        try
        {
            var result = Editor.Save(path, new WttSaveOptions(bump, _context.BackupFolder));
            Status = $"Saved {Path.GetFileName(result.Path)} as version {result.Version}"
                + (result.BackupPath is { } backup ? $"; the file it replaced is backed up as {Path.GetFileName(backup)}." : ".");
            Error = null;
            AutosaveNow();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(ex, path);
            _dialogs.ShowError("Cannot save", $"{path} could not be saved: {ex.Message}");
            return false;
        }

        static string List(IEnumerable<ValidationIssue> issues)
        {
            var all = issues.ToList();
            var shown = all.Take(12).Select(i => $"• {(i.Headcode is { } h ? h + ": " : string.Empty)}{i.Message}");
            return string.Join("\n", shown) + (all.Count > 12 ? $"\n… and {all.Count - 12} more (see the Issues panel)" : string.Empty);
        }
    }

    private void Close(bool discard)
    {
        IsClosed = true;
        if (discard)
        {
            try
            {
                DraftStore.Delete(DraftPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogAutosaveFailed(ex, DraftPath);
            }
        }
        else
        {
            AutosaveNow(open: false);
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private string? AskHeadcode(string title, string label, string initial)
    {
        var prompt = new PromptViewModel(title, label, new PromptField("_Headcode", initial))
        {
            Validate = p => Core.Headcode.TryCreate(p[0], out _) ? null : "Give a headcode, such as 2C01.",
        };
        return _dialogs.Prompt(prompt) ? prompt[0] : null;
    }

    private void AddTrain(XElement train, string description)
    {
        var (container, make) = DraftDocuments.TrainsContainer(Document);
        var at = SelectedTrainRow is { } row && row.Element.Parent == container ? row.Element.ElementsBeforeSelf().Count() + 1 : (int?)null;
        var insert = TimetableEdit.Insert(container, train, at);
        if (TryApply(make is null ? TimetableEdit.Composite(description, [insert]) : TimetableEdit.Composite(description, [make, insert])) && _rows.TryGetValue(train, out var added))
        {
            if (!Trains.Contains(added))
            {
                (TrainFilter, IssuesOnly) = (string.Empty, false);
            }

            SelectedTrainRow = added;
        }
    }

    private void OnSnapshot(DraftSnapshot snapshot)
    {
        Snapshot = snapshot;
        var categories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var category in snapshot.Document.Categories)
        {
            if (category.Id is { } id)
            {
                categories.TryAdd(id, category.Description is { Length: > 0 } d ? d : id);
            }
        }

        var byTrain = snapshot.Validation.Issues.Where(i => i.TrainElement is not null).ToLookup(i => i.TrainElement!);
        var present = new HashSet<XElement>();
        foreach (var train in snapshot.Document.Trains)
        {
            present.Add(train.Element);
            if (!_rows.TryGetValue(train.Element, out var row))
            {
                _rows[train.Element] = row = new DraftTrainRow(train.Element);
            }

            row.Load(train, categories, [.. byTrain[train.Element]]);
        }

        foreach (var gone in _rows.Keys.Where(e => !present.Contains(e)).ToList())
        {
            _rows.Remove(gone);
        }

        RefreshTrainList();
        if (SelectedTrainRow is { } selected && !_rows.ContainsKey(selected.Element))
        {
            SelectedTrainRow = null;
        }

        _trainChoices = null;
        Train?.Load(snapshot);
        Header.Load(snapshot);
        Rules.Load(snapshot);
        SeedGroups.Load(snapshot);
        Categories.Load(snapshot);
        Lists.Load(snapshot);
        RefreshIssues();
        if (!IsClosed)
        {
            ScheduleAutosave();
        }
    }

    private void RefreshTrainList()
    {
        var filter = TrainFilter.Trim();
        Trains.ReplaceAll(Document.Trains.Select(t => _rows[t.Element]).Where(r => r.Matches(filter) && (!IssuesOnly || r.Worst >= IssueSeverity.Warning)));
    }

    private void RefreshIssues()
    {
        var least = IssueLevel switch
        {
            IssueLevel.Errors => IssueSeverity.Error,
            IssueLevel.Warnings => IssueSeverity.Warning,
            _ => IssueSeverity.Info,
        };
        var filter = IssueFilter.Trim();
        var selected = SelectedIssue?.Issue;
        var rows = Snapshot.Validation.Issues.Concat(TestIssues)
            .Where(i => i.Severity >= least)
            .Select(i => new DraftIssueRow(i, Where(i)))
            .Where(r => filter.Length == 0 || r.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Train.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Code.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.Where.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _refreshingIssues = true;
        try
        {
            Issues.ReplaceAll(rows);
            SelectedIssue = selected is null ? null : rows.FirstOrDefault(r => r.Issue == selected);
        }
        finally
        {
            _refreshingIssues = false;
        }
    }

    private static string Where(ValidationIssue issue) =>
        issue.Element is { Name.LocalName: "Trip" } trip ? (string?)trip.Element("Location") ?? string.Empty : string.Empty;

    private void ScheduleAutosave()
    {
        var generation = ++_autosaveGeneration;
        if (_autosavePending && _time.GetUtcNow() - _lastAutosave > AutosaveDelay * 15)
        {
            // Edits keep coming faster than the delay: autosave now and then anyway.
            AutosaveNow();
            return;
        }

        _autosavePending = true;
        _ = Task.Delay(AutosaveDelay, _time).ContinueWith(
            _ => _ui.Post(() =>
            {
                if (generation == _autosaveGeneration && _autosavePending && !IsClosed)
                {
                    AutosaveNow();
                }
            }),
            TaskScheduler.Default);
    }

    private IReadOnlyList<CodeChoice> LocationChoices()
    {
        var names = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var location in Editor.Knowledge?.Locations ?? [])
        {
            names[location.Tiploc.Code] = location.Name;
        }

        foreach (var point in Snapshot.Timetable.Trains.SelectMany(t => t.Points))
        {
            names.TryAdd(point.Location.Code, null);
        }

        return [.. names.Select(n => new CodeChoice(n.Key, n.Value is { Length: > 0 } name ? $"{n.Key}  {name}" : n.Key))];
    }

    private static string Plural(int count, string one, string? many = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many ?? one + "s")}");

    [LoggerMessage(Level = LogLevel.Information, Message = "Edit refused: {Description}")]
    private partial void LogEditRefused(Exception ex, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not autosave the draft {Path}")]
    private partial void LogAutosaveFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the timetable {Path}")]
    private partial void LogSaveFailed(Exception ex, string path);
}
