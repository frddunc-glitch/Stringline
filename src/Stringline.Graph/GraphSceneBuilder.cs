using Stringline.Core;
using Stringline.Core.Events;
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

    /// <summary>
    /// Draws the WTT lines of trains SimSig will not run (S34, <see cref="TrainForecastStatus.NotRunning"/> in
    /// <see cref="Forecast"/>) faintly; false hides them.
    /// </summary>
    public bool ShowNotRunning { get; init; } = true;

    /// <summary>The working plan: trains whose plan differs from the WTT (and clones) draw on the plan layer, except cancelled ones.</summary>
    public WorkingPlan? Plan { get; init; }

    public bool ShowPlan { get; init; } = true;

    public bool ShowActual { get; init; } = true;

    /// <summary>The forecast: <see cref="Forecast.Drawn"/> trains draw on the forecast layer, on from their latest actual.</summary>
    public Forecast? Forecast { get; init; }

    public bool ShowForecast { get; init; } = true;

    /// <summary>
    /// Each running train's live head (S36): its actual line carried on to <see cref="Now"/> along its forecast. Needs
    /// <see cref="Forecast"/> and <see cref="Now"/>; it is drawn whether or not the forecast layer is.
    /// </summary>
    public bool ShowLiveHead { get; init; } = true;

    /// <summary>Draws the plan's blocks, speed restrictions and notes on this corridor (S13).</summary>
    public bool ShowDisruptions { get; init; } = true;

    /// <summary>The plan's conflicts (S14); those meeting this corridor are marked.</summary>
    public ConflictSet? Conflicts { get; init; }

    public bool ShowConflicts { get; init; } = true;

    /// <summary>Colour actual lines by category (the default) or by delay band.</summary>
    public GraphColourMode ColourMode { get; init; } = GraphColourMode.Category;

    /// <summary>
    /// The sim and timetable for the title band. Default: from <see cref="Profile"/> and <see cref="Timetable"/>'s header.
    /// </summary>
    public SimTitle? SimTitle { get; init; }

    /// <summary>
    /// The learned places to draw with (S38): actual lines bend at the signals they pass, and the axis shows them as
    /// <see cref="Signals"/> says. Null takes the profile's usable places.
    /// </summary>
    public UsablePlaces? Places { get; init; }

    /// <summary>How the placed signals show on the axis (View ▸ Signals, S38).</summary>
    public SignalDisplay Signals { get; init; } = SignalDisplay.Ticks;

    /// <summary>A request for the timetable and live trains of a <see cref="PlanSnapshot"/>, with the now line at its last reported clock.</summary>
    public static GraphSceneRequest For(StringlineProfile profile, Corridor corridor, PlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new GraphSceneRequest(profile, corridor)
        {
            Timetable = snapshot.Timetable,
            LiveTrains = snapshot.Trains,
            Plan = snapshot.Plan,
            Forecast = snapshot.Forecast,
            Conflicts = snapshot.Conflicts,
            Now = snapshot.Clock.HasTime ? snapshot.Clock.LastReported : null,
            Places = snapshot.Settings.Network.Places.IsEmpty ? null : snapshot.Settings.Network.Places,
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

    /// <summary>Colour of cancelled trains on the plan layer (<c>05-…md</c> §2).</summary>
    public static Colour CancelledColour { get; } = new(0x9E, 0x9E, 0x9E);

    /// <summary>A not-running train's WTT line is drawn at this share of its category colour's alpha (on top of the WTT layer's own).</summary>
    public const double NotRunningAlpha = 0.4;

    /// <summary>The colour of a not-running train's WTT line: its category colour, fainter.</summary>
    public static Colour NotRunningColour(Colour colour) => colour with { A = (byte)Math.Round(colour.A * NotRunningAlpha) };

    public static GraphScene Build(GraphSceneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        var corridor = request.Corridor;

        var locations = corridor.Locations
            .Select(l => new GraphLocation(l.Tiploc, profile.DisplayName(l.Tiploc), l.AxisPosition, !l.Minor && (l.Platforms > 0 || !l.IsTimingPointOnly), l.Platforms, l.Minor))
            .ToList();

        var places = request.Places ?? UsablePlaces.Of(profile.Places);
        var paths = new List<GraphPath>();
        if (request.ShowWtt && request.Timetable is { } timetable)
        {
            foreach (var train in timetable.Trains)
            {
                var style = profile.StyleFor(train.CategoryId);
                var notRunning = request.Forecast?.Find(train.Id)?.Status == TrainForecastStatus.NotRunning;
                if (notRunning && !request.ShowNotRunning)
                {
                    continue;
                }

                AddWtt(paths, corridor, train, notRunning ? NotRunningColour(style.Colour) : style.Colour, style.Style);
            }
        }

        if (request.ShowPlan && request.Plan is { } plan)
        {
            foreach (var train in plan.Modified)
            {
                if (request.Forecast?.Find(train.Id)?.Status == TrainForecastStatus.NotRunning)
                {
                    continue; // re-planned, but SimSig will not run it: only its faint WTT line shows
                }

                var style = profile.StyleFor(train.CategoryId);
                paths.AddRange(PlanPaths(corridor, train, train.Cancelled ? CancelledColour : style.Colour, style: train.Cancelled ? LineStyle.Solid : style.Style));
            }
        }

        // Actual lines are worked out whether or not they are shown: a live head carries on from the end of one.
        var lastActual = new Dictionary<string, GraphVertex>(StringComparer.Ordinal);
        var actual = new List<GraphPath>();
        foreach (var live in request.LiveTrains)
        {
            for (var w = 0; w < live.Workings.Count; w++)
            {
                if (AddActual(actual, corridor, places, live, w, request) is { } end && w == live.CurrentIndex)
                {
                    lastActual[live.SimUid] = end;
                }
            }
        }

        if (request.ShowActual)
        {
            paths.AddRange(actual);
        }

        if (request.ShowForecast && request.Forecast is { } forecast)
        {
            foreach (var train in forecast.Drawn)
            {
                AddForecast(paths, corridor, train, request);
            }
        }

        var sim = request.SimTitle ?? profile.Title(profile.AreaId, request.Timetable?.Header);
        var title = $"{sim} — {corridor.Name}";
        var scene = new GraphScene(corridor.Id, title, locations, paths, request.Now);
        if (request.ShowDisruptions && request.Plan is { } withDisruptions)
        {
            var (disruptions, notes) = GraphAnnotations.For(corridor, withDisruptions);
            scene = scene with { Disruptions = disruptions, Notes = notes };
        }

        if (request.ShowConflicts && request.Conflicts is { Count: > 0 } conflicts)
        {
            scene = scene with { Conflicts = GraphConflicts.For(corridor, conflicts) };
        }

        if (request.ShowLiveHead && request.Forecast is { } withHeads && request.Now is not null)
        {
            scene = scene with { LiveHeads = LiveHeads(corridor, withHeads, request, lastActual) };
        }

        if (request.Signals != SignalDisplay.Off && !places.IsEmpty)
        {
            scene = scene with { Signals = SignalMarks(corridor, places), SignalDisplay = request.Signals };
        }

        return scene;
    }

    /// <summary>
    /// The placed signals on this corridor (S38): one mark per signal and pair of timing locations, at the place for
    /// trains running through if learned, else the best other (<see cref="UsablePlaces.Choose"/>).
    /// </summary>
    internal static IReadOnlyList<GraphSignal> SignalMarks(Corridor corridor, UsablePlaces places)
    {
        var marks = new List<GraphSignal>();
        foreach (var group in places.Signals.GroupBy(p => (Id: p.Id.ToUpperInvariant(), p.From, p.To)))
        {
            var place = UsablePlaces.Choose(group, RunKind.Through)!;
            if (UsablePlaces.PositionOn(corridor, place.From, place.To, place.Fraction) is not { } axis)
            {
                continue;
            }

            var direction = place.Direction ?? (corridor.PositionOf(place.To) > corridor.PositionOf(place.From) ? TrackDirection.Down : TrackDirection.Up);
            marks.Add(new GraphSignal(place.Id, axis, direction, place.From, place.To, place.Fraction, place.Run, place.SampleCount, place.HandSet));
        }

        return [.. marks.OrderBy(m => m.Axis).ThenBy(m => m.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The live head of each running train on this corridor (S36): from its last report (an unbooked anchor, or its
    /// last actual point) along its forecast to the next point it will report. None where any of those is off the
    /// corridor. Coloured as its actual line: by category, or by its latest delay.
    /// </summary>
    private static List<GraphLiveHead> LiveHeads(Corridor corridor, Forecast forecast, GraphSceneRequest request, Dictionary<string, GraphVertex> lastActual)
    {
        var heads = new List<GraphLiveHead>();
        var byDelay = request.ColourMode == GraphColourMode.Delay;
        foreach (var train in forecast.Trains)
        {
            var end = train.LiveUid is { } uid && lastActual.TryGetValue(uid, out var v) ? v : (GraphVertex?)null;
            if (train.Status != TrainForecastStatus.Running || HeadVertices(corridor, train, end) is not { } vertices)
            {
                continue;
            }

            var wtt = request.Timetable?.Find(train.Train);
            var categoryId = request.Plan?.Find(train.Train)?.CategoryId ?? wtt?.CategoryId;
            var style = request.Profile.StyleFor(categoryId);
            var colour = style.Colour;
            if (byDelay)
            {
                var latest = train.Points.LastOrDefault(p => p.Basis == ForecastBasis.Actual && p.DelaySeconds is not null)?.DelaySeconds;
                colour = DelayBands.ColourOfSeconds(latest);
            }

            heads.Add(new GraphLiveHead($"head:{train.Train}", train.Train, train.Headcode, colour, style.Style, vertices, train.LiveUid));
        }

        return heads;
    }

    /// <summary>
    /// A running train's head line: its last report, the end of a dwell there, then the next point; null if not all on
    /// the corridor. Since S38 it starts at the end of the actual line (<paramref name="lastActual"/>, the last signal
    /// passed if placed), goes through a learned place reached since (a track circuit or berth), and stops at the
    /// signal a train is standing at.
    /// </summary>
    private static List<GraphVertex>? HeadVertices(Corridor corridor, TrainForecast train, GraphVertex? lastActual = null)
    {
        var first = train.FirstForecast;
        if (first < 0)
        {
            return null;
        }

        var p = train.Points[first];
        var vertices = new List<GraphVertex>(3);
        if (p.Basis == ForecastBasis.Arrived)
        {
            // Standing at its last report: there until its forecast departure, then on to the next timed point.
            if (corridor.PositionOf(p.Location) is not { } at || p.Arrive is not { } arrived || p.Depart is not { } departs)
            {
                return null;
            }

            vertices.Add(new GraphVertex(arrived.Seconds, at));
            vertices.Add(new GraphVertex(departs.Seconds, at));
            var next = train.Points.Skip(first + 1).FirstOrDefault(q => q.FirstTime is not null);
            if (next is null || corridor.PositionOf(next.Location) is not { } to)
            {
                return null;
            }

            vertices.Add(new GraphVertex(next.FirstTime!.Value.Seconds, to));
            return vertices;
        }

        var last = train.Points.Take(first).LastOrDefault(q => q.Basis == ForecastBasis.Actual && q.LastTime is not null);
        if (last is null || p.FirstTime is not { } reach || corridor.PositionOf(p.Location) is not { } nextAt)
        {
            return null;
        }

        var anchorAt = train.Anchor is { } anchor && anchor.After == last.Index ? AnchorVertex(corridor, anchor) : null;
        var start = anchorAt is { Place: null } unbooked ? unbooked.Vertex
            : corridor.PositionOf(last.Location) is { } lastAt ? new GraphVertex(last.LastTime!.Value.Seconds, lastAt) : (GraphVertex?)null;
        if (lastActual is { } end && start is { } s && end.Time > s.Time)
        {
            start = end; // a signal passed since, placed on the actual line
        }

        if (start is not { } from)
        {
            return null;
        }

        vertices.Add(from);
        if (anchorAt is { Place: not null, Vertex: var placed } && placed.Time > from.Time)
        {
            vertices.Add(placed);
        }

        if (train.Standing is { } standing && anchorAt is { Vertex: var signal })
        {
            // Standing at a red signal: the head waits there, flat, until a report moves it.
            if (vertices[^1] != signal)
            {
                vertices.Add(signal with { Time = Math.Max(signal.Time, vertices[^1].Time) });
            }

            vertices.Add(signal with { Time = standing.Since.Seconds + SimTime.SecondsPerDay });
            return vertices;
        }

        if (reach.Seconds < vertices[^1].Time)
        {
            return null;
        }

        vertices.Add(new GraphVertex(reach.Seconds, nextAt));
        return vertices;
    }

    /// <summary>Where an anchor draws on the corridor: an unbooked timing location, or a learned place between two (S38); null if off it.</summary>
    private static (GraphVertex Vertex, AnchorPlace? Place)? AnchorVertex(Corridor corridor, ProgressAnchor anchor)
    {
        if (anchor.Place is { } place)
        {
            return UsablePlaces.PositionOn(corridor, place.From, place.To, place.Fraction) is { } axis ? (new GraphVertex(anchor.Time.Seconds, axis), place) : null;
        }

        return corridor.PositionOf(anchor.Location) is { } at ? (new GraphVertex(anchor.Time.Seconds, at), null) : null;
    }

    private static (Colour Colour, LineStyle Style) StyleOf(LiveWorking working, GraphSceneRequest request) =>
        working is { IsMatched: true, Train: { } id } && request.Timetable?.Find(id) is { } train
            ? (request.Profile.StyleFor(train.CategoryId).Colour, request.Profile.StyleFor(train.CategoryId).Style)
            : (working.IsMatched ? CategoryStyle.Fallback.Colour : UnmatchedColour, LineStyle.Solid);

    private static void AddWtt(List<GraphPath> paths, Corridor corridor, Train train, Colour colour, LineStyle style)
    {
        var builder = new PathAccumulator(corridor);
        for (var i = 0; i < train.Points.Count; i++)
        {
            var point = train.Points[i];
            AddBooked(builder, point.Location, point.Arrive, point.Depart, point.IsPass, i);
        }

        AddPaths(paths, corridor, builder, $"wtt:{train.Id}", GraphLayer.Wtt, train.Id, train.Headcode, colour, cancelled: false, style);
    }

    /// <summary>
    /// A train's plan as plan-layer paths along a corridor (more than one if it leaves the corridor and comes back), as
    /// the scene draws a re-planned train; a cancelled train is marked so. Also used for a drag's ghost preview, with
    /// <paramref name="keyPrefix"/> keeping its keys apart from the scene's. <paramref name="style"/> is the category's line style.
    /// </summary>
    public static IReadOnlyList<GraphPath> PlanPaths(Corridor corridor, TrainPlan train, Colour colour, string keyPrefix = "plan", LineStyle style = LineStyle.Solid)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        ArgumentNullException.ThrowIfNull(train);
        var builder = new PathAccumulator(corridor);
        foreach (var point in train.RunningPoints)
        {
            AddBooked(builder, point.Location, point.Arrive, point.Depart, point.IsPass, point.Index);
        }

        var paths = new List<GraphPath>(1);
        AddPaths(paths, corridor, builder, $"{keyPrefix}:{train.Id}", GraphLayer.Plan, train.Id, train.Headcode, colour, train.Cancelled, style);
        return paths;
    }

    /// <summary>
    /// A train's forecast path: from its latest actual time (so it carries on from the end of its actual line) through
    /// every forecast point. Coloured by category, or by forecast delay band like an actual line.
    /// </summary>
    private static void AddForecast(List<GraphPath> paths, Corridor corridor, TrainForecast forecast, GraphSceneRequest request)
    {
        var first = forecast.FirstForecast;
        if (first < 0)
        {
            return;
        }

        var categoryId = request.Plan?.Find(forecast.Train)?.CategoryId ?? request.Timetable?.Find(forecast.Train)?.CategoryId;
        var colour = request.Profile.StyleFor(categoryId).Colour;
        var byDelay = request.ColourMode == GraphColourMode.Delay;
        var builder = new PathAccumulator(corridor);
        if (forecast.Points[first].Basis == ForecastBasis.Forecast)
        {
            for (var k = first - 1; k >= 0; k--)
            {
                if (forecast.Points[k] is { Basis: ForecastBasis.Actual, LastTime: { } anchor } p)
                {
                    // From an unbooked timing location (S36) or a learned place (S38) passed since, if it is on the
                    // corridor; it stands in for the run's start. A train standing at a signal waits there first.
                    if (forecast.Anchor is { } passed && passed.After == k && AnchorVertex(corridor, passed) is { } from)
                    {
                        if (from.Place is { } place)
                        {
                            builder.AddPlace(place.From, from.Vertex.Axis, passed.Time, p.DelaySeconds, place.Id, p.Index);
                            if (forecast.Standing is { } standing && standing.Leaves > passed.Time)
                            {
                                builder.AddPlace(place.From, from.Vertex.Axis, standing.Leaves, p.DelaySeconds, place.Id, p.Index);
                            }
                        }
                        else
                        {
                            builder.Add(passed.Location, passed.Time, p.DelaySeconds, p.Index);
                        }
                    }
                    else
                    {
                        builder.Add(p.Location, anchor, p.DelaySeconds, p.Index);
                    }

                    break;
                }
            }
        }

        for (var k = first; k < forecast.Points.Count; k++)
        {
            var p = forecast.Points[k];
            if (p.Arrive is { } arrive && p.Depart is { } depart && depart > arrive)
            {
                builder.Add(p.Location, arrive, p.DelaySeconds, p.Index);
                builder.Add(p.Location, depart, p.DelaySeconds, p.Index);
            }
            else if (p.FirstTime is { } time)
            {
                builder.Add(p.Location, time, p.DelaySeconds, p.Index);
            }
        }

        var key = $"fc:{forecast.Train}";
        foreach (var (segment, index, count) in builder.Finish())
        {
            var (pathColour, segmentColours) = byDelay ? DelayColours(segment.Delays) : (colour, null);
            paths.Add(new GraphPath(count == 1 ? key : $"{key}#{index}", GraphLayer.Forecast, forecast.Train, forecast.Headcode, pathColour,
                segment.Vertices, Direction: corridor.DirectionOf(segment.Locations), LiveUid: forecast.LiveUid, SegmentColours: segmentColours, Points: segment.Indices, Signals: segment.Signals));
        }
    }

    /// <summary>A booked or planned point: one vertex for a pass or a point with one time, two for a stop with a dwell.</summary>
    private static void AddBooked(PathAccumulator builder, Tiploc location, SimTime? arrive, SimTime? depart, bool isPass, int index)
    {
        if (arrive is null && depart is null)
        {
            return;
        }

        if (isPass || arrive is null || depart is null || depart <= arrive)
        {
            builder.Add(location, (arrive ?? depart)!.Value, point: index);
        }
        else
        {
            builder.Add(location, arrive.Value, point: index);
            builder.Add(location, depart.Value, point: index);
        }
    }

    private static void AddPaths(List<GraphPath> paths, Corridor corridor, PathAccumulator builder, string key, GraphLayer layer, TrainId train, Headcode headcode, Colour colour, bool cancelled, LineStyle style)
    {
        foreach (var (segment, index, count) in builder.Finish())
        {
            paths.Add(new GraphPath(count == 1 ? key : $"{key}#{index}", layer, train, headcode, colour,
                segment.Vertices, Direction: corridor.DirectionOf(segment.Locations), Points: segment.Indices, IsCancelled: cancelled, Style: style));
        }
    }

    /// <summary>
    /// A working's actual line: its reports at corridor locations, and since S38 the placed signals it passed between
    /// them, so the line bends where the train was checked. Returns the line's last vertex, if it has one.
    /// </summary>
    private static GraphVertex? AddActual(List<GraphPath> paths, Corridor corridor, UsablePlaces places, LiveTrain live, int working, GraphSceneRequest request)
    {
        var w = live.Workings[working];
        var wtt = w is { IsMatched: true, Train: { } id } ? request.Timetable?.Find(id) : null;
        var byDelay = request.ColourMode == GraphColourMode.Delay && w.IsMatched;

        var builder = new PathAccumulator(corridor);
        ActualEvent? from = null;
        var passes = new List<ActualEvent>();
        foreach (var actual in live.ActualsOf(working))
        {
            if (!actual.AtTimingPoint)
            {
                if (from is not null && !places.IsEmpty)
                {
                    passes.Add(actual);
                }

                continue;
            }

            if (from is not null && passes.Count > 0)
            {
                AddPasses(builder, corridor, places, from, actual, passes);
            }

            passes.Clear();
            builder.Add(actual.Location, actual.Time, byDelay && wtt is not null ? TrainDelays.At(actual, wtt)?.Seconds : null);
            from = actual;
        }

        if (from is not null && passes.Count > 0)
        {
            AddPasses(builder, corridor, places, from, null, passes);
        }

        var (colour, style) = StyleOf(w, request);
        var key = $"act:{live.SimUid}/{working}";
        GraphVertex? end = null;
        foreach (var (segment, index, count) in builder.Finish())
        {
            var (pathColour, segmentColours) = byDelay ? DelayColours(segment.Delays) : (colour, null);
            paths.Add(new GraphPath(count == 1 ? key : $"{key}#{index}", GraphLayer.Actual, w.Train, w.Headcode, pathColour,
                segment.Vertices, IsUnmatched: !w.IsMatched, Direction: corridor.DirectionOf(segment.Locations), LiveUid: live.SimUid, Working: working,
                SegmentColours: segmentColours, Style: style, Signals: segment.Signals));
            end = segment.Vertices[^1];
        }

        return end;
    }

    /// <summary>
    /// Vertices for the signals a train passed between timing locations <paramref name="from"/> and
    /// <paramref name="to"/> (null while it is still on its way), at their places for the kind of run it made (S38).
    /// On its way, a signal is placed only if its places from there all lead to the same next location.
    /// </summary>
    private static void AddPasses(PathAccumulator builder, Corridor corridor, UsablePlaces places, ActualEvent from, ActualEvent? to, List<ActualEvent> passes)
    {
        var departed = from.Action != TrainLocationAction.Pass;
        foreach (var pass in passes)
        {
            var candidates = places.SignalFrom(pass.Location.Code, from.Location);
            SignallingPlace? place;
            if (to is not null)
            {
                place = UsablePlaces.Choose(candidates.Where(p => p.To == to.Location), SignallingPlaces.RunOf(departed, to.Action == TrainLocationAction.Arrive));
            }
            else
            {
                var onward = candidates.Where(p => corridor.Contains(p.To)).ToList();
                place = onward.Select(p => p.To).Distinct().Count() == 1
                    ? UsablePlaces.KindsStarting(departed).Select(k => onward.FirstOrDefault(p => p.Run == k)).FirstOrDefault(p => p is not null) ?? UsablePlaces.Choose(onward, null)
                    : null;
            }

            if (place is not null && UsablePlaces.PositionOn(corridor, place.From, place.To, place.Fraction) is { } axis)
            {
                builder.AddPlace(from.Location, axis, pass.Time, signal: pass.Location.Code);
            }
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

    /// <param name="Indices">The plan point index of each vertex, or null if some vertex has none (actuals).</param>
    /// <param name="Signals">The signal (or other learned place) at each vertex, null at a timing location; null if there are none (S38).</param>
    private sealed record Segment(List<GraphVertex> Vertices, List<Tiploc> Locations, List<int?> Delays, List<int>? Indices, List<string?>? Signals = null);

    /// <summary>Collects timed locations in running order into one or more corridor visits.</summary>
    private sealed class PathAccumulator(Corridor corridor)
    {
        private readonly List<Segment> _segments = [];
        private Segment? _current;
        private int _lastIndex = -1;
        private bool _away;

        /// <param name="delay">Seconds late at this point, if known (delay colouring only).</param>
        /// <param name="point">The plan point index, for booked, planned and forecast points; -1 for none.</param>
        public void Add(Tiploc location, SimTime time, int? delay = null, int point = -1)
        {
            var index = corridor.IndexOf(location);
            if (index < 0)
            {
                _away |= _current is not null;
                return;
            }

            if (_current is null || (_away && index == _lastIndex))
            {
                _current = new Segment([], [], [], [], []);
                _segments.Add(_current);
            }

            _away = false;
            Append(new GraphVertex(time.Seconds, corridor.Locations[index].AxisPosition), location, delay, point, null);
            _lastIndex = index;
        }

        /// <summary>
        /// A learned place between <paramref name="from"/> (on the corridor) and the next location, at axis position
        /// <paramref name="axis"/> (S38). Skipped while the train is away from the corridor.
        /// </summary>
        public void AddPlace(Tiploc from, double axis, SimTime time, int? delay = null, string? signal = null, int point = -1)
        {
            var index = corridor.IndexOf(from);
            if (index < 0 || _away)
            {
                return;
            }

            if (_current is null)
            {
                _current = new Segment([], [], [], [], []);
                _segments.Add(_current);
            }

            Append(new GraphVertex(time.Seconds, axis), from, delay, point, signal);
            _lastIndex = index;
        }

        private void Append(GraphVertex vertex, Tiploc location, int? delay, int point, string? signal)
        {
            var current = _current!;
            if (current.Vertices.Count == 0 || current.Vertices[^1] != vertex)
            {
                current.Vertices.Add(vertex);
                current.Locations.Add(location);
                current.Delays.Add(delay);
                current.Indices!.Add(point);
                current.Signals!.Add(signal);
            }
            else if (delay is not null)
            {
                current.Delays[^1] ??= delay;
            }
        }

        /// <summary>The visits that draw as a line (at least two vertices), with their index and how many there are.</summary>
        public IEnumerable<(Segment Segment, int Index, int Count)> Finish()
        {
            var drawn = _segments.Where(s => s.Vertices.Count >= 2)
                .Select(s => s with { Indices = s.Indices!.Contains(-1) ? null : s.Indices, Signals = s.Signals!.Any(id => id is not null) ? s.Signals : null })
                .ToList();
            return drawn.Select((s, i) => (s, i, drawn.Count));
        }
    }
}
