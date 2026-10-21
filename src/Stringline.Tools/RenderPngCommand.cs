using System.CommandLine;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Planning;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline render-png</c> (or <c>render</c>): draw one corridor's train graph to a PNG, or to a PDF when the output
/// ends in <c>.pdf</c> (S17), from a WTT and optionally a recording played up to the chosen time, with the same renderer
/// the app uses. With <c>--plot</c> (S21) the PDF is a multi-page plot: a time range at a fixed scale a page, time
/// running down on A3 portrait unless told otherwise.
/// </summary>
internal static class RenderPngCommand
{
    public static Command Create()
    {
        var output = new Argument<FileInfo>("output") { Description = "The PNG file to write, or a PDF if it ends in .pdf." };
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
        var vertical = new Option<bool>("--vertical") { Description = "Time runs down the image, the corridor across (Up end on the left)." };
        var colour = new Option<GraphColourMode>("--colour") { Description = "Colour actual lines by category or by delay band.", DefaultValueFactory = _ => GraphColourMode.Category };
        var noForecast = new Option<bool>("--no-forecast") { Description = "Leave out the forecast layer (drawn with --log)." };
        var noKnockOn = new Option<bool>("--no-knock-on") { Description = "Forecast each train on its own, without knock-on delay from the others." };
        var noConflicts = new Option<bool>("--no-conflicts") { Description = "Leave out the conflict marks (found with --log, unless --no-forecast)." };
        var liveHead = new Option<bool>("--live-head") { Description = "Draw each running train's live head: its line carried on from its last report to the time (with --log)." };
        var signals = new Option<SignalDisplay>("--signals") { Description = "Placed signals on the location axis (the profile's learned places): off, ticks or rows.", DefaultValueFactory = _ => SignalDisplay.Ticks };
        var page = new Option<string?>("--page") { Description = "PDF page: a3 or a4 (landscape), a3p or a4p (portrait), or view (--width × --height points). Default: a3, or a3p for a vertical plot." };
        var plot = new Option<bool>("--plot") { Description = "Write a multi-page PDF plot of --from to --to at --per-page a page, time running down (S21)." };
        var from = new Option<string?>("--from") { Description = "With --plot: where it starts (HH:mm). Default: the hour before --at, or the timetable's start." };
        var to = new Option<string?>("--to") { Description = "With --plot: where it ends (HH:mm). Default: --hours after --from." };
        var perPage = new Option<int>("--per-page") { Description = "With --plot: minutes a page.", DefaultValueFactory = _ => GraphPlotOptions.DefaultSecondsPerPage / SimTime.SecondsPerMinute };
        var horizontal = new Option<bool>("--horizontal") { Description = "With --plot: time runs across the pages rather than down." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("render-png", "Draw a corridor's train graph (WTT, actuals and forecast) to a PNG, or a PDF.")
        {
            output, wtt, log, profile, corridor, at, hours, nowAt, width, height, noWtt, vertical, colour, noForecast, noKnockOn, noConflicts, liveHead, signals, page, plot, from, to, perPage, horizontal, verbose,
        };
        command.Aliases.Add("render");

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(output)!, parse.GetValue(wtt)!, parse.GetValue(log), parse.GetValue(profile), parse.GetValue(corridor),
                parse.GetValue(at), parse.GetValue(hours), parse.GetValue(nowAt), parse.GetValue(width), parse.GetValue(height), parse.GetValue(noWtt), parse.GetValue(verbose))
            {
                Vertical = parse.GetValue(vertical),
                Colour = parse.GetValue(colour),
                NoForecast = parse.GetValue(noForecast),
                NoKnockOn = parse.GetValue(noKnockOn),
                NoConflicts = parse.GetValue(noConflicts),
                LiveHead = parse.GetValue(liveHead),
                Signals = parse.GetValue(signals),
                Page = parse.GetValue(page),
                Plot = parse.GetValue(plot),
                From = parse.GetValue(from),
                To = parse.GetValue(to),
                PerPageMinutes = parse.GetValue(perPage),
                Horizontal = parse.GetValue(horizontal),
            },
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
        bool Verbose)
    {
        /// <summary>Time down the image rather than across.</summary>
        public bool Vertical { get; init; }

        public GraphColourMode Colour { get; init; } = GraphColourMode.Category;

        /// <summary>Leave out the forecast layer.</summary>
        public bool NoForecast { get; init; }

        /// <summary>Forecast without the interaction pass.</summary>
        public bool NoKnockOn { get; init; }

        /// <summary>Leave out the conflict marks.</summary>
        public bool NoConflicts { get; init; }

        /// <summary>Draw the live heads (S36); off by default in exports.</summary>
        public bool LiveHead { get; init; }

        /// <summary>How placed signals show on the axis (S38); ticks, as in the app.</summary>
        public SignalDisplay Signals { get; init; } = SignalDisplay.Ticks;

        /// <summary>The PDF page by name (<see cref="PdfPage.Parse"/>), for a <c>.pdf</c> output; null for the default.</summary>
        public string? Page { get; init; }

        /// <summary>A multi-page plot (S21) rather than one view.</summary>
        public bool Plot { get; init; }

        /// <summary>Where the plot starts; null for the hour before <see cref="At"/>, or the timetable's start.</summary>
        public string? From { get; init; }

        /// <summary>Where the plot ends; null for <see cref="Hours"/> after the start.</summary>
        public string? To { get; init; }

        public int PerPageMinutes { get; init; } = GraphPlotOptions.DefaultSecondsPerPage / SimTime.SecondsPerMinute;

        /// <summary>A plot with time across the pages.</summary>
        public bool Horizontal { get; init; }

        /// <summary>The page asked for, or the default: A3 landscape, or A3 portrait for a vertical plot.</summary>
        public string PageName => Page ?? (Plot && !Horizontal ? "a3p" : "a3");

        public bool IsPdf => Output.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

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

        if (settings.IsPdf && PdfPage.Parse(settings.PageName, GraphViewport.Create(0, 3600, settings.Width, settings.Height)) is null)
        {
            await error.WriteLineAsync($"--page '{settings.Page}' is not a page (a3, a4, a3p, a4p or view).").ConfigureAwait(false);
            return 2;
        }

        if (settings.Plot && !settings.IsPdf)
        {
            await error.WriteLineAsync("--plot writes a PDF: give an output ending in .pdf.").ConfigureAwait(false);
            return 2;
        }

        SimTime? plotFrom = null;
        SimTime? plotTo = null;
        foreach (var (name, text) in new[] { ("--from", settings.From), ("--to", settings.To) })
        {
            if (text is null)
            {
                continue;
            }

            if (!SimTime.TryParse(text, out var parsed))
            {
                await error.WriteLineAsync($"{name} '{text}' is not a time (HH:mm or HH:mm:ss).").ConfigureAwait(false);
                return 2;
            }

            if (name == "--from")
            {
                plotFrom = parsed;
            }
            else
            {
                plotTo = parsed;
            }
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
        Forecast? forecast = null;
        ConflictSet? conflicts = null;
        var now = at;
        if (recording is not null)
        {
            var state = Play(recording, tt, at, loggerFactory, cancellationToken);
            live = state.Trains.ToList();
            now ??= state.Clock.HasTime ? state.Clock.LastReported : null;
            if (!settings.NoForecast)
            {
                var plan = WorkingPlan.FromTimetable(tt);
                var network = ForecastNetwork.For(profile, tt);
                forecast = Forecaster.Run(new ForecastRequest(plan)
                {
                    LiveTrains = live,
                    Now = now,
                    SessionStart = state.SessionStart,
                    EntryDelays = state.EntryDelays,
                    Network = network,
                    Options = new ForecastOptions { Interaction = !settings.NoKnockOn },
                    LineSide = state.LineSide,
                });
                if (!settings.NoConflicts)
                {
                    conflicts = ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network });
                }
            }
        }

        now ??= tt.Header.StartTime;
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(profile, corridor)
        {
            Timetable = tt,
            LiveTrains = live,
            Now = recording is null && at is null ? null : now,
            ShowWtt = !settings.NoWtt,
            ColourMode = settings.Colour,
            Forecast = forecast,
            Conflicts = conflicts,
            ShowLiveHead = settings.LiveHead,
            Signals = settings.Signals,
        });
        var orientation = settings.Vertical ? GraphOrientation.Vertical : GraphOrientation.Horizontal;
        var viewport = GraphViewport.Around(now.Value, (int)Math.Round(settings.Hours * SimTime.SecondsPerHour), settings.Width, settings.Height, settings.NowAt,
            GraphLabels.MarginsFor(orientation, scene.Locations), orientation);

        var pdfPage = settings.IsPdf ? PdfPage.Parse(settings.PageName, viewport) : null;
        if (settings.Plot)
        {
            return await PlotAsync(settings, scene, corridor, pdfPage!, plotFrom ?? PlotStart(at, tt), plotTo, output, error, cancellationToken).ConfigureAwait(false);
        }

        byte[] bytes;
        using (var renderer = new SkiaGraphRenderer())
        {
            bytes = pdfPage is null ? renderer.RenderPng(scene, viewport) : renderer.RenderPdf(scene, viewport, page: pdfPage);
        }

        try
        {
            await File.WriteAllBytesAsync(settings.Output.FullName, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Cannot write {settings.Output.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var visible = scene.Paths.Count(p => viewport.Overlaps(p.TimeMin, p.TimeMax, p.AxisMin, p.AxisMax));
        await output.WriteLineAsync(
            $"Wrote {settings.Output.Name}{(pdfPage is null ? string.Empty : $" ({pdfPage.Name})")}: {corridor.Id} ({corridor.Name}), {viewport.TimeStart.ToSimTime()}–{viewport.TimeEnd.ToSimTime()}, " +
            $"now {now}, {visible} paths in view ({scene.InLayer(GraphLayer.Actual).Count()} actual, {scene.InLayer(GraphLayer.Forecast).Count()} forecast in all)" +
            (conflicts is null ? "." : $", {scene.Conflicts.Count} of {conflicts.Count} conflicts on this corridor.") +
            (settings.LiveHead ? $" {scene.LiveHeads.Count} live heads." : string.Empty)).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The plot's default start: the whole hour before --at, else the timetable's start hour.</summary>
    private static SimTime PlotStart(SimTime? at, Core.Timetables.WorkingTimetable tt)
    {
        var from = at is { } t ? t.Seconds - SimTime.SecondsPerHour : tt.Header.StartTime.Seconds;
        return new SimTime(Math.Max(0, from / SimTime.SecondsPerHour * SimTime.SecondsPerHour));
    }

    private static async Task<int> PlotAsync(Settings settings, GraphScene scene, Network.Corridor corridor, PdfPage page, SimTime from, SimTime? to, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var options = new GraphPlotOptions(from, to ?? from + (int)Math.Round(settings.Hours * SimTime.SecondsPerHour))
        {
            SecondsPerPage = settings.PerPageMinutes * SimTime.SecondsPerMinute,
            Page = page,
            Orientation = settings.Horizontal ? GraphOrientation.Horizontal : GraphOrientation.Vertical,
        };
        if (options.Problem is { } problem)
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
            return 2;
        }

        byte[] bytes;
        using (var renderer = new SkiaGraphRenderer())
        {
            bytes = renderer.RenderPlot(scene, options);
        }

        try
        {
            await File.WriteAllBytesAsync(settings.Output.FullName, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Cannot write {settings.Output.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        await output.WriteLineAsync(
            $"Wrote {settings.Output.Name} ({page.Name}, {(settings.Horizontal ? "time across" : "time down")}): {corridor.Id} ({corridor.Name}), {options.From}–{options.To}, " +
            $"{options.PageCount} pages of {settings.PerPageMinutes} min.").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Plays a recording through <see cref="LiveState"/> until the first clock or movement after <paramref name="until"/>.</summary>
    internal static LiveState Play(SslogRecording recording, Core.Timetables.WorkingTimetable tt, SimTime? until, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        var state = new LiveState(tt, logger: loggerFactory.CreateLogger<LiveState>());
        Play(state, recording.Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())), until, cancellationToken);
        return state;
    }

    /// <summary>Applies events to <paramref name="state"/> until the first clock or movement after <paramref name="until"/>.</summary>
    internal static void Play(LiveState state, IEnumerable<GatewayEvent> events, SimTime? until, CancellationToken cancellationToken)
    {
        foreach (var e in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var time = e switch
            {
                ClockEvent c => c.Clock,
                TrainLocationEvent m => m.Time,
                _ => (SimTime?)null,
            };
            if (until is not null && time > until && !(e is ClockEvent { Clock.Seconds: 0 }))
            {
                return;
            }

            state.Apply(e);
        }
    }

    private static SimTime ToSimTime(this double seconds) => new((int)Math.Round(seconds));
}
