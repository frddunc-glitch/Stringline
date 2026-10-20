using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>What to look for conflicts in: the plan and its forecast.</summary>
/// <param name="Plan">The working plan: its trains, disruptions and minimum run times.</param>
/// <param name="Forecast">
/// The forecast of the plan. Conflicts are found on its <see cref="Forecast.Independent"/> times (Fred, S14: actuals so
/// far, then the plan with lateness, trains running through each other), and its knock-on (with the interaction pass)
/// is given with each.
/// </param>
public sealed record ConflictRequest(WorkingPlan Plan, Forecast Forecast)
{
    /// <summary>The sections to check headways and single lines on; none checks only platforms, blocks and run times.</summary>
    public ForecastNetwork Network { get; init; } = ForecastNetwork.Empty;

    public ConflictOptions Options { get; init; } = new();
}

/// <summary>Settings for the <see cref="ConflictDetector"/>.</summary>
public sealed record ConflictOptions
{
    /// <summary>The least time between one train leaving a platform and the next arriving in it.</summary>
    public int PlatformMarginSeconds { get; init; } = 60;

    /// <summary>
    /// The same pair clashing in sections one after another, entering the next within this long of leaving the last,
    /// is one conflict (a late train catching up keeps clashing all the way along).
    /// </summary>
    public int MergeGapSeconds { get; init; } = 600;

    /// <summary>
    /// Clashes needing less delay than this are not reported (Fred, S14: 30 s): they are booking rounding, as the
    /// WTT's half-minute times and the forecast's shortest dwells differ by seconds.
    /// </summary>
    public int MinSeveritySeconds { get; init; } = 30;
}

/// <summary>
/// Finds the clashes in a plan (<c>06-…md</c> §3, FC-3). Pure, deterministic and thread-safe.
/// </summary>
/// <remarks>
/// <para>Times are the forecast without the interaction pass, so a late train shows the clashes its lateness makes,
/// and re-planning one train (holding it, say) clears them. Each clash is reported against the train that must give
/// way: the one entering the section or platform second, or the one held. A train already on its way into a section,
/// or already at a platform, is not reported (nothing can be re-planned there), but it is in the way of others. Only
/// clashes still to come are found: times wholly in the past are actuals, and they never give way.</para>
/// <list type="bullet">
/// <item><b>Headway, overtake, single line</b>: each run is put through the sections of the network on its route, in
/// entry order, by the rules the interaction pass uses (<see cref="SectionOccupancy{T}"/>): the same way on the same
/// track at least the headway apart at both ends, and on a single line, after an opposing train has cleared. A run
/// takes whichever track lets it through soonest; if none does, it clashes with the train in the way on the best one.
/// No pair need be further apart than the WTT books them (Fred), so the WTT itself makes few conflicts. A pair that
/// clashes in sections one after another is one conflict.</item>
/// <item><b>Platform</b>: trains planned at the same platform of a location (ignoring case), from arrival to departure
/// (a pass for an instant), must be <see cref="ConflictOptions.PlatformMarginSeconds"/> apart, or as far apart as the
/// WTT books them there if less. Trains associated there (next working, divide, join, platform share) are not
/// checked against each other, and a train formed from another holds the platform from the other's arrival.</item>
/// <item><b>Disruption</b>: each block the forecast holds a train for (<see cref="TrainForecast.BlockHolds"/>).</item>
/// <item><b>Unrealistic</b>: a planned run not yet started that is shorter than the minimum run, lengthened by any
/// speed restriction in force when it leaves. The train's own booked run caps the minimum, so a WTT run is never
/// unrealistic as booked.</item>
/// </list>
/// <para>Clashes needing less than <see cref="ConflictOptions.MinSeveritySeconds"/> are left out.</para>
/// </remarks>
public static class ConflictDetector
{
    public static ConflictSet Detect(ConflictRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trains = Timelines(request);
        var knockOn = request.Forecast.Interaction ? request.Forecast : null;
        var found = new List<Conflict>();
        Sections(request, trains, found);
        Platforms(request, trains, found);
        Blocks(trains, found);
        RunTimes(request, trains, found);

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<Conflict>(found.Count);
        foreach (var c in found)
        {
            if (c.SeveritySeconds < request.Options.MinSeveritySeconds)
            {
                continue;
            }

            var id = c.Id;
            if (ids.TryGetValue(id, out var n))
            {
                ids[id] = n + 1;
                id = $"{id}#{n + 1}";
            }
            else
            {
                ids[id] = 1;
            }

            var k = knockOn?.Find(c.A)?.KnockOnSeconds ?? 0;
            result.Add(c with { Id = id, KnockOnSeconds = k });
        }

        return new ConflictSet(result);
    }

    // ---- Timelines ----

    /// <summary>One train's times along its plan, from the forecast.</summary>
    private sealed class Timeline(TrainPlan plan, Train? wtt, TrainForecast forecast, int order)
    {
        public TrainPlan Plan { get; } = plan;

        /// <summary>The WTT train, for a WTT train (not a clone): its bookings and activities.</summary>
        public Train? Wtt { get; } = wtt;

        public TrainForecast Forecast { get; } = forecast;

        public int Order { get; } = order;

        public IReadOnlyList<PlannedPoint> Points { get; } = plan.RunningPoints;

        public Headcode Headcode => Plan.Headcode;

        public TrainId Id => Plan.Id;

        /// <summary>The runs between consecutive timed points, as forecast.</summary>
        public List<Run> Runs { get; } = [];
    }

    /// <param name="Departed">The departure is actual: the train is on its way and cannot be held for this run.</param>
    private readonly record struct Run(int From, int To, int Depart, int Arrive, bool Departed);

    private static List<Timeline> Timelines(ConflictRequest request)
    {
        var plan = request.Plan;
        var list = new List<Timeline>();
        foreach (var forecast in request.Forecast.Independent.Trains)
        {
            if (forecast.Status == TrainForecastStatus.Unavailable || plan.Find(forecast.Train) is not { Cancelled: false } train ||
                forecast.Points.Count != train.RunningPoints.Count)
            {
                continue;
            }

            var wtt = train.Origin == PlanOrigin.Wtt ? plan.Timetable.Find(train.Source) : null;
            var timeline = new Timeline(train, wtt, forecast, list.Count);
            var previous = -1;
            for (var k = 0; k < forecast.Points.Count; k++)
            {
                var p = forecast.Points[k];
                if (p.FirstTime is null)
                {
                    continue;
                }

                if (previous >= 0 && forecast.Points[previous].LastTime is { } depart && p.FirstTime is { } arrive && arrive >= depart)
                {
                    timeline.Runs.Add(new Run(previous, k, depart.Seconds, arrive.Seconds, forecast.Points[previous].Basis == ForecastBasis.Actual));
                }

                previous = k;
            }

            list.Add(timeline);
        }

        return list;
    }

    /// <summary>The WTT's booked departure and arrival for a run, if the train is a WTT train booked at both ends.</summary>
    private static (int Depart, int Arrive)? Booked(Timeline t, int from, int to) =>
        t.Wtt is { } wtt && to < wtt.Points.Count && wtt.Points[from].LastTime is { } d && wtt.Points[to].FirstTime is { } a && a >= d
            ? (d.Seconds, a.Seconds)
            : null;

    // ---- Sections ----

    /// <summary>One train's run through one section.</summary>
    private sealed record Passage(Timeline Train, Run Run, SectionWindow Window, ForecastSection Section)
    {
        public Tiploc Entry => Window.Down ? Section.From : Section.To;

        public Tiploc Exit => Window.Down ? Section.To : Section.From;
    }

    private static void Sections(ConflictRequest request, List<Timeline> trains, List<Conflict> found)
    {
        var network = request.Network;
        if (network.Sections.Count == 0)
        {
            return;
        }

        var bySection = new Dictionary<int, List<Passage>>();
        foreach (var t in trains)
        {
            foreach (var run in t.Runs)
            {
                if (network.Route(t.Points[run.From].Location, t.Points[run.To].Location) is not { } route)
                {
                    continue;
                }

                var booked = Booked(t, run.From, run.To);
                foreach (var step in route)
                {
                    if (!bySection.TryGetValue(step.Section.Id, out var list))
                    {
                        bySection[step.Section.Id] = list = [];
                    }

                    list.Add(new Passage(t, run, SectionWindow.Of(step, run.Depart, run.Arrive - run.Depart, booked), step.Section));
                }
            }
        }

        var raw = new List<(Conflict Conflict, Passage X)>();
        foreach (var passages in bySection.Values)
        {
            passages.Sort((a, b) => a.Window.Enter != b.Window.Enter ? a.Window.Enter.CompareTo(b.Window.Enter)
                : a.Window.Exit != b.Window.Exit ? a.Window.Exit.CompareTo(b.Window.Exit)
                : a.Train.Order.CompareTo(b.Train.Order));
            var occupancy = new SectionOccupancy<Passage>(passages[0].Section);
            foreach (var x in passages)
            {
                var shift = occupancy.Shift(x.Window, x, out var track);
                if (shift > 0 && !x.Run.Departed && occupancy.Blocker(x.Window, x, track) is { } blocker)
                {
                    var l = blocker.Other.Owner;
                    if (!ReferenceEquals(l.Train, x.Train))
                    {
                        var kind = blocker.Clash switch
                        {
                            SectionClash.Overtake => ConflictKind.Overtake,
                            SectionClash.Opposing => ConflictKind.SingleLine,
                            _ => ConflictKind.Headway,
                        };
                        var conflict = new Conflict(string.Empty, kind, x.Train.Id, l.Train.Id, x.Entry, new SimTime(x.Window.Enter), shift)
                        {
                            HeadcodeA = x.Train.Headcode,
                            HeadcodeB = l.Train.Headcode,
                            To = x.Exit,
                            Start = new SimTime(Math.Min(x.Window.Enter, l.Window.Enter)),
                            End = new SimTime(Math.Max(x.Window.Exit, l.Window.Exit)),
                            PointIndex = x.Run.From,
                            HeadwaySeconds = x.Section.HeadwaySeconds,
                            HeadwaySource = x.Section.HeadwaySource,
                        };
                        raw.Add((conflict, x));
                    }
                }

                occupancy.Add(new SectionReservation<Passage>(x, x.Window, track));
            }
        }

        Merge(raw, request.Options.MergeGapSeconds, found);
    }

    /// <summary>
    /// One conflict for a pair clashing in sections one after another: from where the first starts to where the last
    /// ends, as bad as the worst.
    /// </summary>
    private static void Merge(List<(Conflict Conflict, Passage X)> raw, int gap, List<Conflict> found)
    {
        foreach (var group in raw.GroupBy(r => (r.Conflict.Kind, r.Conflict.A, r.Conflict.B)))
        {
            Conflict? current = null;
            var lastExit = 0;
            foreach (var (c, x) in group.OrderBy(r => r.X.Window.Enter).ThenBy(r => r.X.Window.Exit))
            {
                if (current is not null && c.Where == current.To && x.Window.Enter <= lastExit + gap)
                {
                    current = current with
                    {
                        To = c.To,
                        SeveritySeconds = Math.Max(current.SeveritySeconds, c.SeveritySeconds),
                        HeadwaySeconds = c.SeveritySeconds > current.SeveritySeconds ? c.HeadwaySeconds : current.HeadwaySeconds,
                        HeadwaySource = c.SeveritySeconds > current.SeveritySeconds ? c.HeadwaySource : current.HeadwaySource,
                        Start = c.Start < current.Start ? c.Start : current.Start,
                        End = c.End > current.End ? c.End : current.End,
                    };
                }
                else
                {
                    if (current is not null)
                    {
                        found.Add(current);
                    }

                    current = c with { Id = $"{Code(c.Kind)}:{c.A}:{c.B}:{c.Where}" };
                }

                lastExit = x.Window.Exit;
            }

            if (current is not null)
            {
                found.Add(current);
            }
        }
    }

    // ---- Platforms ----

    /// <summary>One train's time at a platform, as forecast and as booked.</summary>
    private sealed record Stay(Timeline Train, int Index, int Start, int End, int? BookedStart, int? BookedEnd, bool Arrived);

    private static void Platforms(ConflictRequest request, List<Timeline> trains, List<Conflict> found)
    {
        var byId = trains.ToDictionary(t => t.Id);
        var byPlatform = new Dictionary<(Tiploc Location, string Platform), List<Stay>>();
        foreach (var t in trains)
        {
            for (var k = 0; k < t.Points.Count; k++)
            {
                var point = t.Points[k];
                var f = t.Forecast.Points[k];
                if (string.IsNullOrWhiteSpace(point.Platform) || f.FirstTime is not { } first || f.LastTime is not { } last)
                {
                    continue;
                }

                var start = first.Seconds;
                var formed = k == 0 && f.Arrive is null ? FormedAt(t, point.Location, byId) : null;
                if (formed?.Arrive < start)
                {
                    start = formed.Value.Arrive; // the train it is formed from brings it in
                }

                int? bookedStart = null, bookedEnd = null;
                if (t.Wtt is { } wtt && k < wtt.Points.Count && wtt.Points[k] is { FirstTime: { } bs, LastTime: { } be } booked &&
                    string.Equals(booked.Platform?.Trim(), point.Platform.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    (bookedStart, bookedEnd) = (Math.Min(bs.Seconds, formed?.BookedArrive ?? int.MaxValue), be.Seconds);
                }

                var key = (point.Location, point.Platform.Trim().ToUpperInvariant());
                if (!byPlatform.TryGetValue(key, out var list))
                {
                    byPlatform[key] = list = [];
                }

                var there = f.Basis is ForecastBasis.Actual or ForecastBasis.Arrived || formed?.Here == true;
                list.Add(new Stay(t, k, start, Math.Max(start, last.Seconds), bookedStart, bookedEnd, there));
            }
        }

        var margin = request.Options.PlatformMarginSeconds;
        foreach (var ((location, platform), stays) in byPlatform)
        {
            if (stays.Count < 2)
            {
                continue;
            }

            stays.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End != b.End ? a.End.CompareTo(b.End) : a.Train.Order.CompareTo(b.Train.Order));
            var longest = 0;
            for (var i = 0; i < stays.Count; i++)
            {
                var y = stays[i];
                longest = Math.Max(longest, y.End - y.Start);
                if (y.Arrived)
                {
                    continue; // already there: nothing to re-plan
                }

                Stay? worst = null;
                var severity = 0;
                for (var j = i - 1; j >= 0 && stays[j].Start >= y.Start - longest - margin; j--)
                {
                    var x = stays[j];
                    if (ReferenceEquals(x.Train, y.Train) || Associated(x, y))
                    {
                        continue;
                    }

                    var need = Gap(x, y, margin);
                    var shift = x.End + need - y.Start;
                    if (shift > severity)
                    {
                        (worst, severity) = (x, shift);
                    }
                }

                if (worst is { } other)
                {
                    found.Add(new Conflict($"{Code(ConflictKind.Platform)}:{y.Train.Id}:{other.Train.Id}:{location}:{platform}", ConflictKind.Platform, y.Train.Id, other.Train.Id, location, new SimTime(y.Start), severity)
                    {
                        HeadcodeA = y.Train.Headcode,
                        HeadcodeB = other.Train.Headcode,
                        Platform = y.Train.Points[y.Index].Platform!.Trim(),
                        Start = new SimTime(y.Start),
                        End = new SimTime(y.Start + severity), // the wait for the platform
                        PointIndex = y.Index,
                    });
                }
            }
        }
    }

    /// <summary>
    /// The time needed from <paramref name="x"/> leaving to <paramref name="y"/> arriving: the margin, or what the WTT
    /// books between them at this platform if they are booked here in this order and that is less (even an overlap).
    /// </summary>
    private static int Gap(Stay x, Stay y, int margin) =>
        x.BookedStart is { } xs && x.BookedEnd is { } xe && y.BookedStart is { } ys && ys >= xs ? Math.Min(margin, ys - xe) : margin;

    /// <summary>
    /// When the train a train is formed from arrives where it forms it, as forecast and as booked, and whether it has
    /// (so the train is there), if that is <paramref name="location"/>.
    /// </summary>
    private static (int Arrive, int? BookedArrive, bool Here)? FormedAt(Timeline t, Tiploc location, Dictionary<TrainId, Timeline> byId)
    {
        if (t.Wtt?.FormedFrom is not { } parentId || !byId.TryGetValue(parentId, out var parent))
        {
            return null;
        }

        for (var k = parent.Points.Count - 1; k >= 0; k--)
        {
            if (parent.Points[k].Location == location && parent.Forecast.Points[k].FirstTime is { } arrive)
            {
                var booked = parent.Wtt is { } wtt && k < wtt.Points.Count ? wtt.Points[k].FirstTime?.Seconds : null;
                return (arrive.Seconds, booked, parent.Forecast.Points[k].Basis is ForecastBasis.Actual or ForecastBasis.Arrived);
            }
        }

        return null;
    }

    /// <summary>True if either train's WTT activities at its stay name the other (next working, divide, join, share…).</summary>
    private static bool Associated(Stay x, Stay y) =>
        Names(x.Train, x.Index, y.Train.Plan.Source) || Names(y.Train, y.Index, x.Train.Plan.Source) ||
        x.Train.Wtt?.FormedFrom == y.Train.Plan.Source || y.Train.Wtt?.FormedFrom == x.Train.Plan.Source;

    private static bool Names(Timeline t, int index, TrainId other) =>
        t.Wtt is { } wtt && index < wtt.Points.Count && wtt.Points[index].Activities.Any(a => a.Train == other);

    // ---- Blocks ----

    private static void Blocks(List<Timeline> trains, List<Conflict> found)
    {
        foreach (var t in trains)
        {
            foreach (var hold in t.Forecast.BlockHolds)
            {
                if (hold.Index >= t.Points.Count || hold.ToIndex >= t.Points.Count)
                {
                    continue;
                }

                var where = t.Points[hold.Index].Location;
                found.Add(new Conflict($"{Code(ConflictKind.Disruption)}:{t.Id}:{hold.DisruptionId}:{where}", ConflictKind.Disruption, t.Id, null, where, hold.Departure, hold.Seconds)
                {
                    HeadcodeA = t.Headcode,
                    To = t.Points[hold.ToIndex].Location,
                    DisruptionId = hold.DisruptionId,
                    Start = hold.Departure,
                    End = hold.HeldUntil,
                    PointIndex = hold.Index,
                });
            }
        }
    }

    // ---- Run times ----

    private static void RunTimes(ConflictRequest request, List<Timeline> trains, List<Conflict> found)
    {
        var plan = request.Plan;
        foreach (var t in trains)
        {
            var points = t.Points;
            var previous = -1;
            for (var k = 0; k < points.Count; k++)
            {
                if (!points[k].IsTimed)
                {
                    continue;
                }

                if (previous >= 0 && points[previous].LastTime is { } depart && points[k].FirstTime is { } arrive &&
                    t.Forecast.Points[previous].Basis != ForecastBasis.Actual)
                {
                    var planned = arrive - depart;
                    int? ceiling = Booked(t, previous, k) is { } b ? b.Arrive - b.Depart - t.Wtt!.Points[previous].AllowanceSeconds : null;
                    if (plan.MinRuns.Along(points, previous, k, depart, plan.Disruptions, ceiling: ceiling) is { } min && min > planned)
                    {
                        var where = points[previous].Location;
                        var leaves = t.Forecast.Points[previous].LastTime ?? depart; // when it is now expected to leave
                        found.Add(new Conflict($"{Code(ConflictKind.Unrealistic)}:{t.Id}:{where}:{points[k].Location}", ConflictKind.Unrealistic, t.Id, null, where, leaves, min - planned)
                        {
                            HeadcodeA = t.Headcode,
                            To = points[k].Location,
                            Start = leaves,
                            End = leaves + min,
                            PointIndex = previous,
                        });
                    }
                }

                previous = k;
            }
        }
    }

    private static string Code(ConflictKind kind) => kind switch
    {
        ConflictKind.Headway => "H",
        ConflictKind.Overtake => "O",
        ConflictKind.SingleLine => "S",
        ConflictKind.Platform => "P",
        ConflictKind.Disruption => "D",
        _ => "U",
    };
}
