using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Editing.Knowledge;
using Stringline.Gateway;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Timetable;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline simdata</c> (S41): builds (or reads from its cache) what Stringline knows about a sim from its
/// timetables, recordings and sessions, and profile, and prints a summary, a location or a link.
/// </summary>
internal static class SimdataCommand
{
    public static Command Create()
    {
        var area = new Option<string>("--area") { Description = "The sim's area ID, e.g. waterloo.", Required = true };
        var folders = new Option<DirectoryInfo[]>("--folder") { Description = "A folder of timetables (searched with its subfolders); give it more than once for several. Default: SimSig's Timetables folder.", AllowMultipleArgumentsPerToken = true };
        var sessions = new Option<DirectoryInfo?>("--sessions") { Description = "The saved sessions folder; the sim's sessions are learned from. Default: Documents\\Stringline\\sessions." };
        var noSessions = new Option<bool>("--no-sessions") { Description = "Learn from no saved sessions." };
        var logs = new Option<FileInfo[]>("--log") { Description = "A recording (.sslog) or session (.stringline) to learn from too; give it more than once for several.", AllowMultipleArgumentsPerToken = true };
        var profile = new Option<FileInfo?>("--profile") { Description = "The sim's profile. Default: its saved profile, if any." };
        var noProfile = new Option<bool>("--no-profile") { Description = "Use no profile." };
        var exclude = new Option<FileInfo[]>("--exclude") { Description = "A timetable to leave out (the draft being edited).", AllowMultipleArgumentsPerToken = true };
        var cache = new Option<DirectoryInfo?>("--cache-folder") { Description = "Where to cache the knowledge. Default: beside the profile." };
        var rebuild = new Option<bool>("--rebuild") { Description = "Build it again even if the cache is up to date." };
        var location = new Option<string?>("--location") { Description = "Print what is known about this TIPLOC." };
        var link = new Option<string[]>("--link") { Description = "Print what is known about the link from one TIPLOC to another: --link WLOO VAUX.", AllowMultipleArgumentsPerToken = true, Arity = new ArgumentArity(2, 2) };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("simdata", "What Stringline knows about a sim from its timetables, recordings and profile.")
        {
            area, folders, sessions, noSessions, logs, profile, noProfile, exclude, cache, rebuild, location, link, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(
                parse.GetValue(area)!,
                parse.GetValue(folders) ?? [],
                parse.GetValue(noSessions) ? null : parse.GetValue(sessions) ?? new DirectoryInfo(PlayFiles.DefaultSessionsFolder),
                parse.GetValue(logs) ?? [],
                parse.GetValue(profile),
                parse.GetValue(noProfile),
                parse.GetValue(exclude) ?? [],
                parse.GetValue(cache),
                parse.GetValue(rebuild),
                parse.GetValue(location),
                parse.GetValue(link) is [var from, var to] ? (from, to) : null,
                parse.GetValue(verbose)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(
        string Area,
        IReadOnlyList<DirectoryInfo> Folders,
        DirectoryInfo? Sessions,
        IReadOnlyList<FileInfo> Logs,
        FileInfo? Profile = null,
        bool NoProfile = false,
        IReadOnlyList<FileInfo>? Exclude = null,
        DirectoryInfo? CacheFolder = null,
        bool Rebuild = false,
        string? Location = null,
        (string From, string To)? Link = null,
        bool Verbose = false);

    /// <returns>0 on success, 2 on bad arguments.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Area))
        {
            await error.WriteLineAsync("Give the sim's --area.").ConfigureAwait(false);
            return 2;
        }

        Tiploc location = default, from = default, to = default;
        if ((settings.Location is { } l && !Tiploc.TryCreate(l, out location))
            || (settings.Link is { } k && (!Tiploc.TryCreate(k.From, out from) || !Tiploc.TryCreate(k.To, out to))))
        {
            await error.WriteLineAsync("A TIPLOC must not be empty or hold spaces.").ConfigureAwait(false);
            return 2;
        }

        if (settings.Profile is { Exists: false } missing)
        {
            await error.WriteLineAsync($"No profile {missing.FullName}.").ConfigureAwait(false);
            return 2;
        }

        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        var profiles = new ProfileStore();
        var profilePath = settings.NoProfile ? null : settings.Profile?.FullName ?? profiles.PathFor(settings.Area);
        var folders = settings.Folders.Count > 0 ? settings.Folders.Select(f => f.FullName).ToList() : [WttCatalogue.DefaultFolder];
        var play = new List<string>();
        if (settings.Sessions is { } sessions)
        {
            play.AddRange(PlayFiles.ForArea(sessions.FullName, settings.Area));
        }

        play.AddRange(settings.Logs.Select(f => f.FullName));
        var request = new SimKnowledgeRequest
        {
            AreaId = settings.Area.Trim(),
            TimetableFolders = folders,
            PlayFiles = play,
            ReadPlay = path => PlayFiles.Read(path, parser),
            ProfilePath = profilePath,
            Exclude = [.. (settings.Exclude ?? []).Select(f => f.FullName)],
        };

        var store = new SimKnowledgeStore(settings.CacheFolder?.FullName ?? (profilePath is null ? profiles.Directory : Path.GetDirectoryName(Path.GetFullPath(profilePath))!));
        var result = store.Get(request, settings.Rebuild, cancellationToken);
        var text = new StringWriter(CultureInfo.InvariantCulture);
        WriteSummary(text, result, store.PathFor(request.AreaId));
        if (!location.IsEmpty)
        {
            WriteLocation(text, result.Knowledge, location);
        }

        if (!from.IsEmpty)
        {
            WriteLink(text, result.Knowledge, from, to);
        }

        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static void WriteSummary(TextWriter w, SimKnowledgeResult result, string cache)
    {
        var k = result.Knowledge;
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{k.AreaId}: {k.Timetables} timetables ({k.Trains} trains), {k.Plays} recordings or sessions, {(k.Sources.Any(s => s.Kind == KnowledgeSourceKind.Profile) ? "the profile" : "no profile")}; {(result.FromCache ? "from the cache" : "built")} in {result.Elapsed.TotalSeconds:0.00} s ({cache})."));
        foreach (var problem in result.Problems)
        {
            w.WriteLine("  Left out: " + problem);
        }

        int Count(LocationRoles role) => k.Locations.Count(l => l.Is(role));
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Locations: {k.Locations.Count} (timing {Count(LocationRoles.Timing)}, stations {Count(LocationRoles.Station)}, key {Count(LocationRoles.Key)}, reversing {Count(LocationRoles.Reversing)}, entry {Count(LocationRoles.Entry)}, seed {Count(LocationRoles.Seed)}, reported {Count(LocationRoles.Reported)})."));
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Links: {k.Links.Count} ({k.Links.Count(l => l.RunTimes is not null)} timed); skip chains {k.SkipChains.Count}; signals {k.Signals.Count} ({k.Signals.Count(s => s.Reports > 0)} reported, {k.Signals.Count(s => s.Entries > 0)} entered at, {k.Signals.Count(s => s.Places is not null)} placed); track circuits {k.TrackCircuits.Count}; berths {k.Berths.Count}."));
    }

    private static void WriteLocation(TextWriter w, SimKnowledge k, Tiploc tiploc)
    {
        w.WriteLine();
        if (k.Location(tiploc) is not { } at)
        {
            w.WriteLine(k.Signal(tiploc.Code) is { } signal ? Describe(signal) : $"{tiploc}: not known.");
            return;
        }

        w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{tiploc}{(at.Name is { } name ? " (" + name + ")" : string.Empty)}: {(at.Roles == LocationRoles.None ? "no roles" : at.Roles.ToString())}"));
        w.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Calls {at.Calls}, stops {at.Stops}, activities {at.Activities}, reversals {at.Reversals}, entries {at.Entries}, seeds {at.Seeds}, reports {at.Reports}."));
        WriteCounts(w, "Platforms", at.Platforms);
        WriteCounts(w, "Lines out", at.Lines);
        WriteCounts(w, "Paths in", at.Paths);
        WriteCounts(w, "Reported platforms", at.ReportedPlatforms);
        WriteTimes(w, "Dwells", at.Dwells);
        if (at.Corridors is { Count: > 0 } corridors)
        {
            w.WriteLine("  Corridors: " + string.Join(", ", corridors));
        }

        foreach (var link in k.LinksFrom(tiploc).OrderByDescending(l => l.Count))
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  → {link.To} ({link.Count})"));
        }

        foreach (var link in k.Links.Where(l => l.To == tiploc).OrderByDescending(l => l.Count))
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  ← {link.From} ({link.Count})"));
        }
    }

    private static void WriteLink(TextWriter w, SimKnowledge k, Tiploc from, Tiploc to)
    {
        w.WriteLine();
        if (k.Link(from, to) is { } link)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{from} → {to}: booked {link.Count} times (Down {link.Down}, Up {link.Up})."));
            WriteCounts(w, "Lines", link.Lines);
            WriteCounts(w, "Paths", link.Paths);
            WriteTimes(w, "Run times", link.RunTimes);
        }
        else
        {
            w.WriteLine($"{from} → {to}: never booked.");
        }

        if (k.Chain(from, to) is { Count: > 2 } chain)
        {
            w.WriteLine("  Booked through: " + string.Join(" ", chain));
        }
    }

    private static string Describe(SignalKnowledge s)
    {
        var entry = s.EntryPoints is { Count: > 0 } points ? " (" + string.Join(", ", points.Select(p => $"{p.Key} {p.Value}")) + ")" : string.Empty;
        var places = s.Places is { Count: > 0 } placed
            ? "; placed " + string.Join(", ", placed.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.From}→{p.To} {p.Fraction:0.000}{(p.Usable ? string.Empty : " (learning)")}")))
            : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"Signal {s.Id}: reported {s.Reports}, entered at {s.Entries}{entry}{places}.");
    }

    private static void WriteCounts(TextWriter w, string title, IReadOnlyDictionary<string, int>? counts)
    {
        if (counts is { Count: > 0 })
        {
            w.WriteLine($"  {title}: " + string.Join(", ", counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}")));
        }
    }

    private static void WriteTimes(TextWriter w, string title, IReadOnlyDictionary<RunBand, TimeStats>? times)
    {
        if (times is not { Count: > 0 })
        {
            return;
        }

        w.WriteLine($"  {title} (least / usual, samples):");
        foreach (var (band, t) in times)
        {
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {band,-14} {Clock(t.LeastSeconds)} / {Clock(t.UsualSeconds)}  ({t.Samples})"));
        }
    }

    private static string Clock(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
}
