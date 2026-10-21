using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Planning;
using Stringline.Timetable;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline learn</c> (S37, #19): learns signal, track circuit and berth places from one or more recordings into a
/// profile, and reports them per section, what is still learning, and how far places move between recordings.
/// </summary>
internal static class LearnCommand
{
    public static Command Create()
    {
        var wtt = new Option<FileInfo>("--wtt") { Description = "The SimSig timetable (.wtt).", Required = true };
        var logs = new Option<FileInfo[]>("--log") { Description = "A recording (.sslog) or saved live session (.stringline); give it more than once to learn from several.", Required = true, AllowMultipleArgumentsPerToken = true };
        var profile = new Option<FileInfo?>("--profile") { Description = "A profile JSON to learn into. Default: generated from the WTT." };
        var output = new Option<FileInfo?>("--out") { Description = "Write the profile with the learned places here." };
        var report = new Option<bool>("--report") { Description = "List each section's places, with samples, spread and state." };
        var minSamples = new Option<int>("--min-samples") { Description = "Samples a place needs to be used.", DefaultValueFactory = _ => SignallingPlaces.DefaultMinSamples };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("learn", "Learn where signals, track circuits and TD berths are between timing locations, from recordings.")
        {
            wtt, logs, profile, output, report, minSamples, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(wtt)!, parse.GetValue(logs) ?? [], parse.GetValue(profile), parse.GetValue(output), parse.GetValue(report), parse.GetValue(minSamples), parse.GetValue(verbose)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo Wtt, IReadOnlyList<FileInfo> Logs, FileInfo? Profile = null, FileInfo? Out = null, bool Report = false, int MinSamples = SignallingPlaces.DefaultMinSamples, bool Verbose = false);

    /// <summary>One recording's own places, for comparing recordings.</summary>
    private sealed record Learned(string Name, SignallingPlaces Places);

    /// <returns>0 on success, 2 on bad arguments or a file that cannot be read.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (settings.Logs.Count == 0)
        {
            await error.WriteLineAsync("Give at least one --log.").ConfigureAwait(false);
            return 2;
        }

        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        var text = new StringWriter(CultureInfo.InvariantCulture);
        WttImportResult import;
        StringlineProfile profile;
        try
        {
            import = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(settings.Wtt.FullName);
            profile = settings.Profile is null
                ? StringlineProfile.Create(import.Timetable)
                : ProfileJson.Deserialize(await File.ReadAllTextAsync(settings.Profile.FullName, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException or ProfileFormatException)
        {
            await error.WriteLineAsync($"Cannot read input: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var each = new List<Learned>();
        LearnOutcome? outcome = null;
        foreach (var log in settings.Logs)
        {
            IEnumerable<GatewayEvent> events;
            string id;
            SimTime? first;
            try
            {
                // A saved live session (S38) is learned from as the app learns from it; a replay session from its recording.
                var saved = string.Equals(log.Extension, SessionCatalogue.Extension, StringComparison.OrdinalIgnoreCase) ? SavedSession.Load(log.FullName) : null;
                if (saved is { Info.Kind: SavedSessionKind.Live })
                {
                    (events, id, first) = (saved.Events(parser).ToList(), SignalLearning.SessionId(saved.Info.Created), null);
                }
                else
                {
                    var recording = SslogRecording.Load(saved?.Info.RecordingPath ?? log.FullName);
                    (events, id, first) = (recording.Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())), SignalLearning.RecordingId(recording.Header.Created), recording.FirstSim);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SessionFormatException)
            {
                await error.WriteLineAsync($"Cannot read {log.Name}: {ex.Message}").ConfigureAwait(false);
                return 2;
            }

            var learner = new SignalLearner();
            foreach (var e in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                learner.Observe(e);
                first ??= e is ClockEvent { Clock.Seconds: > 0 } clock ? clock.Clock : null;
            }

            var learned = learner.Learn(import.Timetable);
            outcome = SignalLearning.Apply(profile, learned, id, settings.MinSamples);
            profile = outcome.Profile;
            each.Add(new Learned(log.Name, SignalLearning.Apply(profile with { Signalling = null }, learned, id, settings.MinSamples).Profile.Places));
            WriteRecording(text, log.Name, first, learned, outcome.Added);
        }

        WriteSummary(text, profile.Places, settings.MinSamples);
        for (var i = 1; i < each.Count; i++)
        {
            WriteComparison(text, each[0], each[i], settings.MinSamples);
        }

        if (settings.Report)
        {
            WriteReport(text, profile, settings.MinSamples);
        }

        if (settings.Out is { } target)
        {
            try
            {
                await File.WriteAllTextAsync(target.FullName, ProfileJson.Serialize(profile), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Cannot write {target.Name}: {ex.Message}").ConfigureAwait(false);
                return 2;
            }

            text.WriteLine($"Wrote {target.Name}.");
        }

        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static void WriteRecording(TextWriter w, string name, SimTime? first, LearnedPlaces learned, int added)
    {
        var t = learned.Tracks;
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name} ({first?.ToString() ?? "-"} to {learned.UpTo?.ToString() ?? "-"}): {learned.Runs} runs between timing locations, {learned.RunsUsed} usable; samples: {learned.Count(SignallingKind.Signal)} signal, {learned.Count(SignallingKind.TrackCircuit)} track circuit, {learned.Count(SignallingKind.Berth)} berth ({added} new)."));
        if (t.Occupations > 0)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  Track circuits occupied: {t.Occupations}; put to one train {t.Attributed} ({t.AttributedShare:P1}), {t.Placed} of them in a usable run; two or more trains near {t.Ambiguous}; no train near {t.NoTrain}; no settled place before it {t.UnknownTrack}."));
        }

        if (learned.Steps > 0)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Berth steps: {learned.Steps}; placed {learned.StepsPlaced}."));
        }
    }

    private static void WriteSummary(TextWriter w, SignallingPlaces places, int minSamples)
    {
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Places: signals {PlaceCounts.Of(places.Signals, minSamples)}; track circuits {PlaceCounts.Of(places.TrackCircuits, minSamples)}; berths {PlaceCounts.Of(places.Berths, minSamples)}."));
        var passed = places.Signals.Where(p => p.SampleCount >= minSamples).ToList();
        var used = passed.Count(p => p.StateFor(minSamples) != PlaceState.Learning);
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Signals passed by {minSamples} or more trains: {passed.Count}, used {used} ({(passed.Count == 0 ? 0 : (double)used / passed.Count):P1})."));
    }

    /// <summary>S37's gate: how far each place used in both recordings moves between them, against its spread.</summary>
    private static void WriteComparison(TextWriter w, Learned a, Learned b, int minSamples)
    {
        foreach (var kind in Enum.GetValues<SignallingKind>())
        {
            var moves = new List<(double Move, SignallingPlace A, SignallingPlace B)>();
            foreach (var place in a.Places.Of(kind).Where(p => p.IsUsable(minSamples)))
            {
                if (b.Places.Find(kind, place.Id, place.From, place.To, place.Run) is { } other && other.IsUsable(minSamples))
                {
                    moves.Add((Math.Abs(place.Fraction - other.Fraction), place, other));
                }
            }

            if (moves.Count == 0)
            {
                continue;
            }

            moves.Sort((x, y) => x.Move.CompareTo(y.Move));
            var beyondSpread = moves.Count(m => m.Move > Math.Max(m.A.Spread, m.B.Spread));
            var beyondHalf = moves.Count(m => m.Move > SignallingPlaces.MaxSpread / 2);
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{a.Name} against {b.Name}: {moves.Count} {SignallingPlaces.Name(kind)}s used in both; places move median {moves[moves.Count / 2].Move:0.000}, 95th {moves[Math.Min(moves.Count - 1, (int)Math.Ceiling(0.95 * moves.Count) - 1)].Move:0.000}, most {moves[^1].Move:0.000}; {beyondSpread} move more than their spread, {beyondHalf} more than {SignallingPlaces.MaxSpread / 2:0.00}."));
            foreach (var (move, x, y) in moves.TakeLast(3).Reverse().Where(m => m.Move > SignallingPlaces.MaxSpread / 2))
            {
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {x.Id} {x.From}→{x.To} {SignallingPlaces.Name(x.Run)}: {x.Fraction:0.000} (spread {x.Spread:0.000}, {x.SampleCount}) against {y.Fraction:0.000} (spread {y.Spread:0.000}, {y.SampleCount})"));
            }
        }
    }

    private static void WriteReport(TextWriter w, StringlineProfile profile, int minSamples)
    {
        var places = profile.Places;
        foreach (var section in places.All.GroupBy(p => (p.Place.From, p.Place.To)).OrderBy(g => g.Key.From.Code, StringComparer.Ordinal).ThenBy(g => g.Key.To.Code, StringComparer.Ordinal))
        {
            w.WriteLine();
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{section.Key.From} → {section.Key.To}"));
            foreach (var (kind, place) in section.OrderBy(p => p.Place.Run).ThenBy(p => p.Place.Fraction).ThenBy(p => p.Kind))
            {
                var end = place.EndFraction is { } e ? string.Create(CultureInfo.InvariantCulture, $"–{e:0.000}") : string.Empty;
                var extra = string.Join(", ", new[] { place.Line is { } line ? "line " + line : null, place.Signal is { } signal ? "signal " + signal : null }.OfType<string>());
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {Short(kind),-3} {place.Id,-7} {Short(place.Run),-2} {place.Fraction:0.000}{end,-6} {place.SampleCount,5} samples  spread {place.Spread:0.000}  {State(place.StateFor(minSamples))}{(extra.Length > 0 ? "  " + extra : string.Empty)}"));
            }
        }
    }

    private static string Short(SignallingKind kind) => kind switch
    {
        SignallingKind.Signal => "Sig",
        SignallingKind.TrackCircuit => "TC",
        _ => "TD",
    };

    private static string Short(RunKind run) => run switch
    {
        RunKind.Through => "-",
        RunKind.Starting => "S",
        RunKind.Stopping => "T",
        _ => "ST",
    };

    private static string State(PlaceState state) => state switch
    {
        PlaceState.Used => "used",
        PlaceState.HandSet => "hand-set",
        _ => "learning",
    };
}
