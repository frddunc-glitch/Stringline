using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Core.Live;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline match</c>: play a recording through <see cref="LiveState"/> against its WTT, as fast as possible, and
/// report how the live trains matched.
/// </summary>
internal static class MatchCommand
{
    public static Command Create()
    {
        var wtt = new Option<FileInfo>("--wtt") { Description = "The SimSig timetable the recording was made with (.wtt).", Required = true };
        var log = new Option<FileInfo>("--log") { Description = "The recording (.sslog).", Required = true };
        var all = new Option<bool>("--all") { Description = "List every working, not only the ones that did not match." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging, including each match decision." };

        var command = new Command("match", "Replay a recording against its timetable and report which live trains matched which WTT trains.")
        {
            wtt, log, all, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(wtt)!, parse.GetValue(log)!, parse.GetValue(all), parse.GetValue(verbose)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo Wtt, FileInfo Log, bool All, bool Verbose);

    /// <returns>0 when done, 2 if a file cannot be read.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        WttImportResult import;
        SslogRecording recording;
        try
        {
            import = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(settings.Wtt.FullName);
            recording = SslogRecording.Load(settings.Log.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException or InvalidDataException)
        {
            await error.WriteLineAsync($"Cannot read input: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var state = new LiveState(import.Timetable, logger: loggerFactory.CreateLogger<LiveState>());
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        foreach (var message in recording.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var e in parser.Parse(message.ToGatewayMessage()))
            {
                state.Apply(e);
            }
        }

        var text = new StringWriter(CultureInfo.InvariantCulture);
        Write(text, state, settings.Log.Name, settings.Wtt.Name, settings.All);
        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    internal static void Write(TextWriter w, LiveState state, string logName, string wttName, bool all)
    {
        var s = state.Summarise();
        w.WriteLine($"{logName} against {wttName}: sim {state.Clock.AreaId ?? "?"}, {state.SessionStart?.ToString() ?? "-"} to {state.Clock.LastReported}.");
        w.WriteLine($"{s.Trains} live trains, {s.Workings} workings: {s.Matched} matched ({s.MatchedShare:P1}), {s.Pending} pending, {s.Unmatched} unmatched.");
        if (s.ByMethod.Count > 0)
        {
            w.WriteLine($"Matched by: {string.Join(", ", s.ByMethod.OrderBy(m => m.Key).Select(m => $"{Describe(m.Key)} {m.Value}"))}.");
        }

        var rows = new List<string[]>();
        foreach (var train in state.Trains)
        {
            for (var i = 0; i < train.Workings.Count; i++)
            {
                var working = train.Workings[i];
                if (!all && working.IsMatched)
                {
                    continue;
                }

                var actuals = train.ActualsOf(i).ToList();
                var first = actuals.FirstOrDefault(a => a.AtTimingPoint) ?? actuals[0];
                rows.Add(
                [
                    train.SimUid,
                    working.Headcode.Value,
                    working.Status.ToString(),
                    working.Train?.Value ?? "",
                    working.IsMatched ? Describe(working.Method) : "",
                    working.DeviationSeconds?.ToString(CultureInfo.InvariantCulture) ?? "",
                    $"{first.Action.ToString().ToLowerInvariant()} {first.Location} {first.Time}",
                    working.Reason ?? "",
                ]);
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        w.WriteLine();
        string[] header = ["Uid", "Headcode", "Status", "Train", "By", "Dev s", "First seen", "Note"];
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        w.WriteLine(Row(header).TrimEnd());
        foreach (var row in rows)
        {
            w.WriteLine(Row(row).TrimEnd());
        }

        string Row(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i])));
    }

    private static string Describe(MatchMethod method) => method switch
    {
        MatchMethod.HeadcodeAndTime => "headcode+time",
        MatchMethod.NextWorking => "next working",
        MatchMethod.Formed => "formed",
        MatchMethod.Manual => "manual",
        MatchMethod.Untimed => "untimed",
        _ => "-",
    };
}
