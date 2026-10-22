using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.Editing.Knowledge;

/// <summary>
/// Gathers <see cref="SimKnowledge"/> for one sim from its timetables, recordings and sessions, and profile, in any
/// order, then <see cref="Build"/>s it. Not thread-safe.
/// <list type="bullet">
/// <item><b>Timetables</b> give the locations and their roles, the platform, line and path codes, the booked links with
/// their run times by band (from the departure or pass at A to the arrival or pass at B, less A's allowances, as
/// <c>CorridorMeasurement</c>; runs of zero or less are bad data and left out), dwells at stops without activities, and
/// skip chains (<see cref="LocationGraph"/> over all the timetables' trains).</item>
/// <item><b>Play</b> gives the locations trains reported and the platforms they reported there, the signals they passed,
/// and the signals trains entered at. A reported location is a timing location if some timetable books it, it is on
/// a corridor, or a train stopped there; anything else is a signal (SimSig reports only those two).</item>
/// <item><b>The profile</b> gives location names, corridors, and the learned places of signals, track circuits and
/// berths (S37).</item>
/// </list>
/// </summary>
public sealed class SimKnowledgeBuilder
{
    /// <summary>A train first reported this long after play began entered the sim then (as <c>LiveState</c> takes it).</summary>
    public const int PresentAtStartSeconds = 120;

    private readonly Dictionary<Tiploc, LocationAccumulator> _locations = [];
    private readonly Dictionary<(Tiploc, Tiploc), LinkAccumulator> _links = [];
    private readonly List<Train> _allTrains = [];
    private readonly Dictionary<Headcode, HashSet<string>> _entryPointsByHeadcode = [];
    private readonly List<PlaySummary> _plays = [];
    private StringlineProfile? _profile;
    private int _timetables;

    public SimKnowledgeBuilder(string areaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        AreaId = areaId.Trim();
    }

    public string AreaId { get; }

    /// <summary>Adds a timetable for this sim.</summary>
    /// <exception cref="ArgumentException">It is another sim's.</exception>
    public void AddTimetable(WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        if (!string.Equals(timetable.AreaId, AreaId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"The timetable is for '{timetable.AreaId}', not '{AreaId}'.", nameof(timetable));
        }

        var file = _timetables++;
        foreach (var train in timetable.Trains)
        {
            AddTrain(timetable, train);
            _allTrains.Add(train with { Id = new TrainId($"{file}/{train.Id}") });
        }
    }

    /// <summary>
    /// Adds a recording's or session's events. A source whose clock names another sim is left out.
    /// </summary>
    /// <returns>Whether it was used.</returns>
    public bool AddPlay(IEnumerable<GatewayEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var play = new PlaySummary();
        var first = new Dictionary<string, (Headcode Headcode, Tiploc Location, SimTime Time)>(StringComparer.Ordinal);
        SimTime? start = null;
        foreach (var e in events)
        {
            switch (e)
            {
                case ClockEvent clock:
                    if (clock.AreaId.Trim().Length > 0 && !string.Equals(clock.AreaId.Trim(), AreaId, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (clock.Clock.Seconds > 0 && (start is null || clock.Clock < start))
                    {
                        start = clock.Clock;
                    }

                    break;
                case TrainLocationEvent { Uid.Length: > 0 } report when !report.Location.IsEmpty:
                    start = start is { } s && s <= report.Time ? s : report.Time;
                    first.TryAdd(report.Uid, (report.Headcode, report.Location, report.Time));
                    var at = play.Locations.TryGetValue(report.Location, out var a) ? a : play.Locations[report.Location] = new PlayLocation();
                    at.Reports++;
                    at.Stops += report.Action is TrainLocationAction.Arrive or TrainLocationAction.Depart ? 1 : 0;
                    at.WithAspect |= report.AspectPassed is not null || report.AspectApproach is not null;
                    if (report.Platform.Trim() is { Length: > 0 } platform)
                    {
                        Count(at.Platforms, platform);
                    }

                    break;
            }
        }

        foreach (var (headcode, location, time) in first.Values)
        {
            if (start is { } s && time - s > PresentAtStartSeconds)
            {
                play.Entries.Add((headcode, location));
            }
        }

        _plays.Add(play);
        return true;
    }

    /// <summary>Uses this profile (the last one added) for names, corridors and learned places.</summary>
    public void AddProfile(StringlineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    public SimKnowledge Build(IReadOnlyList<KnowledgeSource>? sources = null, DateTimeOffset? built = null)
    {
        // Reported locations: timing locations or signals.
        var corridorOf = new Dictionary<Tiploc, SortedSet<string>>();
        foreach (var corridor in _profile?.Corridors ?? [])
        {
            foreach (var location in corridor.Locations)
            {
                (corridorOf.TryGetValue(location.Tiploc, out var ids) ? ids : corridorOf[location.Tiploc] = new SortedSet<string>(StringComparer.Ordinal)).Add(corridor.Id);
            }
        }

        var signals = new SortedDictionary<string, SignalAccumulator>(StringComparer.Ordinal);
        foreach (var play in _plays)
        {
            foreach (var (location, seen) in play.Locations)
            {
                var timing = !seen.WithAspect && (_locations.TryGetValue(location, out var booked) && booked.Calls > 0 || corridorOf.ContainsKey(location) || seen.Stops > 0);
                if (timing)
                {
                    var at = Location(location);
                    at.Reports += seen.Reports;
                    foreach (var (platform, count) in seen.Platforms)
                    {
                        Count(at.ReportedPlatforms, platform, count);
                    }
                }
                else
                {
                    Signal(signals, location.Code).Reports += seen.Reports;
                }
            }
        }

        foreach (var (headcode, location) in _plays.SelectMany(p => p.Entries))
        {
            if (!signals.TryGetValue(location.Code, out var signal))
            {
                continue; // first reported at a timing location: seeded, or formed there
            }

            signal.Entries++;
            if (_entryPointsByHeadcode.TryGetValue(headcode, out var points) && points.Count == 1)
            {
                Count(signal.EntryPoints, points.First());
            }
        }

        // The profile's learned places.
        var places = _profile?.Places ?? SignallingPlaces.Empty;
        var minSamples = SignallingPlaces.DefaultMinSamples;
        foreach (var place in places.Signals)
        {
            Signal(signals, place.Id).Places.Add(ToKnowledge(place, minSamples));
        }

        foreach (var name in _profile?.LocationNames ?? new Dictionary<Tiploc, string>())
        {
            if (_locations.TryGetValue(name.Key, out var at) && !string.IsNullOrWhiteSpace(name.Value))
            {
                at.Name = name.Value.Trim();
            }
        }

        var graph = LocationGraph.Build(new WorkingTimetable(
            new TimetableHeader(AreaId, string.Empty, string.Empty, string.Empty, null, SimTime.Zero, SimTime.Zero), [], _allTrains, []));
        var chains = graph.Expansions
            .OrderBy(e => e.Key.A.Code, StringComparer.Ordinal).ThenBy(e => e.Key.B.Code, StringComparer.Ordinal)
            .Select(e => new SkipChain([.. e.Value]))
            .ToList();

        return new SimKnowledge
        {
            AreaId = AreaId,
            Built = built ?? DateTimeOffset.UtcNow,
            Sources = sources ?? [],
            Timetables = _timetables,
            Trains = _allTrains.Count,
            Plays = _plays.Count,
            Locations = [.. _locations.Values.OrderBy(l => l.Tiploc.Code, StringComparer.Ordinal).Select(l => l.ToKnowledge(corridorOf.GetValueOrDefault(l.Tiploc)))],
            Links = [.. _links.Values.OrderBy(l => l.From.Code, StringComparer.Ordinal).ThenBy(l => l.To.Code, StringComparer.Ordinal).Select(l => l.ToKnowledge())],
            SkipChains = chains,
            Signals = [.. signals.Values.Select(s => s.ToKnowledge())],
            TrackCircuits = [.. places.TrackCircuits.Select(p => ToKnowledge(p, minSamples))],
            Berths = [.. places.Berths.Select(p => ToKnowledge(p, minSamples))],
        };
    }

    private void AddTrain(WorkingTimetable timetable, Train train)
    {
        var band = RunBand.Of(train);
        if (train.Start == TrainStart.EntryPoint && train.EntryPoint is { } entry)
        {
            var at = Location(entry);
            at.Entries++;
            foreach (var power in (train.Electrification ?? string.Empty).Trim())
            {
                Count(at.EntryPower, power.ToString());
            }

            (_entryPointsByHeadcode.TryGetValue(train.Headcode, out var set) ? set : _entryPointsByHeadcode[train.Headcode] = new HashSet<string>(StringComparer.Ordinal)).Add(entry.Code);
        }
        else if (train.Start == TrainStart.Seeded && train.SeedPoint is { } seed)
        {
            Location(seed).Seeds++;
        }

        var points = train.Points;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var at = Location(point.Location);
            at.Calls++;
            at.Stops += point.IsPass || !point.IsTimed ? 0 : 1;
            at.Activities += point.Activities.Count > 0 ? 1 : 0;
            Count(at.Platforms, point.Platform);
            Count(at.Lines, point.Line);
            Count(at.Paths, point.Path);
            if (i == points.Count - 1 && point.IsPass)
            {
                at.Exits++;
            }
            else if (i == points.Count - 1)
            {
                at.Ends++;
            }
            else if (i > 0)
            {
                at.Through++;
            }

            if (!point.IsPass && point.Activities.Count == 0 && i > 0 && i < points.Count - 1
                && point is { Arrive: { } arrive, Depart: { } depart } && depart >= arrive
                && points[i - 1].Location != point.Location && points[i + 1].Location != point.Location)
            {
                at.Dwells(band).Add(depart - arrive);
            }
        }

        // Visits: consecutive points at one location are one call there.
        var visits = new List<(int First, int Last)>();
        for (var i = 0; i < points.Count; i++)
        {
            if (visits.Count > 0 && points[visits[^1].Last].Location == points[i].Location)
            {
                visits[^1] = (visits[^1].First, i);
            }
            else
            {
                visits.Add((i, i));
            }
        }

        for (var v = 1; v < visits.Count; v++)
        {
            var (from, to) = (points[visits[v - 1].Last], points[visits[v].First]);
            var link = _links.TryGetValue((from.Location, to.Location), out var l) ? l : _links[(from.Location, to.Location)] = new LinkAccumulator(from.Location, to.Location);
            link.Count++;
            switch (to.DownDirection ?? from.DownDirection)
            {
                case true:
                    link.Down++;
                    break;
                case false:
                    link.Up++;
                    break;
            }

            Count(link.Lines, from.Line);
            Count(link.Paths, to.Path);
            if (from.LastTime is { } leaves && to.FirstTime is { } reaches && reaches - leaves - from.AllowanceSeconds is var run and > 0)
            {
                link.Runs(band).Add(run);
            }

            // Out and back: a reversal at the middle visit.
            if (v + 1 < visits.Count && points[visits[v + 1].First].Location == from.Location)
            {
                Location(to.Location).Reversals++;
            }
        }

        // A train that terminates and forms one back the way it came reverses there too.
        if (visits.Count >= 2 && train.NextWorking is { } next && timetable.Find(next) is { Points: var onward }
            && onward[0].Location == train.Destination && onward.FirstOrDefault(p => p.Location != train.Destination) is { } leaving
            && leaving.Location == points[visits[^2].Last].Location)
        {
            Location(train.Destination).Reversals++;
        }
    }

    private LocationAccumulator Location(Tiploc tiploc) =>
        _locations.TryGetValue(tiploc, out var at) ? at : _locations[tiploc] = new LocationAccumulator(tiploc);

    private static SignalAccumulator Signal(SortedDictionary<string, SignalAccumulator> signals, string id) =>
        signals.TryGetValue(id, out var s) ? s : signals[id] = new SignalAccumulator(id);

    private static PlaceKnowledge ToKnowledge(SignallingPlace place, int minSamples) =>
        new(place.Id, place.From, place.To, place.Run, Math.Round(place.Fraction, 4), place.SampleCount, place.IsUsable(minSamples), place.Line);

    private static void Count(Dictionary<string, int> counts, string? code, int by = 1)
    {
        if (code?.Trim() is { Length: > 0 } trimmed)
        {
            counts[trimmed] = counts.GetValueOrDefault(trimmed) + by;
        }
    }

    private static SortedDictionary<string, int>? Sorted(Dictionary<string, int> counts) =>
        counts.Count == 0 ? null : new SortedDictionary<string, int>(counts, StringComparer.Ordinal);

    private static Dictionary<RunBand, TimeStats>? Stats(Dictionary<RunBand, List<int>> byBand) =>
        byBand.Count == 0 ? null : byBand
            .OrderBy(b => b.Key.Freight).ThenBy(b => b.Key.Speed)
            .ToDictionary(b => b.Key, b => TimeStats.Of(b.Value)!);

    private sealed class PlayLocation
    {
        public int Reports { get; set; }

        public int Stops { get; set; }

        public bool WithAspect { get; set; }

        public Dictionary<string, int> Platforms { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PlaySummary
    {
        public Dictionary<Tiploc, PlayLocation> Locations { get; } = [];

        public List<(Headcode Headcode, Tiploc Location)> Entries { get; } = [];
    }

    private sealed class LocationAccumulator(Tiploc tiploc)
    {
        private readonly Dictionary<RunBand, List<int>> _dwells = [];

        public Tiploc Tiploc => tiploc;

        public string? Name { get; set; }

        public int Calls { get; set; }

        public int Stops { get; set; }

        public int Activities { get; set; }

        public int Reversals { get; set; }

        public int Entries { get; set; }

        public int Seeds { get; set; }

        public int Reports { get; set; }

        public int Ends { get; set; }

        public int Exits { get; set; }

        public int Through { get; set; }

        public Dictionary<string, int> EntryPower { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Platforms { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Lines { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Paths { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> ReportedPlatforms { get; } = new(StringComparer.Ordinal);

        public List<int> Dwells(RunBand band) => _dwells.TryGetValue(band, out var list) ? list : _dwells[band] = [];

        public LocationKnowledge ToKnowledge(SortedSet<string>? corridors)
        {
            var roles = LocationRoles.None;
            roles |= Calls > 0 ? LocationRoles.Timing : 0;
            roles |= Entries > 0 ? LocationRoles.Entry : 0;
            roles |= Seeds > 0 ? LocationRoles.Seed : 0;
            roles |= Stops > 0 ? LocationRoles.Station : 0;
            roles |= Activities > 0 ? LocationRoles.Key : 0;
            roles |= Reversals > 0 ? LocationRoles.Reversing : 0;
            roles |= Reports > 0 ? LocationRoles.Reported : 0;
            return new LocationKnowledge(
                tiploc, roles, Name, Calls, Stops, Activities, Reversals, Entries, Seeds, Reports,
                Sorted(Platforms), Sorted(Lines), Sorted(Paths), Sorted(ReportedPlatforms), Stats(_dwells),
                corridors is null ? null : [.. corridors], Ends, Exits, Through, Sorted(EntryPower));
        }
    }

    private sealed class LinkAccumulator(Tiploc from, Tiploc to)
    {
        private readonly Dictionary<RunBand, List<int>> _runs = [];

        public Tiploc From => from;

        public Tiploc To => to;

        public int Count { get; set; }

        public int Down { get; set; }

        public int Up { get; set; }

        public Dictionary<string, int> Lines { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Paths { get; } = new(StringComparer.Ordinal);

        public List<int> Runs(RunBand band) => _runs.TryGetValue(band, out var list) ? list : _runs[band] = [];

        public LinkKnowledge ToKnowledge() => new(from, to, Count, Down, Up, Sorted(Lines), Sorted(Paths), Stats(_runs));
    }

    private sealed class SignalAccumulator(string id)
    {
        public int Reports { get; set; }

        public int Entries { get; set; }

        public Dictionary<string, int> EntryPoints { get; } = new(StringComparer.Ordinal);

        public List<PlaceKnowledge> Places { get; } = [];

        public SignalKnowledge ToKnowledge() => new(id, Reports, Entries, Sorted(EntryPoints), Places.Count == 0 ? null : [.. Places]);
    }
}
