using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Planning;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline gaps</c>: how often a recording's trains report where they are on the corridors (S36, #17), as the
/// baseline for S38: unbooked timing locations, and the gaps between reports with and without them and signals.
/// </summary>
internal static class GapsCommand
{
    public static Command Create()
    {
        var log = new Option<FileInfo?>("--log") { Description = "A recording (.sslog)." };
        var session = new Option<FileInfo?>("--session") { Description = "A saved live session (.stringline)." };
        var wtt = new Option<FileInfo?>("--wtt") { Description = "The SimSig timetable (.wtt). Default for --session: the one it used." };
        var profile = new Option<FileInfo?>("--profile") { Description = "A profile JSON (its corridors). Default: generated from the WTT." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("gaps", "Measure how long trains go between reports on the corridors, and how often they report a location their WTT leaves out.")
        {
            log, session, wtt, profile, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(log), parse.GetValue(session), parse.GetValue(wtt), parse.GetValue(profile), parse.GetValue(verbose)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo? Log, FileInfo? Session, FileInfo? Wtt, FileInfo? Profile, bool Verbose = false);

    /// <returns>0 on success, 2 on bad arguments or a file that cannot be read.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if ((settings.Log is null) == (settings.Session is null))
        {
            await error.WriteLineAsync("Give either --log or --session.").ConfigureAwait(false);
            return 2;
        }

        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        WttImportResult import;
        StringlineProfile profile;
        IEnumerable<GatewayEvent> events;
        try
        {
            var saved = settings.Session is null ? null : SavedSession.Load(settings.Session.FullName);
            var wttPath = settings.Wtt?.FullName ?? saved?.Info.TimetablePath;
            if (wttPath is null)
            {
                await error.WriteLineAsync("The session names no timetable: give --wtt.").ConfigureAwait(false);
                return 2;
            }

            import = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(wttPath);
            profile = settings.Profile is null
                ? StringlineProfile.Create(import.Timetable)
                : ProfileJson.Deserialize(await File.ReadAllTextAsync(settings.Profile.FullName, cancellationToken).ConfigureAwait(false));
            if (saved is null)
            {
                events = SslogRecording.Load(settings.Log!.FullName).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()));
            }
            else if (saved.Info.Kind == SavedSessionKind.Replay)
            {
                if (saved.Info.RecordingPath is not { } recordingPath)
                {
                    await error.WriteLineAsync("The replay session names no recording.").ConfigureAwait(false);
                    return 2;
                }

                events = SslogRecording.Load(recordingPath).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()));
            }
            else
            {
                events = saved.Events(parser);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException or InvalidDataException or ProfileFormatException or SessionFormatException)
        {
            await error.WriteLineAsync($"Cannot read input: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var state = new LiveState(import.Timetable, logger: loggerFactory.CreateLogger<LiveState>());
        RenderPngCommand.Play(state, events, null, cancellationToken);
        var gaps = ReportGaps.Measure(import.Timetable, state.Trains, profile.Corridors, profile.Places.Count == 0 ? null : UsablePlaces.Of(profile.Places));
        var text = new StringWriter(CultureInfo.InvariantCulture);
        Write(text, gaps, state.SessionStart?.ToString() ?? "-", state.Clock.LastReported.ToString(), profile.Corridors.Count);
        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    internal static void Write(TextWriter w, ReportGaps gaps, string from, string to, int corridors)
    {
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{from} to {to}: {gaps.Workings} matched workings, {corridors} corridors."));
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Timing location reports: {gaps.TimingReports}; not in the train's WTT: {gaps.UnbookedReports} ({gaps.UnbookedShare:P1}), {gaps.UnbookedOnCorridor} of them on a corridor."));
        w.WriteLine("Gaps between reports on the corridors (seconds):");
        w.WriteLine("  Reports                      Gaps   Median   95th   Longest");
        Row("booked timing locations", gaps.Booked);
        Row("all timing locations", gaps.Timing);
        Row("with signals", gaps.Signals);
        if (gaps.Placed is { } placed)
        {
            Row("placed signals (S38)", placed);
        }

        void Row(string name, GapStats s) =>
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {name,-26} {s.Count,6} {s.Median,8} {s.P95,6} {s.Max,9}"));
    }
}
