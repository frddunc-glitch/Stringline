using System.CommandLine;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Stringline.Gateway;
using Stringline.Gateway.Recording;

namespace Stringline.Tools;

/// <summary><c>stringline record</c>: capture every gateway frame to a <c>.sslog</c> file until Ctrl+C.</summary>
internal static partial class RecordCommand
{
    public const string PasswordVariable = "STRINGLINE_PASSWORD";

    public static Command Create()
    {
        var host = new Option<string>("--host") { Description = "Gateway host.", DefaultValueFactory = _ => "localhost" };
        var port = new Option<int>("--port") { Description = "Gateway port.", DefaultValueFactory = _ => GatewayTopics.DefaultPort };
        var user = new Option<string?>("--user")
        {
            Description = $"SimSig forum username, for payware sims. The password is read from {PasswordVariable}, or prompted for.",
        };
        var output = new Option<FileInfo>("--out") { Description = "Recording to write (.sslog).", Required = true };
        var td = new Option<bool>("--td") { Description = "Also record the TD/signalling topic (much busier); requests a snapshot on connect." };
        var summary = new Option<bool>("--ttsummary") { Description = "Subscribe to TRAIN_MVT_SUMMARY and send a ttsummary request after connecting (experimental)." };
        var force = new Option<bool>("--force") { Description = "Overwrite the output file if it exists." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("record", "Record SimSig gateway traffic to a .sslog file until Ctrl+C.")
        {
            host, port, user, output, td, summary, force, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(
                parse.GetValue(host)!,
                parse.GetValue(port),
                parse.GetValue(user),
                parse.GetValue(output)!,
                parse.GetValue(td),
                parse.GetValue(summary),
                parse.GetValue(force),
                parse.GetValue(verbose)),
            cancellationToken));
        return command;
    }

    private sealed record Settings(string Host, int Port, string? User, FileInfo Output, bool Td, bool TtSummary, bool Force, bool Verbose);

    private static async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        var log = loggerFactory.CreateLogger("record");

        string? password = null;
        if (!string.IsNullOrWhiteSpace(settings.User) && !TryGetPassword(out password))
        {
            await Console.Error.WriteLineAsync($"A password is needed for --user. Set {PasswordVariable} or run interactively to be prompted.").ConfigureAwait(false);
            return 2;
        }

        List<string> topics = [.. settings.Td ? GatewayTopics.WithTrainDescriber : GatewayTopics.Default];
        if (settings.TtSummary)
        {
            topics.Add(GatewayTopics.TrainMovementSummary);
        }

        var options = new GatewayClientOptions
        {
            Host = settings.Host,
            Port = settings.Port,
            Login = string.IsNullOrWhiteSpace(settings.User) ? null : settings.User.Trim(),
            Passcode = password,
            Topics = topics,
        };

        if (settings.Output.Exists && !settings.Force)
        {
            await Console.Error.WriteLineAsync($"{settings.Output.FullName} already exists. Use --force to overwrite.").ConfigureAwait(false);
            return 2;
        }

        var header = SslogWriter.LiveHeader(DateTimeOffset.UtcNow, options.Host, options.Port, options.Topics);
        using var writer = TryCreateWriter(settings, header, out var error);
        if (writer is null)
        {
            await Console.Error.WriteLineAsync($"Cannot write {settings.Output.FullName}: {error}").ConfigureAwait(false);
            return 2;
        }

        await using var client = new GatewayClient(options, loggerFactory.CreateLogger<GatewayClient>());
        var recorder = new GatewayRecorder(client, writer, new MessageParser(loggerFactory.CreateLogger<MessageParser>()));

        if (settings.TtSummary)
        {
            client.ConnectionChanged += (_, change) =>
            {
                if (change.State == GatewayConnectionState.Connected)
                {
                    _ = SendQuietlyAsync(client, GatewayCommands.TimetableSummary(), log);
                }
            };
        }

        var target = options.ToString();
        LogStart(log, settings.Output.FullName, target);
        using var statusStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var status = ReportStatusAsync(recorder.Stats, writer, log, statusStop.Token);
        try
        {
            await recorder.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayConnectException ex)
        {
            LogRejected(log, ex.Message);
            return 1;
        }
        finally
        {
            await statusStop.CancelAsync().ConfigureAwait(false);
            await status.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        await Console.Out.WriteAsync(Summary(recorder.Stats, writer.MessagesWritten, settings.Output.FullName)).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Creates the output folder and file; a bad path (e.g. <c>&lt;</c> on Windows) gives a message, not a stack trace.</summary>
    private static SslogWriter? TryCreateWriter(Settings settings, SslogHeader header, out string? error)
    {
        try
        {
            settings.Output.Directory?.Create();
            if (settings.Force)
            {
                settings.Output.Delete();
            }

            error = null;
            return SslogWriter.Create(settings.Output.FullName, header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool TryGetPassword(out string? password)
    {
        password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (!string.IsNullOrEmpty(password))
        {
            return true;
        }

        if (Console.IsInputRedirected)
        {
            return false;
        }

        Console.Error.Write("SimSig password: ");
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        password = text.ToString();
        return password.Length > 0;
    }

    private static async Task SendQuietlyAsync(GatewayClient client, GatewayCommand command, ILogger log)
    {
        try
        {
            await client.SendAsync(command).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            LogSendFailed(log, command.ToString(), ex.Message);
        }
    }

    /// <summary>Flushes the recording every second, so a quiet sim or a killed process loses little, and logs progress every ten.</summary>
    private static async Task ReportStatusAsync(RecordingStats stats, SslogWriter writer, ILogger log, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastMessages = 0L;
        var ticks = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            writer.Flush();

            // Retries are already logged by the client; only report progress while recording.
            if (++ticks % 10 != 0 || stats.State != GatewayConnectionState.Connected)
            {
                continue;
            }

            var messages = stats.Messages;
            var clock = stats.LastClock?.ToString() ?? "--:--:--";
            var speed = stats.SpeedRatio is { } ratio ? ratio.ToString("0.##", CultureInfo.InvariantCulture) + "×" + (stats.Paused ? " paused" : string.Empty) : "?";
            var rate = (messages - lastMessages) / 10.0;
            LogStatus(log, stats.State, stats.AreaId ?? "?", clock, speed, messages, rate);
            lastMessages = messages;
        }
    }

    internal static string Summary(RecordingStats stats, long written, string path)
    {
        var elapsed = DateTimeOffset.UtcNow - stats.Started;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Recorded {written} messages ({stats.Bytes / 1024.0:0.#} KiB) in {elapsed:hh\\:mm\\:ss} to {path}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Sim: {stats.AreaId ?? "?"}, last clock {stats.LastClock?.ToString() ?? "?"}, connections {stats.Connections}, unparsed {stats.UnknownEvents}");
        foreach (var (kind, count) in stats.ByKind.OrderByDescending(k => k.Value))
        {
            var rate = elapsed.TotalSeconds > 0 ? count / elapsed.TotalSeconds : 0;
            text.AppendLine(CultureInfo.InvariantCulture, $"  {kind,-24} {count,9}  {rate,8:0.00}/s");
        }

        return text.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Target} to {Path}. Press Ctrl+C to stop.")]
    private static partial void LogStart(ILogger log, string path, string target);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stopped: {Reason}")]
    private static partial void LogRejected(ILogger log, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send {Command}: {Reason}")]
    private static partial void LogSendFailed(ILogger log, string command, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{State} {Area} {Clock} {Speed} — {Messages} messages, {Rate:0.0}/s")]
    private static partial void LogStatus(ILogger log, GatewayConnectionState state, string area, string clock, string speed, long messages, double rate);
}
