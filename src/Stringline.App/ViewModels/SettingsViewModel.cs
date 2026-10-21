using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Services;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// The settings window (S18): forecast and conflicts, suggestions, folders, logging and housekeeping, learning signal
/// places (S37), and display. The values
/// are edited as text and checked together when OK is pressed (<see cref="TryAccept"/>); the window stays open showing
/// what is wrong until they pass, and then <see cref="Result"/> holds the new settings. Connection details stay in the
/// Connect dialog (the Connection tab only forgets a saved password, S30), and the view options in the View menu.
/// </summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private AppSettings _original;
    private readonly IDialogService _dialogs;

    /// <param name="settings">The settings now, with the view's own values (knock-on, cancellations) up to date.</param>
    /// <param name="defaults">The folders used when none is chosen.</param>
    public SettingsViewModel(AppSettings settings, AppPaths defaults, IDialogService dialogs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(defaults);
        _original = settings;
        _dialogs = dialogs;
        Defaults = defaults;
        HasSavedPassword = AppSettingsStore.HasPassword(settings);
        Show(settings);
    }

    /// <summary>Removes the saved password from the settings at once (S30); set by the main view model.</summary>
    public Action? ForgetSavedPassword { get; init; }

    // Connection (S30).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForgetPasswordCommand))]
    [NotifyPropertyChangedFor(nameof(SavedPasswordText))]
    public partial bool HasSavedPassword { get; private set; }

    public string SavedPasswordText => HasSavedPassword
        ? "A SimSig password is saved, encrypted with Windows (DPAPI) so that only your Windows account on this PC can read it."
        : "No SimSig password is saved. Tick \"Remember the password\" when you connect to save one.";

    /// <summary>The folders used when a box is left blank, shown as hints.</summary>
    public AppPaths Defaults { get; }

    public string DefaultTimetableFolder { get; } = Timetable.WttCatalogue.DefaultFolder;

    /// <summary>The settings to use once <see cref="TryAccept"/> has passed; null until then.</summary>
    public AppSettings? Result { get; private set; }

    // Forecast and conflicts.
    [ObservableProperty]
    public partial bool ForecastKnockOn { get; set; }

    /// <summary>Options ▸ Forecast ▸ Apply timetable rules (S32).</summary>
    [ObservableProperty]
    public partial bool ApplyTimetableRules { get; set; }

    [ObservableProperty]
    public partial string DefaultDwell { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DefaultTurnaround { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PossiblyStopped { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PlatformMargin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MinConflict { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DefaultHeadway { get; set; } = string.Empty;

    // Suggestions.
    [ObservableProperty]
    public partial bool AllowCancellation { get; set; }

    [ObservableProperty]
    public partial string PlatformChangePenalty { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LineChangePenalty { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TerminatePenalty { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CancelPenalty { get; set; } = string.Empty;

    // Re-planning (S20).
    [ObservableProperty]
    public partial string ReplanHorizon { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReplanTimeLimit { get; set; } = string.Empty;

    /// <summary>Re-plan with the optimiser (else with the suggestion rules, one conflict at a time).</summary>
    [ObservableProperty]
    public partial bool ReplanWithOptimiser { get; set; }

    [ObservableProperty]
    public partial bool ReplanPlatformChanges { get; set; }

    // Folders.
    [ObservableProperty]
    public partial string TimetableFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SessionsFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RecordingsFolder { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExportsFolder { get; set; } = string.Empty;

    // Logging and housekeeping.
    [ObservableProperty]
    public partial bool DetailedLogging { get; set; }

    [ObservableProperty]
    public partial string LogRetentionDays { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool DeleteOldSessions { get; set; }

    [ObservableProperty]
    public partial string DeleteSessionsAfterDays { get; set; } = string.Empty;

    // Profile (S37).
    [ObservableProperty]
    public partial bool LearnSignalPlaces { get; set; }

    [ObservableProperty]
    public partial string LearnMinSamples { get; set; } = string.Empty;

    // Display.
    /// <summary>The sim clock sizes offered (#13).</summary>
    public IReadOnlyList<ClockSize> ClockSizes { get; } = Enum.GetValues<ClockSize>();

    [ObservableProperty]
    public partial ClockSize ClockSize { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Called by OK: true (and no error) if every value can be used; <see cref="Result"/> then holds them.</summary>
    public bool TryAccept()
    {
        try
        {
            Result = Read();
            Error = null;
            return true;
        }
        catch (ProfileEditException ex)
        {
            Result = null;
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>Puts every value back to its default (folders to the defaults too); OK still has to be pressed.</summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        Show(_original with
        {
            ForecastKnockOn = defaults.ForecastKnockOn,
            SuggestCancellations = defaults.SuggestCancellations,
            Planning = defaults.Planning,
            TimetableFolder = null,
            SessionsFolder = null,
            RecordingsFolder = null,
            ExportsFolder = null,
            DetailedLogging = defaults.DetailedLogging,
            LogRetentionDays = defaults.LogRetentionDays,
            DeleteSessionsAfterDays = null,
            LearnSignalPlaces = defaults.LearnSignalPlaces,
            LearnMinSamples = defaults.LearnMinSamples,
            ClockSize = defaults.ClockSize,
        });
        Error = null;
    }

    /// <summary>Picks a folder for one of the boxes: <c>timetables</c>, <c>sessions</c>, <c>recordings</c> or <c>exports</c>.</summary>
    [RelayCommand]
    private void Browse(string which)
    {
        var (title, current, fallback) = which switch
        {
            "timetables" => ("Choose the folder to look for timetables in", TimetableFolder, DefaultTimetableFolder),
            "sessions" => ("Choose the folder to save sessions in", SessionsFolder, Defaults.Sessions),
            "recordings" => ("Choose the folder to save recordings in", RecordingsFolder, Defaults.Recordings),
            "exports" => ("Choose the folder to offer exports in", ExportsFolder, Defaults.Exports),
            _ => throw new ArgumentOutOfRangeException(nameof(which), which, "Not a folder setting."),
        };
        if (_dialogs.PickFolder(title, current.Trim().Length > 0 ? current.Trim() : fallback) is not { } folder)
        {
            return;
        }

        switch (which)
        {
            case "timetables":
                TimetableFolder = folder;
                break;
            case "sessions":
                SessionsFolder = folder;
                break;
            case "recordings":
                RecordingsFolder = folder;
                break;
            default:
                ExportsFolder = folder;
                break;
        }
    }

    /// <summary>
    /// Forget saved password (S30): the settings lose it straight away, whether the window then closes with OK or
    /// Cancel, and OK does not bring it back.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSavedPassword))]
    private void ForgetPassword()
    {
        ForgetSavedPassword?.Invoke();
        _original = AppSettingsStore.WithoutPassword(_original);
        HasSavedPassword = false;
    }

    /// <summary>Opens the log folder in Explorer.</summary>
    [RelayCommand]
    private void OpenLogs()
    {
        Directory.CreateDirectory(Defaults.Logs);
        if (_dialogs.OpenInShell(Defaults.Logs) is { } why)
        {
            _dialogs.ShowError("Open the log folder", $"Could not open {Defaults.Logs}: {why}");
        }
    }

    private void Show(AppSettings s)
    {
        var p = s.Planning;
        ForecastKnockOn = s.ForecastKnockOn;
        ApplyTimetableRules = p.ApplyTimetableRules;
        DefaultDwell = ProfileEditorText.FormatDuration(p.DefaultDwellSeconds);
        DefaultTurnaround = ProfileEditorText.FormatDuration(p.DefaultTurnaroundSeconds);
        PossiblyStopped = ProfileEditorText.FormatDuration(p.PossiblyStoppedSeconds);
        PlatformMargin = ProfileEditorText.FormatDuration(p.PlatformMarginSeconds);
        MinConflict = ProfileEditorText.FormatDuration(p.MinConflictSeconds);
        DefaultHeadway = ProfileEditorText.FormatDuration(p.DefaultHeadwaySeconds);
        AllowCancellation = s.SuggestCancellations;
        PlatformChangePenalty = Minutes(p.PlatformChangePenalty);
        LineChangePenalty = Minutes(p.LineChangePenalty);
        TerminatePenalty = Minutes(p.TerminatePenalty);
        CancelPenalty = Minutes(p.CancelPenalty);
        ReplanHorizon = p.ReplanHorizonMinutes.ToString(CultureInfo.CurrentCulture);
        ReplanTimeLimit = p.ReplanTimeLimitSeconds.ToString("0.#", CultureInfo.CurrentCulture);
        ReplanWithOptimiser = p.ReplanMethod == ReplanMethod.Optimiser;
        ReplanPlatformChanges = p.ReplanPlatformChanges;
        TimetableFolder = s.TimetableFolder ?? string.Empty;
        SessionsFolder = s.SessionsFolder ?? string.Empty;
        RecordingsFolder = s.RecordingsFolder ?? string.Empty;
        ExportsFolder = s.ExportsFolder ?? string.Empty;
        DetailedLogging = s.DetailedLogging;
        LogRetentionDays = s.LogRetentionDays.ToString(CultureInfo.CurrentCulture);
        DeleteOldSessions = s.DeleteSessionsAfterDays is not null;
        DeleteSessionsAfterDays = (s.DeleteSessionsAfterDays ?? 90).ToString(CultureInfo.CurrentCulture);
        LearnSignalPlaces = s.LearnSignalPlaces;
        LearnMinSamples = s.LearnMinSamples.ToString(CultureInfo.CurrentCulture);
        ClockSize = s.ClockSize;
    }

    /// <exception cref="ProfileEditException">A value cannot be used; the message says which and why.</exception>
    private AppSettings Read()
    {
        var planning = new PlanningSettings
        {
            DefaultDwellSeconds = Duration(DefaultDwell, "default dwell", 0, 10),
            DefaultTurnaroundSeconds = Duration(DefaultTurnaround, "default turnaround", 0, 60),
            PossiblyStoppedSeconds = Duration(PossiblyStopped, "time before a train may have stopped", 1, 60),
            ApplyTimetableRules = ApplyTimetableRules,
            PlatformMarginSeconds = Duration(PlatformMargin, "platform margin", 0, 10),
            MinConflictSeconds = Duration(MinConflict, "least conflict shown", 0, 10),
            DefaultHeadwaySeconds = Duration(DefaultHeadway, "default headway", 0, 15),
            PlatformChangePenalty = Penalty(PlatformChangePenalty, "platform change penalty"),
            LineChangePenalty = Penalty(LineChangePenalty, "line change penalty"),
            TerminatePenalty = Penalty(TerminatePenalty, "terminate short penalty"),
            CancelPenalty = Penalty(CancelPenalty, "cancellation penalty"),
            ReplanHorizonMinutes = Count(ReplanHorizon, "re-plan horizon (minutes)", 10, 240),
            ReplanTimeLimitSeconds = Seconds(ReplanTimeLimit, "re-plan time limit", 1, 60),
            ReplanMethod = ReplanWithOptimiser ? ReplanMethod.Optimiser : ReplanMethod.Rules,
            ReplanPlatformChanges = ReplanPlatformChanges,
        };
        return _original with
        {
            ForecastKnockOn = ForecastKnockOn,
            SuggestCancellations = AllowCancellation,
            Planning = planning,
            TimetableFolder = Folder(TimetableFolder, "timetables folder", mustExist: true),
            SessionsFolder = Folder(SessionsFolder, "sessions folder", mustExist: false),
            RecordingsFolder = Folder(RecordingsFolder, "recordings folder", mustExist: false),
            ExportsFolder = Folder(ExportsFolder, "exports folder", mustExist: false),
            DetailedLogging = DetailedLogging,
            LogRetentionDays = Count(LogRetentionDays, "days of logs to keep", 1, 365),
            DeleteSessionsAfterDays = DeleteOldSessions ? Count(DeleteSessionsAfterDays, "days to keep saved sessions", 1, 3650) : null,
            LearnSignalPlaces = LearnSignalPlaces,
            LearnMinSamples = Count(LearnMinSamples, "samples a learned place needs", 1, 50),
            ClockSize = ClockSize,
        };
    }

    private static int Duration(string text, string what, int minMinutes, int maxMinutes)
    {
        var seconds = ProfileEditorText.ParseDuration(text, what);
        return seconds >= minMinutes * 60 && seconds <= maxMinutes * 60
            ? seconds
            : throw new ProfileEditException($"The {what} must be from {minMinutes} to {maxMinutes} minutes.");
    }

    /// <summary>A penalty typed in minutes, kept in weighted seconds.</summary>
    private static double Penalty(string text, string what)
    {
        var t = text.Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var minutes)
            && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out minutes))
        {
            throw new ProfileEditException($"The {what} must be a number of minutes.");
        }

        return minutes is >= 0 and <= 24 * 60
            ? Math.Round(minutes * 60)
            : throw new ProfileEditException($"The {what} must be from 0 to 1440 minutes.");
    }

    /// <summary>A number of seconds (fractions allowed).</summary>
    private static double Seconds(string text, string what, double min, double max)
    {
        var t = text.Trim().TrimEnd('s', 'S').Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds)
            && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            throw new ProfileEditException($"The {what} must be a number of seconds.");
        }

        return seconds >= min && seconds <= max
            ? seconds
            : throw new ProfileEditException(string.Create(CultureInfo.CurrentCulture, $"The {what} must be from {min} to {max} seconds."));
    }

    private static int Count(string text, string what, int min, int max)
    {
        var n = ProfileEditorText.ParseCount(text, what);
        return n >= min && n <= max ? n : throw new ProfileEditException($"The {what} must be from {min} to {max}.");
    }

    /// <summary>A folder typed or picked; null (the default) when blank.</summary>
    private static string? Folder(string text, string what, bool mustExist)
    {
        var t = text.Trim().Trim('"');
        if (t.Length == 0)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(t) || t.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ProfileEditException($"The {what} must be a full path, such as D:\\Stringline, or blank for the default.");
        }

        return !mustExist || Directory.Exists(t) ? t : throw new ProfileEditException($"The {what} {t} does not exist.");
    }

    private static string Minutes(double weightedSeconds) => (weightedSeconds / 60).ToString("0.##", CultureInfo.CurrentCulture);
}
