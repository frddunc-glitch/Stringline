using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Tools;

/// <summary><c>stringline corridors</c>: propose corridors for a WTT and optionally write them to a profile.</summary>
internal static class CorridorsCommand
{
    public static Command Create()
    {
        var file = new Argument<FileInfo>("timetable") { Description = "SimSig timetable (.wtt, or its SavedTimetable.xml)." };
        var top = new Option<int>("--top") { Description = "Most corridors to propose.", DefaultValueFactory = _ => new CorridorGeneratorOptions().MaxCorridors };
        var minTrains = new Option<int>("--min-trains") { Description = "Fewest trains on the edge a corridor starts from (out-and-back moves not counted).", DefaultValueFactory = _ => new CorridorGeneratorOptions().MinTrains };
        var edges = new Option<bool>("--edges") { Description = "Also list every edge of the location graph and the skip edges that were expanded." };
        var profile = new Option<FileInfo?>("--profile") { Description = "Write a new profile (corridors and default category styles) to this JSON file." };
        var force = new Option<bool>("--force") { Description = "Overwrite the --profile file if it exists." };
        var headways = new Option<bool>("--headways") { Description = "Also list each section's headway: where it comes from, and the spacing the WTT books trains at." };
        var log = new Option<FileInfo?>("--log") { Description = "With --headways: a recording (.sslog) made with this timetable, to show the spacing trains actually ran at beside the WTT's (reported only, never applied)." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("corridors", "Propose train-graph corridors for a SimSig timetable, with section run times, headways and the derived axis.")
        {
            file, top, minTrains, edges, profile, force, headways, log, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(file)!, parse.GetValue(top), parse.GetValue(minTrains), parse.GetValue(edges), parse.GetValue(profile), parse.GetValue(force), parse.GetValue(verbose))
            {
                Headways = parse.GetValue(headways) || parse.GetValue(log) is not null,
                Log = parse.GetValue(log),
            },
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo File, int Top, int MinTrains, bool Edges, FileInfo? Profile, bool Force, bool Verbose)
    {
        /// <summary>List each section's headway (S27); implied by <see cref="Log"/>.</summary>
        public bool Headways { get; init; }

        /// <summary>A recording to measure the spacing trains actually ran at.</summary>
        public FileInfo? Log { get; init; }
    }

    /// <returns>0 on success, 2 if the timetable cannot be read or the profile cannot be written.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (settings.Top < 1 || settings.MinTrains < 1)
        {
            await error.WriteLineAsync("--top and --min-trains must be at least 1.").ConfigureAwait(false);
            return 2;
        }

        if (settings.Profile is { Exists: true } existing && !settings.Force)
        {
            await error.WriteLineAsync($"{existing.FullName} exists; use --force to overwrite it.").ConfigureAwait(false);
            return 2;
        }

        WttImportResult result;
        WorkingTimetable? observed = null;
        using (var loggerFactory = ConsoleLogging.Create(settings.Verbose))
        {
            try
            {
                result = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(settings.File.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
            {
                await error.WriteLineAsync($"Cannot read {settings.File.FullName}: {ex.Message}").ConfigureAwait(false);
                return 2;
            }

            if (settings.Log is { } log)
            {
                try
                {
                    observed = Observe(result.Timetable, SslogRecording.Load(log.FullName), loggerFactory, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    await error.WriteLineAsync($"Cannot read {log.FullName}: {ex.Message}").ConfigureAwait(false);
                    return 2;
                }
            }
        }

        var tt = result.Timetable;
        var options = new CorridorGeneratorOptions { MaxCorridors = settings.Top, MinTrains = settings.MinTrains };
        var graph = LocationGraph.Build(tt, options.MaxSkipped);
        var candidates = CorridorGenerator.Generate(tt, graph, options);

        var text = new StringWriter(CultureInfo.InvariantCulture);
        text.WriteLine($"{settings.File.Name}: sim {tt.AreaId}, {graph.Locations.Count()} locations, {graph.Edges.Count} edges ({graph.Expansions.Count} skip edges expanded).");
        if (settings.Edges)
        {
            WriteGraph(text, graph);
        }

        if (candidates.Count == 0)
        {
            text.WriteLine($"No corridors: no edge has {settings.MinTrains} or more trains.");
        }

        if (observed is not null)
        {
            text.WriteLine($"Observed: {observed.Trains.Count} trains in {settings.Log!.Name}.");
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            text.WriteLine();
            WriteCandidate(text, i + 1, candidates[i]);
            if (settings.Headways)
            {
                text.WriteLine();
                WriteHeadways(text, candidates[i].Corridor, observed);
            }
        }

        if (settings.Profile is { } path)
        {
            var newProfile = StringlineProfile.Create(tt, candidates.Select(c => c.Corridor));
            try
            {
                await File.WriteAllTextAsync(path.FullName, ProfileJson.Serialize(newProfile), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
                await error.WriteLineAsync($"Cannot write {path.FullName}: {ex.Message}").ConfigureAwait(false);
                return 2;
            }

            text.WriteLine();
            text.WriteLine($"Wrote profile {path.FullName} ({candidates.Count} corridors, {newProfile.Categories.Count} category styles). Save it as {tt.AreaId}.json in {ProfileStore.DefaultDirectory} to use it.");
        }

        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static void WriteGraph(TextWriter w, LocationGraph graph)
    {
        w.WriteLine("Edges (trains):");
        foreach (var (pair, weight) in graph.Edges.OrderByDescending(e => e.Value).ThenBy(e => e.Key.ToString(), StringComparer.Ordinal))
        {
            w.WriteLine($"  {pair,-24} {weight,5}");
        }

        if (graph.Expansions.Count > 0)
        {
            w.WriteLine("Skip edges expanded:");
            foreach (var (pair, chain) in graph.Expansions.OrderBy(e => e.Key.ToString(), StringComparer.Ordinal))
            {
                w.WriteLine($"  {pair,-24} via {string.Join(", ", chain.Skip(1).Take(chain.Count - 2))}");
            }
        }
    }

    internal static void WriteCandidate(TextWriter w, int number, CorridorCandidate candidate)
    {
        var c = candidate.Corridor;
        var m = candidate.Measurement;
        w.WriteLine($"{number}. {c.Id}: {c.Name}. {c.Locations.Count} locations, {m.Trains} trains, {candidate.Traffic} train-sections ({candidate.NewTraffic} not on an earlier corridor).");
        string[] header = ["#", "Location", "Axis", "Plat", "Calls", "Stops", "Start", "End", "Next run", "Down", "Up", "Runs", "Opp", "Headway", "From", "Hint"];
        var rows = new List<string[]>();
        for (var i = 0; i < c.Locations.Count; i++)
        {
            var l = c.Locations[i];
            var ls = m.Locations[i];
            var s = i < c.Sections.Count ? c.Sections[i] : null;
            var ss = i < m.Sections.Count ? m.Sections[i] : null;
            rows.Add(
            [
                (i + 1).ToString(CultureInfo.InvariantCulture),
                l.Tiploc.Code,
                l.AxisPosition.ToString("0.000", CultureInfo.InvariantCulture),
                l.Platforms > 0 ? l.Platforms.ToString(CultureInfo.InvariantCulture) : "",
                ls.Calls.ToString(CultureInfo.InvariantCulture),
                l.IsTimingPointOnly ? "pass" : ls.Stops.ToString(CultureInfo.InvariantCulture),
                ls.Starts.ToString(CultureInfo.InvariantCulture),
                ls.Terminates.ToString(CultureInfo.InvariantCulture),
                s is null ? "" : ss!.Samples == 0 ? $"{Duration(s.MinRunSeconds)}?" : Duration(s.MinRunSeconds),
                Duration(s?.MinRunDownSeconds),
                Duration(s?.MinRunUpSeconds),
                ss?.Samples.ToString(CultureInfo.InvariantCulture) ?? "",
                ss?.OpposingOverlaps.ToString(CultureInfo.InvariantCulture) ?? "",
                Duration(s?.DefaultHeadwaySeconds),
                s is null ? "" : HeadwaySources.Name(s.HeadwaySource),
                ss?.SingleLineHint == true ? "single line?" : "",
            ]);
        }

        WttDumpCommand.WriteTable(w, header, rows);
    }

    /// <summary>
    /// Each section's headway and where it comes from (<c>06-…md</c> §3), the WTT's booked spacing it is derived from,
    /// and with a recording, the spacing trains ran at: the same percentile, and the closest pair.
    /// </summary>
    internal static void WriteHeadways(TextWriter w, Corridor c, WorkingTimetable? observed)
    {
        var seen = observed is null ? null : CorridorMeasurement.Measure(observed, c.Locations.Select(l => l.Tiploc).ToList());
        w.WriteLine(observed is null
            ? $"Headways (WTT: {BookedHeadway.Percentile:0}th percentile of booked spacing on a track, at least {BookedHeadway.MinPairs} pairs, used if less than the default):"
            : $"Headways (WTT and observed: {BookedHeadway.Percentile:0}th percentile of spacing on a track; observed is reported only):");
        List<string> header = ["Section", "Headway", "From", "WTT", "Pairs"];
        if (seen is not null)
        {
            header.AddRange(["Observed", "Pairs", "Closest"]);
        }

        var rows = new List<string[]>();
        for (var i = 0; i < c.Sections.Count; i++)
        {
            var s = c.Sections[i];
            List<string> row =
            [
                $"{s.From}–{s.To}",
                Duration(s.DefaultHeadwaySeconds),
                HeadwaySources.Name(s.HeadwaySource),
                Duration(s.BookedHeadwaySeconds),
                s.BookedHeadwayPairs.ToString(CultureInfo.InvariantCulture),
            ];
            if (seen is not null)
            {
                var o = seen.Sections[i].BookedHeadway;
                row.AddRange([Duration(o.SpacingSeconds), o.Pairs.ToString(CultureInfo.InvariantCulture), Duration(o.ClosestSeconds)]);
            }

            rows.Add([.. row]);
        }

        WttDumpCommand.WriteTable(w, [.. header], rows);
    }

    /// <summary>Plays the recording through <see cref="LiveState"/> and returns what its trains actually did.</summary>
    private static WorkingTimetable Observe(WorkingTimetable timetable, SslogRecording recording, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var state = new LiveState(timetable, logger: loggerFactory.CreateLogger<LiveState>());
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        foreach (var message in recording.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var e in parser.Parse(message.ToGatewayMessage()))
            {
                state.Apply(e);
            }
        }

        return ObservedTimetable.From(timetable.Header, state.Trains);
    }

    /// <summary>A duration as <c>m:ss</c>, or empty.</summary>
    private static string Duration(int? seconds) =>
        seconds is { } s ? $"{s / 60}:{s % 60:00}" : "";
}
