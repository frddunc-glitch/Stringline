using System.CommandLine;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Planning;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline report</c>: the delay table (SES-2, S17) as CSV, from a recording or a saved session played up to a
/// time. A session also brings its plan edits (as they stood at that time), so this is the post-run report of a session.
/// </summary>
internal static class ReportCommand
{
    public static Command Create()
    {
        var output = new Argument<FileInfo>("output") { Description = "The CSV file to write." };
        var log = new Option<FileInfo?>("--log") { Description = "A recording (.sslog) to report on." };
        var session = new Option<FileInfo?>("--session") { Description = "A saved session (.stringline) to report on, with its plan edits." };
        var wtt = new Option<FileInfo?>("--wtt") { Description = "The SimSig timetable (.wtt). Default for --session: the one it used." };
        var profile = new Option<FileInfo?>("--profile") { Description = "A profile JSON (location names, sections for the forecast). Default: generated from the WTT." };
        var at = new Option<string?>("--at") { Description = "Report as at this time (HH:mm or HH:mm:ss). Default: the end." };
        var all = new Option<bool>("--all") { Description = "List every train in the plan, not only those seen." };
        var byTrain = new Option<bool>("--by-train") { Description = "One row per train (worst and final delay) instead of one per train and timing point." };
        var noKnockOn = new Option<bool>("--no-knock-on") { Description = "Forecast each train on its own, without knock-on delay from the others." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("report", "Write the delay table (booked, planned, actual or forecast times and delays) as CSV.")
        {
            output, log, session, wtt, profile, at, all, byTrain, noKnockOn, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(output)!, parse.GetValue(log), parse.GetValue(session), parse.GetValue(wtt), parse.GetValue(profile), parse.GetValue(at))
            {
                All = parse.GetValue(all),
                ByTrain = parse.GetValue(byTrain),
                NoKnockOn = parse.GetValue(noKnockOn),
                Verbose = parse.GetValue(verbose),
            },
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo Output, FileInfo? Log, FileInfo? Session, FileInfo? Wtt, FileInfo? Profile, string? At)
    {
        public bool All { get; init; }

        public bool ByTrain { get; init; }

        public bool NoKnockOn { get; init; }

        public bool Verbose { get; init; }
    }

    /// <returns>0 on success, 2 on bad arguments or a file that cannot be read or written.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if ((settings.Log is null) == (settings.Session is null))
        {
            await error.WriteLineAsync("Give either --log or --session.").ConfigureAwait(false);
            return 2;
        }

        SimTime? at = null;
        if (settings.At is not null)
        {
            if (!SimTime.TryParse(settings.At, out var parsed))
            {
                await error.WriteLineAsync($"--at '{settings.At}' is not a time (HH:mm or HH:mm:ss).").ConfigureAwait(false);
                return 2;
            }

            at = parsed;
        }

        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        SavedSession? saved = null;
        WttImportResult import;
        StringlineProfile profile;
        IEnumerable<GatewayEvent> events;
        try
        {
            saved = settings.Session is null ? null : SavedSession.Load(settings.Session.FullName);
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
                var recording = SslogRecording.Load(settings.Log!.FullName);
                events = recording.Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()));
            }
            else if (saved.Info.Kind == SavedSessionKind.Replay)
            {
                if (saved.Info.RecordingPath is not { } recordingPath)
                {
                    await error.WriteLineAsync("The replay session names no recording.").ConfigureAwait(false);
                    return 2;
                }

                var recording = SslogRecording.Load(recordingPath);
                events = recording.Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()));
                at ??= saved.Info.Position;
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

        var tt = import.Timetable;
        var plan = WorkingPlan.FromTimetable(tt);
        var edits = 0;
        if (saved is not null)
        {
            var done = PlanLog.Fold(at is null ? saved.Steps : PlanLog.By(saved.Steps, at)).Done;
            var (edited, results) = plan.ApplyAll(done.Select(s => s.Command!));
            plan = edited;
            edits = results.Count(r => r.Applied);
            foreach (var problem in saved.Problems.Concat(results.Where(r => !r.Applied).Select(r => r.Error ?? "an edit no longer applies")))
            {
                await error.WriteLineAsync($"Skipped: {problem}").ConfigureAwait(false);
            }
        }

        var state = new LiveState(tt, logger: loggerFactory.CreateLogger<LiveState>());
        foreach (var link in plan.Links)
        {
            if (link.Train is not { } id || tt.Find(id) is not null)
            {
                state.Link(link.SimUid, link.Headcode, link.Train);
            }
        }

        RenderPngCommand.Play(state, events, at, cancellationToken);
        var live = state.Trains.ToList();
        var now = at ?? (state.Clock.HasTime ? state.Clock.LastReported : null);
        var network = ForecastNetwork.For(profile, tt);
        var options = new ForecastOptions { Interaction = !settings.NoKnockOn };
        var forecast = Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = live,
            Now = now,
            SessionStart = state.SessionStart,
            EntryDelays = state.EntryDelays,
            Network = network,
            Options = options,
        });
        var clock = state.Clock.HasTime ? SimClockReading.None with { HasTime = true, LastReported = now ?? state.Clock.LastReported, AreaId = state.Clock.AreaId } : SimClockReading.None;
        var snapshot = new PlanSnapshot(1, tt, live, clock, state.Summarise(), state.Version, state.SessionStart, plan, Forecast: forecast);
        var report = DelayReport.For(snapshot, settings.All ? DelayReportFilter.All : DelayReportFilter.Seen);
        var title = saved?.Info.SimName is { } savedName ? SimTitle.From(savedName, state.Clock.AreaId, tt.Header) : profile.Title(state.Clock.AreaId, tt.Header);

        try
        {
            await using var writer = new StreamWriter(settings.Output.FullName, append: false, DelayReportCsv.Encoding);
            if (settings.ByTrain)
            {
                DelayReportCsv.WriteTrains(writer, report, title);
            }
            else
            {
                DelayReportCsv.WritePoints(writer, report, profile, title);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Cannot write {settings.Output.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var late = report.Trains.Count(t => t.WorstDelaySeconds >= SimTime.SecondsPerMinute);
        var worst = report.Trains.Where(t => t.WorstDelaySeconds is not null).MaxBy(t => t.WorstDelaySeconds);
        await output.WriteLineAsync(
            $"Wrote {settings.Output.Name}: {report.Trains.Count} trains{(settings.ByTrain ? string.Empty : $", {report.Trains.Sum(t => t.Points.Count)} rows")}, as at {now?.ToString() ?? "the start"}" +
            (saved is null ? string.Empty : $", {edits} edit{(edits == 1 ? string.Empty : "s")}") +
            $"; {late} late at some point" +
            (worst is null ? "." : $", worst {worst.Headcode} {worst.WorstDelaySeconds / SimTime.SecondsPerMinute} min at {worst.Worst!.Location}.") +
            (report.UnmatchedLive > 0 ? $" {report.UnmatchedLive} live trains not matched to the timetable are left out." : string.Empty) +
            (report.NotRunning > 0 ? $" {report.NotRunning} trains SimSig will not run this session are counted apart." : string.Empty)).ConfigureAwait(false);
        return 0;
    }
}
