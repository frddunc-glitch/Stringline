using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network.Tests;

/// <summary>Builds small timetables in code for the corridor tests.</summary>
internal static class TestTimetable
{
    public static WorkingTimetable Of(params Train[] trains) => Of(trains, []);

    public static WorkingTimetable Of(IReadOnlyList<Train> trains, IReadOnlyList<TrainCategory> categories) =>
        new(new TimetableHeader("test", "5.0", "Test", "", null, SimTime.FromClock(4, 0), SimTime.FromClock(23, 0)), categories, trains, []);

    /// <summary>A train calling at these points; its ID is the headcode.</summary>
    public static Train Train(string headcode, params TimingPoint[] points) => Train(headcode, null, points);

    public static Train Train(string headcode, string? category, params TimingPoint[] points) => new()
    {
        Id = new TrainId(headcode),
        Headcode = new Headcode(headcode),
        CategoryId = category,
        Points = points.Select((p, i) => p with { Index = i }).ToList(),
    };

    /// <summary>A stop. Times are clock text; null for none.</summary>
    public static TimingPoint Call(string location, string? arrive, string? depart, string? platform = null, bool? down = null, int allowance = 0) => new()
    {
        Index = 0,
        Location = new Tiploc(location),
        Arrive = arrive is null ? null : SimTime.Parse(arrive),
        Depart = depart is null ? null : SimTime.Parse(depart),
        Platform = platform,
        DownDirection = down,
        PerformanceAllowanceSeconds = allowance,
    };

    public static TimingPoint Pass(string location, string time, bool? down = null, int allowance = 0) => new()
    {
        Index = 0,
        Location = new Tiploc(location),
        Depart = SimTime.Parse(time),
        IsPass = true,
        DownDirection = down,
        PerformanceAllowanceSeconds = allowance,
    };

    /// <summary>An untimed point, as in a shunt move.</summary>
    public static TimingPoint Untimed(string location) => new() { Index = 0, Location = new Tiploc(location) };

    /// <summary>A train passing these locations one minute apart from <paramref name="start"/>, flagged Down if <paramref name="down"/> is given.</summary>
    public static Train Runner(string headcode, string start, bool? down, params string[] locations)
    {
        var t = SimTime.Parse(start);
        return Train(headcode, locations.Select((l, i) => Pass(l, (t + (60 * i)).ToString(), down)).ToArray());
    }

    public static IReadOnlyList<Tiploc> Order(params string[] locations) => locations.Select(l => new Tiploc(l)).ToList();
}
