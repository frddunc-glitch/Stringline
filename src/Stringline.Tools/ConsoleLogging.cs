using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Stringline.Tools;

/// <summary>Serilog to stderr behind <see cref="ILoggerFactory"/>, so stdout stays free for command output.</summary>
internal static class ConsoleLogging
{
    public static ILoggerFactory Create(bool verbose)
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: LogEventLevel.Verbose,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
        return new SerilogLoggerFactory(logger, dispose: true);
    }
}
