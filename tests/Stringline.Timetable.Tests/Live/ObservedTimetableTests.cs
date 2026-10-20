using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Timetable.Live;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Timetable.Tests.Live;

/// <summary>S27: what live trains did, as a timetable, for the observed spacing in <c>stringline corridors --headways</c>.</summary>
public sealed class ObservedTimetableTests
{
    private static LiveTrain Live(string uid, params ActualEvent[] actuals) => new(
        uid,
        [new LiveWorking(new Headcode("2A01"), 0, MatchStatus.Pending, null, MatchMethod.None)],
        [.. actuals],
        PresentAtStart: false);

    [Fact]
    public void Each_visit_to_a_timing_point_is_a_point_timed_as_reported()
    {
        var train = Live(
            "123",
            Actual("A", "07:00", TrainLocationAction.Depart),
            Actual("S1", "07:01", atTimingPoint: false),
            Actual("B", "07:03"),
            Actual("C", "07:05", TrainLocationAction.Arrive),
            Actual("C", "07:06", TrainLocationAction.Depart),
            Actual("D", "07:09", TrainLocationAction.Arrive));

        var timetable = ObservedTimetable.From(Of().Header, [train]);

        var t = timetable.Trains.Should().ContainSingle().Subject;
        (t.Id, t.Headcode).Should().Be((new TrainId("123"), new Headcode("2A01")));
        t.Points.Select(p => $"{p.Index} {p.Location} {p.Arrive?.ToString() ?? "-"}/{p.Depart?.ToString() ?? "-"}{(p.IsPass ? " pass" : "")}").Should().Equal(
            "0 A -/07:00:00",
            "1 B -/07:03:00 pass",
            "2 C 07:05:00/07:06:00",
            "3 D 07:09:00/-");
    }

    [Fact]
    public void A_train_seen_only_at_signals_is_left_out()
    {
        var timetable = ObservedTimetable.From(Of().Header, [Live("9", Actual("S1", "07:01", atTimingPoint: false))]);

        timetable.Trains.Should().BeEmpty();
        timetable.AreaId.Should().Be("test");
    }

    [Fact]
    public void Trains_reported_two_minutes_apart_are_measured_as_two_minutes_apart()
    {
        var trains = Enumerable.Range(0, 3).Select(i => Live(
            $"{i}",
            Actual("A", SimTime.FromClock(7, 2 * i).ToString(), TrainLocationAction.Depart),
            Actual("B", SimTime.FromClock(7, (2 * i) + 3).ToString())));

        var timetable = ObservedTimetable.From(Of().Header, [.. trains]);

        timetable.Trains.Select(t => t.Points[1].Depart!.Value - t.Points[0].Depart!.Value).Should().OnlyContain(s => s == 180);
        timetable.Trains.Zip(timetable.Trains.Skip(1), (a, b) => b.Points[0].Depart!.Value - a.Points[0].Depart!.Value).Should().OnlyContain(s => s == 120);
    }
}
