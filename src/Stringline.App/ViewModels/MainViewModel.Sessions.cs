using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;
using Stringline.Persistence;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Session persistence and review (S16, SES-1 and SES-3). Every live or replay session autosaves to a
/// <c>.stringline</c> file: the live frames the engine uses (a replay refers to its recording instead), every plan step
/// and what the session is. A session left running when the app closed is offered for resuming at the next start.
/// Resuming rebuilds the live state from the saved frames and the plan from its steps; review replays the session with
/// the plan as it was at each moment, read-only.
/// </summary>
internal sealed partial class MainViewModel
{
    private SessionWriter? _store;
    private IReadOnlyList<PlanStep>? _reviewSteps;
    private int _reviewApplied = -1;
    private SessionInfo? _offered;

    /// <summary>Reviewing a saved session: the plan follows the replay and cannot be changed.</summary>
    [ObservableProperty]
    public partial bool IsReviewing { get; private set; }

    /// <summary>A resumed live session waiting for Connect to carry on.</summary>
    [ObservableProperty]
    public partial bool IsResumedIdle { get; private set; }

    /// <summary>The info bar offers to resume the last session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoShowsBrowse))]
    public partial bool InfoHasResume { get; private set; }

    /// <summary>The info bar's Browse… (for a timetable) button.</summary>
    public bool InfoShowsBrowse => !InfoHasResume;

    /// <summary>The session file being saved to, if any.</summary>
    public string? SessionPath => _store?.Path;

    private SessionWriter? Store => Volatile.Read(ref _store);

    private void InitSessions() => _engine.PlanStepped += (_, step) => Store?.OnStep(step);

    /// <summary>At start-up: if the last session was left running, offer to resume it (Fred, S16).</summary>
    public async Task OfferResumeAsync()
    {
        var folder = _paths.Sessions;
        var latest = await Task.Run(() => SessionCatalogue.LatestUnfinished(folder));
        if (latest is null || _session is not null || _store is not null || (InfoMessage is not null && InfoSeverity != InfoSeverity.Info))
        {
            return;
        }

        _offered = latest;
        var at = latest.LastSimTime is { } t ? $" at {t}" : string.Empty;
        ShowInfo(InfoSeverity.Info, $"The last session ({latest.DisplayName}{at}, {Describe(latest)}) was not ended. Resume it?");
        InfoHasResume = true;
    }

    [RelayCommand]
    private async Task ResumeOfferedAsync()
    {
        if (_offered?.Path is { } path)
        {
            await ResumeSessionAsync(path);
        }
    }

    [RelayCommand]
    private async Task ReviewOfferedAsync()
    {
        if (_offered?.Path is { } path)
        {
            await ReviewSessionAsync(path);
        }
    }

    [RelayCommand]
    private async Task ResumeSessionAsync()
    {
        if (_dialogs.PickFile("Resume a saved session", FileFilters.Session, SessionsFolder()) is { } path)
        {
            await ResumeSessionAsync(path);
        }
    }

    [RelayCommand]
    private async Task ReviewSessionAsync()
    {
        if (_dialogs.PickFile("Review a saved session", FileFilters.Session, SessionsFolder()) is { } path)
        {
            await ReviewSessionAsync(path);
        }
    }

    /// <summary>
    /// Carry on with a saved session. A live one is rebuilt from its frames and plan steps, and Connect carries it on;
    /// a replay one starts its recording again, paused where it had got to, with its plan.
    /// </summary>
    public async Task ResumeSessionAsync(string path)
    {
        if (await LoadSessionAsync(path) is not { } saved)
        {
            return;
        }

        await LeaveSessionAsync();
        if (!await LoadSessionTimetableAsync(saved))
        {
            return;
        }

        if (saved.Info.Kind == SavedSessionKind.Replay)
        {
            if (await LoadSessionRecordingAsync(saved) is not { } recording)
            {
                return;
            }

            _recording = recording;
            _recordingName = Path.GetFileName(saved.Info.RecordingPath);
            _replayOptions = new ReplayOptions { Speed = ReplaySpeed(), StartAt = saved.Info.Position, StartPaused = true };
            StartReplay();
            await _engine.RestorePlan(saved.Steps);
        }
        else
        {
            _engine.Clear();
            var engine = _engine;
            await Task.Run(() =>
            {
                foreach (var e in saved.Events(new MessageParser()))
                {
                    engine.Post(e);
                }
            });
            await _engine.RestorePlan(saved.Steps);
            IsResumedIdle = true;
            Navigator.Follow();
        }

        if (!OpenStore(() => SessionWriter.Append(path, logger: _loggerFactory.CreateLogger<SessionWriter>())))
        {
            return;
        }

        var to = saved.Info.LastSimTime is { } t ? $" to {t}" : string.Empty;
        var next = saved.Info.Kind == SavedSessionKind.Live ? " Connect to SimSig to carry on." : " Press play to carry on.";
        ShowInfo(
            saved.Problems.Count > 0 ? InfoSeverity.Warning : InfoSeverity.Info,
            $"Resumed {saved.Info.DisplayName}{to}: {Edits(saved.Edits)}.{next}" + Problems(saved));
        LogResumed(path, saved.Frames.Count, saved.Steps.Count);
        UpdateConnection();
        UpdatePlaceholder();
        UpdateTitle();
    }

    /// <summary>
    /// Post-run review (SES-3): replay a saved session from its start, paused, with each plan edit re-applied when the
    /// replay reaches the sim time it was made (Fred, S16). The plan cannot be changed, and nothing is saved.
    /// </summary>
    public async Task ReviewSessionAsync(string path)
    {
        if (await LoadSessionAsync(path) is not { } saved)
        {
            return;
        }

        await LeaveSessionAsync();
        if (!await LoadSessionTimetableAsync(saved))
        {
            return;
        }

        SslogRecording? recording;
        if (saved.Info.Kind == SavedSessionKind.Replay)
        {
            recording = await LoadSessionRecordingAsync(saved);
        }
        else if (saved.Frames.Count == 0)
        {
            ShowInfo(InfoSeverity.Error, $"{Path.GetFileName(path)} has nothing to replay.");
            recording = null;
        }
        else
        {
            recording = saved.ToRecording();
        }

        if (recording is null)
        {
            return;
        }

        _reviewSteps = saved.Steps;
        _reviewApplied = -1;
        IsReviewing = true;
        _recording = recording;
        _recordingName = $"Review: {Path.GetFileName(path)}";
        _replayOptions = new ReplayOptions { Speed = ReplaySpeed(), StartPaused = true };
        StartReplay();
        ShowInfo(
            saved.Problems.Count > 0 ? InfoSeverity.Warning : InfoSeverity.Info,
            $"Reviewing {saved.Info.DisplayName} ({Edits(saved.Edits)}). Press play; the plan changes as it did. Edits are off while reviewing." + Problems(saved));
        LogReviewing(path);
        UpdateEditCommands();
        UpdateTitle();
    }

    /// <summary>Stop reviewing (another session is starting): edits are allowed again.</summary>
    private void LeaveReview()
    {
        if (!IsReviewing)
        {
            return;
        }

        IsReviewing = false;
        _reviewSteps = null;
        _reviewApplied = -1;
        UpdateEditCommands();
        UpdateTitle();
    }

    /// <summary>Review: bring the plan to where it was at the snapshot's sim time.</summary>
    private void UpdateReview(PlanSnapshot snapshot)
    {
        if (!IsReviewing || _reviewSteps is not { } steps)
        {
            return;
        }

        var now = snapshot.Clock.HasTime ? snapshot.Clock.LastReported : (SimTime?)null;
        var by = PlanLog.By(steps, now);
        if (by.Count != _reviewApplied)
        {
            _reviewApplied = by.Count;
            _ = _engine.RestorePlan(by);
        }
    }

    /// <summary>The refusal shown for an edit while reviewing, or null.</summary>
    private string? ReviewRefusal => IsReviewing ? "Reviewing a saved session: the plan cannot be changed. Resume the session to carry on planning." : null;

    /// <summary>End whatever is running (and its session file) before another session starts.</summary>
    private async Task LeaveSessionAsync()
    {
        HideInfo();
        LeaveReview();
        await StopSessionAsync();
        CloseStore(ended: true);
    }

    /// <summary>Start saving a new session of this kind.</summary>
    private void StartStore(SessionInfo info)
    {
        CloseStore(ended: true);
        var folder = _paths.Sessions;
        OpenStore(() => SessionWriter.Create(folder, info with
        {
            TimetablePath = _timetable?.Path,
            TimetableName = _timetable?.Timetable.Header.Name,
            TimetableVersion = _timetable?.Timetable.Header.TimetableVersion,
            AreaId = _timetable?.Timetable.AreaId is { Length: > 0 } area ? area : null,
            SimName = Profile?.Name,
        }, logger: _loggerFactory.CreateLogger<SessionWriter>()));
    }

    private bool OpenStore(Func<SessionWriter> open)
    {
        try
        {
            Volatile.Write(ref _store, open());
            OnPropertyChanged(nameof(SessionPath));
            return true;
        }
        catch (Exception ex) when (ex is SessionFormatException or IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot save this session: {ex.Message}");
            return false;
        }
    }

    /// <param name="ended">Mark the session as ended (so it is not offered for resuming); false when the app closes.</param>
    private void CloseStore(bool ended)
    {
        if (Interlocked.Exchange(ref _store, null) is not { } store)
        {
            return;
        }

        IsResumedIdle = false;
        if (ended)
        {
            store.End();
        }

        store.Dispose();
        OnPropertyChanged(nameof(SessionPath));
    }

    /// <summary>A few times a second: a replay session keeps its position.</summary>
    private void TickStore()
    {
        if (Store is { Kind: SavedSessionKind.Replay } store && !IsReviewing && _session?.Timeline?.Position is { } position)
        {
            store.SetPosition(position);
        }
    }

    private async Task<SavedSession?> LoadSessionAsync(string path)
    {
        ShowInfo(InfoSeverity.Info, $"Loading {Path.GetFileName(path)}…");
        try
        {
            return await Task.Run(() => SavedSession.Load(path));
        }
        catch (Exception ex) when (ex is SessionFormatException or IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot open the session: {ex.Message}");
            return null;
        }
    }

    /// <summary>Load the session's timetable if it is not the one loaded; ask for it if it has moved. False to give up.</summary>
    private async Task<bool> LoadSessionTimetableAsync(SavedSession saved)
    {
        var path = saved.Info.TimetablePath;
        if (path is not null && string.Equals(path, _timetable?.Path, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path is null || !File.Exists(path))
        {
            var start = path is not null && Directory.Exists(Path.GetDirectoryName(path)) ? path : _settings.TimetableFolder ?? Timetable.WttCatalogue.DefaultFolder;
            path = _dialogs.PickFile($"Choose the timetable for {saved.Info.DisplayName}{(path is null ? string.Empty : $" (was {Path.GetFileName(path)})")}", FileFilters.Timetable, start);
            if (path is null)
            {
                ShowInfo(InfoSeverity.Warning, "The session needs its timetable; nothing was opened.");
                return false;
            }
        }

        await LoadTimetableAsync(path);
        return string.Equals(path, _timetable?.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A replay session's recording; ask for it if it has moved.</summary>
    private async Task<SslogRecording?> LoadSessionRecordingAsync(SavedSession saved)
    {
        var path = saved.Info.RecordingPath;
        if (path is null || !File.Exists(path))
        {
            path = _dialogs.PickFile($"Choose the recording for {saved.Info.DisplayName}{(path is null ? string.Empty : $" (was {Path.GetFileName(path)})")}", FileFilters.Recording, _paths.Recordings);
            if (path is null)
            {
                ShowInfo(InfoSeverity.Warning, "The session needs its recording; nothing was opened.");
                return null;
            }
        }

        try
        {
            return await Task.Run(() => SslogRecording.Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot open the recording: {ex.Message}");
            return null;
        }
    }

    private string SessionsFolder() => Directory.Exists(_paths.Sessions) ? _paths.Sessions : _paths.Recordings;

    private double ReplaySpeed() => _settings.ReplaySpeed <= 0 ? double.PositiveInfinity : _settings.ReplaySpeed;

    private static string Describe(SessionInfo info) => (info.Kind == SavedSessionKind.Live ? "live" : $"replay of {Path.GetFileName(info.RecordingPath)}")
        + (info.Steps == 1 ? ", 1 plan step" : $", {info.Steps} plan steps");

    private static string Edits(int count) => count == 1 ? "1 edit" : $"{count} edits";

    private static string Problems(SavedSession saved) => saved.Problems.Count == 0
        ? string.Empty
        : $" {saved.Problems.Count} saved edits could not be read and were skipped.";

    [LoggerMessage(Level = LogLevel.Information, Message = "Resumed session {Path}: {Frames} frames, {Steps} plan steps")]
    private partial void LogResumed(string path, int frames, int steps);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reviewing session {Path}")]
    private partial void LogReviewing(string path);
}
