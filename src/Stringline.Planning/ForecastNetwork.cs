using System.Collections.Concurrent;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.Planning;

/// <summary>
/// One line between two adjacent corridor locations as the forecaster's interaction pass sees it: a resource that
/// trains occupy. A section on more than one corridor (a branch's corridor follows its trains onto the main line) is
/// one <see cref="ForecastSection"/>, so trains from both corridors interact on it.
/// </summary>
/// <param name="From">The end that comes first on the corridor it was taken from (Down runs go From → To).</param>
/// <param name="HeadwaySeconds">The section's headway (<see cref="Section.DefaultHeadwaySeconds"/>).</param>
/// <param name="SingleLine">One bi-directional track: opposing trains may not be in it at once.</param>
/// <param name="DownTracks">How many trains can run Down through it side by side (see <see cref="ForecastNetwork"/>).</param>
/// <param name="UpTracks">How many trains can run Up through it side by side.</param>
public sealed record ForecastSection(
    int Id,
    Tiploc From,
    Tiploc To,
    int HeadwaySeconds,
    bool SingleLine,
    int DownTracks,
    int UpTracks,
    int MinRunDownSeconds,
    int MinRunUpSeconds)
{
    public int TracksFor(bool down) => down ? DownTracks : UpTracks;

    public int MinRunFor(bool down) => down ? MinRunDownSeconds : MinRunUpSeconds;

    public override string ToString() => $"{From}–{To}{(SingleLine ? " single" : string.Empty)} h{HeadwaySeconds}s {DownTracks}D/{UpTracks}U";
}

/// <summary>One section a run passes through, and when it enters and leaves it as fractions of the run's time.</summary>
internal readonly record struct RouteStep(ForecastSection Section, bool Down, double EnterShare, double ExitShare);

/// <summary>
/// The track layout the forecaster's interaction pass works on (<c>06-…md</c> §2 step 4): every section of the
/// profile's corridors, with its headway, whether it is single line, and how many trains each way it can hold side by
/// side. Built once per timetable and profile; immutable and thread-safe.
/// </summary>
/// <remarks>
/// <para>A run of a train between two of its points is mapped onto the sections between them on the first corridor
/// that has both. A run no corridor has both ends of is not checked: it takes its own time.</para>
/// <para><b>Tracks side by side.</b> The profile's tracks give one each way on a double line, but many corridors are
/// wider than that (Waterloo's main line is four tracks). As the WTT is assumed to work, a section has at least as
/// many tracks each way as the WTT uses at once: booked runs are put on tracks in entry order, a run sharing a track
/// with one entering or leaving within <see cref="ParallelSeconds"/> of it (or overtaking it) needing another.</para>
/// </remarks>
public sealed class ForecastNetwork
{
    /// <summary>Two booked runs the same way entering or leaving a section closer together than this are on different tracks.</summary>
    public const int ParallelSeconds = 60;

    private readonly IReadOnlyList<Corridor> _corridors;
    private readonly Dictionary<LocationPair, ForecastSection> _sections;
    private readonly ConcurrentDictionary<(Tiploc From, Tiploc To), RouteStep[]?> _routes = new();

    private ForecastNetwork(IReadOnlyList<Corridor> corridors, Dictionary<LocationPair, ForecastSection> sections, IReadOnlyDictionary<string, int> priorities)
    {
        _corridors = corridors;
        _sections = sections;
        Priorities = priorities;
    }

    /// <summary>No sections: the interaction pass changes nothing.</summary>
    public static ForecastNetwork Empty { get; } = new([], [], new Dictionary<string, int>());

    /// <summary>Priority by category ID (higher first); breaks ties between trains wanting a section at the same time.</summary>
    public IReadOnlyDictionary<string, int> Priorities { get; }

    public IReadOnlyCollection<ForecastSection> Sections => _sections.Values;

    /// <summary>
    /// The network of these corridors. With a timetable, the tracks each way are widened to what its trains use at
    /// once; <paramref name="priorities"/> are category priorities (from the profile's category styles).
    /// </summary>
    public static ForecastNetwork Build(IEnumerable<Corridor> corridors, WorkingTimetable? timetable = null, IReadOnlyDictionary<string, int>? priorities = null)
    {
        ArgumentNullException.ThrowIfNull(corridors);
        var list = corridors.ToList();
        var sections = new Dictionary<LocationPair, ForecastSection>();
        foreach (var corridor in list)
        {
            foreach (var s in corridor.Sections)
            {
                var pair = new LocationPair(s.From, s.To);
                if (!sections.ContainsKey(pair))
                {
                    var single = s.IsSingleLine;
                    sections[pair] = new ForecastSection(
                        sections.Count,
                        s.From,
                        s.To,
                        Math.Max(0, s.DefaultHeadwaySeconds),
                        single,
                        Math.Max(1, s.Tracks.Count(t => t.Direction != TrackDirection.Up)),
                        Math.Max(1, s.Tracks.Count(t => t.Direction != TrackDirection.Down)),
                        Math.Max(1, s.MinRunFor(TrackDirection.Down)),
                        Math.Max(1, s.MinRunFor(TrackDirection.Up)));
                }
            }
        }

        var network = new ForecastNetwork(list, sections, priorities ?? new Dictionary<string, int>());
        return timetable is null || sections.Count == 0 ? network : network.WidenedFor(timetable);
    }

    /// <summary>The network of a profile's corridors, widened for the timetable, with its category styles' priorities.</summary>
    public static ForecastNetwork For(StringlineProfile profile, WorkingTimetable? timetable)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var priorities = new Dictionary<string, int>();
        foreach (var style in profile.Categories)
        {
            priorities.TryAdd(style.CategoryId, style.Priority);
        }

        return Build(profile.Corridors, timetable, priorities);
    }

    /// <summary>The section between two adjacent corridor locations, in either order, if any.</summary>
    public ForecastSection? FindSection(Tiploc a, Tiploc b) => _sections.GetValueOrDefault(new LocationPair(a, b));

    public int PriorityOf(string? categoryId) => categoryId is not null && Priorities.TryGetValue(categoryId, out var p) ? p : 0;

    /// <summary>The sections a run from one location to another passes through, or null if no corridor has both.</summary>
    internal RouteStep[]? Route(Tiploc from, Tiploc to) => from == to ? null : _routes.GetOrAdd((from, to), Find);

    private RouteStep[]? Find((Tiploc From, Tiploc To) run)
    {
        foreach (var corridor in _corridors)
        {
            int i = corridor.IndexOf(run.From), j = corridor.IndexOf(run.To);
            if (i < 0 || j < 0)
            {
                continue;
            }

            var down = j > i;
            var sections = new List<ForecastSection>();
            for (var k = Math.Min(i, j); k < Math.Max(i, j); k++)
            {
                sections.Add(_sections[new LocationPair(corridor.Sections[k].From, corridor.Sections[k].To)]);
            }

            if (!down)
            {
                sections.Reverse();
            }

            // A section's own direction is by the corridor it was first defined on, which may run the other way.
            var downs = new bool[sections.Count];
            var location = run.From;
            for (var k = 0; k < sections.Count; k++)
            {
                downs[k] = sections[k].From == location;
                location = downs[k] ? sections[k].To : sections[k].From;
            }

            double total = sections.Select((s, k) => s.MinRunFor(downs[k])).Sum();
            var steps = new RouteStep[sections.Count];
            double at = 0;
            for (var k = 0; k < sections.Count; k++)
            {
                var exit = k == sections.Count - 1 ? 1 : at + (sections[k].MinRunFor(downs[k]) / total);
                steps[k] = new RouteStep(sections[k], downs[k], at, exit);
                at = exit;
            }

            return steps;
        }

        return null;
    }

    private ForecastNetwork WidenedFor(WorkingTimetable timetable)
    {
        // Booked occupations per section and direction, then the tracks they need side by side.
        var booked = new Dictionary<(int Section, bool Down), List<(int Enter, int Exit)>>();
        foreach (var train in timetable.Trains)
        {
            var previous = -1;
            for (var i = 0; i < train.Points.Count; i++)
            {
                if (train.Points[i].FirstTime is null)
                {
                    continue;
                }

                if (previous >= 0 && train.Points[previous].LastTime is { } depart && train.Points[i].FirstTime is { } arrive && arrive > depart &&
                    Route(train.Points[previous].Location, train.Points[i].Location) is { } route)
                {
                    var run = arrive - depart;
                    foreach (var step in route)
                    {
                        var key = (step.Section.Id, step.Down);
                        if (!booked.TryGetValue(key, out var list))
                        {
                            booked[key] = list = [];
                        }

                        list.Add((depart.Seconds + (int)Math.Round(run * step.EnterShare), depart.Seconds + (int)Math.Round(run * step.ExitShare)));
                    }
                }

                previous = i;
            }
        }

        var widened = new Dictionary<LocationPair, ForecastSection>(_sections.Count);
        foreach (var (pair, section) in _sections)
        {
            var down = Math.Max(section.DownTracks, booked.TryGetValue((section.Id, true), out var d) ? TracksNeeded(d) : 0);
            var up = Math.Max(section.UpTracks, booked.TryGetValue((section.Id, false), out var u) ? TracksNeeded(u) : 0);
            widened[pair] = section with { DownTracks = down, UpTracks = up };
        }

        return new ForecastNetwork(_corridors, widened, Priorities);
    }

    /// <summary>Greedy track assignment of booked runs in entry order: the number of tracks used.</summary>
    internal static int TracksNeeded(List<(int Enter, int Exit)> runs)
    {
        runs.Sort();
        var last = new List<(int Enter, int Exit)>();
        foreach (var run in runs)
        {
            var track = last.FindIndex(t => run.Enter >= t.Enter + ParallelSeconds && run.Exit >= t.Exit + ParallelSeconds);
            if (track < 0)
            {
                last.Add(run);
            }
            else
            {
                last[track] = run;
            }
        }

        return last.Count;
    }
}
