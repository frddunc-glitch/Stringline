using System.CommandLine;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline render-png</c>: draw one corridor's train graph to a PNG, from a WTT and optionally a recording
/// played up to the chosen time, with the same renderer the app uses.
/// </summary>
internal static class RenderPngCommand
{
    public static Command Create()
    {
        var output = new Argument<FileInfo>("output") { Description = "The PNG file to write." };
        var wtt = new Option<FileInfo>("--wtt") { Description = "The SimSig timetable (.wtt).", Required = true };
        var log = new Option<FileInfo?>("--log") { Description = "A recording (.sslog) to draw actuals from, played up to --at." };
        var profile = new Option<FileInfo?>("--profile") { Description = "A profile JSON (corridors, names, colours). Default: generated from the WTT." };
        var corridor = new Option<string?>("--corridor") { Description = "Corridor ID, e.g. main. Default: the profile's first." };
        var at = new Option<string?>("--at") { Description = "The now time (HH:mm or HH:mm:ss). Default: the end of the recording, or the timetable's start without one." };
        var hours = new Option<double>("--hours") { Description = "Hours across the graph.", DefaultValueFactory = _ => 3 };
        var nowAt = new Option<double>("--now-at") { Description = "Where the now line sits across the graph, 0 (left) to 1 (right).", DefaultValueFactory = _ => 1.0 / 3 };
        var width = new Option<int>("--width") { Description = "Image width in pixels.", DefaultValueFactory = _ => 1600 };
        var height = new Option<int>("--height") { Description = "Image height in pixels.", DefaultValueFactory = _ => 900 };
        var noWtt = new Option<bool>("--no-wtt") { Description = "Leave out the WTT layer." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("render-png", "Draw a corridor's train graph (WTT and actuals) to a PNG.")
        {
            output, wtt, log, profile, corridor, at, hours, nowAt, width, height, noWtt, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(output)!, parse.GetValue(wtt)!, parse.GetValue(log), parse.GetValue(profile), parse.GetValue(corridor),
                parse.GetValue(at), parse.GetValue(hours), parse.GetValue(nowAt), parse.GetValue(width), parse.GetValue(height), parse.GetValue(noWtt), parse.GetValue(verbose)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(
        FileInfo Output,
        FileInfo Wtt,
        FileInfo? Log,
        FileInfo? Profile,
        string? Corridor,
        string? At,
        double Hours,
        double NowAt,
        int Width,
        int Height,
        bool NoWtt,
        bool Verbose);

    /// <returns>0 on success, 2 on bad arguments or a file that cannot be read or written.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
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

        if (settings.Hours is <= 0 or > 48 || settings.NowAt is < 0 or > 1 || settings.Width is < 200 or > 16384 || settings.Height is < 150 or > 16384)
        {
            await error.WriteLineAsync("--hours must be in (0, 48], --now-at in [0, 1], --width 200–16384 and --height 150–16384.").ConfigureAwait(false);
            return 2;
        }

        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        WttImportResult import;
        StringlineProfile profile;
        SslogRecording? recording = null;
        try
        {
            import = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(settings.Wtt.FullName);
            profile = settings.Profile is null
                ? StringlineProfile.Create(import.Timetable)
                : ProfileJson.Deserialize(await File.ReadAllTextAsync(settings.Profile.FullName, cancellationToken).ConfigureAwait(false));
            if (settings.Log is not null)
            {
                recording = SslogRecording.Load(settings.Log.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException or InvalidDataException or ProfileFormatException)
        {
            await error.WriteLineAsync($"Cannot read input: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var tt = import.Timetable;
        var corridor = settings.Corridor is null ? (profile.Corridors.Count > 0 ? profile.Corridors[0] : null) : profile.FindCorridor(settings.Corridor);
        if (corridor is null)
        {
            var known = profile.Corridors.Count == 0 ? "none" : string.Join(", ", profile.Corridors.Select(c => c.Id));
            await error.WriteLineAsync($"No corridor '{settings.Corridor}' in the profile (corridors: {known}).").ConfigureAwait(false);
            return 2;
        }

        IReadOnlyCollection<Core.Live.LiveTrain> live = [];
        var now = at;
        if (recording is not null)
        {
            var state = Play(recording, tt, at, loggerFactory, cancellationToken);
            live = state.Trains.ToList();
            now ??= state.Clock.HasTime ? state.Clock.LastReported : null;
        }

        now ??= tt.Header.StartTime;
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(profile, corridor)
        {
            Timetable = tt,
            LiveTrains = live,
            Now = recording is null && at is null ? null : now,
            ShowWtt = !settings.NoWtt,
        });
        var viewport = GraphViewport.Around(now.Value, (int)Math.Round(settings.Hours * SimTime.SecondsPerHour), settings.Width, settings.Height, settings.NowAt);

        byte[] png;
        using (var renderer = new SkiaGraphRenderer())
        {
            png = renderer.RenderPng(scene, viewport);
        }

        try
        {
            await File.WriteAllBytesAsync(settings.Output.FullName, png, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Cannot write {settings.Output.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var visible = scene.Paths.Count(p => viewport.Overlaps(p.TimeMin, p.TimeMax, p.AxisMin, p.AxisMax));
        await output.WriteLineAsync(
            $"Wrote {settings.Output.Name}: {corridor.Id} ({corridor.Name}), {viewport.TimeStart.ToSimTime()}–{viewport.TimeEnd.ToSimTime()}, " +
            $"now {now}, {visible} paths in view ({scene.InLayer(GraphLayer.Actual).Count()} actual in all).").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Plays a recording through <see cref="LiveState"/> until the first clock or movement after <paramref name="until"/>.</summary>
    internal static LiveState Play(SslogRecording recording, Core.Timetables.WorkingTimetable tt, SimTime? until, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var state = new LiveState(tt, logger: loggerFactory.CreateLogger<LiveState>());
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        foreach (var message in recording.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var e in parser.Parse(message.ToGatewayMessage()))
            {
                var time = e switch
                {
                    ClockEvent c => c.Clock,
                    TrainLocationEvent m => m.Time,
                    _ => (SimTime?)null,
                };
                if (until is not null && time > until && !(e is ClockEvent { Clock.Seconds: 0 }))
                {
                    return state;
                }

                state.Apply(e);
            }
        }

        return state;
    }

    private static SimTime ToSimTime(this double seconds) => new((int)Math.Round(seconds));
}
