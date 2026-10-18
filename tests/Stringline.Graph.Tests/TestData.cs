using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.Graph.Tests;

/// <summary>A small corridor A–B–C–D with timetables and live trains built in code.</summary>
internal static class TestData
{
    public static readonly Colour Express = new(0xD3, 0x2F, 0x2F);
    public static readonly Colour Stopper = new(0x19, 0x76, 0xD2);

    /// <summary>A (0), B (⅓), C (⅔), D (1); B is a timing point only, the rest are stations.</summary>
    public static Corridor TestCorridor { get; } = new(
        "main",
        "A – D",
        [
            new CorridorLocation(new Tiploc("A"), 0, Platforms: 4),
            new CorridorLocation(new Tiploc("B"), 1.0 / 3, IsTimingPointOnly: true),
            new CorridorLocation(new Tiploc("C"), 2.0 / 3, Platforms: 2),
            new CorridorLocation(new Tiploc("D"), 1),
        ],
        [
            new Section(new Tiploc("A"), new Tiploc("B"), Section.DoubleLine, Section.StandardHeadwaySeconds, 120),
            new Section(new Tiploc("B"), new Tiploc("C"), Section.DoubleLine, Section.StandardHeadwaySeconds, 120),
            new Section(new Tiploc("C"), new Tiploc("D"), Section.DoubleLine, Section.StandardHeadwaySeconds, 120),
        ]);

    public static StringlineProfile Profile { get; } = new()
    {
        AreaId = "test",
        Name = "Test sim",
        Corridors = [TestCorridor],
        LocationNames = ImmutableDictionary<Tiploc, string>.Empty.Add(new Tiploc("A"), "Alpha").Add(new Tiploc("C"), "Charlie"),
        Categories = [new CategoryStyle("XP", "Express", 80, Express), new CategoryStyle("OO", "Stopper", 40, Stopper)],
    };

    public static WorkingTimetable Tt(params Train[] trains) =>
        new(new TimetableHeader("test", "5.0", "Test sim", "", null, SimTime.FromClock(6, 0), SimTime.FromClock(12, 0)), [], trains, []);

    public static Train Train(string headcode, string? category, params TimingPoint[] points) => new()
    {
        Id = new TrainId(headcode),
        Headcode = new Headcode(headcode),
        CategoryId = category,
        Points = points.Select((p, i) => p with { Index = i }).ToList(),
    };

    public static TimingPoint Call(string location, string? arrive, string? depart) => new()
    {
        Index = 0,
        Location = new Tiploc(location),
        Arrive = arrive is null ? null : SimTime.Parse(arrive),
        Depart = depart is null ? null : SimTime.Parse(depart),
    };

    public static TimingPoint Pass(string location, string time) => new()
    {
        Index = 0,
        Location = new Tiploc(location),
        Depart = SimTime.Parse(time),
        IsPass = true,
    };

    public static TimingPoint Untimed(string location) => new() { Index = 0, Location = new Tiploc(location) };

    /// <summary>A Down express A→D passing B and C, and an Up stopper D→A calling at C and A.</summary>
    public static WorkingTimetable TwoTrains() => Tt(
        Train("1A01", "XP", Call("A", null, "07:00"), Pass("B", "07:04"), Pass("C", "07:08"), Call("D", "07:12", null)),
        Train("2B02", "OO", Call("D", null, "07:05"), Call("C", "07:10", "07:12"), Pass("B", "07:16"), Call("A", "07:21", null)));

    public static LiveTrain Live(string uid, IEnumerable<(string Headcode, MatchStatus Status, string? Train)> workings, params (string Location, TrainLocationAction Action, string Time, int Working, bool AtTimingPoint)[] actuals)
    {
        var list = workings.ToList();
        var events = actuals.Select(a => new ActualEvent(new Tiploc(a.Location), a.Action, SimTime.Parse(a.Time), "", a.AtTimingPoint, a.Working)).ToImmutableList();
        var built = list.Select((w, i) => new LiveWorking(
            new Headcode(w.Headcode),
            events.FindIndex(e => e.Working == i),
            w.Status,
            w.Train is null ? null : new TrainId(w.Train),
            w.Status == MatchStatus.Matched ? MatchMethod.HeadcodeAndTime : MatchMethod.None)).ToImmutableList();
        return new LiveTrain(uid, built, events, false);
    }

    public static LiveTrain Live(string uid, string headcode, MatchStatus status, string? train, params (string Location, TrainLocationAction Action, string Time)[] actuals) =>
        Live(uid, [(headcode, status, train)], actuals.Select(a => (a.Location, a.Action, a.Time, 0, true)).ToArray());
}
