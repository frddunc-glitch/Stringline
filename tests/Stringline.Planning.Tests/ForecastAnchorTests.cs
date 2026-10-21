using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// S36 (#17): a running train reporting a timing location its plan does not book is forecast on from there. 1A01 is
/// booked A–C–D, leaving B out; 2Z01, where there is one, is booked A–B–C, so the WTT gives a minimum from B to C.
/// </summary>
public sealed class ForecastAnchorTests
{
    private static readonly TrainId Fast = new("1A01");
    private static readonly TrainId Slow = new("2B01");

    /// <summary>1A01 A 06:00, pass C 06:10, D 06:15; with <paramref name="viaB"/>, 2Z01 A 06:30, pass B 06:34, C 06:40 (B–C 6 min).</summary>
    private static WorkingTimetable Timetable(bool viaB) => viaB
        ? Of(
            Train("1A01", points: [Call("A", null, "06:00"), Pass("C", "06:10"), Call("D", "06:15", null)]),
            Train("2Z01", points: [Call("A", null, "06:30"), Pass("B", "06:34"), Call("C", "06:40", null)]))
        : Of(Train("1A01", points: [Call("A", null, "06:00"), Pass("C", "06:10"), Call("D", "06:15", null)]));

    /// <summary>A corridor with these section minimums (seconds) between its locations.</summary>
    private static Corridor Corridor(string[] locations, params int[] mins) => new(
        "main",
        "Main",
        locations.Select((l, i) => new CorridorLocation(new Tiploc(l), (double)i / (locations.Length - 1))).ToList(),
        [.. locations.Zip(locations.Skip(1), (a, b) => (a, b)).Select((p, i) => new Section(new Tiploc(p.a), new Tiploc(p.b), Section.DoubleLine, 180, mins[i]))]);

    private static ForecastNetwork Network(WorkingTimetable timetable, Corridor corridor) => ForecastNetwork.Build([corridor], timetable);

    [Fact]
    public void A_report_at_an_unbooked_location_on_the_way_moves_the_forecast_to_it()
    {
        var tt = Timetable(viaB: true);
        var network = Network(tt, Corridor(["A", "B", "C", "D"], 120, 360, 300));

        // Left A two minutes late but passed B at 06:04: the WTT's fastest B–C is 6 min, so it is back on time at C,
        // where the whole run from A (its minimum is the 10 min booked) would have kept it two minutes late.
        var forecast = Run(WorkingPlan.FromTimetable(tt), "06:04:10", network, interaction: false, Running("10", "1A01", Dep("A", "06:02"), Passed("B", "06:04")));

        var fast = forecast.Find(Fast)!;
        fast.Anchor.Should().Be(new ProgressAnchor(new Tiploc("B"), SimTime.Parse("06:04"), 0, 1, 0.25));
        fast.Anchor!.ToString().Should().Be("passed B 06:04:00 (not booked)");
        Times(fast).Should().Equal("A -/06:02:00 A", "C pass 06:10:00 F", "D 06:15:00/- F");
        fast.Points.Select(p => p.DelaySeconds).Should().Equal(120, 0, 0); // no delay is measured at B, which has no booked time

        var before = Forecaster.Run(new ForecastRequest(WorkingPlan.FromTimetable(tt))
        {
            LiveTrains = [Running("10", "1A01", Dep("A", "06:02"), Passed("B", "06:04"))],
            Now = SimTime.Parse("06:04:10"),
            Network = network,
            Options = new ForecastOptions { Interaction = false, UnbookedAnchors = false },
        });
        before.Find(Fast)!.Anchor.Should().BeNull();
        Times(before.Find(Fast)!)[1].Should().Be("C pass 06:12:00 F", "as before S36, from the departure at A");
    }

    [Fact]
    public void With_no_minimum_from_it_the_rest_of_the_run_is_its_share_of_the_whole()
    {
        var tt = Timetable(viaB: false);
        var network = Network(tt, Corridor(["A", "B", "C", "D"], 120, 360, 300));

        // B is a quarter of the way (120 of 480 s); passed at 06:03, half a minute behind 06:02:30, so the rest takes its
        // three quarters of the 10 min booked: C at 06:10:30.
        var forecast = Run(WorkingPlan.FromTimetable(tt), "06:03:10", network, interaction: false, Running("10", "1A01", Dep("A", "06:00"), Passed("B", "06:03")));

        var fast = forecast.Find(Fast)!;
        fast.Anchor!.Share.Should().Be(0.25);
        Times(fast)[1].Should().Be("C pass 06:10:30 F");
    }

    [Fact]
    public void A_location_off_the_corridor_counts_if_trains_run_through_it_between_the_two_points()
    {
        var tt = Timetable(viaB: true);
        var network = Network(tt, Corridor(["A", "C", "D"], 600, 300)); // B is not on the corridor

        var forecast = Run(WorkingPlan.FromTimetable(tt), "06:04:10", network, interaction: false, Running("10", "1A01", Dep("A", "06:00"), Passed("B", "06:04")));

        // 2Z01 runs A, B, C, so B lies between them, 4 of the 10 min (its booked A–B and B–C) along.
        var fast = forecast.Find(Fast)!;
        fast.Anchor!.Share.Should().BeApproximately(0.4, 1e-9);
        Times(fast)[1].Should().Be("C pass 06:10:00 F");
    }

    [Fact]
    public void A_report_somewhere_else_is_not_an_anchor()
    {
        var tt = Timetable(viaB: true);
        var network = Network(tt, Corridor(["A", "B", "C", "D"], 120, 360, 300));

        var forecast = Run(WorkingPlan.FromTimetable(tt), "06:03:10", network, interaction: false, Running("10", "1A01", Dep("A", "06:00"), Passed("X", "06:03")));

        forecast.Find(Fast)!.Anchor.Should().BeNull("no corridor or train puts X between A and C");
        Times(forecast.Find(Fast)!)[1].Should().Be("C pass 06:10:00 F");
    }

    [Fact]
    public void The_latest_report_on_the_way_is_the_anchor_and_a_booked_report_clears_it()
    {
        var tt = Of(Train("1A01", points: [Call("A", null, "06:00"), Pass("D", "06:12"), Call("E", "06:15", null)]));
        var network = Network(tt, Corridor(["A", "B", "C", "D", "E"], 120, 120, 120, 120));

        var two = Run(WorkingPlan.FromTimetable(tt), "06:06:10", network, interaction: false, Running("10", "1A01", Dep("A", "06:00"), Passed("B", "06:03"), Passed("C", "06:06")));
        two.Find(Fast)!.Anchor!.Location.Should().Be(new Tiploc("C"));
        two.Find(Fast)!.Anchor!.Share.Should().BeApproximately(2.0 / 3, 1e-9);

        var past = Run(WorkingPlan.FromTimetable(tt), "06:12:10", network, interaction: false, Running("10", "1A01", Dep("A", "06:00"), Passed("C", "06:06"), Passed("D", "06:12")));
        past.Find(Fast)!.Anchor.Should().BeNull("D is booked, and nothing unbooked was passed after it");
    }

    [Fact]
    public void A_train_that_left_a_stop_without_reporting_it_is_forecast_from_where_it_was_seen()
    {
        // 2B01 stops at B; a report at X between B and C shows it has gone.
        var tt = WorkingPlanTests.Timetable();
        var network = Network(tt, Corridor(["A", "B", "X", "C", "D"], 300, 120, 120, 300));

        var forecast = Run(WorkingPlan.FromTimetable(tt), "07:09:10", network, interaction: false, Running("10", "2B01", Dep("A", "07:00"), Arr("B", "07:06"), Passed("X", "07:09")));

        var slow = forecast.Find(Slow)!;
        slow.Anchor.Should().Be(new ProgressAnchor(new Tiploc("X"), SimTime.Parse("07:09"), 1, 2, 0.5));
        Times(slow).Should().Equal("A -/07:00:00 A", "B 07:06:00/- A", "C pass 07:11:00 F", "D 07:17:00/- F");
    }
}
