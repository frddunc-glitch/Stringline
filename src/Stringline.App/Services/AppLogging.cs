using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Stringline.App.Services;

/// <summary>
/// The app's Serilog set-up (S18): rolling daily files in <see cref="AppPaths.Logs"/>, kept for the days the settings
/// say, at Information or, with detailed logging on, Debug. The level can be changed while running; the number of files
/// kept takes effect at the next start. Credentials are never logged (CLAUDE.md).
/// <para>
/// Each sim has its own files (<c>stringline-waterloo-20261101.log</c>), so a bug report can attach the one sim's log:
/// lines go to <c>stringline-20261101.log</c> until <see cref="UseSim"/> names a sim, then to that sim's file. Each sim's
/// files are kept separately, the same number of them.
/// </para>
/// </summary>
internal sealed class AppLogging
{
    public const string FilePattern = "stringline-.log";

    private readonly Lock _gate = new();
    private string? _sim;

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

    /// <summary>The sim whose files the log is going to, as it appears in their names; null before one is known.</summary>
    public string? Sim
    {
        get
        {
            lock (_gate)
            {
                return _sim;
            }
        }
    }

    /// <summary>
    /// Send the log to <paramref name="areaId"/>'s files from now on. True if that changes the files; a blank area, or
    /// one whose name has nothing usable in a file name, changes nothing.
    /// </summary>
    public bool UseSim(string? areaId)
    {
        if (FileKey(areaId) is not { } key)
        {
            return false;
        }

        lock (_gate)
        {
            if (key == _sim)
            {
                return false;
            }

            _sim = key;
            return true;
        }
    }

    /// <summary>The name pattern of <paramref name="areaId"/>'s files (Serilog adds the date before the extension).</summary>
    public static string FilePatternFor(string? areaId) => FileKey(areaId) is { } key ? $"stringline-{key}-.log" : FilePattern;

    /// <summary>
    /// The sim's area ID as it goes into a file name: lower case, letters, digits and underscores kept, anything else a
    /// hyphen; null if nothing is left.
    /// </summary>
    public static string? FileKey(string? areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
        {
            return null;
        }

        var text = new StringBuilder(areaId.Length);
        foreach (var c in areaId.Trim().ToLowerInvariant())
        {
            text.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '-');
        }

        var key = text.ToString().Trim('-');
        return key.Length == 0 ? null : key.Length > 40 ? key[..40].TrimEnd('-') : key;
    }

    /// <summary>The logger writing to <see cref="Folder"/>, keeping <paramref name="retentionDays"/> files for each sim.</summary>
    public Logger CreateLogger(int retentionDays) => new LoggerConfiguration()
        .MinimumLevel.ControlledBy(Level)
        .Enrich.FromLogContext()
        .WriteTo.Sink(new SimFileSink(this, Math.Clamp(retentionDays, 1, 365)))
        .CreateLogger();

    /// <summary>One line saying what is running where, for the top of each run's log.</summary>
    public static string Environment() =>
        $"{AboutInfo.Product} {AboutInfo.Version} ({AboutInfo.Commit ?? "unknown commit"}) on {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, " +
        $"{RuntimeInformation.FrameworkDescription}, {(AboutInfo.IsSingleFile ? "single-file" : "from build output")}";

    /// <summary>Writes each line to the rolling files of the sim <see cref="Sim"/> names at the time, opening them when it changes.</summary>
    private sealed class SimFileSink(AppLogging owner, int retainedFiles) : ILogEventSink, IDisposable
    {
        private readonly Lock _gate = new();
        private Logger? _file;
        private string? _fileSim;
        private bool _disposed;

        public void Emit(LogEvent logEvent)
        {
            var sim = owner.Sim;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (_file is null || sim != _fileSim)
                {
                    _file?.Dispose();
                    _file = Open(sim);
                    _fileSim = sim;
                }

                _file.Write(logEvent);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _file?.Dispose();
                _file = null;
            }
        }

        private Logger Open(string? sim) => new LoggerConfiguration()
            .MinimumLevel.Verbose() // the outer logger has filtered already
            .WriteTo.File(
                Path.Combine(owner.Folder, FilePatternFor(sim)),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedFiles,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
    }
}
