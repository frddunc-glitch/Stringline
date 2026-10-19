using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Services;
using Stringline.Persistence;

namespace Stringline.App.ViewModels;

/// <summary>
/// The settings window (S18) and what follows from it: the forecast and conflict numbers go to the engine, the
/// suggestion numbers to the next suggestions, the folders to the next save, and the log level at once. Saved sessions
/// older than the days chosen are deleted at start-up, and when the setting is turned on (after asking).
/// </summary>
internal sealed partial class MainViewModel
{
    /// <summary>The folders in use: the defaults, with any the user chose instead.</summary>
    internal AppPaths Paths => _paths;

    /// <summary>The settings as the window should show them, with the view's own values up to date.</summary>
    private AppSettings CurrentSettings => _settings with { ForecastKnockOn = ForecastKnockOn, SuggestCancellations = SuggestionPanel.AllowCancellation, Layout = Layout.Current };

    [RelayCommand]
    private void ShowSettings()
    {
        var dialog = new SettingsViewModel(CurrentSettings, _defaultPaths, _dialogs);
        if (_dialogs.ShowSettings(dialog) && dialog.Result is { } result)
        {
            ApplySettings(result);
        }
    }

    /// <summary>The setup guide: the README on GitHub.</summary>
    [RelayCommand]
    private void OpenHelp() => Open(AboutInfo.SourceUrl + "#readme");

    [RelayCommand]
    private void OpenLogFolder()
    {
        System.IO.Directory.CreateDirectory(_defaultPaths.Logs);
        Open(_defaultPaths.Logs);
    }

    private void Open(string target)
    {
        if (_dialogs.OpenInShell(target) is { } why)
        {
            _dialogs.ShowError("Stringline", $"Could not open {target}: {why}");
        }
    }

    /// <summary>Use these settings from now on, and save them.</summary>
    public void ApplySettings(AppSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var before = CurrentSettings;
        _settings = next;
        _settingsStore.Save(next);
        _paths = _defaultPaths.WithFolders(next);
        if (_logging is { } logging && logging.Detailed != next.DetailedLogging)
        {
            logging.Detailed = next.DetailedLogging;
            LogLoggingChanged(next.DetailedLogging ? "Debug" : "Information");
        }

        // Setting these through the properties also tells the engine and the suggestions; the settings already match.
        ForecastKnockOn = next.ForecastKnockOn;
        ClockSize = next.ClockSize;
        SuggestionPanel.AllowCancellation = next.SuggestCancellations;
        if (before.Planning != next.Planning)
        {
            _engine.SetForecast(options: next.Planning.ToForecastOptions(next.ForecastKnockOn), conflicts: next.Planning.ToConflictOptions());
            UpdateSuggestions();
        }

        if (next.DeleteSessionsAfterDays is { } days && before.DeleteSessionsAfterDays != days)
        {
            AskToTidySessions(days);
        }
    }

    /// <summary>At start-up: delete saved sessions older than the settings keep, if the user asked for that.</summary>
    /// <param name="keep">A session named on the command line, kept whatever its age.</param>
    public async Task TidySessionsAsync(string? keep = null)
    {
        if (_settings.DeleteSessionsAfterDays is not { } days)
        {
            return;
        }

        var (folder, cutoff) = (_paths.Sessions, _time.GetUtcNow().AddDays(-days));
        var deleted = await Task.Run(() => SessionCatalogue.DeleteOlderThan(folder, cutoff, keep ?? Store?.Path));
        if (deleted.Count > 0)
        {
            LogSessionsDeleted(deleted.Count, days, folder);
        }
    }

    private void AskToTidySessions(int days)
    {
        var (folder, cutoff) = (_paths.Sessions, _time.GetUtcNow().AddDays(-days));
        // The newest is never deleted; nor is the one being written.
        var old = SessionCatalogue.List(folder).Skip(1).Count(s => s.Updated < cutoff && s.Path != Store?.Path);
        if (old == 0)
        {
            return;
        }

        var plural = old == 1 ? "session" : "sessions";
        if (_dialogs.Choose(
                "Delete old sessions",
                string.Create(CultureInfo.CurrentCulture, $"{old} saved {plural} in {folder} were last saved more than {days} days ago. Delete them now?"),
                ["Delete", "Not now"]) != 0)
        {
            return;
        }

        var deleted = SessionCatalogue.DeleteOlderThan(folder, cutoff, Store?.Path);
        LogSessionsDeleted(deleted.Count, days, folder);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Logging at {Level} from now on")]
    private partial void LogLoggingChanged(string level);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} saved sessions older than {Days} days from {Folder}")]
    private partial void LogSessionsDeleted(int count, int days, string folder);
}
