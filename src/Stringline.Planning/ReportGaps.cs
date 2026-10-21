using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network;

namespace Stringline.Planning;

/// <summary>The spread of the gaps between one kind of report (median, 95th percentile and longest, in seconds).</summary>
public sealed record GapStats(int Count, int Median, int P95, int Max)
{
    public static GapStats Empty { get; } = new(0, 0, 0, 0);

    public static GapStats Of(List<int> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        if (gaps.Count == 0)
        {
            return Empty;
        }

        gaps.Sort();
        return new GapStats(gaps.Count, Percentile(gaps, 0.5), Percentile(gaps, 0.95), gaps[^1]);
    }

    /// <summary>Nearest-rank percentile of a sorted list.</summary>
    private static int Percentile(List<int> sorted, double share) => sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Count) - 1, 0, sorted.Count - 1)];
}

/// <summary>
/// How often a recording's matched trains report where they are, on the profile's corridors (S36, #17): the baseline
/// S38 measures its gain against.
/// </summary>
/// <param name="TimingReports">Reports at timing locations by matched workings.</param>
/// <param name="UnbookedReports">Of those, reports at a timing location the train's WTT schedule leaves out.</param>
/// <param name="UnbookedOnCorridor">Of those, reports at a location on a corridor.</param>
/// <param name="Booked">
/// Gaps between a train's reports at booked corridor locations (all the forecast used before S36), between two
/// different locations, so a dwell is not a gap.
/// </param>
/// <param name="Timing">The same, with unbooked timing locations on a corridor counted too (S36).</param>
/// <param name="Signals">
/// Gaps between any two reports (signals too) while the train is between two corridor locations: what S38 can reach.
/// </param>
public sealed record ReportGaps(int Workings, int TimingReports, int UnbookedReports, int UnbookedOnCorridor, GapStats Booked, GapStats Timing, GapStats Signals)
{
    public double UnbookedShare => TimingReports == 0 ? 0 : (double)UnbookedReports / TimingReports;

    public static ReportGaps Measure(WorkingTimetable timetable, IEnumerable<LiveTrain> trains, IEnumerable<Corridor> corridors)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        ArgumentNullException.ThrowIfNull(trains);
        ArgumentNullException.ThrowIfNull(corridors);
        var onCorridor = corridors.SelectMany(c => c.Locations).Select(l => l.Tiploc).ToHashSet();
        List<int> booked = [], timing = [], signals = [];
        int workings = 0, reports = 0, unbooked = 0, unbookedOn = 0;
        foreach (var live in trains)
        {
            for (var w = 0; w < live.Workings.Count; w++)
            {
                if (live.Workings[w] is not { IsMatched: true, Train: { } id } || timetable.Find(id) is not { } train)
                {
                    continue;
                }

                workings++;
                var schedule = train.Points.Select(p => p.Location).ToHashSet();
                var actuals = live.ActualsOf(w).ToList();
                ActualEvent? lastBooked = null, lastTiming = null;
                foreach (var a in actuals)
                {
                    if (!a.AtTimingPoint)
                    {
                        continue;
                    }

                    reports++;
                    var isBooked = schedule.Contains(a.Location);
                    unbooked += isBooked ? 0 : 1;
                    if (!onCorridor.Contains(a.Location))
                    {
                        continue;
                    }

                    unbookedOn += isBooked ? 0 : 1;
                    Gap(timing, ref lastTiming, a);
                    if (isBooked)
                    {
                        Gap(booked, ref lastBooked, a);
                    }
                }

                // Any report counts while the train is between corridor reports: from its first to its last.
                var first = actuals.FindIndex(a => a.AtTimingPoint && onCorridor.Contains(a.Location));
                var last = actuals.FindLastIndex(a => a.AtTimingPoint && onCorridor.Contains(a.Location));
                ActualEvent? previous = null;
                for (var i = first; i >= 0 && i <= last; i++)
                {
                    Gap(signals, ref previous, actuals[i]);
                }
            }
        }

        return new ReportGaps(workings, reports, unbooked, unbookedOn, GapStats.Of(booked), GapStats.Of(timing), GapStats.Of(signals));
    }

    /// <summary>Adds the gap since <paramref name="previous"/> if it was somewhere else (time at one place is a dwell).</summary>
    private static void Gap(List<int> gaps, ref ActualEvent? previous, ActualEvent report)
    {
        if (previous is not null && previous.Location != report.Location)
        {
            gaps.Add(report.Time - previous.Time);
        }

        previous = report;
    }
}
