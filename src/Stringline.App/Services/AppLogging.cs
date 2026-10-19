using System.IO;
using System.Runtime.InteropServices;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Stringline.App.Services;

/// <summary>
/// The app's Serilog set-up (S18): rolling daily files in <see cref="AppPaths.Logs"/>, kept for the days the settings
/// say, at Information or, with detailed logging on, Debug. The level can be changed while running; the number of files
/// kept takes effect at the next start. Credentials are never logged (CLAUDE.md).
/// </summary>
internal sealed class AppLogging
{
    public const string FilePattern = "stringline-.log";

    public AppLogging(string folder, bool detailed)
    {
        Folder = folder;
        Detailed = detailed;
    }

    public string Folder { get; }

    /// <summary>The level every logger reads, changed by <see cref="Detailed"/>.</summary>
    public LoggingLevelSwitch Level { get; } = new(LogEventLevel.Information);

    /// <summary>Log at Debug as well as Information.</summary>
    public bool Detailed
    {
        get => Level.MinimumLevel <= LogEventLevel.Debug;
        set => Level.MinimumLevel = value ? LogEventLevel.Debug : LogEventLevel.Information;
    }

    /// <summary>The logger writing to <see cref="Folder"/>, keeping <paramref name="retentionDays"/> files.</summary>
    public Logger CreateLogger(int retentionDays) => new LoggerConfiguration()
        .MinimumLevel.ControlledBy(Level)
        .Enrich.FromLogContext()
        .WriteTo.File(
            Path.Combine(Folder, FilePattern),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: Math.Clamp(retentionDays, 1, 365),
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
            formatProvider: System.Globalization.CultureInfo.InvariantCulture)
        .CreateLogger();

    /// <summary>One line saying what is running where, for the top of each run's log.</summary>
    public static string Environment() =>
        $"{AboutInfo.Product} {AboutInfo.Version} ({AboutInfo.Commit ?? "unknown commit"}) on {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, " +
        $"{RuntimeInformation.FrameworkDescription}, {(AboutInfo.IsSingleFile ? "single-file" : "from build output")}";
}
