using System.Globalization;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>One row of a tooltip: a label and its value.</summary>
public sealed record HoverLine(string Label, string Value);

/// <summary>What the tooltip over a path says (GR-6): a heading and labelled rows.</summary>
public sealed record HoverInfo(string Heading, IReadOnlyList<HoverLine> Lines)
{
    public string? ValueOf(string label) => Lines.FirstOrDefault(l => l.Label == label)?.Value;

    public override string ToString() => Heading + Environment.NewLine + string.Join(Environment.NewLine, Lines.Select(l => $"{l.Label}: {l.Value}"));
}

/// <summary>
/// Describes the path under the cursor at the timing point nearest it: headcode and description, the location, the
/// booked (WTT) and actual times there, and the delay. Pure; the live data comes from a <see cref="PlanSnapshot"/>.
/// </summary>
public static class GraphHover
{
    public const string Location = "Location";
    public const string Booked = "WTT";
    public const string Actual = "Actual";
    public const string Delay = "Delay";
    public const string Match = "Match";
    public const string Train = "Train";

    public static HoverInfo Describe(GraphHit hit, GraphScene scene, PlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = hit.Path;
        var wtt = path.Train is { } id ? snapshot.Timetable?.Find(id) : null;
        var (live, working) = FindLive(path, snapshot);
        var vertex = hit.Vertex;
        var location = scene.Locations.FirstOrDefault(l => Math.Abs(l.Axis - vertex.Axis) < 1e-9);

        var heading = wtt is { Description.Length: > 0 } ? $"{path.Headcode}  {wtt.Description}" : path.Headcode.Value;
        var lines = new List<HoverLine>();
        if (location is not null)
        {
            lines.Add(new HoverLine(Location, location.Name == location.Tiploc.Code ? location.Name : $"{location.Name} ({location.Tiploc})"));
        }

        var point = location is null || wtt is null ? null : NearestPoint(wtt, location.Tiploc, vertex.Time);
        if (point is not null)
        {
            lines.Add(new HoverLine(Booked, FormatBooked(point)));
        }
        else if (wtt is null && path.Layer == GraphLayer.Wtt)
        {
            lines.Add(new HoverLine(Booked, "—"));
        }

        var actuals = location is null || live is null || working is null ? [] : NearestVisit(live, working.Value, location.Tiploc, vertex.Time);
        if (actuals.Count > 0)
        {
            lines.Add(new HoverLine(Actual, string.Join("  ", actuals.Select(FormatActual))));
            if (wtt is not null && actuals.Select(a => TrainDelays.At(a, wtt)).LastOrDefault(d => d is not null) is { } delay)
            {
                lines.Add(new HoverLine(Delay, FormatDelay(delay.Seconds)));
            }
        }

        if (live is not null && working is { } w)
        {
            lines.Add(new HoverLine(Match, FormatMatch(live.Workings[w])));
        }

        var train = wtt is null ? (live is null ? null : $"sim uid {live.SimUid}") : live is null ? $"{wtt.Id}" : $"{wtt.Id}, sim uid {live.SimUid}";
        if (train is not null)
        {
            lines.Add(new HoverLine(Train, train));
        }

        return new HoverInfo(heading, lines);
    }

    /// <summary>"3 min late", "1 min early", or "Right time" within a minute either way.</summary>
    public static string FormatDelay(int seconds)
    {
        var minutes = seconds / SimTime.SecondsPerMinute;
        return minutes switch
        {
            0 => "Right time",
            > 0 => $"{minutes} min late",
            _ => $"{-minutes} min early",
        };
    }

    private static (LiveTrain? Live, int? Working) FindLive(GraphPath path, PlanSnapshot snapshot)
    {
        if (path.LiveUid is { } uid && snapshot.FindByUid(uid) is { } live)
        {
            return (live, path.Working ?? live.CurrentIndex);
        }

        if (path.Train is { } id && snapshot.FindByTrain(id) is { } found)
        {
            return (found.Train, found.Working);
        }

        return (null, null);
    }

    private static TimingPoint? NearestPoint(Train train, Tiploc location, double time) =>
        train.Points
            .Where(p => p.Location == location && p.IsTimed)
            .MinBy(p => Math.Abs(p.FirstTime!.Value.Seconds - time));

    /// <summary>The actual events of one visit to a location: the arrival, departure or pass nearest the time.</summary>
    private static List<ActualEvent> NearestVisit(LiveTrain live, int working, Tiploc location, double time)
    {
        var at = live.ActualsOf(working).Where(a => a.Location == location).ToList();
        if (at.Count <= 1)
        {
            return at;
        }

        var nearest = at.MinBy(a => Math.Abs(a.Time.Seconds - time))!;
        var index = at.IndexOf(nearest);
        var visit = new List<ActualEvent> { nearest };
        if (nearest.Action == TrainLocationAction.Arrive && index + 1 < at.Count && at[index + 1].Action == TrainLocationAction.Depart)
        {
            visit.Add(at[index + 1]);
        }
        else if (nearest.Action == TrainLocationAction.Depart && index > 0 && at[index - 1].Action == TrainLocationAction.Arrive)
        {
            visit.Insert(0, at[index - 1]);
        }

        return visit;
    }

    private static string FormatBooked(TimingPoint point)
    {
        var platform = point.Platform is { Length: > 0 } p ? $"  P{p}" : string.Empty;
        if (point.IsPass)
        {
            return $"pass {Format(point.FirstTime!.Value, "H")}{platform}";
        }

        var parts = new List<string>(2);
        if (point.Arrive is { } arrive)
        {
            parts.Add($"arr {Format(arrive, "H")}");
        }

        if (point.Depart is { } depart)
        {
            parts.Add($"dep {Format(depart, "H")}");
        }

        return string.Join("  ", parts) + platform;
    }

    private static string FormatActual(ActualEvent actual)
    {
        var verb = actual.Action switch
        {
            TrainLocationAction.Arrive => "arr",
            TrainLocationAction.Depart => "dep",
            TrainLocationAction.Pass => "pass",
            _ => "at",
        };
        var platform = actual.Platform.Length > 0 ? $" P{actual.Platform}" : string.Empty;
        return $"{verb} {Format(actual.Time, "T")}{platform}";
    }

    private static string FormatMatch(LiveWorking working) => working.Status switch
    {
        MatchStatus.Matched => working.Method switch
        {
            MatchMethod.NextWorking => "Matched (next working)",
            MatchMethod.Formed => "Matched (formed)",
            MatchMethod.Manual => "Linked by hand",
            _ => "Matched",
        },
        MatchStatus.Pending => "Matching…",
        _ => working.Reason is { Length: > 0 } reason ? $"Not matched: {reason}" : "Not matched",
    };

    private static string Format(SimTime time, string format) => time.ToString(format, CultureInfo.InvariantCulture);
}
