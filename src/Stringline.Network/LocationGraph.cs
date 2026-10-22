using System.Runtime.CompilerServices;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network;

/// <summary>An unordered pair of locations: <see cref="A"/> sorts before <see cref="B"/> (ordinal).</summary>
public readonly record struct LocationPair
{
    public LocationPair(Tiploc x, Tiploc y)
    {
        (A, B) = string.CompareOrdinal(x.Code, y.Code) <= 0 ? (x, y) : (y, x);
    }

    public Tiploc A { get; }

    public Tiploc B { get; }

    public Tiploc Other(Tiploc location) => location == A ? B : A;

    public override string ToString() => $"{A}–{B}";
}

/// <summary>
/// The timetable's locations as an undirected graph: an edge joins two locations that some train calls at one
/// after the other. Built in two steps:
/// <list type="number">
/// <item>Each train becomes a sequence of locations (repeats in a row, such as a reversal, merged).</item>
/// <item><b>Skip edges</b> are expanded. SimSig schedules may leave out timing locations a train passes (Waterloo's
/// 1W51 is booked WLOO–CJML though it passes VAUX), so an edge A–C is replaced by A–B–C when another train is booked
/// A, B, C (up to <see cref="MaxSkipped"/> locations between, no location twice, and not much slower than the direct
/// trains). The most common such chain wins, as long as it agrees with the orders already accepted. This repeats
/// until nothing changes, so the graph has the finest detail any train shows.</item>
/// </list>
/// Edge weights and through counts are then counted over the expanded sequences, each train once (a shunt may loop).
/// </summary>
public sealed class LocationGraph
{
    /// <summary>Default for the most locations a skip edge may stand for.</summary>
    public const int MaxSkipped = 6;

    /// <summary>A chain is rejected as a different route if it is booked slower than 2 × the direct time + 5 min.</summary>
    private const double ChainTimeFactor = 2.0;

    private const int ChainTimeSlackSeconds = 300;

    private const int MaxRounds = 10;

    private readonly Dictionary<LocationPair, int> _edges;
    private readonly Dictionary<(Tiploc Via, LocationPair Ends), int> _through;
    private readonly Dictionary<LocationPair, int> _reversing;
    private readonly Dictionary<Tiploc, SortedSet<string>> _neighbours;

    private static readonly ConditionalWeakTable<WorkingTimetable, LocationGraph> Cache = [];

    private LocationGraph(
        Dictionary<LocationPair, int> edges,
        Dictionary<(Tiploc, LocationPair), int> through,
        Dictionary<LocationPair, int> reversing,
        Dictionary<LocationPair, IReadOnlyList<Tiploc>> expansions,
        IReadOnlyList<Tiploc[]> routes)
    {
        _edges = edges;
        _through = through;
        _reversing = reversing;
        Expansions = expansions;
        Routes = routes;
        _neighbours = [];
        foreach (var pair in edges.Keys)
        {
            Neighbour(pair.A).Add(pair.B.Code);
            Neighbour(pair.B).Add(pair.A.Code);
        }

        SortedSet<string> Neighbour(Tiploc t) => _neighbours.TryGetValue(t, out var set) ? set : _neighbours[t] = new SortedSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Each edge with the number of trains that run over it (either direction).</summary>
    public IReadOnlyDictionary<LocationPair, int> Edges => _edges;

    /// <summary>The skip edges that were expanded: the pair, and the chain that replaced it (from <see cref="LocationPair.A"/> to <see cref="LocationPair.B"/>).</summary>
    public IReadOnlyDictionary<LocationPair, IReadOnlyList<Tiploc>> Expansions { get; }

    public IEnumerable<Tiploc> Locations => _neighbours.Keys;

    /// <summary>Each train's locations in running order, with skip edges expanded (a reversal is one location).</summary>
    public IReadOnlyList<Tiploc[]> Routes { get; }

    /// <summary>Trains that run over the edge between these locations (either direction), 0 if none.</summary>
    public int Weight(Tiploc a, Tiploc b) => _edges.GetValueOrDefault(new LocationPair(a, b));

    public int Weight(LocationPair pair) => _edges.GetValueOrDefault(pair);

    /// <summary>
    /// Trains that use the edge only for out-and-back moves: to the far end and straight back, as in a run-round or a
    /// shunt into a siding (Llangollen's CARROG–REVCA19–CARROG). Such an edge is a spur, not a line.
    /// </summary>
    public int Reversing(LocationPair pair) => _reversing.GetValueOrDefault(pair);

    /// <summary>Trains that run over the edge other than out and back: a line, not a spur (<see cref="Reversing"/>).</summary>
    public int Line(LocationPair pair) => Weight(pair) - Reversing(pair);

    /// <summary>Trains that go <paramref name="from"/>, <paramref name="via"/>, <paramref name="to"/> in a row, in either direction.</summary>
    public int Through(Tiploc from, Tiploc via, Tiploc to) => _through.GetValueOrDefault((via, new LocationPair(from, to)));

    /// <summary>The locations joined to this one by an edge, in ordinal order.</summary>
    public IEnumerable<Tiploc> Neighbours(Tiploc location) =>
        _neighbours.TryGetValue(location, out var set) ? set.Select(c => new Tiploc(c)) : [];

    /// <summary>
    /// The graph of this timetable with the default skip limit, built once and kept as long as the timetable is
    /// (corridors are measured against it again on every refresh).
    /// </summary>
    public static LocationGraph For(WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        return Cache.GetValue(timetable, t => Build(t));
    }

    public static LocationGraph Build(WorkingTimetable timetable, int maxSkipped = MaxSkipped)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSkipped);
        var sequences = timetable.Trains.Select(ToSequence).Where(s => s.Count > 1).ToList();
        var expansions = new Dictionary<LocationPair, IReadOnlyList<Tiploc>>();
        var between = new HashSet<(Tiploc Middle, LocationPair Ends)>();

        for (var round = 0; round < MaxRounds; round++)
        {
            var found = FindExpansions(sequences, maxSkipped, between);
            if (found.Count == 0)
            {
                break;
            }

            foreach (var (pair, chain) in found)
            {
                expansions.TryAdd(pair, chain);
            }

            for (var s = 0; s < sequences.Count; s++)
            {
                sequences[s] = Expand(sequences[s], found);
            }
        }

        // Each train counts once per edge and per through move, however often it runs over them (a shunt can loop).
        var edges = new Dictionary<LocationPair, int>();
        var through = new Dictionary<(Tiploc, LocationPair), int>();
        var reversing = new Dictionary<LocationPair, int>();
        var trainEdges = new HashSet<LocationPair>();
        var trainThrough = new HashSet<LocationPair>();
        var trainMoves = new HashSet<(Tiploc, LocationPair)>();
        foreach (var sequence in sequences)
        {
            trainEdges.Clear();
            trainThrough.Clear();
            trainMoves.Clear();
            for (var i = 1; i < sequence.Count; i++)
            {
                var pair = new LocationPair(sequence[i - 1].Location, sequence[i].Location);
                trainEdges.Add(pair);
                var back = i >= 2 && sequence[i - 2].Location == sequence[i].Location;
                var outward = i + 1 < sequence.Count && sequence[i + 1].Location == sequence[i - 1].Location;
                if (!back && !outward)
                {
                    trainThrough.Add(pair);
                }

                if (i >= 2 && !back)
                {
                    trainMoves.Add((sequence[i - 1].Location, new LocationPair(sequence[i - 2].Location, sequence[i].Location)));
                }
            }

            foreach (var pair in trainEdges)
            {
                edges[pair] = edges.GetValueOrDefault(pair) + 1;
                if (!trainThrough.Contains(pair))
                {
                    reversing[pair] = reversing.GetValueOrDefault(pair) + 1;
                }
            }

            foreach (var move in trainMoves)
            {
                through[move] = through.GetValueOrDefault(move) + 1;
            }
        }

        return new LocationGraph(edges, through, reversing, expansions, sequences.Select(s => s.Select(n => n.Location).ToArray()).ToList());
    }

    /// <param name="Arrive">First booked time at the location in this visit, if any (none for an inserted location).</param>
    /// <param name="Depart">Last booked time in this visit, if any.</param>
    private readonly record struct Node(Tiploc Location, SimTime? Arrive, SimTime? Depart);

    private static List<Node> ToSequence(Train train)
    {
        var nodes = new List<Node>(train.Points.Count);
        foreach (var point in train.Points)
        {
            if (nodes.Count > 0 && nodes[^1].Location == point.Location)
            {
                nodes[^1] = nodes[^1] with { Depart = point.LastTime ?? nodes[^1].Depart };
            }
            else
            {
                nodes.Add(new Node(point.Location, point.FirstTime, point.LastTime));
            }
        }

        return nodes;
    }

    /// <summary>For each direct edge that other trains show with locations in between, the chain to replace it with.</summary>
    private static Dictionary<LocationPair, IReadOnlyList<Tiploc>> FindExpansions(List<List<Node>> sequences, int maxSkipped, HashSet<(Tiploc Middle, LocationPair Ends)> between)
    {
        // Direct edges and their fastest booked time.
        var direct = new Dictionary<LocationPair, int?>();
        foreach (var sequence in sequences)
        {
            for (var i = 1; i < sequence.Count; i++)
            {
                var pair = new LocationPair(sequence[i - 1].Location, sequence[i].Location);
                direct[pair] = MinNullable(direct.GetValueOrDefault(pair), direct.ContainsKey(pair), Elapsed(sequence[i - 1], sequence[i]));
            }
        }

        // Chains between the ends of a direct edge, keyed by their text in A→B order.
        var chains = new Dictionary<LocationPair, Dictionary<string, ChainStats>>();
        var seen = new HashSet<Tiploc>();
        foreach (var sequence in sequences)
        {
            for (var i = 0; i < sequence.Count - 2; i++)
            {
                seen.Clear();
                seen.Add(sequence[i].Location);
                seen.Add(sequence[i + 1].Location);
                for (var j = i + 2; j < sequence.Count && j - i - 1 <= maxSkipped; j++)
                {
                    if (!seen.Add(sequence[j].Location))
                    {
                        break;
                    }

                    var pair = new LocationPair(sequence[i].Location, sequence[j].Location);
                    if (!direct.ContainsKey(pair))
                    {
                        continue;
                    }

                    var chain = sequence.GetRange(i, j - i + 1).Select(n => n.Location).ToList();
                    if (chain[0] != pair.A)
                    {
                        chain.Reverse();
                    }

                    var key = string.Join(' ', chain);
                    var byChain = chains.TryGetValue(pair, out var c) ? c : chains[pair] = new Dictionary<string, ChainStats>(StringComparer.Ordinal);
                    var stats = byChain.TryGetValue(key, out var s) ? s : byChain[key] = new ChainStats(chain);
                    stats.Count++;
                    stats.MinElapsed = MinNullable(stats.MinElapsed, stats.Count > 1, Elapsed(sequence[i], sequence[j]));
                }
            }
        }

        // Accept the most common chains first. A chain must agree with every order already accepted: if B was put
        // between A and C, nothing may later put A between B and C. Otherwise A–C via B and A–B via C would keep
        // expanding each other.
        var found = new Dictionary<LocationPair, IReadOnlyList<Tiploc>>();
        var ranked = chains.Select(c => (Pair: c.Key, Chains: c.Value
                .OrderByDescending(x => x.Value.Count)
                .ThenByDescending(x => x.Value.Chain.Count)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Value)
                .ToList()))
            .OrderByDescending(c => c.Chains[0].Count)
            .ThenBy(c => c.Pair.A.Code, StringComparer.Ordinal)
            .ThenBy(c => c.Pair.B.Code, StringComparer.Ordinal);
        foreach (var (pair, candidates) in ranked)
        {
            var fastestDirect = direct[pair];
            var best = candidates.FirstOrDefault(c =>
                (fastestDirect is not { } d || c.MinElapsed is not { } e || e <= (d * ChainTimeFactor) + ChainTimeSlackSeconds)
                && Consistent(c.Chain, between));
            if (best is not null)
            {
                found[pair] = best.Chain;
                for (var i = 0; i < best.Chain.Count; i++)
                {
                    for (var j = i + 1; j < best.Chain.Count; j++)
                    {
                        for (var k = j + 1; k < best.Chain.Count; k++)
                        {
                            between.Add((best.Chain[j], new LocationPair(best.Chain[i], best.Chain[k])));
                        }
                    }
                }
            }
        }

        return found;
    }

    /// <summary>True if no accepted order puts an end of a sub-chain between its other two locations.</summary>
    private static bool Consistent(IReadOnlyList<Tiploc> chain, HashSet<(Tiploc Middle, LocationPair Ends)> between)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            for (var j = i + 1; j < chain.Count; j++)
            {
                for (var k = j + 1; k < chain.Count; k++)
                {
                    if (between.Contains((chain[i], new LocationPair(chain[j], chain[k]))) || between.Contains((chain[k], new LocationPair(chain[i], chain[j]))))
                    {
                        return false;
                    }
                }
            }
        }

        return true;

    }

    private static List<Node> Expand(List<Node> sequence, Dictionary<LocationPair, IReadOnlyList<Tiploc>> found)
    {
        var result = new List<Node>(sequence.Count) { sequence[0] };
        for (var i = 1; i < sequence.Count; i++)
        {
            var (from, to) = (sequence[i - 1].Location, sequence[i].Location);
            if (found.TryGetValue(new LocationPair(from, to), out var chain))
            {
                var forward = chain[0] == from;
                for (var k = 1; k < chain.Count - 1; k++)
                {
                    result.Add(new Node(chain[forward ? k : chain.Count - 1 - k], null, null));
                }
            }

            result.Add(sequence[i]);
        }

        return result;
    }

    private static int? Elapsed(Node from, Node to) =>
        from.Depart is { } d && to.Arrive is { } a && a - d > 0 ? a - d : null;

    /// <summary>The smaller of an existing minimum (if <paramref name="hasExisting"/>) and a new value; null means unknown.</summary>
    private static int? MinNullable(int? existing, bool hasExisting, int? value) =>
        !hasExisting ? value : (existing, value) switch
        {
            ({ } e, { } v) => Math.Min(e, v),
            ({ } e, null) => e,
            (null, var v) => v,
        };

    private sealed class ChainStats(IReadOnlyList<Tiploc> chain)
    {
        public IReadOnlyList<Tiploc> Chain { get; } = chain;

        public int Count { get; set; }

        public int? MinElapsed { get; set; }
    }
}
