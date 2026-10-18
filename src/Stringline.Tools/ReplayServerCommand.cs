using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;

namespace Stringline.Tools;

/// <summary><c>stringline replay-server</c>: play a <c>.sslog</c> recording as a fake SimSig gateway until Ctrl+C.</summary>
internal static partial class ReplayServerCommand
{
    public static Command Create()
    {
        var recording = new Argument<FileInfo>("recording") { Description = "Recording to play (.sslog)." };
        var speed = new Option<double>("--speed")
        {
            Description = "Playback speed relative to the recording, e.g. 8. 0 plays as fast as clients can take it.",
            DefaultValueFactory = _ => 1,
        };
        var host = new Option<string>("--host")
        {
            Description = "Address to listen on: 127.0.0.1 (default, this PC only), localhost, any, or an IP address.",
            DefaultValueFactory = _ => "127.0.0.1",
        };
        var port = new Option<int>("--port") { Description = "Port to listen on.", DefaultValueFactory = _ => GatewayTopics.DefaultPort };
        var start = new Option<string?>("--start") { Description = "Sim time to start from, e.g. 09:00. Earlier frames build up state but are not sent." };
        var noWait = new Option<bool>("--no-wait") { Description = "Start playing straight away instead of waiting for the first client." };
        var exitAtEnd = new Option<bool>("--exit-at-end") { Description = "Stop when the recording ends instead of serving until Ctrl+C." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("replay-server", "Play a .sslog recording as a fake SimSig Interface Gateway (STOMP) until Ctrl+C.")
        {
            recording, speed, host, port, start, noWait, exitAtEnd, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(
                parse.GetValue(recording)!,
                parse.GetValue(speed),
                parse.GetValue(host)!,
                parse.GetValue(port),
                parse.GetValue(start),
                parse.GetValue(noWait),
                parse.GetValue(exitAtEnd),
                parse.GetValue(verbose)),
            cancellationToken));
        return command;
    }

    private sealed record Settings(FileInfo Recording, double Speed, string Host, int Port, string? Start, bool NoWait, bool ExitAtEnd, bool Verbose);

    private static async Task<int> RunAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (!TryGetOptions(settings, out var options, out var problem))
        {
            await Console.Error.WriteLineAsync(problem).ConfigureAwait(false);
            return 2;
        }

        SslogRecording recording;
        try
        {
            recording = SslogRecording.Load(settings.Recording.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Console.Error.WriteLineAsync($"Cannot read {settings.Recording.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        await Console.Out.WriteAsync(Describe(recording, settings.Recording.Name)).ConfigureAwait(false);

        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        var log = loggerFactory.CreateLogger("replay-server");
        ReplayServer server;
        try
        {
            server = ReplayServer.Start(recording, options, loggerFactory.CreateLogger<ReplayServer>());
        }
        catch (SocketException ex)
        {
            await Console.Error.WriteLineAsync($"Cannot listen on {options.Address}:{options.Port}: {ex.Message}. Is SimSig (or another replay server) using the port?").ConfigureAwait(false);
            return 2;
        }

        await using (server.ConfigureAwait(false))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            LogReady(log, server.Port, options.WaitForClient ? "waiting for the first client" : "playing");
            if (!Console.IsInputRedirected)
            {
                LogKeys(log);
                _ = Task.Run(() => ReadKeysAsync(server.Timeline, stop, log), CancellationToken.None);
            }

            var status = ReportStatusAsync(server, log, stop.Token);
            try
            {
                if (settings.ExitAtEnd)
                {
                    await server.Completion.WaitAsync(stop.Token).ConfigureAwait(false);
                    await server.FlushAsync(TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C or Q.
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await status.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await Console.Out.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"Played {server.Timeline.NextIndex} of {recording.Messages.Count} frames, sent {server.MessagesSent} messages, last sim time {server.Timeline.Position?.ToString() ?? "?"}.")).ConfigureAwait(false);
        }

        return 0;
    }

    private static bool TryGetOptions(Settings settings, out ReplayServerOptions options, out string problem)
    {
        options = new ReplayServerOptions();
        problem = string.Empty;
        if (!settings.Recording.Exists)
        {
            problem = $"{settings.Recording.FullName} does not exist.";
            return false;
        }

        if (settings.Speed < 0 || double.IsNaN(settings.Speed) || double.IsInfinity(settings.Speed))
        {
            problem = "--speed must be 0 (as fast as possible) or a positive number.";
            return false;
        }

        if (settings.Port is < 0 or > IPEndPoint.MaxPort)
        {
            problem = "--port must be between 0 and 65535.";
            return false;
        }

        if (ParseAddress(settings.Host) is not { } address)
        {
            problem = $"--host '{settings.Host}' is not an IP address, localhost or any.";
            return false;
        }

        SimTime? startAt = null;
        if (settings.Start is { } text)
        {
            if (!SimTime.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
            {
                problem = $"--start '{text}' is not a sim time such as 09:00 or 09:00:30.";
                return false;
            }

            startAt = parsed;
        }

        options = new ReplayServerOptions
        {
            Address = address,
            Port = settings.Port,
            WaitForClient = !settings.NoWait,
            Replay = new ReplayOptions { Speed = settings.Speed == 0 ? double.PositiveInfinity : settings.Speed, StartAt = startAt },
        };
        return true;
    }

    internal static IPAddress? ParseAddress(string host) => host.Trim().ToUpperInvariant() switch
    {
        "LOCALHOST" => IPAddress.Loopback,
        "ANY" or "*" => IPAddress.Any,
        _ => IPAddress.TryParse(host.Trim(), out var address) ? address : null,
    };

    /// <summary>A short description of the recording: sim, times, size and what was recorded.</summary>
    internal static string Describe(SslogRecording recording, string name)
    {
        var parser = new MessageParser();
        var area = recording.Messages
            .Where(m => m.Topic == GatewayTopics.SimSig)
            .SelectMany(m => parser.Parse(m.ToGatewayMessage()))
            .OfType<ClockEvent>()
            .Select(c => c.AreaId)
            .FirstOrDefault(a => a.Length > 0);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{name}: sim {area ?? "?"}, {recording.FirstSim?.ToString() ?? "?"} to {recording.LastSim?.ToString() ?? "?"}, {recording.Messages.Count} frames over {recording.Duration:hh\\:mm\\:ss} recorded {recording.Header.Created:yyyy-MM-dd HH:mm} UTC");
        text.AppendLine(CultureInfo.InvariantCulture, $"Topics: {string.Join(", ", recording.Header.Topics)}");
        if (recording.SkippedLines.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Skipped {recording.SkippedLines.Count} unreadable lines, first: {recording.SkippedLines[0]}");
        }

        return text.ToString();
    }

    private static void LogControl(ILogger log, string what, ReplayTimeline timeline)
    {
        var speed = FormatSpeed(timeline.Speed);
        LogControl(log, what, speed);
    }

    internal static string FormatSpeed(double speed) =>
        double.IsPositiveInfinity(speed) ? "max speed" : speed.ToString("0.###", CultureInfo.InvariantCulture) + "×";

    /// <summary>Space pauses or resumes, + and - double or halve the speed, Q stops.</summary>
    private static async Task ReadKeysAsync(ReplayTimeline timeline, CancellationTokenSource stop, ILogger log)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(100, stop.Token).ConfigureAwait(false);
                    continue;
                }

                switch (Console.ReadKey(intercept: true).KeyChar)
                {
                    case ' ':
                        if (timeline.IsPaused)
                        {
                            timeline.Resume();
                        }
                        else
                        {
                            timeline.Pause();
                        }

                        LogControl(log, timeline.IsPaused ? "Paused" : "Playing", timeline);
                        break;
                    case '+' or '=':
                        timeline.Speed = double.IsPositiveInfinity(timeline.Speed) ? timeline.Speed : timeline.Speed * 2;
                        LogControl(log, "Speed", timeline);
                        break;
                    case '-' or '_':
                        timeline.Speed = double.IsPositiveInfinity(timeline.Speed) ? 64 : Math.Max(timeline.Speed / 2, 1.0 / 64);
                        LogControl(log, "Speed", timeline);
                        break;
                    case 'q' or 'Q':
                        await stop.CancelAsync().ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            // Stopped, or the console went away.
        }
    }

    private static async Task ReportStatusAsync(ReplayServer server, ILogger log, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var total = server.Timeline.Recording.Messages.Count;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var timeline = server.Timeline;
            var state = timeline.IsFinished ? "finished" : timeline.IsPaused ? "paused" : "playing";
            var sim = timeline.Position?.ToString() ?? "--:--:--";
            var speed = FormatSpeed(timeline.Speed);
            LogStatus(log, sim, state, speed, server.ClientCount, server.MessagesSent, timeline.NextIndex, total);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ready on port {Port}, {State}. Press Ctrl+C to stop.")]
    private static partial void LogReady(ILogger log, int port, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Keys: Space pause/resume, + faster, - slower, Q quit.")]
    private static partial void LogKeys(ILogger log);

    [LoggerMessage(Level = LogLevel.Information, Message = "{What} at {Speed}")]
    private static partial void LogControl(ILogger log, string what, string speed);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sim} {State} {Speed} — {Clients} clients, {Sent} sent, frame {Frame}/{Total}")]
    private static partial void LogStatus(ILogger log, string sim, string state, string speed, int clients, long sent, int frame, int total);
}
