using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// Golden scenarios for the interaction pass (<c>06-…md</c> §2 step 4, §6): small hand-written corridors and
/// timetables with the expected knock-on worked out by hand.
/// </summary>
public sealed class ForecastScenarioTests
{
    // Double line with a loop at B: the stopping 2S01 waits in the loop at B while 1F01 passes.
    // A–B and B–C are 4 min at best; the headway is 3 min.

    private static WorkingTimetable LoopTimetable() => Of(
        Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]),
        Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]));

    private static ForecastNetwork LoopNetwork(WorkingTimetable timetable) =>
        ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C")], timetable);

    [Fact]
    public void Loop_trains_on_time_have_no_knock_on()
    {
        var timetable = LoopTimetable();
        var forecast = Run(WorkingPlan.FromTimetable(timetable), network: LoopNetwork(timetable));

        forecast.Interaction.Should().BeTrue();
        forecast.Trains.Should().OnlyContain(t => t.KnockOnSeconds == 0 && !t.Deviates);
        forecast.Drawn.Should().BeEmpty();
    }

    [Fact]
    public void Loop_a_late_fast_train_finds_the_stopper_gone_ahead_and_follows_it()
    {
        var timetable = LoopTimetable();
        var forecast = Run(WorkingPlan.FromTimetable(timetable), "08:05", LoopNetwork(timetable), live:
        [
            Running("10", "1F01", Passed("A", "08:05")),
            Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")),
        ]);

        // 2S01 leaves the loop on time at 08:06; 1F01 is due past B at 08:09 and must follow it by the headway to C.
        Times(forecast.Find(new TrainId("2S01"))!).Should().Equal("A -/07:55:00 A", "B 07:59:00/08:06:00 R", "C 08:12:00/- F");
        Times(forecast.Find(new TrainId("1F01"))!).Should().Equal("A pass 08:05:00 A", "B 08:09:00/08:11:00 F", "C 08:15:00/- F");
        forecast.Find(new TrainId("1F01"))!.KnockOnSeconds.Should().Be(120);
        forecast.Find(new TrainId("2S01"))!.KnockOnSeconds.Should().Be(0);
    }

    [Fact]
    public void Loop_a_late_stopper_recovers_in_the_loop_and_follows_at_the_booked_spacing()
    {
        var timetable = LoopTimetable();
        var forecast = Run(WorkingPlan.FromTimetable(timetable), "08:03", LoopNetwork(timetable), live:
        [
            Running("10", "1F01", Passed("A", "08:00")),
            Running("11", "2S01", Dep("A", "07:59"), Arr("B", "08:03")),
        ]);

        // Booked 2 min behind 1F01 into B–C, less than the headway: the WTT's own spacing is kept, not the headway.
        Times(forecast.Find(new TrainId("2S01"))!).Should().Equal("A -/07:59:00 A", "B 08:03:00/08:06:00 R", "C 08:12:00/- F");
        forecast.Trains.Should().OnlyContain(t => t.KnockOnSeconds == 0);
    }

    [Fact]
    public void Loop_without_the_interaction_pass_trains_run_through_each_other()
    {
        var timetable = LoopTimetable();
        var forecast = Run(WorkingPlan.FromTimetable(timetable), "08:05", LoopNetwork(timetable), interaction: false, live:
        [
            Running("10", "1F01", Passed("A", "08:05")),
            Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")),
        ]);

        forecast.Interaction.Should().BeFalse();
        Times(forecast.Find(new TrainId("1F01"))!).Should().Equal("A pass 08:05:00 A", "B pass 08:09:00 F", "C 08:13:00/- F");
    }

    // Single line A–B–C with a crossing loop at B: 2D01 (Down) and 2U01 (Up) are booked to cross there.

    private static WorkingTimetable CrossingTimetable(string upDepartsB = "09:11") => Of(
        Train("2D01", points: [Call("A", null, "09:00"), Call("B", "09:10", "09:11"), Call("C", "09:21", null)]),
        Train("2U01", points: [Call("C", null, "09:00"), Call("B", "09:10", upDepartsB), Call("A", "09:21", null)]));

    private static ForecastNetwork SingleLine(WorkingTimetable timetable) =>
        ForecastNetwork.Build([Line("main", 120, single: true, "A", "B", "C")], timetable);

    [Fact]
    public void Crossing_on_time_trains_cross_as_booked()
    {
        var timetable = CrossingTimetable();
        var forecast = Run(WorkingPlan.FromTimetable(timetable), network: SingleLine(timetable));

        forecast.Trains.Should().OnlyContain(t => t.KnockOnSeconds == 0 && !t.Deviates);
    }

    [Fact]
    public void Crossing_booked_to_overlap_by_rounding_still_crosses_as_booked()
    {
        // 2U01 is booked into B–A at 09:10:30, half a minute before 2D01 is booked out of it: booking rounding.
        var timetable = CrossingTimetable(upDepartsB: "09:10:30");
        var forecast = Run(WorkingPlan.FromTimetable(timetable), network: SingleLine(timetable));

        forecast.Trains.Should().OnlyContain(t => t.KnockOnSeconds == 0 && !t.Deviates);
    }

    [Fact]
    public void Crossing_the_opposing_train_waits_in_the_loop_for_a_late_train()
    {
        var timetable = CrossingTimetable();
        var forecast = Run(WorkingPlan.FromTimetable(timetable), "09:10", SingleLine(timetable), live:
        [
            Running("10", "2D01", Dep("A", "09:10")),
            Running("11", "2U01", Dep("C", "09:00")),
        ]);

        // 2D01 is 10 min late into the loop (09:20); 2U01 waits there until it has cleared A–B, plus the booked minute.
        Times(forecast.Find(new TrainId("2D01"))!).Should().Equal("A -/09:10:00 A", "B 09:20:00/09:20:30 F", "C 09:30:30/- F");
        Times(forecast.Find(new TrainId("2U01"))!).Should().Equal("C -/09:00:00 A", "B 09:10:00/09:21:00 F", "A 09:31:00/- F");
        forecast.Find(new TrainId("2U01"))!.KnockOnSeconds.Should().Be(600);
        forecast.Find(new TrainId("2D01"))!.KnockOnSeconds.Should().Be(0);
    }

    [Fact]
    public void Crossing_an_unbooked_meeting_waits_the_full_headway()
    {
        // 2U01 is 15 min late away from C, so it meets 2D01's next path, 2D03, which it is not booked to cross.
        var timetable = Of(
            Train("2D03", points: [Call("A", null, "09:20"), Call("B", "09:30", "09:31"), Call("C", "09:41", null)]),
            Train("2U01", points: [Call("C", null, "09:00"), Call("B", "09:10", "09:11"), Call("A", "09:21", null)]));
        var forecast = Run(WorkingPlan.FromTimetable(timetable), "09:15", SingleLine(timetable), live: Running("11", "2U01", Dep("C", "09:15")));

        // 2U01 leaves B at 09:25:30 at the earliest, but 2D03 holds A–B from 09:20 to 09:30: it waits for it plus 2 min.
        Times(forecast.Find(new TrainId("2U01"))!).Should().Equal("C -/09:15:00 A", "B 09:25:00/09:32:00 F", "A 09:42:00/- F");
        forecast.Find(new TrainId("2D03"))!.KnockOnSeconds.Should().Be(0);
    }

    // A junction at J: the main line from X and a branch from Y join for J–Z. The branch's corridor follows its trains
    // onto the main line, so J–Z is on both corridors: it must be one section, shared.

    private static readonly Corridor Main = Line("main", 180, single: false, "X", "J", "Z");
    private static readonly Corridor Branch = Line("y-z", 180, single: false, "Y", "J", "Z");

    private static WorkingTimetable JunctionTimetable(string yAtJ = "10:07") => Of(
        Train("1X01", points: [Call("X", null, "10:00"), Pass("J", "10:05"), Call("Z", "10:10", null)]) with { CategoryId = "slow" },
        Train("2Y01", points: [Call("Y", null, SimTime.Parse(yAtJ).AddSeconds(-300).ToString()), Pass("J", yAtJ), Call("Z", SimTime.Parse(yAtJ).AddSeconds(300).ToString(), null)]) with { CategoryId = "fast" });

    [Fact]
    public void Junction_a_late_main_line_train_follows_the_branch_train_onto_the_shared_section()
    {
        var timetable = JunctionTimetable();
        var network = ForecastNetwork.Build([Main, Branch], timetable);
        var forecast = Run(WorkingPlan.FromTimetable(timetable), "10:03", network, live:
        [
            Running("10", "1X01", Dep("X", "10:03")),
            Running("11", "2Y01", Dep("Y", "10:02")),
        ]);

        Times(forecast.Find(new TrainId("2Y01"))!).Should().Equal("Y -/10:02:00 A", "J pass 10:07:00 F", "Z 10:12:00/- F");
        Times(forecast.Find(new TrainId("1X01"))!).Should().Equal("X -/10:03:00 A", "J 10:08:00/10:10:00 F", "Z 10:15:00/- F");
        forecast.Find(new TrainId("1X01"))!.KnockOnSeconds.Should().Be(120);
    }

    [Fact]
    public void Junction_trains_meeting_at_the_same_moment_go_in_priority_order()
    {
        // 1X01 is 2 min late and 2Y01 2 min early: both reach J at 10:07.
        var timetable = JunctionTimetable(yAtJ: "10:09");
        var live = new[] { Running("10", "1X01", Dep("X", "10:02")), Running("11", "2Y01", Dep("Y", "10:02")) };
        var plan = WorkingPlan.FromTimetable(timetable);

        var byOrder = Run(plan, "10:02", ForecastNetwork.Build([Main, Branch], timetable), live: live);
        byOrder.Find(new TrainId("1X01"))!.KnockOnSeconds.Should().Be(0, "with equal priority the first in the plan goes first");
        byOrder.Find(new TrainId("2Y01"))!.KnockOnSeconds.Should().Be(180);

        var priorities = new Dictionary<string, int> { ["fast"] = 90, ["slow"] = 10 };
        var byPriority = Run(plan, "10:02", ForecastNetwork.Build([Main, Branch], timetable, priorities), live: live);
        byPriority.Find(new TrainId("2Y01"))!.KnockOnSeconds.Should().Be(0, "the higher priority train goes first");
        byPriority.Find(new TrainId("1X01"))!.KnockOnSeconds.Should().Be(180);
    }

    // Tracks side by side

    [Fact]
    public void A_section_the_WTT_runs_trains_side_by_side_on_has_more_than_one_track_each_way()
    {
        // 1P02 is booked into A–B a minute behind 2P01 and out of it a minute ahead: it overtakes, so there are two Down tracks.
        var timetable = Of(
            Train("2P01", points: [Pass("A", "08:00"), Pass("B", "08:05")]),
            Train("1P02", points: [Pass("A", "08:01"), Pass("B", "08:04")]));
        var corridor = Line("main", 180, single: false, "A", "B");
        var plan = WorkingPlan.FromTimetable(timetable);

        var widened = ForecastNetwork.Build([corridor], timetable);
        widened.FindSection(new Tiploc("A"), new Tiploc("B"))!.DownTracks.Should().Be(2);
        Run(plan, network: widened).Trains.Should().OnlyContain(t => t.KnockOnSeconds == 0);

        var narrow = ForecastNetwork.Build([corridor]);
        narrow.FindSection(new Tiploc("A"), new Tiploc("B"))!.DownTracks.Should().Be(1);
        Run(plan, network: narrow).Find(new TrainId("1P02"))!.KnockOnSeconds.Should().BeGreaterThan(0, "on one track it could not overtake");
    }

    [Fact]
    public void A_train_formed_from_one_whose_only_point_is_its_arrival_starts_in_time_order()
    {
        // Waterloo's ECS arrivals (5L45) are one point; the train they form (2L45) must still start at its own time,
        // not after every other train has taken its path.
        var timetable = Of(
            Train("5Y01", points: [Call("D", "08:15", null, Next("1Y01"))]),
            Train("1Y01", formedFrom: new TrainId("5Y01"), points: [Call("D", null, "08:20"), Call("A", "08:35", null)]),
            Train("2Z01", points: [Call("D", null, "08:25"), Call("A", "08:40", null)]));
        var network = ForecastNetwork.Build([Line("main", 180, single: false, "D", "A")], timetable);

        var forecast = Run(WorkingPlan.FromTimetable(timetable), network: network);

        forecast.Trains.Should().OnlyContain(t => t.KnockOnSeconds == 0);
        Times(forecast.Find(new TrainId("1Y01"))!).Should().Equal("D -/08:20:00 F", "A 08:35:00/- F");
    }
}
