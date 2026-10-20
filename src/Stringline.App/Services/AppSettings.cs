using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Planning;

namespace Stringline.App.Services;

/// <summary>What the app remembers between runs. The SimSig password is only ever stored encrypted.</summary>
internal sealed record AppSettings
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = Gateway.GatewayTopics.DefaultPort;

    /// <summary>SimSig forum username for payware sims, if any.</summary>
    public string? UserName { get; init; }

    /// <summary>True if the user asked for the password to be remembered.</summary>
    public bool RememberPassword { get; init; }

    /// <summary>The password encrypted for the current Windows user (DPAPI), base64; null unless remembered.</summary>
    public string? ProtectedPassword { get; init; }

    /// <summary>Subscribe to the TD topic too (berth steps and signalling state).</summary>
    public bool SubscribeTd { get; init; }

    public string? LastReplayPath { get; init; }

    public double ReplaySpeed { get; init; } = 8;

    /// <summary>The timetable last used for each sim, by area ID (lower case).</summary>
    public IReadOnlyDictionary<string, string> TimetableByArea { get; init; } = new Dictionary<string, string>();

    /// <summary>The last timetable picked in any dialog.</summary>
    public string? LastTimetablePath { get; init; }

    /// <summary>Where to look for timetables to suggest; SimSig's own folder when null.</summary>
    public string? TimetableFolder { get; init; }

    /// <summary>Colour actual lines by delay band rather than by category (GR-7).</summary>
    public bool ColourByDelay { get; init; }

    /// <summary>Draw the graph with time running down (GR-1).</summary>
    public bool VerticalGraph { get; init; }

    /// <summary>The forecast includes knock-on delay from other trains (the interaction pass). On unless turned off.</summary>
    public bool ForecastKnockOn { get; init; } = true;

    /// <summary>Suggestions may terminate a train short or cancel it, for a block (S15). Off unless turned on.</summary>
    public bool SuggestCancellations { get; init; }

    /// <summary>The folder the last graph export went to.</summary>
    public string? LastExportFolder { get; init; }

    /// <summary>
    /// The sim name the user gave each timetable that has no sim ID of its own (S14a), by full path in lower case, so
    /// its profile is found again next time.
    /// </summary>
    public IReadOnlyDictionary<string, string> SimIdByTimetable { get; init; } = new Dictionary<string, string>();

    /// <summary>The forecast, conflict and suggestion numbers from the settings window (S18).</summary>
    public PlanningSettings Planning { get; init; } = new();

    /// <summary>Where sessions are saved; <c>Documents\Stringline\sessions</c> when null.</summary>
    public string? SessionsFolder { get; init; }

    /// <summary>Where recordings are made; <c>Documents\Stringline\recordings</c> when null.</summary>
    public string? RecordingsFolder { get; init; }

    /// <summary>Where exports are offered first; <c>Documents\Stringline\exports</c> when null.</summary>
    public string? ExportsFolder { get; init; }

    /// <summary>Log at Debug rather than Information, for chasing a problem.</summary>
    public bool DetailedLogging { get; init; }

    /// <summary>How many days of log files are kept.</summary>
    public int LogRetentionDays { get; init; } = DefaultLogRetentionDays;

    public const int DefaultLogRetentionDays = 14;

    /// <summary>How big the sim clock at the toolbar's right is drawn (#13).</summary>
    public ClockSize ClockSize { get; init; } = ClockSize.Normal;

    /// <summary>The main window's panels, splitters and place (#9).</summary>
    public WindowLayout Layout { get; init; } = new();

    /// <summary>The last plot's choices (S21), offered again.</summary>
    public PlotSettings Plot { get; init; } = new();

    /// <summary>Saved sessions last saved more than this many days ago are deleted at start-up; never when null.</summary>
    public int? DeleteSessionsAfterDays { get; init; }

    public string? SimIdFor(string? timetablePath) =>
        timetablePath is { Length: > 0 } && SimIdByTimetable.TryGetValue(PathKey(timetablePath), out var id) ? id : null;

    public AppSettings WithSimId(string timetablePath, string simId)
    {
        var map = new Dictionary<string, string>(SimIdByTimetable, StringComparer.Ordinal) { [PathKey(timetablePath)] = simId.Trim() };
        return this with { SimIdByTimetable = map };
    }

    private static string PathKey(string path) => Path.GetFullPath(path).ToLowerInvariant();

    public string? TimetableFor(string? areaId) =>
        areaId is not null && TimetableByArea.TryGetValue(areaId.Trim().ToLowerInvariant(), out var path) ? path : null;

    public AppSettings WithTimetable(string areaId, string path)
    {
        var map = new Dictionary<string, string>(TimetableByArea, StringComparer.Ordinal) { [areaId.Trim().ToLowerInvariant()] = path };
        return this with { TimetableByArea = map, LastTimetablePath = path };
    }
}

/// <summary>
/// File ▸ Export ▸ Plot's choices (S21), remembered: how many hours, the minutes a page, the paper by name
/// (<see cref="Graph.PdfPage.Paper"/>) and whether time runs across the pages rather than down.
/// </summary>
internal sealed record PlotSettings
{
    public double Hours { get; init; } = 6;

    public int MinutesPerPage { get; init; } = Graph.GraphPlotOptions.DefaultSecondsPerPage / Core.SimTime.SecondsPerMinute;

    public string Paper { get; init; } = Graph.PdfPage.A3Portrait.Name;

    public bool Across { get; init; }
}

/// <summary>How big the toolbar's sim clock is (#13).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClockSize>))]
internal enum ClockSize
{
    Normal,
    Large,
    Huge,
}

/// <summary>
/// The planning numbers the user can change (S18), each defaulting to the planning library's own default. Durations
/// are in seconds; penalties are in weighted seconds of delay.
/// </summary>
internal sealed record PlanningSettings
{
    private static readonly ForecastOptions Forecast = new();
    private static readonly ConflictOptions Conflict = new();
    private static readonly SuggestionOptions Suggestion = new();
    private static readonly ReplanOptions Replan = new();

    public int DefaultDwellSeconds { get; init; } = Forecast.DefaultDwellSeconds;

    public int DefaultTurnaroundSeconds { get; init; } = Forecast.DefaultTurnaroundSeconds;

    public int PossiblyStoppedSeconds { get; init; } = Forecast.PossiblyStoppedSeconds;

    public int PlatformMarginSeconds { get; init; } = Conflict.PlatformMarginSeconds;

    public int MinConflictSeconds { get; init; } = Conflict.MinSeveritySeconds;

    /// <summary>
    /// The headway for sections with neither a hand-set nor a WTT headway, unless the sim's profile sets its own
    /// (S27, <c>06-…md</c> §3).
    /// </summary>
    public int DefaultHeadwaySeconds { get; init; } = Network.Section.StandardHeadwaySeconds;

    public double PlatformChangePenalty { get; init; } = Suggestion.PlatformChangePenalty;

    public double LineChangePenalty { get; init; } = Suggestion.LineChangePenalty;

    public double TerminatePenalty { get; init; } = Suggestion.TerminatePenalty;

    public double CancelPenalty { get; init; } = Suggestion.CancelPenalty;

    /// <summary>How far ahead Plan ▸ Re-plan works (S20; Fred: 60 min).</summary>
    public int ReplanHorizonMinutes { get; init; } = Replan.HorizonSeconds / SimTime.SecondsPerMinute;

    /// <summary>How long a re-plan may think (Fred: 5 s).</summary>
    public double ReplanTimeLimitSeconds { get; init; } = Replan.TimeLimitSeconds;

    public ReplanMethod ReplanMethod { get; init; } = ReplanMethod.Optimiser;

    /// <summary>Let a re-plan move trains to other platforms (Fred: yes).</summary>
    public bool ReplanPlatformChanges { get; init; } = Replan.AllowPlatformChanges;

    public ForecastOptions ToForecastOptions(bool interaction) => new()
    {
        Interaction = interaction,
        DefaultDwellSeconds = DefaultDwellSeconds,
        DefaultTurnaroundSeconds = DefaultTurnaroundSeconds,
        PossiblyStoppedSeconds = PossiblyStoppedSeconds,
    };

    public ConflictOptions ToConflictOptions() => new() { PlatformMarginSeconds = PlatformMarginSeconds, MinSeveritySeconds = MinConflictSeconds };

    public ReplanOptions ToReplanOptions() => new()
    {
        HorizonSeconds = ReplanHorizonMinutes * SimTime.SecondsPerMinute,
        TimeLimitSeconds = ReplanTimeLimitSeconds,
        AllowPlatformChanges = ReplanPlatformChanges,
        PlatformChangePenalty = PlatformChangePenalty,
        LineChangePenalty = LineChangePenalty,
    };

    public SuggestionOptions ToSuggestionOptions(bool allowCancellation) => new()
    {
        AllowCancellation = allowCancellation,
        PlatformChangePenalty = PlatformChangePenalty,
        LineChangePenalty = LineChangePenalty,
        TerminatePenalty = TerminatePenalty,
        CancelPenalty = CancelPenalty,
    };
}

/// <summary>Encrypts secrets for storage on this machine.</summary>
internal interface ISecretProtector
{
    string Protect(string secret);

    /// <returns>The secret, or null if it cannot be decrypted (another user or machine, or damaged).</returns>
    string? Unprotect(string protectedSecret);
}

/// <summary>Windows DPAPI, scoped to the current user (CON-5).</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Stringline.SimSigPassword.v1"u8.ToArray();

    public string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. A missing or unreadable file gives the defaults.</summary>
internal sealed partial class AppSettingsStore(AppPaths paths, ISecretProtector protector, ILogger<AppSettingsStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Path => paths.Settings;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(paths.Settings))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.Settings), Json) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogLoadFailed(ex, paths.Settings);
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(paths.Settings)!);
            var temp = paths.Settings + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, paths.Settings, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(ex, paths.Settings);
        }
    }

    /// <summary>The remembered password, or null if none is stored or it cannot be decrypted.</summary>
    public string? PasswordOf(AppSettings settings) =>
        settings is { RememberPassword: true, ProtectedPassword: { Length: > 0 } stored } ? protector.Unprotect(stored) : null;

    /// <summary>The settings with the password stored encrypted if <paramref name="remember"/>, else with none stored.</summary>
    public AppSettings WithPassword(AppSettings settings, string? password, bool remember)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return remember && !string.IsNullOrEmpty(password)
            ? settings with { RememberPassword = true, ProtectedPassword = protector.Protect(password) }
            : settings with { RememberPassword = remember, ProtectedPassword = null };
    }

    /// <summary>True if the settings hold a saved password (whether or not it can still be decrypted).</summary>
    public static bool HasPassword(AppSettings settings) => !string.IsNullOrEmpty(settings?.ProtectedPassword);

    /// <summary>The settings with no saved password and Remember unticked (S30, Forget saved password).</summary>
    public static AppSettings WithoutPassword(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { RememberPassword = false, ProtectedPassword = null };
    }

    /// <summary>
    /// Removes a saved password from the settings file, leaving everything else as it is; the uninstaller runs this
    /// (<c>Stringline.exe --forget-password</c>). Nothing is written when no password is saved.
    /// </summary>
    /// <returns>True if a password was removed.</returns>
    public bool ForgetSavedPassword()
    {
        var settings = Load();
        if (!HasPassword(settings) && !settings.RememberPassword)
        {
            return false;
        }

        Save(WithoutPassword(settings));
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read settings from {Path}; using defaults")]
    private partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save settings to {Path}")]
    private partial void LogSaveFailed(Exception ex, string path);
}
