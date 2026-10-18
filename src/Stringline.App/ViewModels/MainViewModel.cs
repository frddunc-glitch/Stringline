using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;
using Stringline.Graph;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Planning;
using Stringline.Timetable;

namespace Stringline.App.ViewModels;

/// <summary>A corridor tab.</summary>
internal sealed record CorridorTab(Corridor Corridor)
{
    public string Id => Corridor.Id;

    public string Name => Corridor.Name;
}

internal enum InfoSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// The main window: the session (live or replay), the timetable and profile, the corridor tabs, the graph's scene,
/// selection and navigation, the train list, the info bar and the status bar. Background work (engine snapshots,
/// session status, scene builds, file loading) comes back through <see cref="IUiDispatcher"/>, so all state here is
/// touched only on the UI thread.
/// </summary>
internal sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly PlanningEngine _engine;
    private readonly SessionRecorder _recorder;
    private readonly TimetableLoader _loader;
    private readonly AppSettingsStore _settingsStore;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly AppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private AppSettings _settings;
    private SimSession? _session;
    private SslogRecording? _recording;
    private string? _recordingName;
    private ReplayOptions? _replayOptions;
    private LoadedTimetable? _timetable;
    private PlanSnapshot? _latestSnapshot;
    private int _snapshotQueued;
    private bool _building;
    private bool _sceneDirty;
    private string? _suggestedFor;
    private string? _mismatchWarnedFor;
    private double? _frozenNow;
    private bool _syncingSelection;

    public MainViewModel(
        PlanningEngine engine,
        SessionRecorder recorder,
        TimetableLoader loader,
        AppSettingsStore settingsStore,
        IDialogService dialogs,
        IUiDispatcher ui,
        AppPaths paths,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        _engine = engine;
        _recorder = recorder;
        _loader = loader;
        _settingsStore = settingsStore;
        _dialogs = dialogs;
        _ui = ui;
        _paths = paths;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainViewModel>();
        _settings = settingsStore.Load();
        Snapshot = engine.Current;
        // Read both first: setting either saves the view settings, which would overwrite the other.
        var (byDelay, vertical) = (_settings.ColourByDelay, _settings.VerticalGraph);
        IsVertical = vertical;
        ColourMode = byDelay ? GraphColourMode.Delay : GraphColourMode.Category;
        Trains.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrainListViewModel.Selected))
            {
                OnTrainRowSelected(Trains.Selected);
            }
        };
        engine.SnapshotPublished += OnSnapshotPublished;
        recorder.StateChanged += (_, _) => _ui.Post(UpdateRecording);
        UpdateConnection();
        UpdatePlaceholder();
    }

    public GraphNavigator Navigator { get; } = new();

    public TrainListViewModel Trains { get; } = new();

    public ObservableCollection<CorridorTab> Corridors { get; } = [];

    /// <summary>Timetables offered in the info bar when none is loaded for the sim.</summary>
    public ObservableCollection<WttFileInfo> Suggestions { get; } = [];

    public StringlineProfile? Profile => _timetable?.Profile;

    public LoadedTimetable? LoadedTimetable => _timetable;

    public SimSession? Session => _session;

    [ObservableProperty]
    public partial PlanSnapshot Snapshot { get; private set; }

    [ObservableProperty]
    public partial GraphScene? Scene { get; private set; }

    [ObservableProperty]
    public partial GraphSelection? Selection { get; private set; }

    [ObservableProperty]
    public partial CorridorTab? SelectedCorridor { get; set; }

    [ObservableProperty]
    public partial bool ShowWtt { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowActual { get; set; } = true;

    public IReadOnlyList<GraphColourMode> ColourModes { get; } = Enum.GetValues<GraphColourMode>();

    /// <summary>Colour actual lines (and the train list) by category or by delay band (GR-7). Remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColourByDelay))]
    public partial GraphColourMode ColourMode { get; set; }

    /// <summary><see cref="ColourMode"/> as a check box.</summary>
    public bool ColourByDelay
    {
        get => ColourMode == GraphColourMode.Delay;
        set => ColourMode = value ? GraphColourMode.Delay : GraphColourMode.Category;
    }

    /// <summary>Time runs down the graph rather than across (GR-1). Remembered.</summary>
    [ObservableProperty]
    public partial bool IsVertical { get; set; }

    [ObservableProperty]
    public partial TransportViewModel? Transport { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "Stringline";

    [ObservableProperty]
    public partial string ConnectionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial StatusLight ConnectionLight { get; private set; }

    [ObservableProperty]
    public partial string SimName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ClockText { get; private set; } = "Sim —";

    [ObservableProperty]
    public partial string SpeedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TrainsText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    public partial bool IsLive { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    public partial bool HasSession { get; private set; }

    [ObservableProperty]
    public partial bool IsRecording { get; private set; }

    [ObservableProperty]
    public partial string RecordingText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsFollowing { get; private set; } = true;

    /// <summary>Text over the graph when there is nothing to draw yet.</summary>
    [ObservableProperty]
    public partial string? Placeholder { get; private set; }

    [ObservableProperty]
    public partial string? InfoMessage { get; private set; }

    [ObservableProperty]
    public partial InfoSeverity InfoSeverity { get; private set; }

    [ObservableProperty]
    public partial bool InfoHasSuggestions { get; private set; }

    [ObservableProperty]
    public partial bool InfoHasWarnings { get; private set; }

    [ObservableProperty]
    public partial WttFileInfo? SelectedSuggestion { get; set; }

    /// <summary>The graph's now line, in sim seconds, as of the last <see cref="Tick"/>.</summary>
    public double? NowSeconds { get; private set; }

    partial void OnSelectedCorridorChanged(CorridorTab? value)
    {
        Navigator.ShowFullCorridor();
        RequestScene();
    }

    partial void OnShowWttChanged(bool value) => RequestScene();

    partial void OnShowActualChanged(bool value) => RequestScene();

    partial void OnColourModeChanged(GraphColourMode value)
    {
        Trains.ColourMode = value;
        Trains.Update(Snapshot, Profile);
        SaveViewSettings();
        RequestScene();
    }

    partial void OnIsVerticalChanged(bool value)
    {
        Navigator.SetOrientation(value ? GraphOrientation.Vertical : GraphOrientation.Horizontal);
        SaveViewSettings();
    }

    /// <summary>
    /// Called a few times a second on the UI thread: moves the clock, the now line, follow mode and the replay bar on.
    /// Returns the now line's sim time, if any.
    /// </summary>
    public double? Tick()
    {
        var clock = Snapshot.Clock;
        double? now = clock.HasTime ? _frozenNow ?? clock.SecondsAt(_time.GetUtcNow()) : _timetable?.Timetable.Header.StartTime.Seconds;
        NowSeconds = now;
        if (now is { } seconds)
        {
            Navigator.Tick(seconds);
        }

        ClockText = StatusText.Clock(clock, new SimTime((int)Math.Floor(now ?? 0)));
        SpeedText = _frozenNow is not null ? "paused" : StatusText.Speed(clock);
        IsFollowing = Navigator.IsFollowing;
        Transport?.Tick();
        if (IsRecording)
        {
            UpdateRecording();
        }

        return now;
    }

    /// <summary>The tooltip for a path under the cursor.</summary>
    public HoverInfo? Describe(GraphHit hit) => Scene is { } scene ? GraphHover.Describe(hit, scene, Snapshot) : null;

    /// <summary>A click on the graph: select the train hit, or clear the selection on the background.</summary>
    public void SelectPath(GraphHit? hit)
    {
        if (hit is null)
        {
            SetSelection(null, null);
            return;
        }

        var path = hit.Path;
        var uid = path.LiveUid ?? (path.Train is { } id ? Snapshot.FindByTrain(id)?.Train.SimUid : null);
        SetSelection(path.Train, uid);
    }

    /// <summary>Double-click on a train in the list: show where it was last reported.</summary>
    public void CentreOnSelectedTrain()
    {
        if (Trains.Selected is { } row)
        {
            Navigator.CentreOn(row.LastSeen.Seconds);
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var dialog = new ConnectViewModel(_dialogs)
        {
            Host = _settings.Host,
            Port = _settings.Port.ToString(CultureInfo.InvariantCulture),
            UserName = _settings.UserName ?? string.Empty,
            Password = _settingsStore.PasswordOf(_settings) ?? string.Empty,
            RememberPassword = _settings.RememberPassword,
            SubscribeTd = _settings.SubscribeTd,
            TimetablePath = _timetable?.Path ?? _settings.LastTimetablePath ?? string.Empty,
        };
        if (!_dialogs.ShowConnect(dialog) || dialog.Error is not null)
        {
            return;
        }

        var options = dialog.ToOptions();
        _settings = _settingsStore.WithPassword(
            _settings with { Host = options.Host, Port = options.Port, UserName = options.Login, SubscribeTd = dialog.SubscribeTd },
            dialog.Password,
            dialog.RememberPassword);
        _settingsStore.Save(_settings);

        await StopSessionAsync();
        if (dialog.TimetablePath.Length > 0 && !string.Equals(dialog.TimetablePath, _timetable?.Path, StringComparison.OrdinalIgnoreCase))
        {
            await LoadTimetableAsync(dialog.TimetablePath);
        }

        StartSession(SimSession.StartLive(options, _engine, _recorder, _loggerFactory));
        LogConnecting(options);
    }

    [RelayCommand]
    private async Task OpenReplayAsync()
    {
        var dialog = new ReplayViewModel(_dialogs)
        {
            RecordingPath = _settings.LastReplayPath ?? string.Empty,
            Speed = ReplaySpeedOption.Nearest(_settings.ReplaySpeed <= 0 ? double.PositiveInfinity : _settings.ReplaySpeed),
            TimetablePath = _timetable?.Path ?? _settings.LastTimetablePath ?? string.Empty,
        };
        if (!_dialogs.ShowReplay(dialog) || dialog.Error is not null)
        {
            return;
        }

        await OpenReplayAsync(dialog.RecordingPath, dialog.TimetablePath.Length > 0 ? dialog.TimetablePath : null, dialog.Speed.Speed, dialog.StartTime);
    }

    /// <summary>Replay a recording, loading a timetable first if given.</summary>
    public async Task OpenReplayAsync(string recordingPath, string? timetablePath, double speed, SimTime? startAt)
    {
        _settings = _settings with { LastReplayPath = recordingPath, ReplaySpeed = double.IsPositiveInfinity(speed) ? 0 : speed };
        _settingsStore.Save(_settings);
        await StopSessionAsync();

        ShowInfo(InfoSeverity.Info, $"Loading {Path.GetFileName(recordingPath)}…");
        SslogRecording recording;
        try
        {
            recording = await Task.Run(() => SslogRecording.Load(recordingPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot open the recording: {ex.Message}");
            return;
        }

        HideInfo();
        if (timetablePath is not null && !string.Equals(timetablePath, _timetable?.Path, StringComparison.OrdinalIgnoreCase))
        {
            await LoadTimetableAsync(timetablePath);
        }

        _recording = recording;
        _recordingName = Path.GetFileName(recordingPath);
        _replayOptions = new ReplayOptions { Speed = speed, StartAt = startAt };
        StartReplay();
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task DisconnectAsync()
    {
        await StopSessionAsync();
        UpdateConnection();
    }

    [RelayCommand(CanExecute = nameof(IsLive))]
    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            ShowInfo(InfoSeverity.Info, $"Recording saved to {_recorder.Path} ({_recorder.MessagesWritten} messages).");
            return;
        }

        if (_session is not { Kind: SessionKind.Live, Options: { } options })
        {
            return;
        }

        var path = SessionRecorder.NewPath(_paths.Recordings, Snapshot.AreaId, _time.GetUtcNow());
        try
        {
            _recorder.Start(path, options.Host, options.Port, options.Topics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot start recording: {ex.Message}");
            return;
        }

        // Ask for the clock straight away, so the recording starts with one rather than at the next sim minute.
        _ = _session.SendAsync(GatewayCommands.IdRequest());
    }

    [RelayCommand]
    private async Task LoadTimetableAsync()
    {
        var start = _timetable?.Path ?? _settings.TimetableFor(Snapshot.AreaId) ?? _settings.TimetableFolder ?? WttCatalogue.DefaultFolder;
        if (_dialogs.PickFile("Choose the sim's timetable", FileFilters.Timetable, start) is { } path)
        {
            await LoadTimetableAsync(path);
        }
    }

    [RelayCommand]
    private async Task LoadSuggestionAsync()
    {
        if (SelectedSuggestion is { } suggestion)
        {
            await LoadTimetableAsync(suggestion.Path);
        }
    }

    [RelayCommand]
    private void DismissInfo() => HideInfo();

    [RelayCommand]
    private void ShowWarnings()
    {
        if (_timetable is { } loaded)
        {
            var lines = loaded.Import.Warnings.Select(w => w.ToString()).ToList();
            _dialogs.ShowList("Timetable import warnings", $"{Path.GetFileName(loaded.Path)}: {lines.Count} warnings", lines);
        }
    }

    [RelayCommand]
    private void Follow() => Navigator.Follow();

    [RelayCommand]
    private void ToggleFollow() => Navigator.ToggleFollow();

    /// <summary>A zoom preset; the parameter is the hours across (1, 3, 6).</summary>
    [RelayCommand]
    private void Zoom(string hours)
    {
        if (double.TryParse(hours, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0)
        {
            Navigator.ShowSpan(h * SimTime.SecondsPerHour);
        }
    }

    [RelayCommand]
    private void ShowFullCorridor() => Navigator.ShowFullCorridor();

    [RelayCommand]
    private void ClearSelection() => SetSelection(null, null);

    /// <summary>
    /// Save the graph as shown (GR-10, Fred: the current view) to a PNG the user picks: the same time and corridor
    /// range, orientation, colours and selection, at the graph's size times the parameter (1 or 2).
    /// </summary>
    [RelayCommand]
    private void ExportPng(string? scale)
    {
        if (Scene is not { } scene)
        {
            ShowInfo(InfoSeverity.Warning, "There is no graph to export yet.");
            return;
        }

        var factor = double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, 4) : 1;
        var now = NowSeconds is { } seconds ? new SimTime((int)Math.Floor(seconds)) : scene.Now;
        var folder = _settings.LastExportFolder is { Length: > 0 } last && Directory.Exists(last) ? last : _paths.Exports;
        if (_dialogs.PickSaveFile("Export the graph as PNG", FileFilters.Png, Path.Combine(folder, ExportName(scene, now))) is not { } path)
        {
            return;
        }

        try
        {
            byte[] png;
            using (var renderer = new SkiaGraphRenderer())
            {
                png = renderer.RenderPng(scene with { Now = now }, Navigator.Viewport, Selection, (float)factor);
            }

            File.WriteAllBytes(path, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot export the graph: {ex.Message}");
            return;
        }

        _settings = _settings with { LastExportFolder = Path.GetDirectoryName(path) };
        _settingsStore.Save(_settings);
        ShowInfo(InfoSeverity.Info, $"Exported the graph to {path}.");
    }

    /// <summary>A file name for an export, e.g. <c>waterloo-main-0700.png</c>.</summary>
    private string ExportName(GraphScene scene, SimTime? now)
    {
        var area = Profile?.AreaId is { Length: > 0 } a ? a : "graph";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. $"{area}-{scene.CorridorId}".Select(c => invalid.Contains(c) || c == ' ' ? '_' : c)]);
        if (now is not { } t)
        {
            return safe + ".png";
        }

        var day = ((t.Seconds % SimTime.SecondsPerDay) + SimTime.SecondsPerDay) % SimTime.SecondsPerDay;
        return string.Create(CultureInfo.InvariantCulture, $"{safe}-{day / SimTime.SecondsPerHour:00}{day % SimTime.SecondsPerHour / SimTime.SecondsPerMinute:00}.png");
    }

    private void SaveViewSettings()
    {
        var byDelay = ColourMode == GraphColourMode.Delay;
        if (_settings.ColourByDelay != byDelay || _settings.VerticalGraph != IsVertical)
        {
            _settings = _settings with { ColourByDelay = byDelay, VerticalGraph = IsVertical };
            _settingsStore.Save(_settings);
        }
    }

    /// <summary>Load a timetable (and its profile) and use it from now on.</summary>
    public async Task LoadTimetableAsync(string path)
    {
        ShowInfo(InfoSeverity.Info, $"Loading {Path.GetFileName(path)}…");
        LoadedTimetable loaded;
        try
        {
            loaded = await Task.Run(() => _loader.Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot load the timetable: {ex.Message}");
            return;
        }

        _timetable = loaded;
        _engine.SetTimetable(loaded.Timetable);
        var area = loaded.Timetable.AreaId;
        if (area.Length > 0)
        {
            _settings = _settings.WithTimetable(area, path);
            _settingsStore.Save(_settings);
            _suggestedFor = area;
        }

        var previous = SelectedCorridor?.Id;
        Corridors.Clear();
        foreach (var corridor in loaded.Profile.Corridors)
        {
            Corridors.Add(new CorridorTab(corridor));
        }

        SelectedCorridor = Corridors.FirstOrDefault(c => c.Id == previous) ?? Corridors.FirstOrDefault();
        var warnings = loaded.Import.Warnings.Count;
        var message = $"Loaded {(loaded.Timetable.Header.Name.Length > 0 ? loaded.Timetable.Header.Name : Path.GetFileName(path))}: {loaded.Timetable.Trains.Count} trains, {Corridors.Count} corridors"
            + (warnings > 0 ? $", {warnings} import warnings." : ".")
            + (loaded.ProfileNote is { } note ? " " + note : string.Empty);
        ShowInfo(loaded.ProfileNote is null ? InfoSeverity.Info : InfoSeverity.Warning, message, hasWarnings: warnings > 0);
        _mismatchWarnedFor = null;
        CheckTimetableMatchesSim();
        UpdateTitle();
        UpdatePlaceholder();
        RequestScene();
    }

    public async ValueTask DisposeAsync()
    {
        _engine.SnapshotPublished -= OnSnapshotPublished;
        await StopSessionAsync();
        _recorder.Stop();
    }

    private void StartReplay()
    {
        if (_recording is null || _replayOptions is null)
        {
            return;
        }

        var session = SimSession.StartReplay(_recording, _recordingName ?? "recording", _replayOptions, _engine, _loggerFactory);
        StartSession(session);
        Transport = new TransportViewModel(session, _replayOptions.Speed, RestartReplayAt);
    }

    private async void RestartReplayAt(SimTime target)
    {
        try
        {
            var speed = Transport?.Speed.Speed ?? _replayOptions?.Speed ?? 1;
            await StopSessionAsync();
            _replayOptions = (_replayOptions ?? new ReplayOptions()) with { StartAt = target, Speed = speed };
            StartReplay();
        }
#pragma warning disable CA1031 // An async void handler must not throw onto the dispatcher.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ShowInfo(InfoSeverity.Error, $"Cannot restart the replay: {ex.Message}");
        }
    }

    private void StartSession(SimSession session)
    {
        _session = session;
        _frozenNow = null;
        _suggestedFor = _timetable?.Timetable.AreaId is { Length: > 0 } area ? area : null;
        _mismatchWarnedFor = null;
        session.StatusChanged += OnSessionStatusChanged;
        HasSession = true;
        IsLive = session.Kind == SessionKind.Live;
        Navigator.Follow();
        UpdateConnection();
        UpdatePlaceholder();
    }

    private async Task StopSessionAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        session.StatusChanged -= OnSessionStatusChanged;
        if (session.Kind == SessionKind.Live)
        {
            _recorder.Stop();
        }

        await session.DisposeAsync();
        Transport = null;
        HasSession = false;
        IsLive = false;
        _frozenNow = null;
        UpdateConnection();
    }

    private void OnSessionStatusChanged(object? sender, SessionStatus status) => _ui.Post(() =>
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        _frozenNow = status.State == SessionState.Paused ? Snapshot.Clock.SecondsAt(_time.GetUtcNow()) : null;
        if (!status.IsActive)
        {
            if (_session?.Kind == SessionKind.Live)
            {
                _recorder.Stop();
            }

            IsLive = false;
        }

        if (status.State == SessionState.Failed)
        {
            ShowInfo(InfoSeverity.Error, status.Detail ?? "The session failed.");
        }

        Transport?.Tick();
        UpdateConnection();
    });

    private void OnSnapshotPublished(object? sender, PlanSnapshot snapshot)
    {
        Volatile.Write(ref _latestSnapshot, snapshot);
        if (Interlocked.Exchange(ref _snapshotQueued, 1) == 0)
        {
            _ui.Post(ApplySnapshot);
        }
    }

    private void ApplySnapshot()
    {
        Volatile.Write(ref _snapshotQueued, 0);
        if (Volatile.Read(ref _latestSnapshot) is not { } snapshot || snapshot.Sequence <= Snapshot.Sequence)
        {
            return;
        }

        Snapshot = snapshot;
        Trains.Update(snapshot, Profile);
        TrainsText = snapshot.Trains.Count == 0 ? string.Empty : StatusText.Trains(snapshot.Matches);
        if (snapshot.AreaId is { Length: > 0 } area)
        {
            if (_timetable is null && !string.Equals(_suggestedFor, area, StringComparison.OrdinalIgnoreCase))
            {
                _ = SuggestTimetablesAsync(area);
            }

            CheckTimetableMatchesSim();
        }

        UpdateTitle();
        RequestScene();
    }

    private void CheckTimetableMatchesSim()
    {
        if (_timetable?.Timetable.AreaId is not { Length: > 0 } tt || Snapshot.AreaId is not { Length: > 0 } sim ||
            string.Equals(tt, sim, StringComparison.OrdinalIgnoreCase) || string.Equals(_mismatchWarnedFor, sim, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _mismatchWarnedFor = sim;
        _ = SuggestTimetablesAsync(sim, $"The timetable is for {tt}, but the sim is {sim}.");
    }

    private async Task SuggestTimetablesAsync(string area, string? problem = null)
    {
        _suggestedFor = area;
        var folder = _settings.TimetableFolder ?? WttCatalogue.DefaultFolder;
        var remembered = _settings.TimetableFor(area);
        var found = await Task.Run(() =>
        {
            var list = new List<WttFileInfo>();
            if (remembered is not null && WttCatalogue.ReadInfo(remembered) is { } info && string.Equals(info.AreaId, area, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(info);
            }

            list.AddRange(WttCatalogue.FindForArea(folder, area).Where(f => !string.Equals(f.Path, remembered, StringComparison.OrdinalIgnoreCase)));
            return list;
        });

        if (_timetable is not null && string.Equals(_timetable.Timetable.AreaId, area, StringComparison.OrdinalIgnoreCase))
        {
            return; // loaded meanwhile
        }

        Suggestions.Clear();
        foreach (var f in found)
        {
            Suggestions.Add(f);
        }

        SelectedSuggestion = Suggestions.FirstOrDefault();
        var lead = problem ?? $"No timetable is loaded for {area}.";
        if (found.Count > 0)
        {
            ShowInfo(problem is null ? InfoSeverity.Info : InfoSeverity.Warning, $"{lead} Choose one to load:", suggestions: true);
        }
        else
        {
            ShowInfo(InfoSeverity.Warning, $"{lead} None was found in {(folder.Length > 0 ? folder : "the SimSig timetable folder")}; use Browse… to choose one.");
        }
    }

    private void RequestScene()
    {
        if (_building)
        {
            _sceneDirty = true;
            return;
        }

        if (Profile is not { } profile || SelectedCorridor is not { } tab)
        {
            Scene = null;
            UpdatePlaceholder();
            return;
        }

        var request = GraphSceneRequest.For(profile, tab.Corridor, Snapshot) with
        {
            Timetable = _timetable?.Timetable,
            ShowWtt = ShowWtt,
            ShowActual = ShowActual,
            ColourMode = ColourMode,
        };
        _building = true;
        _sceneDirty = false;
        _ = Task.Run(() => GraphSceneBuilder.Build(request)).ContinueWith(
            t => _ui.Post(() =>
            {
                _building = false;
                if (t.IsCompletedSuccessfully)
                {
                    Scene = t.Result;
                }
                else if (t.Exception is { } ex)
                {
                    LogSceneFailed(ex.GetBaseException());
                }

                UpdatePlaceholder();
                if (_sceneDirty)
                {
                    RequestScene();
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void OnTrainRowSelected(TrainRowViewModel? row)
    {
        if (_syncingSelection)
        {
            return;
        }

        SetSelection(row?.Train, row?.Uid);
    }

    private void SetSelection(TrainId? train, string? uid)
    {
        Selection = train is null && uid is null ? null : new GraphSelection(train, uid);
        _syncingSelection = true;
        try
        {
            Trains.Selected = uid is null ? null : Trains.Find(uid);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void UpdateConnection()
    {
        var (text, light) = StatusText.Connection(_session?.Kind, _session?.Status, _session?.Description);
        ConnectionText = text;
        ConnectionLight = light;
    }

    private void UpdateRecording()
    {
        IsRecording = _recorder.IsRecording;
        RecordingText = IsRecording ? $"Recording {Path.GetFileName(_recorder.Path)} ({_recorder.MessagesWritten:N0})" : string.Empty;
    }

    private void UpdateTitle()
    {
        SimName = Profile?.Name is { Length: > 0 } name ? name : Snapshot.AreaId ?? string.Empty;
        Title = SimName.Length > 0 ? $"Stringline — {SimName}" : "Stringline";
    }

    private void UpdatePlaceholder() => Placeholder =
        Profile is null ? (_session is null ? "Connect to SimSig or open a replay to begin (Session menu)." : "Load the sim's timetable to draw the graph.")
        : Corridors.Count == 0 ? "This timetable gave no corridors to draw."
        : null;

    private void ShowInfo(InfoSeverity severity, string message, bool suggestions = false, bool hasWarnings = false)
    {
        InfoSeverity = severity;
        InfoMessage = message;
        InfoHasSuggestions = suggestions;
        InfoHasWarnings = hasWarnings;
    }

    private void HideInfo()
    {
        InfoMessage = null;
        InfoHasSuggestions = false;
        InfoHasWarnings = false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to {Options}")]
    private partial void LogConnecting(GatewayClientOptions options);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Building the graph scene failed")]
    private partial void LogSceneFailed(Exception ex);
}
