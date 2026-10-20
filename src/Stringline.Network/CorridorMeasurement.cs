using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network;

/// <summary>What the WTT says about one corridor location.</summary>
/// <param name="Calls">Booked calls (stops and passes) here, counting each train once per visit.</param>
/// <param name="Stops">Calls that are not passes.</param>
/// <param name="Platforms">Distinct platform names booked here, in ordinal order.</param>
/// <param name="Starts">Trains whose first point is here.</param>
/// <param name="Terminates">Trains whose last point is here.</param>
public sealed record LocationStats(Tiploc Tiploc, int Calls, int Stops, IReadOnlyList<string> Platforms, int Starts, int Terminates)
{
    /// <summary>True if trains are booked here but none stops.</summary>
    public bool IsTimingPointOnly => Calls > 0 && Stops == 0;
}

/// <summary>What the WTT says about one corridor section (between locations <c>i</c> and <c>i + 1</c>).</summary>
/// <param name="MinRunDownSeconds">Fastest timed Down run (ascending corridor order), less allowances.</param>
/// <param name="MinRunUpSeconds">Fastest timed Up run, less allowances.</param>
/// <param name="DownRuns">Timed Down runs.</param>
/// <param name="UpRuns">Timed Up runs.</param>
/// <param name="OpposingOverlaps">Pairs of an Up and a Down run booked in the section at the same time for more than a minute (or half the shorter run).</param>
/// <param name="Headway">The headway the WTT books trains to here; <see cref="BookedHeadway.None"/> if not measured.</param>
public sealed record SectionStats(int? MinRunDownSeconds, int? MinRunUpSeconds, int DownRuns, int UpRuns, int OpposingOverlaps, BookedHeadway? Headway = null)
{
    /// <summary>The headway the WTT books trains to here; never null (<see cref="BookedHeadway.None"/> if not measured).</summary>
    public BookedHeadway? Headway { get; init; } = Headway ?? BookedHeadway.None;

    /// <summary>The headway the WTT books trains to here.</summary>
    public BookedHeadway BookedHeadway => Headway ?? BookedHeadway.None;

    /// <summary>The fastest timed run in either direction, if any.</summary>
    public int? MinRunSeconds => (MinRunDownSeconds, MinRunUpSeconds) switch
    {
        ({ } d, { } u) => Math.Min(d, u),
        ({ } d, null) => d,
        (null, var u) => u,
    };

    public int Samples => DownRuns + UpRuns;

    /// <summary>
    /// A hint that the section may be single line: at least two timed runs each way and never one of each in the
    /// section at once (opposing trains are booked to cross elsewhere). The WTT cannot prove it, so the user decides.
    /// </summary>
    public bool SingleLineHint => DownRuns >= 2 && UpRuns >= 2 && OpposingOverlaps == 0;
}

/// <summary>Timetable statistics for a line of locations, as used to build and refresh a <see cref="Corridor"/>.</summary>
/// <param name="Trains">Trains with at least one run over a section of the corridor (timed or not).</param>
public sealed record CorridorMeasurement(IReadOnlyList<LocationStats> Locations, IReadOnlyList<SectionStats> Sections, int Trains)
{
    /// <summary>
    /// Measures every train in the timetable against this order of locations. A train's points are projected onto
    /// the corridor (points elsewhere are skipped), and each step between adjacent corridor locations is one run:
    /// from the departure or pass at one end to the arrival or pass at the other, less the allowances booked in
    /// between. Runs of zero or less (bad data) are ignored. A train booked straight past corridor locations (nothing
    /// in between) has its run shared between the sections it covers (see <see cref="SpreadSpans"/>); a step between
    /// non-adjacent corridor locations with other points between gives no run time.
    /// </summary>
    public static CorridorMeasurement Measure(WorkingTimetable timetable, IReadOnlyList<Tiploc> order)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentNullException.ThrowIfNull(order);
        var index = new Dictionary<Tiploc, int>(order.Count);
        for (var i = 0; i < order.Count; i++)
        {
            if (!index.TryAdd(order[i], i))
            {
                throw new ArgumentException($"Location {order[i]} appears twice in the corridor.", nameof(order));
            }
        }

        var locations = order.Select(t => new LocationAccumulator(t)).ToArray();
        var sections = Enumerable.Range(0, Math.Max(0, order.Count - 1)).Select(_ => new SectionAccumulator()).ToArray();
        var spans = new List<Run>();
        var trains = 0;

        foreach (var train in timetable.Trains)
        {
            var visits = Project(train, index);
            foreach (var visit in visits)
            {
                locations[visit.Corridor].Add(train, visit);
            }

            if (visits.Count > 0)
            {
                locations[visits[0].Corridor].Starts += visits[0].First == 0 ? 1 : 0;
                locations[visits[^1].Corridor].Terminates += visits[^1].Last == train.Points.Count - 1 ? 1 : 0;
            }

            var runs = false;
            for (var v = 1; v < visits.Count; v++)
            {
                var (from, to) = (visits[v - 1], visits[v]);
                var span = Math.Abs(to.Corridor - from.Corridor);

                // A train booked straight past corridor locations (no points in between) still runs over those sections.
                if (span != 1 && to.First != from.Last + 1)
                {
                    continue;
                }

                runs = true;
                var depart = train.Points[from.Last].LastTime;
                var arrive = train.Points[to.First].FirstTime;
                if (depart is null || arrive is null)
                {
                    continue;
                }

                var allowances = 0;
                for (var p = from.Last; p < to.First; p++)
                {
                    allowances += train.Points[p].AllowanceSeconds;
                }

                var run = new Run(from.Corridor, to.Corridor, arrive.Value - depart.Value - allowances, depart.Value, arrive.Value);
                if (run.Seconds <= 0)
                {
                    continue;
                }

                if (span == 1)
                {
                    sections[run.Section].Add(run.Down, run.Seconds, run.Depart, run.Arrive);
                }
                else
                {
                    spans.Add(run);
                }
            }

            trains += runs ? 1 : 0;
        }

        SpreadSpans(sections, spans);
        return new CorridorMeasurement(
            locations.Select(l => l.ToStats()).ToList(),
            sections.Select(s => s.ToStats()).ToList(),
            trains);
    }

    /// <summary>
    /// Shares each run over several sections between them in proportion to their direct minimums (from runs over one
    /// section, in that direction if any; <see cref="Section.DefaultMinRunSeconds"/> if none), and adds each share as
    /// a run of its section. Most fast trains at Waterloo are booked VAUX–CJML with nothing between, so without this
    /// the sections between would have only the few trains that are timed there.
    /// </summary>
    private static void SpreadSpans(SectionAccumulator[] sections, List<Run> spans)
    {
        var direct = sections.Select(s => s.ToStats()).ToArray();
        foreach (var run in spans)
        {
            var (lo, hi) = (Math.Min(run.From, run.To), Math.Max(run.From, run.To));
            var weights = new double[hi - lo];
            for (var k = lo; k < hi; k++)
            {
                var stats = direct[k];
                weights[k - lo] = (run.Down ? stats.MinRunDownSeconds : stats.MinRunUpSeconds) ?? stats.MinRunSeconds ?? Section.DefaultMinRunSeconds;
            }

            var total = weights.Sum();
            var elapsed = 0.0;
            for (var step = 0; step < weights.Length; step++)
            {
                // Walk the sections in running order so each share starts where the previous one ended.
                var k = run.Down ? lo + step : hi - 1 - step;
                var share = run.Seconds * weights[k - lo] / total;
                var start = run.Depart + (int)Math.Round(elapsed);
                elapsed += share;
                var end = run.Depart + (int)Math.Round(elapsed);
                sections[k].Add(run.Down, Math.Max(1, (int)Math.Round(share)), start, end);
            }
        }
    }

    /// <summary>A timed run from one corridor location to another, less allowances.</summary>
    /// <summary>
    /// Opposing runs may share a section this long without counting as an overlap. WTT times are to the minute, and
    /// trains crossing at a loop are often booked through it a minute apart (Llangollen's 1L02 and 2C03 at DEESIDE).
    /// On a section shorter than two minutes, half the shorter run is the tolerance instead.
    /// </summary>
    private const int OverlapToleranceSeconds = 60;

    private readonly record struct Run(int From, int To, int Seconds, SimTime Depart, SimTime Arrive)
    {
        public bool Down => To > From;

        public int Section => Math.Min(From, To);
    }

    /// <summary>A train's points on the corridor, with consecutive points at one location (a reversal or shunt) merged into one visit.</summary>
    private static List<Visit> Project(Train train, Dictionary<Tiploc, int> index)
    {
        var visits = new List<Visit>();
        for (var p = 0; p < train.Points.Count; p++)
        {
            if (!index.TryGetValue(train.Points[p].Location, out var c))
            {
                continue;
            }

            if (visits.Count > 0 && visits[^1].Corridor == c && visits[^1].Last == p - 1)
            {
                visits[^1] = visits[^1] with { Last = p };
            }
            else
            {
                visits.Add(new Visit(c, p, p));
            }
        }

        return visits;
    }

    /// <param name="Corridor">Index on the corridor.</param>
    /// <param name="First">First point index of the visit (the arrival).</param>
    /// <param name="Last">Last point index of the visit (the departure).</param>
    private readonly record struct Visit(int Corridor, int First, int Last);

    private sealed class LocationAccumulator(Tiploc tiploc)
    {
        private readonly SortedSet<string> _platforms = new(StringComparer.Ordinal);
        private int _calls;
        private int _stops;

        public int Starts { get; set; }

        public int Terminates { get; set; }

        public void Add(Train train, Visit visit)
        {
            _calls++;
            var stops = false;
            for (var p = visit.First; p <= visit.Last; p++)
            {
                var point = train.Points[p];
                stops |= !point.IsPass;
                if (!string.IsNullOrWhiteSpace(point.Platform))
                {
                    _platforms.Add(point.Platform.Trim());
                }
            }

            _stops += stops ? 1 : 0;
        }

        public LocationStats ToStats() => new(tiploc, _calls, _stops, _platforms.ToList(), Starts, Terminates);
    }

    private sealed class SectionAccumulator
    {
        private readonly List<(int Start, int End)> _down = [];
        private readonly List<(int Start, int End)> _up = [];
        private int? _minDown;
        private int? _minUp;

        public void Add(bool down, int run, SimTime depart, SimTime arrive)
        {
            if (down)
            {
                _minDown = Math.Min(_minDown ?? int.MaxValue, run);
                _down.Add((depart.Seconds, arrive.Seconds));
            }
            else
            {
                _minUp = Math.Min(_minUp ?? int.MaxValue, run);
                _up.Add((depart.Seconds, arrive.Seconds));
            }
        }

        public SectionStats ToStats() => new(_minDown, _minUp, _down.Count, _up.Count, CountOverlaps(), BookedHeadway.Derive(_down, _up));

        /// <summary>Opposing pairs booked in the section together for more than <see cref="OverlapToleranceSeconds"/> (or half the shorter run).</summary>
        private int CountOverlaps()
        {
            var count = 0;
            foreach (var d in _down)
            {
                foreach (var u in _up)
                {
                    var shorter = Math.Min(d.End - d.Start, u.End - u.Start);
                    count += Math.Min(d.End, u.End) - Math.Max(d.Start, u.Start) > Math.Min(OverlapToleranceSeconds, shorter / 2) ? 1 : 0;
                }
            }

            return count;
        }
    }
}
