using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Tests.Live;

/// <summary>Builds small timetables and gateway events in code for the matcher and live-state tests.</summary>
internal static class TestTimetable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    public static WorkingTimetable Of(params Train[] trains) =>
        new(new TimetableHeader("test", "5.0", "Test", "", null, SimTime.FromClock(4, 0), SimTime.FromClock(23, 0)), [], trains, []);

    public static Train Train(
        string headcode,
        string? id = null,
        TrainStart start = TrainStart.AtFirstLocation,
        string? entry = null,
        TrainId? formedFrom = null,
        params TimingPoint[] points) => new()
        {
            Id = new TrainId(id ?? headcode),
            Headcode = new Headcode(headcode),
            Start = start,
            EntryPoint = start == TrainStart.EntryPoint ? new Tiploc("EIN") : null,
            EntryTime = entry is null ? null : SimTime.Parse(entry),
            SeedPoint = start == TrainStart.Seeded ? new Tiploc("S1") : null,
            FormedFrom = formedFrom,
            Points = points.Select((p, i) => p with { Index = i }).ToList(),
        };

    public static TimingPoint Call(string location, string? arrive, string? depart, params TrainActivity[] activities) => new()
    {
        Index = 0,
        Location = new Tiploc(location),
        Arrive = arrive is null ? null : SimTime.Parse(arrive),
        Depart = depart is null ? null : SimTime.Parse(depart),
        Activities = activities,
    };

    public static TimingPoint Pass(string location, string time) => new()
    {
        Index = 0,
        Location = new Tiploc(location),
        Depart = SimTime.Parse(time),
        IsPass = true,
    };

    public static TrainActivity Next(string trainId) => Forms(ActivityKind.Next, trainId);

    public static TrainActivity Forms(ActivityKind kind, string trainId) => new(kind, (int)kind, null, null, new TrainId(trainId));

    public static ActualEvent Actual(string location, string time, TrainLocationAction action = TrainLocationAction.Pass, bool atTimingPoint = true) =>
        new(new Tiploc(location), action, SimTime.Parse(time), "", atTimingPoint, 0);

    public static TrainLocationEvent Location(string uid, string headcode, string location, string time, TrainLocationAction action = TrainLocationAction.Pass, string platform = "") =>
        new(T0, headcode.Length == 0 ? default : new Headcode(headcode), uid, action, new Tiploc(location), platform, SimTime.Parse(time));

    public static ClockEvent Clock(string time, int interval = 500, bool paused = false, double wallSeconds = 0) =>
        new(T0.AddSeconds(wallSeconds), "test", SimTime.Parse(time), interval, paused);
}
