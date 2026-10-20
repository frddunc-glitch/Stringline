using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Live;

/// <summary>
/// What live trains actually did, as a timetable: one train per sim uid (through every working it ran as), with a point
/// at each timing point it was reported at, timed as reported. Measuring it as a WTT is measured
/// (<c>CorridorMeasurement</c>) gives observed run times and spacings (S27's <c>stringline corridors --headways</c>).
/// </summary>
public static class ObservedTimetable
{
    /// <summary>The trains of a recording or session, under the timetable's header.</summary>
    public static WorkingTimetable From(TimetableHeader header, IEnumerable<LiveTrain> trains)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(trains);
        var list = new List<Train>();
        foreach (var live in trains)
        {
            var points = Points(live);
            if (points.Count > 0)
            {
                list.Add(new Train
                {
                    Id = new TrainId(live.SimUid),
                    Headcode = live.Workings[0].Headcode,
                    Points = points,
                });
            }
        }

        return new WorkingTimetable(header, [], list, []);
    }

    /// <summary>
    /// A point per visit to a timing point: reports in a row at one location (an arrival then a departure) are one
    /// point. A pass is timed as a booked pass is, by its departure time.
    /// </summary>
    internal static List<TimingPoint> Points(LiveTrain live)
    {
        var points = new List<TimingPoint>();
        foreach (var e in live.Actuals)
        {
            if (!e.AtTimingPoint || e.Action == TrainLocationAction.Unknown)
            {
                continue;
            }

            if (points.Count == 0 || points[^1].Location != e.Location)
            {
                points.Add(new TimingPoint { Index = points.Count, Location = e.Location, Platform = e.Platform.Length > 0 ? e.Platform : null });
            }

            var p = points[^1];
            points[^1] = e.Action switch
            {
                TrainLocationAction.Arrive => p with { Arrive = p.Arrive ?? e.Time, IsPass = false },
                TrainLocationAction.Depart => p with { Depart = e.Time, IsPass = false },
                _ => p with { Depart = e.Time, IsPass = p.Arrive is null },
            };
        }

        return points;
    }
}
