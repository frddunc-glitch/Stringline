using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>What to put in a scene. Only <see cref="Profile"/> and <see cref="Corridor"/> are needed; the rest add layers.</summary>
/// <param name="Corridor">The corridor to draw, normally one of <see cref="Profile"/>'s.</param>
public sealed record GraphSceneRequest(StringlineProfile Profile, Corridor Corridor)
{
    /// <summary>Draws the WTT layer, and colours matched actuals by their WTT train's category.</summary>
    public WorkingTimetable? Timetable { get; init; }

    /// <summary>Live trains for the actual layer (<c>LiveState.Trains</c>).</summary>
    public IEnumerable<LiveTrain> LiveTrains { get; init; } = [];

    /// <summary>The now line, if any.</summary>
    public SimTime? Now { get; init; }

    public bool ShowWtt { get; init; } = true;

    /// <summary>The working plan: trains whose plan differs from the WTT (and clones) draw on the plan layer, except cancelled ones.</summary>
    public WorkingPlan? Plan { get; init; }

    public bool ShowPlan { get; init; } = true;

    public bool ShowActual { get; init; } = true;

    /// <summary>Colour actual lines by category (the default) or by delay band.</summary>
    public GraphColourMode ColourMode { get; init; } = GraphColourMode.Category;

    /// <summary>A request for the timetable and live trains of a <see cref="PlanSnapshot"/>, with the now line at its last reported clock.</summary>
    public static GraphSceneRequest For(StringlineProfile profile, Corridor corridor, PlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new GraphSceneRequest(profile, corridor)
        {
            Timetable = snapshot.Timetable,
            LiveTrains = snapshot.Trains,
            Plan = snapshot.Plan,
            Now = snapshot.Clock.HasTime ? snapshot.Clock.LastReported : null,
        };
    }
}

/// <summary>
/// Turns a timetable and live trains into a <see cref="GraphScene"/> for one corridor (<c>05-…md</c> §7). Pure and
/// thread-safe: call it off the UI thread whenever the data changes.
/// </summary>
/// <remarks>
/// Only timing points on the corridor become vertices. A train booked past corridor locations it has no time at
/// draws straight between the ones it has, which is right because it runs past them. A train that leaves the
/// corridor and comes back to the same location (out and back on a branch) gets a new path, so the time away does
/// not draw as a stop.
/// </remarks>
public static class GraphSceneBuilder
{
    /// <summary>Colour of live trains not matched to the WTT.</summary>
    public static Colour UnmatchedColour { get; } = new(0x8A, 0x8A, 0x8A);

    public static GraphScene Build(GraphSceneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        var corridor = request.Corridor;

        var locations = corridor.Locations
            .Select(l => new GraphLocation(l.Tiploc, profile.DisplayName(l.Tiploc), l.AxisPosition, l.Platforms > 0 || !l.IsTimingPointOnly, l.Platforms))
            .ToList();

        var paths = new List<GraphPath>();
        if (request.ShowWtt && request.Timetable is { } timetable)
        {
            foreach (var train in timetable.Trains)
            {
                AddWtt(paths, corridor, train, profile.StyleFor(train.CategoryId).Colour);
            }
        }

        if (request.ShowPlan && request.Plan is { } plan)
        {
            foreach (var train in plan.Modified)
            {
                if (!train.Cancelled)
                {
                    AddPlan(paths, corridor, train, profile.StyleFor(train.CategoryId).Colour);
                }
            }
        }

        if (request.ShowActual)
        {
            foreach (var live in request.LiveTrains)
            {
                for (var w = 0; w < live.Workings.Count; w++)
                {
                    AddActual(paths, corridor, live, w, request);
                }
            }
        }

        var title = $"{profile.Name ?? profile.AreaId} — {corridor.Name}";
        return new GraphScene(corridor.Id, title, locations, paths, request.Now);
    }

    private static Colour ColourOf(LiveWorking working, GraphSceneRequest request) =>
        working is { IsMatched: true, Train: { } id } && request.Timetable?.Find(id) is { } train
            ? request.Profile.StyleFor(train.CategoryId).Colour
            : working.IsMatched ? CategoryStyle.Fallback.Colour : UnmatchedColour;

    private static void AddWtt(List<GraphPath> paths, Corridor corridor, Train train, Colour colour)
    {
        var builder = new PathAccumulator(corridor);
        foreach (var point in train.Points)
        {
            AddBooked(builder, point.Location, point.Arrive, point.Depart, point.IsPass);
        }

        AddPaths(paths, corridor, builder, $"wtt:{train.Id}", GraphLayer.Wtt, train.Id, train.Headcode, colour);
    }

    private static void AddPlan(List<GraphPath> paths, Corridor corridor, TrainPlan train, Colour colour)
    {
        var builder = new PathAccumulator(corridor);
        foreach (var point in train.RunningPoints)
        {
            AddBooked(builder, point.Location, point.Arrive, point.Depart, point.IsPass);
        }

        AddPaths(paths, corridor, builder, $"plan:{train.Id}", GraphLayer.Plan, train.Id, train.Headcode, colour);
    }

    /// <summary>A booked or planned point: one vertex for a pass or a point with one time, two for a stop with a dwell.</summary>
    private static void AddBooked(PathAccumulator builder, Tiploc location, SimTime? arrive, SimTime? depart, bool isPass)
    {
        if (arrive is null && depart is null)
        {
            return;
        }

        if (isPass || arrive is null || depart is null || depart <= arrive)
        {
            builder.Add(location, (arrive ?? depart)!.Value);
        }
        else
        {
            builder.Add(location, arrive.Value);
            builder.Add(location, depart.Value);
        }
    }

    private static void AddPaths(List<GraphPath> paths, Corridor corridor, PathAccumulator builder, string key, GraphLayer layer, TrainId train, Headcode headcode, Colour colour)
    {
        foreach (var (segment, index, count) in builder.Finish())
        {
            paths.Add(new GraphPath(count == 1 ? key : $"{key}#{index}", layer, train, headcode, colour,
                segment.Vertices, Direction: corridor.DirectionOf(segment.Locations)));
        }
    }

    private static void AddActual(List<GraphPath> paths, Corridor corridor, LiveTrain live, int working, GraphSceneRequest request)
    {
        var w = live.Workings[working];
        var wtt = w is { IsMatched: true, Train: { } id } ? request.Timetable?.Find(id) : null;
        var byDelay = request.ColourMode == GraphColourMode.Delay && w.IsMatched;

        var builder = new PathAccumulator(corridor);
        foreach (var actual in live.ActualsOf(working))
        {
            if (actual.AtTimingPoint)
            {
                builder.Add(actual.Location, actual.Time, byDelay && wtt is not null ? TrainDelays.At(actual, wtt)?.Seconds : null);
            }
        }

        var colour = ColourOf(w, request);
        var key = $"act:{live.SimUid}/{working}";
        foreach (var (segment, index, count) in builder.Finish())
        {
            var (pathColour, segmentColours) = byDelay ? DelayColours(segment.Delays) : (colour, null);
            paths.Add(new GraphPath(count == 1 ? key : $"{key}#{index}", GraphLayer.Actual, w.Train, w.Headcode, pathColour,
                segment.Vertices, IsUnmatched: !w.IsMatched, Direction: corridor.DirectionOf(segment.Locations), LiveUid: live.SimUid, Working: working,
                SegmentColours: segmentColours));
        }
    }

    /// <summary>
    /// Delay-band colours for each segment of an actual path (Fred, S9): a segment takes the band of the delay at the
    /// timing point it arrives at. Where that point is not booked, the latest known delay carries on; segments before
    /// the first booked point take the first known delay; a path with none is <see cref="DelayBands.Unknown"/>. The
    /// path's own colour is its last segment's (the train's latest delay). Segment colours are null if all agree.
    /// </summary>
    internal static (Colour Colour, IReadOnlyList<Colour>? Segments) DelayColours(IReadOnlyList<int?> delays)
    {
        var first = delays.FirstOrDefault(d => d is not null);
        var colours = new Colour[Math.Max(0, delays.Count - 1)];
        var known = delays.Count > 0 ? delays[0] : null;
        for (var i = 0; i < colours.Length; i++)
        {
            known = delays[i + 1] ?? known;
            colours[i] = DelayBands.ColourOfSeconds(known ?? first);
        }

        if (colours.Length == 0)
        {
            return (DelayBands.ColourOfSeconds(first), null);
        }

        return colours.All(c => c == colours[0]) ? (colours[0], null) : (colours[^1], colours);
    }

    private sealed record Segment(List<GraphVertex> Vertices, List<Tiploc> Locations, List<int?> Delays);

    /// <summary>Collects timed locations in running order into one or more corridor visits.</summary>
    private sealed class PathAccumulator(Corridor corridor)
    {
        private readonly List<Segment> _segments = [];
        private Segment? _current;
        private int _lastIndex = -1;
        private bool _away;

        /// <param name="delay">Seconds late at this point, if known (delay colouring only).</param>
        public void Add(Tiploc location, SimTime time, int? delay = null)
        {
            var index = corridor.IndexOf(location);
            if (index < 0)
            {
                _away |= _current is not null;
                return;
            }

            if (_current is null || (_away && index == _lastIndex))
            {
                _current = new Segment([], [], []);
                _segments.Add(_current);
            }

            _away = false;
            var vertex = new GraphVertex(time.Seconds, corridor.Locations[index].AxisPosition);
            if (_current.Vertices.Count == 0 || _current.Vertices[^1] != vertex)
            {
                _current.Vertices.Add(vertex);
                _current.Locations.Add(location);
                _current.Delays.Add(delay);
            }
            else if (delay is not null)
            {
                _current.Delays[^1] ??= delay;
            }

            _lastIndex = index;
        }

        /// <summary>The visits that draw as a line (at least two vertices), with their index and how many there are.</summary>
        public IEnumerable<(Segment Segment, int Index, int Count)> Finish()
        {
            var drawn = _segments.Where(s => s.Vertices.Count >= 2).ToList();
            return drawn.Select((s, i) => (s, i, drawn.Count));
        }
    }
}
