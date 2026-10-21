using Stringline.Core;
using Stringline.Core.Live;

namespace Stringline.Network.Profiles;

/// <summary>
/// A placed signal inside one section, one way (S39): how far through the section trains pass it, for each kind of
/// run, and the block headway measured there.
/// </summary>
/// <param name="Id">The signal's ID.</param>
/// <param name="Shares">The share of the section's run time at which trains pass it, by <see cref="RunKind"/>.</param>
/// <param name="HeadwaySeconds">
/// The time from a leader passing it to the leader clearing the blocks a follower passing it needs clear to run on
/// green, running unchecked (<see cref="BlockHeadways"/>); null if the signals ahead are not all placed and timed.
/// </param>
public sealed record BlockSignal(string Id, IReadOnlyList<double> Shares, int? HeadwaySeconds)
{
    /// <summary>The share for a kind of run (each kind has one, from its own place or the signal's best other).</summary>
    public double ShareFor(RunKind run) => Shares[(int)run];
}

/// <summary>The signal blocks of one section (S39): its block headway, and its placed signals each way.</summary>
/// <param name="HeadwaySeconds">The largest block headway at its signals (Fred, S39), at least <see cref="BlockHeadways.FloorSeconds"/>; null if none was measured.</param>
/// <param name="Signals">How many of its signals have a block headway.</param>
/// <param name="Aspects">The aspects measured with: set by hand, else learned on its corridor (4 if a double yellow was seen on it, else 3), else 4.</param>
/// <param name="FromA">Its signals in order for trains running from <see cref="A"/> to <see cref="B"/>.</param>
/// <param name="FromB">Its signals in order for trains running from <see cref="B"/> to <see cref="A"/>.</param>
public sealed record SectionBlocks(Tiploc A, Tiploc B, int? HeadwaySeconds, int Signals, int Aspects, IReadOnlyList<BlockSignal> FromA, IReadOnlyList<BlockSignal> FromB)
{
    /// <summary>The signals in order for trains entering the section at <paramref name="entry"/>.</summary>
    public IReadOnlyList<BlockSignal> SignalsFrom(Tiploc entry) => entry == A ? FromA : entry == B ? FromB : [];
}

/// <summary>
/// Section headways from the learned signal blocks (S39, #19; <c>06-…md</c> §3). A follower passing a signal runs on
/// green only if the leader has cleared the blocks ahead of it: three for four-aspect signalling, two for three-aspect
/// (Fred, S39), and the overlap past the last of them. The headway at a signal is how long the leader takes from
/// passing it to clearing all that, running unchecked: the learner's per-block times (the fastest a train not checked
/// on the way took from each signal to the next, <see cref="SignallingPlace.NextSeconds"/>), followed along each line by
/// the signal trains passed next, and the overlap's clearing, the fastest the first track circuit past the last signal
/// cleared behind a train (<see cref="SignallingPlace.ClearSeconds"/>; else <see cref="OverlapSeconds"/>, Fred). A
/// section's headway is the largest at its signals (Fred). The block times are observed: the WTT's minimum runs are
/// both faster and slower than SimSig's trains run (Waterloo: WESTLON → CJWIN is booked 30 s at least,
/// run in 83 s; CJML → QTRDBAT 2:15, run in 74 s).
/// </summary>
/// <remarks>
/// Only places whose two timing locations are both on a corridor count (as for the axis). The aspects come from the
/// section's setting, else from what trains passed the signals on its corridor at (Fred, S39): a double yellow (or a
/// flashing yellow) anywhere on it is four-aspect, anything else three-aspect, and nothing seen (places learned before
/// S39) is taken as four-aspect. A section on two corridors takes the higher.
/// </remarks>
public sealed class BlockHeadways
{
    /// <summary>The overlap's clearing where no track circuit past the signal has a learned clearing time.</summary>
    public const int OverlapSeconds = 15;

    /// <summary>The least block headway used.</summary>
    public const int FloorSeconds = BookedHeadway.FloorSeconds;

    /// <summary>The aspects assumed where play has shown none.</summary>
    public const int DefaultAspects = 4;

    private readonly Dictionary<LocationPair, SectionBlocks> _sections;

    private BlockHeadways(Dictionary<LocationPair, SectionBlocks> sections) => _sections = sections;

    public static BlockHeadways None { get; } = new([]);

    public IReadOnlyCollection<SectionBlocks> Sections => _sections.Values;

    /// <summary>The blocks of the section between two adjacent corridor locations, either order, if any signals are placed in it.</summary>
    public SectionBlocks? Find(Tiploc a, Tiploc b) => _sections.GetValueOrDefault(new LocationPair(a, b));

    /// <summary>Blocks a follower needs clear ahead of the signal it passes to run on green: one more than the aspects less two.</summary>
    public static int BlocksNeeded(int aspects) => Math.Max(1, aspects - 1);

    /// <summary>The aspects a learned signal's passes show: 4 if any was a double (or flashing) yellow, 3 if others were seen, null if none.</summary>
    public static int? AspectsOf(IReadOnlyList<int>? passed) =>
        passed is not { Count: > 0 } ? null : passed.Any(a => a is SignalAspects.DoubleYellow or SignalAspects.FlashingYellow or SignalAspects.FlashingDoubleYellow) ? 4 : 3;

    /// <summary>Measures the blocks of these corridors' sections from the usable places.</summary>
    public static BlockHeadways Measure(IEnumerable<Corridor> corridors, UsablePlaces places)
    {
        ArgumentNullException.ThrowIfNull(corridors);
        ArgumentNullException.ThrowIfNull(places);
        if (places.Signals.Count == 0)
        {
            return None;
        }

        var list = corridors.ToList();
        var mapped = new Dictionary<LocationPair, SectionWork>();
        foreach (var corridor in list)
        {
            // Aspects by corridor (Fred, S39): the approach to a terminus seldom shows a double yellow.
            int? aspects = null;
            foreach (var place in places.Signals)
            {
                if (corridor.Contains(place.From) && corridor.Contains(place.To) && AspectsOf(place.Aspects) is { } a)
                {
                    aspects = Math.Max(aspects ?? a, a);
                }
            }

            foreach (var down in (bool[])[true, false])
            {
                Map(corridor, down, places, aspects, mapped);
            }
        }

        var result = new Dictionary<LocationPair, SectionBlocks>(mapped.Count);
        foreach (var (pair, work) in mapped)
        {
            var aspects = work.Section.Aspects ?? (work.Learned.Count == 0 ? DefaultAspects : work.Learned.Max());
            var blocks = BlocksNeeded(aspects);
            var fromA = Signals(work, work.A, blocks, places);
            var fromB = Signals(work, work.B, blocks, places);
            var measured = fromA.Concat(fromB).Where(s => s.HeadwaySeconds is not null).Select(s => s.HeadwaySeconds!.Value).ToList();
            result[pair] = new SectionBlocks(work.A, work.B, measured.Count == 0 ? null : Math.Max(FloorSeconds, measured.Max()), measured.Count, aspects, fromA, fromB);
        }

        return new BlockHeadways(result);
    }

    /// <summary>The corridors with each section's block headway and aspects measured (cleared where none), and their headways worked out again.</summary>
    public IReadOnlyList<Corridor> Apply(IEnumerable<Corridor> corridors, int fallbackHeadwaySeconds)
    {
        ArgumentNullException.ThrowIfNull(corridors);
        return corridors.Select(c => c with
        {
            Sections = c.Sections.Select(s =>
            {
                var blocks = Find(s.From, s.To);
                return (s with
                {
                    BlockHeadwaySeconds = blocks?.HeadwaySeconds,
                    BlockSignals = blocks?.Signals ?? 0,
                    BlockAspects = blocks?.HeadwaySeconds is null ? null : blocks.Aspects,
                }).ResolveHeadway(fallbackHeadwaySeconds);
            }).ToList(),
        }).ToList();
    }

    /// <summary>A usable signal place on a corridor: where it is in run seconds from the corridor's start one way.</summary>
    private sealed record Placed(SignallingPlace Place, double Position, int Section, double Share);

    /// <summary>One section's placed signals as they are found, from each corridor it is on.</summary>
    private sealed class SectionWork(Section section, Tiploc a, Tiploc b)
    {
        public Section Section { get; } = section;

        public Tiploc A { get; } = a;

        public Tiploc B { get; } = b;

        /// <summary>The aspects learned on the corridors it is on, where it has placed signals.</summary>
        public List<int> Learned { get; } = [];

        /// <summary>By entry location: signal ID → its places in the section that way, and where the line goes on (positions along the corridor it was found on).</summary>
        public Dictionary<Tiploc, Dictionary<string, List<Placed>>> Signals { get; } = [];

        /// <summary>By entry location: the corridor the signals were found on, its running order and positions, for following blocks beyond the section.</summary>
        public Dictionary<Tiploc, Line> Lines { get; } = [];
    }

    /// <summary>A corridor one way: its locations in running order and their positions in minimum run seconds (to place signals in order and in sections).</summary>
    private sealed record Line(List<Tiploc> Order, double[] At, Dictionary<Tiploc, int> Index, List<Section> Sections);

    private static void Map(Corridor corridor, bool down, UsablePlaces places, int? aspects, Dictionary<LocationPair, SectionWork> mapped)
    {
        var order = corridor.Locations.Select(l => l.Tiploc).ToList();
        var sections = corridor.Sections.ToList();
        if (!down)
        {
            order.Reverse();
            sections.Reverse();
        }

        var direction = down ? TrackDirection.Down : TrackDirection.Up;
        var at = new double[order.Count];
        for (var k = 1; k < order.Count; k++)
        {
            at[k] = at[k - 1] + Math.Max(1, sections[k - 1].MinRunFor(direction));
        }

        var index = order.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var line = new Line(order, at, index, sections);
        foreach (var place in places.Signals)
        {
            if (Locate(line, place) is not { } p)
            {
                continue;
            }

            var section = sections[p.Section];
            var pair = new LocationPair(section.From, section.To);
            if (!mapped.TryGetValue(pair, out var work))
            {
                mapped[pair] = work = new SectionWork(section, section.From, section.To);
            }

            var entry = order[p.Section];
            if (!work.Lines.ContainsKey(entry))
            {
                work.Lines[entry] = line;
            }

            if (aspects is { } learned)
            {
                work.Learned.Add(learned);
            }

            var byId = work.Signals.TryGetValue(entry, out var s) ? s : work.Signals[entry] = new Dictionary<string, List<Placed>>(StringComparer.OrdinalIgnoreCase);
            if (!byId.TryGetValue(place.Id, out var placed))
            {
                byId[place.Id] = placed = [];
            }

            if (!placed.Any(x => ReferenceEquals(x.Place, place)))
            {
                placed.Add(p);
            }
        }
    }

    /// <summary>Where a place is along a corridor one way, if both its locations are on it in that order.</summary>
    private static Placed? Locate(Line line, SignallingPlace place)
    {
        if (!line.Index.TryGetValue(place.From, out var i) || !line.Index.TryGetValue(place.To, out var j) || j <= i)
        {
            return null;
        }

        var position = line.At[i] + (Math.Clamp(place.Fraction, 0, 1) * (line.At[j] - line.At[i]));
        var k = i;
        while (k < j - 1 && position >= line.At[k + 1])
        {
            k++;
        }

        var share = Math.Clamp((position - line.At[k]) / (line.At[k + 1] - line.At[k]), 0, 1);
        return new Placed(place, position, k, share);
    }

    private static List<BlockSignal> Signals(SectionWork work, Tiploc entry, int blocks, UsablePlaces places)
    {
        if (!work.Signals.TryGetValue(entry, out var byId) || !work.Lines.TryGetValue(entry, out var line))
        {
            return [];
        }

        var result = new List<BlockSignal>(byId.Count);
        foreach (var (id, placed) in byId)
        {
            var shares = new double[4];
            foreach (var run in Enum.GetValues<RunKind>())
            {
                var chosen = UsablePlaces.Choose(placed.Select(p => p.Place), run)!;
                shares[(int)run] = placed.First(p => ReferenceEquals(p.Place, chosen)).Share;
            }

            var best = placed.First(p => ReferenceEquals(p.Place, UsablePlaces.Choose(placed.Select(x => x.Place), RunKind.Through)));
            result.Add(new BlockSignal(id, shares, Headway(line, best, blocks, places)));
        }

        return [.. result.OrderBy(s => s.ShareFor(RunKind.Through)).ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// From a leader passing <paramref name="start"/> to its clearing the overlap past the signal
    /// <paramref name="blocks"/> ahead on its line, in seconds of unchecked running (the learned block times); null if a
    /// signal on the way is not placed or not timed.
    /// </summary>
    private static int? Headway(Line line, Placed start, int blocks, UsablePlaces places)
    {
        var current = start;
        var total = 0;
        for (var b = 0; b < blocks; b++)
        {
            if (Next(line, current, places) is not { } next)
            {
                return null;
            }

            (current, total) = (next.Place, total + next.Seconds);
        }

        // The overlap: the first track circuit past the last signal clearing behind the leader, which enters it as it
        // passes the signal (03-…md §6).
        var overlap = places.TracksAfter(current.Place.Id)
            .Select(t => (Track: t, At: Locate(line, t)))
            .Where(t => t.At is { } p && p.Position >= current.Position - 1)
            .OrderBy(t => t.At!.Position)
            .Select(t => t.Track.ClearSeconds)
            .FirstOrDefault();
        return total + (overlap ?? OverlapSeconds);
    }

    /// <summary>
    /// The place of the signal trains passed next after this one most often, further along this corridor, and the
    /// fastest time to it; null if none is placed and timed.
    /// </summary>
    private static (Placed Place, int Seconds)? Next(Line line, Placed from, UsablePlaces places)
    {
        if (from.Place.Next is not { Count: > 0 } next || from.Place.NextSeconds is not { Count: > 0 } seconds)
        {
            return null;
        }

        foreach (var id in next.OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).Select(n => n.Key))
        {
            if (!seconds.TryGetValue(id, out var time))
            {
                continue;
            }

            // The same two timing locations first, else a run on from the place's far end, running through.
            var same = places.SignalsBetween(from.Place.From, from.Place.To).Where(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            var chosen = UsablePlaces.Choose(same, from.Place.Run) ?? UsablePlaces.Choose(places.SignalFrom(id, from.Place.To), RunKind.Through);
            if (chosen is not null && Locate(line, chosen) is { } p && p.Position > from.Position)
            {
                return (p, time);
            }
        }

        return null;
    }
}
