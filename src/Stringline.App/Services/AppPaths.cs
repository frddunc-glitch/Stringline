using System.IO;

namespace Stringline.App.Services;

/// <summary>Where the app keeps its files (<c>02-…md</c> §5).</summary>
/// <param name="Settings">The settings file, <c>%APPDATA%\Stringline\settings.json</c>.</param>
/// <param name="Profiles">Per-sim profiles, <c>%APPDATA%\Stringline\profiles</c>.</param>
/// <param name="Logs">Rolling logs, <c>%LOCALAPPDATA%\Stringline\logs</c>.</param>
/// <param name="Recordings">Recordings made with the Record button, <c>Documents\Stringline\recordings</c>.</param>
internal sealed record AppPaths(string Settings, string Profiles, string Logs, string Recordings)
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
                Path.Combine(documents, "recordings"));
        }
    }

    /// <summary>Everything under one folder, for tests.</summary>
    public static AppPaths Under(string root) => new(
        Path.Combine(root, "settings.json"),
        Path.Combine(root, "profiles"),
        Path.Combine(root, "logs"),
        Path.Combine(root, "recordings"));
}
