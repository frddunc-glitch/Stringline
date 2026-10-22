using System.Text.Json.Serialization;
using Stringline.Core;
using Stringline.Network.Profiles;

namespace Stringline.Editing.Knowledge;

/// <summary>What a location is used for, by the timetables and recordings of its sim (<c>08-…md</c> §4).</summary>
[Flags]
public enum LocationRoles
{
    None = 0,

    /// <summary>A timing point booked in at least one timetable.</summary>
    Timing = 1,

    /// <summary>An entry point (trains enter the sim here).</summary>
    Entry = 2,

    /// <summary>A seed point (trains are seeded here).</summary>
    Seed = 4,

    /// <summary>A station: some train is booked to stop here.</summary>
    Station = 8,

    /// <summary>A key location: some train has an activity here.</summary>
    Key = 16,

    /// <summary>Some train reverses here.</summary>
    Reversing = 32,

    /// <summary>Reported by trains in a recording or session.</summary>
    Reported = 64,
}

/// <summary>Where the knowledge came from.</summary>
public enum KnowledgeSourceKind
{
    /// <summary>A <c>.wtt</c> for the sim.</summary>
    Timetable,

    /// <summary>A recording or saved session of the sim being played.</summary>
    Play,

    /// <summary>The sim's profile (learned places, corridors and names).</summary>
    Profile,
}

/// <summary>A file the knowledge was built from, as it was then: its cache is rebuilt when any one changes.</summary>
public sealed record KnowledgeSource(KnowledgeSourceKind Kind, string Path, DateTime LastWriteUtc, long Length)
{
    /// <summary>The same file, unchanged (paths compare without case, as Windows does).</summary>
    public bool SameAs(KnowledgeSource other) =>
        other is not null && Kind == other.Kind && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
        && LastWriteUtc == other.LastWriteUtc && Length == other.Length;

    /// <summary>The file as it is now, or null if it is not there.</summary>
    public static KnowledgeSource? Of(KnowledgeSourceKind kind, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var file = new FileInfo(path);
        return file.Exists ? new KnowledgeSource(kind, file.FullName, file.LastWriteTimeUtc, file.Length) : null;
    }
}

/// <summary>Times seen for something, in seconds: how many, the least and the usual (median).</summary>
public sealed record TimeStats(int Samples, int LeastSeconds, int UsualSeconds)
{
    /// <summary>The stats of some times; null for none.</summary>
    public static TimeStats? Of(IReadOnlyCollection<int> seconds)
    {
        ArgumentNullException.ThrowIfNull(seconds);
        if (seconds.Count == 0)
        {
            return null;
        }

        var sorted = seconds.Order().ToArray();
        return new TimeStats(sorted.Length, sorted[0], sorted[(sorted.Length - 1) / 2]);
    }
}

/// <summary>One location of the sim: what it is used for and the codes used there.</summary>
/// <param name="Name">The profile's name for it, if it has one.</param>
/// <param name="Calls">Booked calls (stops and passes) over all timetables.</param>
/// <param name="Stops">Booked stops.</param>
/// <param name="Activities">Booked calls with an activity.</param>
/// <param name="Reversals">Trains that reverse here.</param>
/// <param name="Entries">Trains entering here.</param>
/// <param name="Seeds">Trains seeded here.</param>
/// <param name="Reports">Reports of trains here in recordings and sessions.</param>
/// <param name="Ends">Trains whose last timing point is a stop here (they terminate, form another or join).</param>
/// <param name="Exits">Trains whose last timing point is a pass here (they leave the area).</param>
/// <param name="Through">Calls here between a train's first and last timing points.</param>
/// <param name="Platforms">The platform codes booked here, with how often.</param>
/// <param name="Lines">The line codes booked out of here, with how often.</param>
/// <param name="Paths">The path codes booked into here, with how often.</param>
/// <param name="ReportedPlatforms">The platforms (or lines at a junction) trains reported here, with how often.</param>
/// <param name="Dwells">Booked dwells at stops, by band.</param>
/// <param name="Corridors">The profile's corridors the location is on, by ID.</param>
/// <param name="EntryPower">The power types (electrification letters) of trains entering here, with how often.</param>
public sealed record LocationKnowledge(
    Tiploc Tiploc,
    LocationRoles Roles,
    string? Name = null,
    int Calls = 0,
    int Stops = 0,
    int Activities = 0,
    int Reversals = 0,
    int Entries = 0,
    int Seeds = 0,
    int Reports = 0,
    IReadOnlyDictionary<string, int>? Platforms = null,
    IReadOnlyDictionary<string, int>? Lines = null,
    IReadOnlyDictionary<string, int>? Paths = null,
    IReadOnlyDictionary<string, int>? ReportedPlatforms = null,
    IReadOnlyDictionary<RunBand, TimeStats>? Dwells = null,
    IReadOnlyList<string>? Corridors = null,
    int Ends = 0,
    int Exits = 0,
    int Through = 0,
    IReadOnlyDictionary<string, int>? EntryPower = null)
{
    public bool Is(LocationRoles role) => (Roles & role) == role;

    public override string ToString() => $"{Tiploc} ({Roles})";
}

/// <summary>
/// A booked link: consecutive timing points A → B in some timetable, with A's line and B's path (as SimSig books them),
/// how often, which way, and the run times by band (net of allowances, as <c>CorridorMeasurement</c> measures them).
/// </summary>
/// <param name="Down">Bookings with SimSig's Down flag set at B (else at A).</param>
/// <param name="Up">Bookings with it clear.</param>
/// <param name="Lines">A's line codes on the link, with how often.</param>
/// <param name="Paths">B's path codes on the link, with how often.</param>
public sealed record LinkKnowledge(
    Tiploc From,
    Tiploc To,
    int Count,
    int Down = 0,
    int Up = 0,
    IReadOnlyDictionary<string, int>? Lines = null,
    IReadOnlyDictionary<string, int>? Paths = null,
    IReadOnlyDictionary<RunBand, TimeStats>? RunTimes = null)
{
    public override string ToString() => $"{From} → {To} ({Count})";
}

/// <summary>
/// A skip chain (<c>LocationGraph</c>): other trains book <see cref="Chain"/> where some book its ends straight after
/// each other. The chain runs from its first location to its last, in either direction.
/// </summary>
public sealed record SkipChain(IReadOnlyList<Tiploc> Chain)
{
    public override string ToString() => string.Join(" ", Chain);
}

/// <summary>A signal, track circuit or TD berth the profile has learned a place for (S37), between two timing locations.</summary>
public sealed record PlaceKnowledge(string Id, Tiploc From, Tiploc To, RunKind Run, double Fraction, int Samples, bool Usable, string? Line = null);

/// <summary>A signal of the sim: reported by trains in play, placed by the profile, or both.</summary>
/// <param name="Reports">Trains reported passing it in recordings and sessions.</param>
/// <param name="Entries">Trains whose first report was here, after the start of play: they entered the sim at it.</param>
/// <param name="EntryPoints">The WTT entry points of those trains (found by headcode), with how often.</param>
/// <param name="Places">Where the profile places it.</param>
public sealed record SignalKnowledge(
    string Id,
    int Reports = 0,
    int Entries = 0,
    IReadOnlyDictionary<string, int>? EntryPoints = null,
    IReadOnlyList<PlaceKnowledge>? Places = null)
{
    public override string ToString() => Id;
}

/// <summary>
/// What Stringline knows about one sim from its timetables, recordings and profile (<c>08-…md</c> §4): the locations
/// and their roles and codes, the booked links with their run times, skip chains and signals. Checks (S42) and bulk
/// tools (S46) ask it what the sim allows. SimSig's own data is encrypted, so this is what has been seen, never what
/// is possible. Immutable once built; cached as <c>{area}.simdata.json</c> (<see cref="SimKnowledgeStore"/>).
/// </summary>
public sealed class SimKnowledge
{
    /// <summary>The cache format this build writes; a cache in any other is rebuilt.</summary>
    public const int CurrentFormat = 2;

    private Dictionary<Tiploc, LocationKnowledge>? _locations;
    private Dictionary<(Tiploc, Tiploc), LinkKnowledge>? _links;
    private Dictionary<Tiploc, List<LinkKnowledge>>? _from;
    private Dictionary<string, SignalKnowledge>? _signals;

    public int Format { get; init; } = CurrentFormat;

    public required string AreaId { get; init; }

    public DateTimeOffset Built { get; init; }

    /// <summary>The files it was built from, as they were.</summary>
    public IReadOnlyList<KnowledgeSource> Sources { get; init; } = [];

    /// <summary>Timetables read.</summary>
    public int Timetables { get; init; }

    /// <summary>Trains in those timetables.</summary>
    public int Trains { get; init; }

    /// <summary>Recordings and sessions read.</summary>
    public int Plays { get; init; }

    /// <summary>Locations in TIPLOC order.</summary>
    public IReadOnlyList<LocationKnowledge> Locations { get; init; } = [];

    /// <summary>Booked links in order of their ends.</summary>
    public IReadOnlyList<LinkKnowledge> Links { get; init; } = [];

    public IReadOnlyList<SkipChain> SkipChains { get; init; } = [];

    /// <summary>Signals in ID order.</summary>
    public IReadOnlyList<SignalKnowledge> Signals { get; init; } = [];

    /// <summary>The profile's learned track circuits.</summary>
    public IReadOnlyList<PlaceKnowledge> TrackCircuits { get; init; } = [];

    /// <summary>The profile's learned TD berths.</summary>
    public IReadOnlyList<PlaceKnowledge> Berths { get; init; } = [];

    public LocationKnowledge? Location(Tiploc location) =>
        (_locations ??= Locations.ToDictionary(l => l.Tiploc)).GetValueOrDefault(location);

    /// <summary>The link A → B as booked, or null if no timetable books it.</summary>
    public LinkKnowledge? Link(Tiploc from, Tiploc to) =>
        (_links ??= Links.ToDictionary(l => (l.From, l.To))).GetValueOrDefault((from, to));

    /// <summary>The links booked out of a location.</summary>
    public IReadOnlyList<LinkKnowledge> LinksFrom(Tiploc from) =>
        (_from ??= Links.GroupBy(l => l.From).ToDictionary(g => g.Key, g => g.ToList())).TryGetValue(from, out var links) ? links : [];

    public SignalKnowledge? Signal(string id) =>
        (_signals ??= Signals.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(id.Trim());

    /// <summary>The run time over A → B for trains of this band, if any was booked.</summary>
    public TimeStats? RunTime(Tiploc from, Tiploc to, RunBand band) => Link(from, to)?.RunTimes?.GetValueOrDefault(band);

    /// <summary>
    /// The run time over A → B for the band nearest this one that has any: the band itself, else the same freight
    /// flag at the nearest speed (the slower of two equally near), else the other flag likewise. A band of unknown
    /// speed is used only when asked for.
    /// </summary>
    public (RunBand Band, TimeStats Times)? NearestRunTime(Tiploc from, Tiploc to, RunBand band) => Nearest(Link(from, to)?.RunTimes, band);

    /// <summary>
    /// The least run known along a train's locations, first to last (S44, a drag on the draft board stops there): the
    /// sum of each link's least run for the nearest band (<see cref="NearestRunTime"/>), else, if a link is unknown, the
    /// direct link's from first to last. Null if neither is known.
    /// </summary>
    public int? LeastRun(IReadOnlyList<Tiploc> along, RunBand band)
    {
        ArgumentNullException.ThrowIfNull(along);
        if (along.Count < 2)
        {
            return null;
        }

        var total = 0;
        for (var k = 1; k < along.Count; k++)
        {
            if (along[k - 1] == along[k])
            {
                continue;
            }

            if (NearestRunTime(along[k - 1], along[k], band) is not { } step)
            {
                return NearestRunTime(along[0], along[^1], band)?.Times.LeastSeconds;
            }

            total += step.Times.LeastSeconds;
        }

        return total;
    }

    /// <summary>The booked dwell at a location for trains of this band, if any.</summary>
    public TimeStats? Dwell(Tiploc location, RunBand band) => Location(location)?.Dwells?.GetValueOrDefault(band);

    /// <summary>The booked dwell for the nearest band that has any (as <see cref="NearestRunTime"/>).</summary>
    public (RunBand Band, TimeStats Times)? NearestDwell(Tiploc location, RunBand band) => Nearest(Location(location)?.Dwells, band);

    /// <summary>
    /// The timing locations trains are booked through between A and B, both included: a skip chain with these ends
    /// (turned round if need be), else the fewest booked links from A to B (up to <paramref name="maxLinks"/>), the
    /// most booked on a tie. Null if none is known. A chain of two is A → B itself.
    /// </summary>
    public IReadOnlyList<Tiploc>? Chain(Tiploc from, Tiploc to, int maxLinks = 8)
    {
        if (from == to)
        {
            return null;
        }

        foreach (var skip in SkipChains)
        {
            if (skip.Chain[0] == from && skip.Chain[^1] == to)
            {
                return skip.Chain;
            }

            if (skip.Chain[0] == to && skip.Chain[^1] == from)
            {
                return [.. skip.Chain.Reverse()];
            }
        }

        // Breadth first, so the first paths to reach B are the shortest; among them, the one whose least-booked link is
        // booked most.
        var best = new Dictionary<Tiploc, (List<Tiploc> Path, int Weakest)> { [from] = ([from], int.MaxValue) };
        var frontier = new List<Tiploc> { from };
        for (var depth = 0; depth < maxLinks && frontier.Count > 0; depth++)
        {
            var reached = new Dictionary<Tiploc, (List<Tiploc> Path, int Weakest)>();
            foreach (var at in frontier)
            {
                var (path, weakest) = best[at];
                foreach (var link in LinksFrom(at))
                {
                    if (best.ContainsKey(link.To))
                    {
                        continue;
                    }

                    var candidate = (Path: (List<Tiploc>)[.. path, link.To], Weakest: Math.Min(weakest, link.Count));
                    if (!reached.TryGetValue(link.To, out var other) || candidate.Weakest > other.Weakest)
                    {
                        reached[link.To] = candidate;
                    }
                }
            }

            foreach (var (location, found) in reached)
            {
                best[location] = found;
            }

            if (reached.TryGetValue(to, out var result))
            {
                return result.Path;
            }

            frontier = [.. reached.Keys.OrderBy(t => t.Code, StringComparer.Ordinal)];
        }

        return null;
    }

    /// <summary>The value for <paramref name="band"/>, else the nearest band's (see <see cref="NearestRunTime"/>).</summary>
    private static (RunBand Band, TimeStats Times)? Nearest(IReadOnlyDictionary<RunBand, TimeStats>? byBand, RunBand band)
    {
        if (byBand is null || byBand.Count == 0)
        {
            return null;
        }

        if (byBand.TryGetValue(band, out var exact))
        {
            return (band, exact);
        }

        foreach (var freight in new[] { band.Freight, !band.Freight })
        {
            var found = byBand.Keys
                .Where(k => k.Freight == freight && k.Speed != SpeedBand.Unknown)
                .OrderBy(k => band.Speed == SpeedBand.Unknown ? 0 : Math.Abs(k.Speed - band.Speed))
                .ThenBy(k => k.Speed)
                .Cast<RunBand?>()
                .FirstOrDefault();
            if (found is { } k)
            {
                return (k, byBand[k]);
            }
        }

        return null;
    }
}

/// <summary>Reads and writes <see cref="SimKnowledge"/> as JSON.</summary>
public static class SimKnowledgeJson
{
    public static System.Text.Json.JsonSerializerOptions Options { get; } = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(SimKnowledge knowledge) => System.Text.Json.JsonSerializer.Serialize(knowledge, Options) + "\n";

    /// <summary>The knowledge in this JSON, or null if it is not readable knowledge in the current format.</summary>
    public static SimKnowledge? TryDeserialize(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<SimKnowledge>(json, Options) is { Format: SimKnowledge.CurrentFormat } knowledge ? knowledge : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
