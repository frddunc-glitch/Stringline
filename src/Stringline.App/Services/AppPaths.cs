using System.IO;

namespace Stringline.App.Services;

/// <summary>Where the app keeps its files (<c>02-…md</c> §5).</summary>
/// <param name="Settings">The settings file, <c>%APPDATA%\Stringline\settings.json</c>.</param>
/// <param name="Profiles">Per-sim profiles, <c>%APPDATA%\Stringline\profiles</c>.</param>
/// <param name="Logs">Rolling logs, <c>%LOCALAPPDATA%\Stringline\logs</c>.</param>
/// <param name="Recordings">Recordings made with the Record button, <c>Documents\Stringline\recordings</c>.</param>
/// <param name="Sessions">Saved sessions (S16), <c>Documents\Stringline\sessions</c>.</param>
/// <param name="Drafts">Timetable drafts being edited, autosaved (S43), <c>Documents\Stringline\drafts</c>.</param>
internal sealed record AppPaths(string Settings, string Profiles, string Logs, string Recordings, string Exports, string Sessions, string Drafts)
{
    public static AppPaths Default
    {
        get
        {
            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Stringline");
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Stringline");
            var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), "Stringline");
            return new AppPaths(
                Path.Combine(roaming, "settings.json"),
                Path.Combine(roaming, "profiles"),
                Path.Combine(local, "logs"),
                Path.Combine(documents, "recordings"),
                Path.Combine(documents, "exports"),
                Path.Combine(documents, "sessions"),
                Path.Combine(documents, "drafts"));
        }
    }

    /// <summary>Where the bundled user guide is written as HTML (S28): <c>%LOCALAPPDATA%\Stringline\help</c>, beside the logs.</summary>
    public string Help => Path.Combine(Path.GetDirectoryName(Logs) ?? Logs, "help");

    /// <summary>Where the timetable editor copies a file before replacing it (S40, S43): <c>%LOCALAPPDATA%\Stringline\timetable-backups</c>.</summary>
    public string TimetableBackups => Path.Combine(Path.GetDirectoryName(Logs) ?? Logs, "timetable-backups");

    /// <summary>These paths with the folders the user chose in the settings (S18), where they chose any.</summary>
    public AppPaths WithFolders(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return this with
        {
            Sessions = Chosen(settings.SessionsFolder) ?? Sessions,
            Recordings = Chosen(settings.RecordingsFolder) ?? Recordings,
            Exports = Chosen(settings.ExportsFolder) ?? Exports,
        };
    }

    private static string? Chosen(string? folder) => string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

    /// <summary>Everything under one folder, for tests.</summary>
    public static AppPaths Under(string root) => new(
        Path.Combine(root, "settings.json"),
        Path.Combine(root, "profiles"),
        Path.Combine(root, "logs"),
        Path.Combine(root, "recordings"),
        Path.Combine(root, "exports"),
        Path.Combine(root, "sessions"),
        Path.Combine(root, "drafts"));
}
