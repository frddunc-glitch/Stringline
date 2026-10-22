using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network;

public sealed record CorridorGeneratorOptions
{
    /// <summary>Most candidates to return.</summary>
    public int MaxCorridors { get; init; } = 6;

    /// <summary>A candidate must start from an edge at least this many trains run over, not counting out-and-back moves.</summary>
    public int MinTrains { get; init; } = 2;

    /// <summary>
    /// A corridor is extended past a location only to the neighbour most trains continue to, and only if at least this
    /// fraction of the trains arriving there carry on to it.
    /// </summary>
    public double ThroughFraction { get; init; } = 0.25;

    /// <summary>
    /// A candidate after the first is dropped if less than this share of its traffic is on sections no earlier
    /// candidate covers: a siding or a short spur off a corridor, which would otherwise repeat most of it.
    /// </summary>
    public double MinNewShare { get; init; } = 0.05;

    /// <summary>A candidate after the first needs at least this many locations; a single section is not much of a graph.</summary>
    public int MinLocations { get; init; } = 3;

    /// <summary>Most locations a skip edge may stand for (see <see cref="LocationGraph"/>).</summary>
    public int MaxSkipped { get; init; } = LocationGraph.MaxSkipped;

    /// <summary>
    /// A corridor is not extended to a location joined directly to one it already has more than this many locations
    /// back: that would close a loop, and trains taking the short way would jump across the graph.
    /// </summary>
    public int MaxChordSkip { get; init; } = 2;

    /// <summary>
    /// A line closes a loop only if at least this share of the trains on the step being taken run over it (and at
    /// least <see cref="MinTrains"/>): a rare move does not cut a corridor short.
    /// </summary>
    public double ChordShare { get; init; } = 0.1;

    /// <summary>
    /// A candidate after the first whose two ends are both on earlier corridors (another way between places they
    /// already show, such as a loop line booked through other timing points) needs at least this many locations no
    /// earlier corridor has.
    /// </summary>
    public int MinNewOnParallel { get; init; } = 3;
}

/// <param name="Traffic">Trains over each of the corridor's sections, summed (a train over 5 sections counts 5).</param>
/// <param name="NewTraffic">The part of <see cref="Traffic"/> on sections no earlier candidate covers.</param>
public sealed record CorridorCandidate(Corridor Corridor, CorridorMeasurement Measurement, int Traffic, int NewTraffic);

/// <summary>
/// Proposes corridors from a WTT (<c>04-…md</c> §4). Greedy: the busiest edge of the <see cref="LocationGraph"/> not
/// yet on a corridor seeds a new one, which grows at its two ends in turn towards the neighbour most trains run
/// through to, for as long as enough of them do. A corridor may run along part of an earlier one: a branch follows its
/// trains onto the main line, so each graph shows its trains end to end (Fred's choice), as long as enough of the
/// branch's own trains carry on (not of all the main line's). A corridor never closes a loop (Edinburgh's Fife
/// Circle): it stops short of a location joined directly to one it already has further back, and an end that stopped
/// so drops the stretch of an earlier corridor it had followed, ending at the junction. Each corridor is then oriented
/// with the Up end first, from SimSig's Down flags; a timetable without them puts the end where more trains start or
/// finish first. The first candidate is <c>main</c>.
/// </summary>
public static class CorridorGenerator
{
    public static IReadOnlyList<CorridorCandidate> Generate(WorkingTimetable timetable, CorridorGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        options ??= new CorridorGeneratorOptions();
        return Generate(timetable, LocationGraph.Build(timetable, options.MaxSkipped), options);
    }

    public static IReadOnlyList<CorridorCandidate> Generate(WorkingTimetable timetable, LocationGraph graph, CorridorGeneratorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new CorridorGeneratorOptions();
        var covered = new HashSet<LocationPair>();
        var candidates = new List<CorridorCandidate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var flags = timetable.Trains.Any(t => t.Points.Any(p => p.DownDirection is not null));

        var routesOver = RoutesOver(graph);

        while (candidates.Count < options.MaxCorridors)
        {
            // Out-and-back moves (run-rounds, shunts into sidings) do not make a line worth a graph.
            var seed = graph.Edges
                .Select(e => new KeyValuePair<LocationPair, int>(e.Key, graph.Line(e.Key)))
                .Where(e => !covered.Contains(e.Key) && e.Value >= options.MinTrains)
                .OrderByDescending(e => e.Value)
                .ThenBy(e => e.Key.A.Code, StringComparer.Ordinal)
                .ThenBy(e => e.Key.B.Code, StringComparer.Ordinal)
                .Select(e => (KeyValuePair<LocationPair, int>?)e)
                .FirstOrDefault();
            if (seed is not { Key: var pair })
            {
                break;
            }

            var path = new Growth(graph, routesOver, covered, options, pair).Run();

            var edges = path.Zip(path.Skip(1), (a, b) => new LocationPair(a, b)).ToList();
            var newTraffic = edges.Where(e => !covered.Contains(e)).Sum(graph.Weight);
            covered.UnionWith(edges);
            var traffic = edges.Sum(graph.Weight);
            if (candidates.Count > 0 && (path.Count < options.MinLocations || candidates.Any(c => path.All(c.Corridor.Contains)) || newTraffic < options.MinNewShare * traffic || IsParallel(path, candidates, options)))
            {
                continue; // Too short, a chord between locations an earlier corridor has, a spur adding next to nothing, or a short parallel way.
            }

            var measurement = CorridorMeasurement.Measure(timetable, path, graph);
            if (ShouldReverse(timetable, path, measurement, flags))
            {
                path.Reverse();
                measurement = CorridorMeasurement.Measure(timetable, path, graph);
            }

            var id = candidates.Count == 0 ? "main" : UniqueId($"{path[0].Code}-{path[^1].Code}".ToLowerInvariant(), ids);
            ids.Add(id);
            var corridor = CorridorDrawAt.Apply(CorridorBuilder.Create(id, Name(path, candidates), path, measurement), timetable);
            candidates.Add(new CorridorCandidate(corridor, measurement, traffic, newTraffic));
        }

        return candidates;
    }

    /// <summary>Both ends on earlier corridors, and fewer than <see cref="CorridorGeneratorOptions.MinNewOnParallel"/> locations of its own.</summary>
    private static bool IsParallel(List<Tiploc> path, List<CorridorCandidate> earlier, CorridorGeneratorOptions options)
    {
        bool Shown(Tiploc t) => earlier.Any(c => c.Corridor.Contains(t));
        return Shown(path[0]) && Shown(path[^1]) && path.Count(t => !Shown(t)) < options.MinNewOnParallel;
    }

    /// <summary>For each edge, the routes (<see cref="LocationGraph.Routes"/>, by index) that run over it.</summary>
    private static Dictionary<LocationPair, List<int>> RoutesOver(LocationGraph graph)
    {
        var over = new Dictionary<LocationPair, List<int>>();
        for (var r = 0; r < graph.Routes.Count; r++)
        {
            var route = graph.Routes[r];
            for (var i = 1; i < route.Length; i++)
            {
                var pair = new LocationPair(route[i - 1], route[i]);
                var list = over.TryGetValue(pair, out var l) ? l : over[pair] = [];
                if (list.Count == 0 || list[^1] != r)
                {
                    list.Add(r);
                }
            }
        }

        return over;
    }

    /// <summary>One corridor growing from its seed edge, an end at a time.</summary>
    private sealed class Growth(LocationGraph graph, Dictionary<LocationPair, List<int>> routesOver, HashSet<LocationPair> covered, CorridorGeneratorOptions options, LocationPair seed)
    {
        private readonly List<Tiploc> _path = [seed.A, seed.B];
        private readonly HashSet<Tiploc> _visited = [seed.A, seed.B];

        /// <summary>Routes over the corridor's edges no earlier corridor has: its own trains.</summary>
        private readonly HashSet<int> _own = [];

        /// <summary>For each end, its own trains where it joined an earlier corridor (null until it has).</summary>
        private readonly int?[] _joined = new int?[2];

        private readonly bool[] _looped = new bool[2];

        public List<Tiploc> Run()
        {
            Own(seed);
            var open = new[] { true, true };
            while (open[0] || open[1])
            {
                for (var end = 0; end < 2; end++)
                {
                    open[end] = open[end] && Step(end);
                }
            }

            for (var end = 0; end < 2; end++)
            {
                // An end that ran into a loop along an earlier corridor goes back to the junction it joined it at.
                while (_looped[end] && _path.Count > 2 && covered.Contains(new LocationPair(At(end, 0), At(end, 1))))
                {
                    _path.RemoveAt(end == 0 ? _path.Count - 1 : 0);
                }
            }

            return _path;
        }

        /// <summary>The location <paramref name="k"/> places in from an end (0: the end itself). End 0 is the last location, end 1 the first.</summary>
        private Tiploc At(int end, int k) => end == 0 ? _path[_path.Count - 1 - k] : _path[k];

        /// <summary>Adds the next location at this end, or returns false if the end stops here.</summary>
        private bool Step(int end)
        {
            var (previous, at) = (At(end, 1), At(end, 0));
            var best = graph.Neighbours(at)
                .Where(n => !_visited.Contains(n))
                .Select(n => (Next: n, Through: graph.Through(previous, at, n), Weight: graph.Weight(at, n)))
                .OrderByDescending(x => x.Through)
                .ThenByDescending(x => x.Weight)
                .FirstOrDefault();
            if (best.Next.IsEmpty)
            {
                return false;
            }

            if (best.Through < Math.Max(1, options.ThroughFraction * graph.Weight(previous, at)))
            {
                return false;
            }

            var edge = new LocationPair(at, best.Next);
            if (covered.Contains(edge) && _own.Count > 0)
            {
                // Following its trains along an earlier corridor: also only as far as enough of its own trains go.
                var joined = _joined[end] ??= Count(previous, at, null);
                if (Count(previous, at, best.Next) < Math.Max(1, options.ThroughFraction * joined))
                {
                    return false;
                }
            }

            if (ClosesLoop(end, best.Next, best.Weight))
            {
                _looped[end] = true;
                return false;
            }

            if (end == 0)
            {
                _path.Add(best.Next);
            }
            else
            {
                _path.Insert(0, best.Next);
            }

            _visited.Add(best.Next);
            if (!covered.Contains(edge))
            {
                Own(edge);
            }

            return true;
        }

        /// <summary>True if <paramref name="next"/> is joined by a line to a location more than <see cref="CorridorGeneratorOptions.MaxChordSkip"/> places back.</summary>
        private bool ClosesLoop(int end, Tiploc next, int weight)
        {
            var least = Math.Max(options.MinTrains, options.ChordShare * weight);
            for (var k = options.MaxChordSkip + 1; k < _path.Count; k++)
            {
                if (graph.Line(new LocationPair(next, At(end, k))) >= least)
                {
                    return true;
                }
            }

            return false;
        }

        private void Own(LocationPair edge)
        {
            if (routesOver.TryGetValue(edge, out var routes))
            {
                _own.UnionWith(routes);
            }
        }

        /// <summary>Own trains that run <paramref name="a"/>, <paramref name="b"/> (then <paramref name="c"/>, if given) in a row, either way.</summary>
        private int Count(Tiploc a, Tiploc b, Tiploc? c)
        {
            var count = 0;
            foreach (var r in routesOver.GetValueOrDefault(new LocationPair(a, b)) ?? [])
            {
                if (_own.Contains(r) && Runs(graph.Routes[r], a, b, c))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool Runs(Tiploc[] route, Tiploc a, Tiploc b, Tiploc? c)
        {
            for (var i = 1; i < route.Length; i++)
            {
                if (route[i] != b)
                {
                    continue;
                }

                if (route[i - 1] == a && (c is not { } after || (i + 1 < route.Length && route[i + 1] == after)))
                {
                    return true;
                }

                if (i + 1 < route.Length && route[i + 1] == a && (c is not { } before || route[i - 1] == before))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// True if the Down direction runs from the last location to the first. Each train that runs along the corridor
    /// votes by how many of its corridor points SimSig flags Down (in a file that uses the flag, an unflagged point is
    /// Up). Without flags, or on a tie, the end where more trains start or terminate goes first.
    /// </summary>
    private static bool ShouldReverse(WorkingTimetable timetable, List<Tiploc> path, CorridorMeasurement measurement, bool flags)
    {
        if (flags)
        {
            var probe = new Corridor("probe", "", path.Select(t => new CorridorLocation(t, 0)).ToList(), []);
            var vote = 0.0;
            foreach (var train in timetable.Trains)
            {
                var on = train.Points.Where(p => probe.Contains(p.Location)).ToList();
                if (on.Count < 2 || probe.DirectionOf(on.Select(p => p.Location)) is not { } direction)
                {
                    continue;
                }

                var down = (double)on.Count(p => p.DownDirection == true) / on.Count;
                vote += (direction == TrackDirection.Down ? 1 : -1) * ((2 * down) - 1);
            }

            if (vote != 0)
            {
                return vote < 0;
            }
        }

        var (first, last) = (measurement.Locations[0], measurement.Locations[^1]);
        return last.Starts + last.Terminates > first.Starts + first.Terminates;
    }

    /// <summary>"A – B", or "A – B via X" if an earlier candidate has the same ends (X: the first location it does not have).</summary>
    private static string Name(List<Tiploc> path, List<CorridorCandidate> earlier)
    {
        var name = $"{path[0]} – {path[^1]}";
        var same = earlier.Where(c => c.Corridor.Name.StartsWith(name, StringComparison.Ordinal)).ToList();
        if (same.Count == 0)
        {
            return name;
        }

        var via = path.FirstOrDefault(t => same.All(c => !c.Corridor.Contains(t)));
        return via.IsEmpty ? $"{name} ({same.Count + 1})" : $"{name} via {via}";
    }

    private static string UniqueId(string id, HashSet<string> taken)
    {
        var candidate = id;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }
}
