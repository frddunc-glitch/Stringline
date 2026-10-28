using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Editing.Drafts;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Planning;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>
/// The Timetable menu (S43, <c>08-…md</c> §7): drafts made from the loaded timetable, a file or nothing, opened in the
/// editor workspace, one at a time (Fred). A draft is always its own document read from disk, so the session's
/// timetable and plan never change while it is edited. The sim's knowledge for the checks is built in the background.
/// </summary>
internal sealed partial class MainViewModel
{
    /// <summary>The draft open in the editor, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraft))]
    [NotifyCanExecuteChangedFor(nameof(SaveDraftCommand), nameof(SaveDraftAsCommand), nameof(ReleaseDraftCommand), nameof(CloseDraftCommand), nameof(CheckTimetableCommand))]
    public partial TimetableEditorViewModel? Draft { get; private set; }

    public bool HasDraft => Draft is not null;

    private DraftStore Drafts => new(_paths.Drafts);

    /// <summary>Timetable ▸ New draft ▸ From the loaded timetable: a copy read again from its file.</summary>
    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private void NewDraftFromLoaded()
    {
        if (_timetable is { } loaded)
        {
            OpenNewDraft(loaded.Path);
        }
    }

    /// <summary>
    /// Timetable ▸ New draft ▸ From the working plan (S47): a draft of the loaded timetable with the session's re-plans
    /// written in as its first edit. Cancelled trains are removed or kept as the user picks; what a timetable cannot
    /// hold (disruptions, notes, holds at signals) is listed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private void NewDraftFromPlan()
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        var plan = Snapshot.Plan;
        if (!plan.Modified.Any() && plan.Disruptions.Count == 0 && plan.Notes.Count == 0)
        {
            _dialogs.ShowError("New draft from the working plan", "The working plan is as the timetable books it: nothing has been re-planned yet. Use From the loaded timetable for a copy.");
            return;
        }

        var removeCancelled = true;
        var cancelled = plan.Modified.Count(t => t.Cancelled);
        if (cancelled > 0)
        {
            var choice = _dialogs.Choose(
                "New draft from the working plan",
                $"{cancelled} train{(cancelled == 1 ? " is" : "s are")} cancelled in the plan. In the draft:",
                ["Remove them", "Keep them as booked", "Cancel"]);
            if (choice is not (0 or 1))
            {
                return;
            }

            removeCancelled = choice == 0;
        }

        OpenNewDraft(loaded.Path);
        if (Draft is not { } draft)
        {
            return;
        }

        var result = PlanToDraft.Build(draft.Document, plan, removeCancelled);
        if (result.Edit is not null)
        {
            draft.TryApply(result.Edit);
        }

        var lines = result.Changes.Select(c => "• " + c).ToList();
        if (result.Problems.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Not in the draft:");
            lines.AddRange(result.Problems.Select(p => "• " + p));
        }

        _dialogs.ShowList("New draft from the working plan", $"{result.Changes.Count} train{(result.Changes.Count == 1 ? "" : "s")} changed from the timetable. The checks have run: see the Issues panel.", lines);
    }

    [RelayCommand]
    private void NewDraftFromFile()
    {
        var start = _timetable?.Path ?? _settings.TimetableFolder ?? WttCatalogue.DefaultFolder;
        if (_dialogs.PickFile("New draft from a timetable", FileFilters.Timetable, start) is { } path)
        {
            OpenNewDraft(path);
        }
    }

    /// <summary>A blank timetable for the sim loaded or connected, with the loaded timetable's categories.</summary>
    [RelayCommand(CanExecute = nameof(KnowsSim))]
    private void NewBlankDraft()
    {
        if (DraftArea() is not { } area || !CloseDraftFirst())
        {
            return;
        }

        WttDocument? from = null;
        if (_timetable is { } loaded && string.Equals(loaded.Timetable.AreaId, area, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                from = WttDocument.Load(loaded.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
            {
                LogDraftFailed(ex, loaded.Path);
            }
        }

        var start = _timetable?.Timetable.Header.StartTime ?? new SimTime(0);
        var document = DraftDocuments.Blank(area, $"{DraftSimName(area)} new timetable", start, from);
        ShowDraft(new TimetableEditor(document), Drafts.NewPath(null, area), "A blank timetable. Add trains with New train.");
    }

    private bool KnowsSim() => DraftArea() is not null;

    /// <summary>Timetable ▸ Open draft…: a draft from the drafts folder, with its undo history.</summary>
    [RelayCommand]
    private void OpenDraft()
    {
        Directory.CreateDirectory(_paths.Drafts);
        if (_dialogs.PickFile("Open draft", "Stringline drafts (*.wtt)|*.wtt", _paths.Drafts + Path.DirectorySeparatorChar) is { } path)
        {
            RestoreDraft(path);
        }
    }

    [RelayCommand(CanExecute = nameof(HasDraft))]
    private void SaveDraft() => Draft?.SaveDraftCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasDraft))]
    private void SaveDraftAs() => Draft?.SaveDraftAsCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasDraft))]
    private void ReleaseDraft() => Draft?.ReleaseVersionCommand.Execute(null);

    [RelayCommand(CanExecute = nameof(HasDraft))]
    private void CloseDraft() => Draft?.RequestClose();

    /// <summary>
    /// Timetable ▸ Check timetable: the draft's full check in its Issues panel, or, with no draft, the loaded
    /// timetable's issues in a list (it is not changed).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckTimetable))]
    private async Task CheckTimetableAsync()
    {
        if (Draft is { } draft)
        {
            draft.CheckAllCommand.Execute(null);
            _dialogs.ShowEditor(draft);
            return;
        }

        if (_timetable is not { } loaded)
        {
            return;
        }

        var path = loaded.Path;
        var request = KnowledgeRequest(loaded.Timetable.AreaId, path, null);
        try
        {
            var (issues, name) = await Task.Run(() =>
            {
                var document = WttDocument.Load(path);
                var knowledge = request is null ? null : new SimKnowledgeStore(_paths.Profiles).Get(request).Knowledge;
                return (new TimetableValidator(knowledge).Validate(document), document.Header.Name ?? Path.GetFileName(path));
            });
            var lines = issues.Issues.OrderByDescending(i => i.Severity).Select(i => i.ToString()).ToList();
            _dialogs.ShowList(
                "Check timetable",
                $"{name}: {issues.Count(IssueSeverity.Error)} errors, {issues.Count(IssueSeverity.Warning)} warnings, {issues.Count(IssueSeverity.Info)} notes. Make a draft (Timetable ▸ New draft) to fix them.",
                lines.Count > 0 ? lines : ["No issues found."]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
        {
            _dialogs.ShowError("Check timetable", $"{path} could not be checked: {ex.Message}");
        }
    }

    private bool CanCheckTimetable() => Draft is not null || _timetable is not null;

    /// <summary>At start-up: if a draft was open when Stringline last stopped, offer to open it again (Fred, S43).</summary>
    public void OfferRestoreDraft()
    {
        if (Draft is not null || Drafts.FindUnclosed() is not { } unclosed)
        {
            return;
        }

        var choice = _dialogs.Choose(
            "Restore draft",
            $"The draft of {unclosed.Name} was open when Stringline last stopped. Open it again, with its undo history?",
            ["Open it", "Not now"]);
        if (choice == 0)
        {
            RestoreDraft(unclosed.Path);
        }
        else
        {
            DraftStore.MarkClosed(unclosed.Path);
        }
    }

    /// <summary>Before the app closes: the draft is closed, asking about unsaved changes. False to stay open.</summary>
    public bool ConfirmCloseDraft() => Draft?.RequestClose() ?? true;

    /// <summary>A new draft of a timetable file (the session's own timetable is never touched).</summary>
    internal void OpenNewDraft(string path)
    {
        if (!CloseDraftFirst())
        {
            return;
        }

        WttDocument document;
        try
        {
            document = WttDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
        {
            LogDraftFailed(ex, path);
            _dialogs.ShowError("New draft", $"{path} cannot be edited: {ex.Message}");
            return;
        }

        var source = Path.GetFullPath(path);
        ShowDraft(new TimetableEditor(document, sourcePath: source), Drafts.NewPath(source, document.Header.AreaId), $"A draft of {Path.GetFileName(source)}; the file stays as it is until you save.");
    }

    internal void RestoreDraft(string draftPath)
    {
        if (!CloseDraftFirst())
        {
            return;
        }

        try
        {
            var opened = DraftStore.Open(draftPath);
            var editor = TimetableEditor.Restore(opened.Document, opened.State);
            ShowDraft(editor, Path.GetFullPath(draftPath), opened.Problem ?? "The draft is as you left it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException or InvalidOperationException)
        {
            LogDraftFailed(ex, draftPath);
            _dialogs.ShowError("Open draft", $"{draftPath} cannot be opened: {ex.Message}");
        }
    }

    /// <summary>One draft at a time (Fred): the open one is closed first, asking about unsaved changes.</summary>
    private bool CloseDraftFirst() => Draft is null || Draft.RequestClose();

    private void ShowDraft(TimetableEditor editor, string draftPath, string status)
    {
        var area = editor.Document.Header.AreaId;
        var context = new DraftContext(
            DraftSimName(area),
            _settings.TimetableFolder is { Length: > 0 } folder ? folder : WttCatalogue.DefaultFolder,
            _paths.TimetableBackups)
        {
            Exports = new DraftExports(
                () => _settings.LastExportFolder is { Length: > 0 } last && Directory.Exists(last) ? last : _paths.Exports,
                path =>
                {
                    _settings = _settings with { LastExportFolder = Path.GetDirectoryName(path) };
                    _settingsStore.Save(_settings);
                },
                () => _settings.Plot,
                plot =>
                {
                    _settings = _settings with { Plot = plot };
                    _settingsStore.Save(_settings);
                }),
        };
        var draft = new TimetableEditorViewModel(editor, draftPath, context, _dialogs, _ui, _time, _loggerFactory.CreateLogger<TimetableEditorViewModel>());
        draft.CloseRequested += (_, _) =>
        {
            if (Draft == draft)
            {
                Draft = null;
            }
        };
        Draft = draft;
        draft.AutosaveNow();
        draft.Status = status;
        _dialogs.ShowEditor(draft);
        LearnForDraft(draft);
        SetUpDraftBoard(draft);
    }

    /// <summary>
    /// The draft board's profile, network and original timetable (S44), made in the background: the loaded profile if it
    /// is the draft's sim, else the sim's saved profile, else corridors generated from the draft and not saved (Fred).
    /// The original is the draft's source file, else the draft as it opened.
    /// </summary>
    private void SetUpDraftBoard(TimetableEditorViewModel draft)
    {
        var area = draft.Document.Header.AreaId;
        var opened = draft.Snapshot.Timetable;
        var source = draft.Editor.SourcePath;
        var loaded = Profile is { } p && area is { Length: > 0 } && string.Equals(p.AreaId, area, StringComparison.OrdinalIgnoreCase) ? p : null;
        var profiles = _loader.Profiles;
        var (minSamples, forecast, conflicts) = (_settings.LearnMinSamples, _settings.Planning.ToForecastOptions(ForecastKnockOn), _settings.Planning.ToConflictOptions());
        var (signals, vertical) = (SignalDisplay, IsVertical);
        _ = Task.Run(() =>
        {
            var original = opened;
            if (source is not null && File.Exists(source))
            {
                try
                {
                    original = WttDocument.Load(source).ToWorkingTimetable();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
                {
                    LogDraftFailed(ex, source);
                }
            }

            string? note = null;
            var profile = loaded;
            if (profile is null && area is { Length: > 0 })
            {
                try
                {
                    profile = profiles.Load(area)?.Refresh(original);
                }
                catch (ProfileFormatException ex)
                {
                    LogDraftFailed(ex, profiles.PathFor(area));
                }
            }

            if (profile is null)
            {
                profile = StringlineProfile.Create(opened.Trains.Count > 0 ? opened : original) with { AreaId = area ?? TimetableLoader.UnnamedSim };
                note = "No profile for this sim: the corridors are generated from the draft and not saved";
            }

            return new DraftBoardSetup(profile, ForecastNetwork.For(profile, original, minSamples), original, forecast, conflicts)
            {
                Note = note,
                Signals = signals,
                Vertical = vertical,
            };
        }).ContinueWith(
            task => _ui.Post(() =>
            {
                if (draft.IsClosed)
                {
                    return;
                }

                if (task.IsCompletedSuccessfully)
                {
                    draft.Board.SetUp(task.Result);
                }
                else
                {
                    LogDraftBoardFailed(task.Exception?.GetBaseException(), area ?? "(none)");
                }
            }),
            TaskScheduler.Default);
    }

    /// <summary>Builds (or reads from its cache) the sim's knowledge for the draft's checks, in the background.</summary>
    private void LearnForDraft(TimetableEditorViewModel draft)
    {
        if (KnowledgeRequest(draft.Document.Header.AreaId, draft.Editor.SourcePath, draft.Editor.TargetPath) is not { } request)
        {
            draft.SetKnowledge(null);
            return;
        }

        var store = new SimKnowledgeStore(_paths.Profiles);
        _ = Task.Run(() => store.Get(request)).ContinueWith(
            task => _ui.Post(() =>
            {
                if (draft.IsClosed)
                {
                    return;
                }

                if (task.IsCompletedSuccessfully)
                {
                    draft.SetKnowledge(task.Result.Knowledge, task.Result.Problems);
                }
                else
                {
                    LogKnowledgeFailed(task.Exception?.GetBaseException(), request.AreaId);
                    draft.SetKnowledge(null);
                }
            }),
            TaskScheduler.Default);
    }

    /// <summary>What the sim's knowledge is built from: its timetables, the saved sessions and the profile; the draft's own files left out.</summary>
    private SimKnowledgeRequest? KnowledgeRequest(string? area, string? sourcePath, string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(area))
        {
            return null;
        }

        var folder = _settings.TimetableFolder is { Length: > 0 } chosen ? chosen : WttCatalogue.DefaultFolder;
        var profile = _loader.Profiles.PathFor(area);
        return new SimKnowledgeRequest
        {
            AreaId = area,
            TimetableFolders = folder.Length > 0 ? [folder] : [],
            PlayFiles = Directory.Exists(_paths.Sessions) ? PlayFiles.ForArea(_paths.Sessions, area) : [],
            ReadPlay = path => PlayFiles.Read(path),
            ProfilePath = File.Exists(profile) ? profile : null,
            Exclude = [.. new[] { sourcePath, targetPath }.OfType<string>()],
        };
    }

    /// <summary>The sim a blank draft is for: the loaded timetable's, else the session's.</summary>
    private string? DraftArea() =>
        _timetable?.Timetable.AreaId is { Length: > 0 } own ? own
        : Snapshot.AreaId is { Length: > 0 } live ? live
        : null;

    private string DraftSimName(string? area) =>
        TimetableNames.SimName(Profile is { } profile && string.Equals(profile.AreaId, area, StringComparison.OrdinalIgnoreCase) ? profile.SimName(_timetable?.Timetable.Header) : null, area);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open a draft of {Path}")]
    private partial void LogDraftFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not learn the sim {Area} for the draft's checks")]
    private partial void LogKnowledgeFailed(Exception? ex, string area);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not set up the draft board for the sim {Area}")]
    private partial void LogDraftBoardFailed(Exception? ex, string area);
}
