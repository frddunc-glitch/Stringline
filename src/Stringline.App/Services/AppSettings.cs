using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

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

    /// <summary>The folder the last graph export went to.</summary>
    public string? LastExportFolder { get; init; }

    public string? TimetableFor(string? areaId) =>
        areaId is not null && TimetableByArea.TryGetValue(areaId.Trim().ToLowerInvariant(), out var path) ? path : null;

    public AppSettings WithTimetable(string areaId, string path)
    {
        var map = new Dictionary<string, string>(TimetableByArea, StringComparer.Ordinal) { [areaId.Trim().ToLowerInvariant()] = path };
        return this with { TimetableByArea = map, LastTimetablePath = path };
    }
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read settings from {Path}; using defaults")]
    private partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save settings to {Path}")]
    private partial void LogSaveFailed(Exception ex, string path);
}
