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
}

/// <param name="Traffic">Trains over each of the corridor's sections, summed (a train over 5 sections counts 5).</param>
/// <param name="NewTraffic">The part of <see cref="Traffic"/> on sections no earlier candidate covers.</param>
public sealed record CorridorCandidate(Corridor Corridor, CorridorMeasurement Measurement, int Traffic, int NewTraffic);

/// <summary>
/// Proposes corridors from a WTT (<c>04-…md</c> §4). Greedy: the busiest edge of the <see cref="LocationGraph"/> not
/// yet on a corridor seeds a new one, which grows at each end towards the neighbour most trains run through to, for
/// as long as enough of them do. A corridor may run along part of an earlier one: a branch follows its trains onto
/// the main line, so each graph shows its trains end to end (Fred's choice). Each corridor is then oriented with the
/// Up end first, from SimSig's Down flags; a timetable without them puts the end where more trains start or finish
/// first. The first candidate is <c>main</c>.
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

        while (candidates.Count < options.MaxCorridors)
        {
            // Out-and-back moves (run-rounds, shunts into sidings) do not make a line worth a graph.
            var seed = graph.Edges
                .Select(e => new KeyValuePair<LocationPair, int>(e.Key, e.Value - graph.Reversing(e.Key)))
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

            var path = new List<Tiploc> { pair.A, pair.B };
            Extend(graph, path, options.ThroughFraction);
            path.Reverse();
            Extend(graph, path, options.ThroughFraction);

            var edges = path.Zip(path.Skip(1), (a, b) => new LocationPair(a, b)).ToList();
            var newTraffic = edges.Where(e => !covered.Contains(e)).Sum(graph.Weight);
            covered.UnionWith(edges);
            var traffic = edges.Sum(graph.Weight);
            if (candidates.Count > 0 && (path.Count < options.MinLocations || candidates.Any(c => path.All(c.Corridor.Contains)) || newTraffic < options.MinNewShare * traffic))
            {
                continue; // Too short, a chord between locations an earlier corridor has, or a spur adding next to nothing.
            }

            var measurement = CorridorMeasurement.Measure(timetable, path);
            if (ShouldReverse(timetable, path, measurement, flags))
            {
                path.Reverse();
                measurement = CorridorMeasurement.Measure(timetable, path);
            }

            var id = candidates.Count == 0 ? "main" : UniqueId($"{path[0].Code}-{path[^1].Code}".ToLowerInvariant(), ids);
            ids.Add(id);
            var corridor = CorridorBuilder.Create(id, Name(path, candidates), path, measurement);
            candidates.Add(new CorridorCandidate(corridor, measurement, traffic, newTraffic));
        }

        return candidates;
    }

    /// <summary>Grows the path at its last location while enough trains run through to an unvisited neighbour.</summary>
    private static void Extend(LocationGraph graph, List<Tiploc> path, double throughFraction)
    {
        var visited = new HashSet<Tiploc>(path);
        while (true)
        {
            var (previous, at) = (path[^2], path[^1]);
            var arriving = graph.Weight(previous, at);
            var best = graph.Neighbours(at)
                .Where(n => !visited.Contains(n))
                .Select(n => (Next: n, Through: graph.Through(previous, at, n), Weight: graph.Weight(at, n)))
                .OrderByDescending(x => x.Through)
                .ThenByDescending(x => x.Weight)
                .FirstOrDefault();
            if (best.Through < Math.Max(1, throughFraction * arriving))
            {
                return;
            }

            path.Add(best.Next);
            visited.Add(best.Next);
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
