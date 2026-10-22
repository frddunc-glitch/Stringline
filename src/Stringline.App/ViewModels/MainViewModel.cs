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
using Stringline.Persistence;
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
    private readonly AppPaths _defaultPaths;
    private readonly HelpGuide _guide;
    private readonly AppLogging? _logging;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private AppSettings _settings;
    private AppPaths _paths;
    private SimSession? _session;
    private SslogRecording? _recording;
    private string? _recordingName;
    private ReplayOptions? _replayOptions;
    private LoadedTimetable? _timetable;
    private PlanSnapshot? _latestSnapshot;
    private int _snapshotQueued;
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
        ILoggerFactory loggerFactory,
        AppLogging? logging = null)
    {
        _engine = engine;
        _recorder = recorder;
        _loader = loader;
        _settingsStore = settingsStore;
        _dialogs = dialogs;
        _ui = ui;
        _defaultPaths = paths;
        _guide = new HelpGuide(paths.Help);
        _paths = paths;
        _logging = logging;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MainViewModel>();
        InitEditing();
        InitBoards();
        InitConflicts();
        InitSessions();
        _settings = settingsStore.Load();
        Layout = new LayoutViewModel(_settings.Layout);
        InitSplit();
        _paths = paths.WithFolders(_settings);
        InitSuggestions();
        InitReports();
        InitAssist();
        Snapshot = engine.Current;
        // Read them all first: setting any saves the view settings, which would overwrite the others.
        var (byDelay, vertical, knockOn, signals) = (_settings.ColourByDelay, _settings.VerticalGraph, _settings.ForecastKnockOn, _settings.SignalDisplay);
        IsVertical = vertical;
        ColourMode = byDelay ? GraphColourMode.Delay : GraphColourMode.Category;
        ForecastKnockOn = knockOn;
        SignalDisplay = signals;
        ClockSize = _settings.ClockSize;
        engine.SetForecast(options: _settings.Planning.ToForecastOptions(knockOn), conflicts: _settings.Planning.ToConflictOptions());
        Trains.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrainListViewModel.Selected) && !Trains.IsUpdating)
            {
                OnTrainRowSelected(Trains.Selected);
            }
        };
        engine.SnapshotPublished += OnSnapshotPublished;
        recorder.StateChanged += (_, _) => _ui.Post(UpdateRecording);
        UpdateConnection();
        UpdatePlaceholder();
    }

    /// <summary>The main graph's navigator; the other boards are locked to it in time (S21).</summary>
    public GraphNavigator Navigator => MainBoard.Navigator;

    /// <summary>The window's panels and splitters (#9).</summary>
    public LayoutViewModel Layout { get; }

    public TrainListViewModel Trains { get; } = new();

    public ObservableCollection<CorridorTab> Corridors { get; } = [];

    /// <summary>Timetables offered in the info bar when none is loaded for the sim.</summary>
    public ObservableCollection<WttFileInfo> Suggestions { get; } = [];

    public StringlineProfile? Profile => _timetable?.Profile;

    public LoadedTimetable? LoadedTimetable => _timetable;

    public SimSession? Session => _session;

    [ObservableProperty]
    public partial PlanSnapshot Snapshot { get; private set; }

    /// <summary>The main graph's scene (each board has its own, S21).</summary>
    public GraphScene? Scene => MainBoard.Scene;

    [ObservableProperty]
    public partial GraphSelection? Selection { get; private set; }

    [ObservableProperty]
    public partial CorridorTab? SelectedCorridor { get; set; }

    [ObservableProperty]
    public partial bool ShowWtt { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowActual { get; set; } = true;

    /// <summary>Draw trains SimSig will not run (S34) faintly on the WTT layer, rather than hide them. Not remembered.</summary>
    [ObservableProperty]
    public partial bool ShowNotRunning { get; set; } = true;

    /// <summary>Show the forecast layer (dash-dot lines on from each running train). Not remembered, like the other layers.</summary>
    [ObservableProperty]
    public partial bool ShowForecast { get; set; } = true;

    /// <summary>
    /// Show each running train's live head (S36): its actual line carried on from its last report to now. On for a live
    /// session, off while reviewing (it can be ticked). Not remembered.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowLiveHead { get; set; } = true;

    /// <summary>How placed signals show on the location axis (View ▸ Signals: off, ticks or rows; S38). Remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignalsOff), nameof(IsSignalTicks), nameof(IsSignalRows))]
    public partial SignalDisplay SignalDisplay { get; set; } = SignalDisplay.Ticks;

    public bool IsSignalsOff => SignalDisplay == SignalDisplay.Off;

    public bool IsSignalTicks => SignalDisplay == SignalDisplay.Ticks;

    public bool IsSignalRows => SignalDisplay == SignalDisplay.Rows;

    /// <summary>View ▸ Signals ▸ Off, Ticks or Rows (the parameter is the <see cref="Graph.SignalDisplay"/> name).</summary>
    [RelayCommand]
    private void SetSignalDisplay(string display) => SignalDisplay = Enum.Parse<SignalDisplay>(display);

    /// <summary>The forecast includes knock-on delay from other trains (headways, single lines). Remembered.</summary>
    [ObservableProperty]
    public partial bool ForecastKnockOn { get; set; } = true;

    /// <summary>Colour actual lines (and the train list) by category or by delay band (GR-7). Remembered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColourByDelay))]
    public partial GraphColourMode ColourMode { get; set; }

    /// <summary><see cref="ColourMode"/> as a check box (View ▸ Colour by delay, and the toolbar's By delay).</summary>
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

    /// <summary>"Sim – timetable" for the status bar (#6); empty until either is known.</summary>
    [ObservableProperty]
    public partial string SimTitleText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ClockText { get; private set; } = "Sim —";

    [ObservableProperty]
    public partial string SpeedText { get; private set; } = string.Empty;

    /// <summary>The toolbar's sim clock (#13): "09:14:32", or a dash before the first clock.</summary>
    [ObservableProperty]
    public partial string SimClockText { get; private set; } = StatusText.NoTime;

    /// <summary>How big the toolbar's clock is, from the settings (#13).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockFontSize), nameof(SpeedFontSize))]
    public partial ClockSize ClockSize { get; private set; }

    /// <summary>The toolbar clock's font size in pixels for <see cref="ClockSize"/>.</summary>
    public double ClockFontSize => StatusText.ClockFontSize(ClockSize);

    /// <summary>The speed beside the toolbar clock: smaller, but never below the toolbar's own text.</summary>
    public double SpeedFontSize => Math.Max(12, Math.Round(ClockFontSize * 0.6));

    [ObservableProperty]
    public partial string TrainsText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand), nameof(ToggleAssistCommand))]
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
        MainBoard.Corridor = value;
        UpdateSuggestionPreview();
        if (KeepsViewOnTabChange(value))
        {
            if (value is not null)
            {
                RequestScene(MainBoard);
            }

            return;
        }

        Navigator.ShowFullCorridor();
        RequestScene(MainBoard);
    }

    partial void OnShowWttChanged(bool value) => RequestScene();

    partial void OnShowActualChanged(bool value) => RequestScene();

    partial void OnShowNotRunningChanged(bool value) => RequestScene();

    partial void OnShowForecastChanged(bool value) => RequestScene();

    partial void OnShowLiveHeadChanged(bool value) => RequestScene();

    partial void OnSignalDisplayChanged(SignalDisplay value)
    {
        SaveViewSettings();
        RequestScene();
    }

    partial void OnForecastKnockOnChanged(bool value)
    {
        _engine.SetForecast(options: _settings.Planning.ToForecastOptions(value));
        SaveViewSettings();
    }

    partial void OnColourModeChanged(GraphColourMode value)
    {
        Trains.ColourMode = value;
        Trains.Update(Snapshot, Profile);
        SaveViewSettings();
        RequestScene();
    }

    partial void OnIsVerticalChanged(bool value)
    {
        foreach (var board in Boards)
        {
            board.Navigator.SetOrientation(value ? GraphOrientation.Vertical : GraphOrientation.Horizontal);
        }

        SaveViewSettings();
    }

    /// <summary>
    /// Called a few times a second on the UI thread: moves the clock, the now line, follow mode and the replay bar on.
    /// Returns the now line's sim time, if any.
    /// </summary>
    public double? Tick()
    {
        var clock = Snapshot.Clock;
        // With no session (disconnected, or resumed and not yet connected) the sim clock stands at its last report.
        double? now = clock.HasTime ? _frozenNow ?? (_session is null ? clock.LastReported.Seconds : clock.SecondsAt(_time.GetUtcNow())) : _timetable?.Timetable.Header.StartTime.Seconds;
        NowSeconds = now;
        if (now is { } seconds)
        {
            // Locked in time (S21): ticking one moves them all, so the rest find nothing to do.
            foreach (var board in Boards)
            {
                board.Navigator.Tick(seconds);
            }
        }

        foreach (var board in Boards)
        {
            board.MoveNow(now);
        }

        ClockText = StatusText.Clock(clock, new SimTime((int)Math.Floor(now ?? 0)));
        SimClockText = StatusText.ClockTime(clock, new SimTime((int)Math.Floor(now ?? 0)));
        SpeedText = _frozenNow is not null ? "paused" : StatusText.Speed(clock);
        IsFollowing = Navigator.IsFollowing;
        Transport?.Tick();
        TickStore();
        UpdateDelays();
        if (IsRecording)
        {
            UpdateRecording();
        }

        return now;
    }

    /// <summary>The tooltip for a path under the cursor.</summary>
    public HoverInfo? Describe(GraphHit hit) => Scene is { } scene ? GraphHover.Describe(hit, scene, Snapshot) : null;

    /// <summary>
    /// The tooltip for what is under the mouse (#12): a line, a conflict (with its top suggestion once worked out), a
    /// block or speed restriction, a note or a label.
    /// </summary>
    public HoverInfo? Describe(GraphTarget target) => Describe(MainBoard, target);

    /// <summary>The tooltip for what is under the mouse on a board.</summary>
    private HoverInfo? Describe(GraphBoardViewModel board, GraphTarget target) =>
        board.Scene is { } scene ? GraphHover.Describe(target, scene, Snapshot, TopSuggestionFor) : null;

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
        // A resumed live session carries on with this connection, or a new one starts (S16).
        var carryOn = false;
        if (IsResumedIdle && Store is { Kind: SavedSessionKind.Live })
        {
            var choice = _dialogs.Choose("Connect to SimSig", "Carry on with the resumed session, or start a new one?", ["Carry on", "Start a new session", "Cancel"]);
            if (choice is null or 2)
            {
                return;
            }

            carryOn = choice == 0;
        }

        var dialog = new ConnectViewModel(_dialogs)
        {
            Host = _settings.Host,
            Port = _settings.Port.ToString(CultureInfo.InvariantCulture),
            UserName = _settings.UserName ?? string.Empty,
            Password = _settingsStore.PasswordOf(_settings) ?? string.Empty,
            RememberPassword = _settings.RememberPassword,
            HasSavedPassword = AppSettingsStore.HasPassword(_settings),
            ForgetSavedPassword = ForgetSavedPassword,
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

        if (carryOn)
        {
            await StopSessionAsync();
        }
        else
        {
            await LeaveSessionAsync();
        }

        if (dialog.TimetablePath.Length > 0 && !string.Equals(dialog.TimetablePath, _timetable?.Path, StringComparison.OrdinalIgnoreCase))
        {
            await LoadTimetableAsync(dialog.TimetablePath);
        }

        if (!carryOn)
        {
            StartStore(new SessionInfo { Kind = SavedSessionKind.Live, Host = options.Host, Port = options.Port });
            StartLearning(Store is { } store ? SignalLearning.SessionId(store.Info.Created) : null);
        }

        IsResumedIdle = false;
        StartSession(SimSession.StartLive(options, _engine, _recorder, _loggerFactory, Store, fresh: !carryOn, observe: LearnerFeed));
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
        await LeaveSessionAsync();

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
        StartStore(new SessionInfo { Kind = SavedSessionKind.Replay, RecordingPath = Path.GetFullPath(recordingPath) });
        StartLearning(RecordingLearningId(recording));
        StartReplay();
    }

    [RelayCommand(CanExecute = nameof(HasSession))]
    private async Task DisconnectAsync()
    {
        LeaveReview();
        await StopSessionAsync();
        CloseStore(ended: true);
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

    /// <summary>Corridor ▸ Whole corridor: on the board last used (S21).</summary>
    [RelayCommand]
    private void ShowFullCorridor() => ActiveBoard.Navigator.ShowFullCorridor();

    /// <summary>Show a corridor by its number in tab order (Ctrl+1 to Ctrl+9); a number past the last does nothing.</summary>
    [RelayCommand]
    private void SelectCorridor(string? number)
    {
        if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Corridors.Count)
        {
            SelectedCorridor = Corridors[n - 1];
        }
    }

    /// <summary>
    /// The Corridor menu's list (S25): every corridor in tab order, the one shown ticked, the first nine numbered as
    /// their access keys with Ctrl+1 to Ctrl+9 beside them. <paramref name="only"/> limits it to those tabs (the
    /// toolbar's » list of tabs that do not fit).
    /// </summary>
    public IReadOnlyList<PlanMenuItem> CorridorMenuItems(IReadOnlyCollection<CorridorTab>? only = null) =>
    [
        .. Corridors.Select((tab, i) => (Tab: tab, Number: i + 1))
            .Where(c => only is null || only.Contains(c.Tab))
            .Select(c => new PlanMenuItem(
                c.Number <= 9 ? $"_{c.Number} {MenuText.Escape(c.Tab.Name)}" : MenuText.Escape(c.Tab.Name),
                new RelayCommand(() => SelectedCorridor = c.Tab),
                c.Number <= 9 ? $"Ctrl+{c.Number}" : null,
                c.Tab == SelectedCorridor)),
    ];

    [RelayCommand]
    private void ClearSelection() => SetSelection(null, null);

    /// <summary>
    /// Save the graph as shown (GR-10, Fred: the current view) to a PNG the user picks: the same time and corridor
    /// range, orientation, colours and selection, at the graph's size times the parameter (1 or 2). The graph is the
    /// board last used (S21).
    /// </summary>
    [RelayCommand]
    private void ExportPng(string? scale)
    {
        if (ActiveBoard.Scene is not { } scene)
        {
            ShowInfo(InfoSeverity.Warning, "There is no graph to export yet.");
            return;
        }

        var factor = double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 1, 4) : 1;
        var now = NowSeconds is { } seconds ? new SimTime((int)Math.Floor(seconds)) : scene.Now;
        var viewport = ActiveBoard.Navigator.Viewport;
        var selection = Selection;
        SaveExport("Export the graph as PNG", FileFilters.Png, ExportName(scene.CorridorId, now, ".png"), "the graph", path =>
        {
            using var renderer = new SkiaGraphRenderer();
            File.WriteAllBytes(path, renderer.RenderPng(scene with { Now = now }, viewport, selection, (float)factor));
        });
    }

    /// <summary>Remember the window's layout (<see cref="Layout"/>) for next time; the window calls this as it closes.</summary>
    public void SaveLayout()
    {
        var layout = BoardsLayout(Layout.Current);
        if (_settings.Layout != layout)
        {
            _settings = _settings with { Layout = layout };
            _settingsStore.Save(_settings);
        }
    }

    private void SaveViewSettings()
    {
        var byDelay = ColourMode == GraphColourMode.Delay;
        if (_settings.ColourByDelay != byDelay || _settings.VerticalGraph != IsVertical || _settings.ForecastKnockOn != ForecastKnockOn || _settings.SignalDisplay != SignalDisplay)
        {
            _settings = _settings with { ColourByDelay = byDelay, VerticalGraph = IsVertical, ForecastKnockOn = ForecastKnockOn, SignalDisplay = SignalDisplay };
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
            var simId = _settings.SimIdFor(path);
            loaded = await Task.Run(() => _loader.Load(path, simId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot load the timetable: {ex.Message}");
            return;
        }

        loaded = loaded with { Profile = WithGlobalHeadway(loaded.Profile) };
        _timetable = loaded;
        HasTimetable = true;
        _engine.SetTimetable(loaded.Timetable);
        Store?.SetTimetable(loaded.Path, loaded.Timetable.Header.Name, loaded.Timetable.AreaId, loaded.Profile.SimName(loaded.Timetable.Header), loaded.Timetable.Header.TimetableVersion);
        _engine.SetForecast(await Task.Run(() => ForecastNetwork.For(loaded.Profile, loaded.Timetable, _settings.LearnMinSamples)));
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
        RefreshBoardCorridors();
        Regulation.SetLocations(loaded.Profile);
        var warnings = loaded.Import.Warnings.Count;
        var message = $"Loaded {(loaded.Timetable.Header.Name.Length > 0 ? loaded.Timetable.Header.Name : Path.GetFileName(path))}: {loaded.Timetable.Trains.Count} trains, {Corridors.Count} corridors"
            + (warnings > 0 ? $", {warnings} import warnings." : ".")
            + (loaded.ProfileNote is { } note ? " " + note : string.Empty);
        ShowInfo(loaded.ProfileNote is null ? InfoSeverity.Info : InfoSeverity.Warning, message, hasWarnings: warnings > 0);
        _mismatchWarnedFor = null;
        CheckTimetableMatchesSim();
        UpdateTitle();
        UpdatePlaceholder();
        UpdateEditing();
        RequestScene();
    }

    public async ValueTask DisposeAsync()
    {
        _engine.SnapshotPublished -= OnSnapshotPublished;
        // Still open (Windows ending the session, or a test): kept, and offered back at the next start.
        Draft?.AutosaveNow(open: true);
        await StopSessionAsync();
        CloseStore(ended: false); // left running: offered for resuming at the next start
        _recorder.Stop();
    }

    /// <param name="fresh">False to keep the plan (restarting the same replay from another time).</param>
    private void StartReplay(bool fresh = true)
    {
        if (_recording is null || _replayOptions is null)
        {
            return;
        }

        if (!fresh)
        {
            RestartLearning();
        }

        var session = SimSession.StartReplay(_recording, _recordingName ?? "recording", _replayOptions, _engine, _loggerFactory, fresh, LearnerFeed);
        StartSession(session);
        Transport = new TransportViewModel(session, _replayOptions.Speed, RestartReplayAt);
    }

    private async void RestartReplayAt(SimTime target)
    {
        try
        {
            var speed = Transport?.Speed.Speed ?? _replayOptions?.Speed ?? 1;
            await StopSessionAsync();
            _replayOptions = (_replayOptions ?? new ReplayOptions()) with { StartAt = target, Speed = speed, StartPaused = false };
            _reviewApplied = -1;
            StartReplay(fresh: false);
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
        EndAssist();
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

    /// <summary>Stop the running source, if any, then learn from the session so far (S37).</summary>
    private async Task StopSessionAsync()
    {
        await StopSourceAsync();
        await LearnNowAsync();
    }

    private async Task StopSourceAsync()
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
            _ = LearnQuietlyAsync();
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
        UpdateReview(snapshot);
        UpdateEditing();
        UpdateConflicts();
        UpdateSuggestions();
        Regulation.Update(snapshot);
        UpdateDelays();
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

    /// <summary>Build the scene again for every board.</summary>
    private void RequestScene()
    {
        foreach (var board in Boards)
        {
            RequestScene(board);
        }
    }

    /// <summary>Build a board's scene off the UI thread; one asked for while another builds follows it.</summary>
    private void RequestScene(GraphBoardViewModel board)
    {
        if (board.Building)
        {
            board.SceneDirty = true;
            return;
        }

        if (Profile is not { } profile || board.Corridor is not { } tab)
        {
            board.Scene = null;
            UpdatePlaceholder();
            return;
        }

        var request = SceneRequest(profile, tab);
        board.Building = true;
        board.SceneDirty = false;
        var planVersion = Snapshot.PlanVersion;
        _ = Task.Run(() =>
        {
            var scene = GraphSceneBuilder.Build(request);
            return (Scene: scene, LabelRoom: GraphLabels.RoomFor(scene.Locations));
        }).ContinueWith(
            t => _ui.Post(() =>
            {
                board.Building = false;
                if (!Boards.Contains(board))
                {
                    return; // closed meanwhile
                }

                if (t.IsCompletedSuccessfully)
                {
                    board.Navigator.FitLabels(t.Result.LabelRoom);
                    board.Scene = t.Result.Scene;
                    SceneBuiltFor(board, planVersion);
                }
                else if (t.Exception is { } ex)
                {
                    LogSceneFailed(ex.GetBaseException());
                }

                UpdatePlaceholder();
                if (board.SceneDirty)
                {
                    RequestScene(board);
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>What a corridor's scene is built from: the snapshot, the layers shown and the colours.</summary>
    private GraphSceneRequest SceneRequest(StringlineProfile profile, CorridorTab tab) =>
        GraphSceneRequest.For(profile, tab.Corridor, Snapshot) with
        {
            Timetable = _timetable?.Timetable,
            ShowWtt = ShowWtt,
            ShowNotRunning = ShowNotRunning,
            ShowActual = ShowActual,
            ShowForecast = ShowForecast,
            ShowLiveHead = ShowLiveHead,
            Signals = SignalDisplay,
            ShowDisruptions = ShowDisruptions,
            ShowConflicts = ShowConflicts,
            ColourMode = ColourMode,
            SimTitle = CurrentSimTitle(),
        };

    private void OnTrainRowSelected(TrainRowViewModel? row)
    {
        if (_syncingSelection)
        {
            return;
        }

        SetSelection(row?.Train, row is { IsLive: true } ? row.Uid : null);
    }

    /// <param name="conflictId">A conflict picked with the train (S14), drawn stronger; null clears the conflicts list's selection.</param>
    private void SetSelection(TrainId? train, string? uid, string? conflictId = null)
    {
        Selection = train is null && uid is null ? null : new GraphSelection(train, uid) { ConflictId = conflictId };
        if (conflictId is null)
        {
            ConflictList.Select(null);
        }

        UpdateEditing();
        _syncingSelection = true;
        try
        {
            Trains.Selected = uid is not null ? Trains.Find(uid) : train is { } t ? Trains.FindNotRunning(t) : null;
            Delays.Selected = train is { } id ? Delays.Find(id) : null;
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
        var sim = CurrentSimTitle();
        SimTitleText = sim?.ToString() ?? string.Empty;
        Title = sim is null ? "Stringline" + (IsReviewing ? " (review)" : string.Empty) : $"Stringline — {sim}";
    }

    /// <summary>
    /// The sim and timetable as every place shows them (#6): the profile's name (unless it is only a timetable's title,
    /// S31), then the gateway's area ID, then the timetable's; null while none of them is known.
    /// </summary>
    private SimTitle? CurrentSimTitle()
    {
        var header = _timetable?.Timetable.Header;
        var area = Snapshot.AreaId is { Length: > 0 } live ? live
            : Profile?.AreaId is { Length: > 0 } own && own != TimetableLoader.UnnamedSim ? own : null;
        return Profile is { } profile ? profile.Title(area, header, IsReviewing)
            : header is null && area is null ? null : SimTitle.From(null, area, header, IsReviewing);
    }

    private void UpdatePlaceholder()
    {
        Placeholder =
            Profile is null ? (_session is null ? "Connect to SimSig or open a replay to begin (Session menu)." : "Load the sim's timetable to draw the graph.")
            : Corridors.Count == 0 ? "This timetable gave no corridors to draw."
            : null;
        foreach (var board in Boards)
        {
            board.Placeholder = Placeholder ?? (board.Corridor is null ? "Choose a corridor for this board." : null);
        }
    }

    private void ShowInfo(InfoSeverity severity, string message, bool suggestions = false, bool hasWarnings = false)
    {
        InfoSeverity = severity;
        InfoMessage = message;
        InfoHasSuggestions = suggestions;
        InfoHasWarnings = hasWarnings;
        InfoHasResume = false;
    }

    private void HideInfo()
    {
        InfoMessage = null;
        InfoHasSuggestions = false;
        InfoHasWarnings = false;
        InfoHasResume = false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to {Options}")]
    private partial void LogConnecting(GatewayClientOptions options);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Building the graph scene failed")]
    private partial void LogSceneFailed(Exception ex);
}
