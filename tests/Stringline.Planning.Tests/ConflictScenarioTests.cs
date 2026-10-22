using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// Golden scenarios for the conflict detector (<c>06-…md</c> §3, §6): every kind on the hand-written corridors of the
/// forecaster's scenarios, with the clash worked out by hand.
/// </summary>
public sealed class ConflictScenarioTests
{
    private static readonly TrainId Fast = new("1F01");
    private static readonly TrainId Stopper = new("2S01");

    private static ConflictSet Detect(WorkingPlan plan, ForecastNetwork? network = null, string? now = null, ConflictOptions? options = null, params Core.Live.LiveTrain[] live)
    {
        var forecast = Run(plan, now, network, interaction: true, live);
        return ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network ?? ForecastNetwork.Empty, Options = options ?? new ConflictOptions() });
    }

    private static WorkingPlan Applied(WorkingPlan plan, IPlanCommand command)
    {
        var (next, result) = plan.Apply(command);
        result.Applied.Should().BeTrue(result.Error);
        return next;
    }

    // ---- Double line with a loop at B (the forecaster's loop scenario; headway 3 min) ----

    private static WorkingTimetable LoopTimetable() => Of(
        Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]),
        Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]));

    private static ForecastNetwork LoopNetwork(WorkingTimetable timetable, int headway = 180) =>
        ForecastNetwork.Build([Line("main", headway, single: false, "A", "B", "C")], timetable);

    [Fact]
    public void Against_a_reference_timetable_a_pair_is_allowed_only_as_close_as_the_reference_books_it()
    {
        // A draft (S44) books 1F03 a minute behind 1F01, where the timetable it was made from booked it ten minutes behind.
        var draft = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]),
            Train("1F03", points: [Pass("A", "08:01"), Pass("B", "08:05"), Pass("C", "08:09")]));
        var original = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]),
            Train("1F03", points: [Pass("A", "08:10"), Pass("B", "08:14"), Pass("C", "08:18")]));
        var rerouted = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]),
            Train("1F03", points: [Pass("A", "08:01"), Pass("C", "08:09")]));
        var plan = WorkingPlan.FromTimetable(draft);
        var network = LoopNetwork(draft);
        var forecast = Run(plan, null, network, interaction: true);
        ConflictSet Against(WorkingTimetable? booked) => ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network, Booked = booked });

        Against(null).Count.Should().Be(0, "a timetable's own bookings are accepted");
        var c = Against(original).All.Should().ContainSingle().Subject;
        (c.Kind, c.A, c.B).Should().Be((ConflictKind.Headway, new TrainId("1F03"), (TrainId?)Fast));
        Against(rerouted).Count.Should().Be(1, "a booking on another route does not cap the train");
        Against(draft).Count.Should().Be(0);
    }

    [Fact]
    public void Trains_on_time_have_no_conflicts()
    {
        var timetable = LoopTimetable();
        Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable)).Count.Should().Be(0);
    }

    [Fact]
    public void Headway_a_late_fast_train_runs_up_behind_the_stopper()
    {
        var timetable = LoopTimetable();
        var conflicts = Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", live:
        [
            Running("10", "1F01", Passed("A", "08:05")),
            Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")),
        ]);

        // 2S01 leaves the loop on time, B–C 08:06–08:12. 1F01, 5 min late, would run B–C 08:09–08:13: it must leave C
        // the headway after 2S01 (08:15), so it is 2 min too close. The full forecast's knock-on says the same.
        var c = conflicts.All.Should().ContainSingle().Subject;
        c.Kind.Should().Be(ConflictKind.Headway);
        (c.A, c.B, c.HeadcodeA, c.HeadcodeB).Should().Be((Fast, Stopper, new Headcode("1F01"), (Headcode?)new Headcode("2S01")));
        (c.Where, c.To, c.When).Should().Be((new Tiploc("B"), (Tiploc?)new Tiploc("C"), SimTime.Parse("08:09")));
        c.SeveritySeconds.Should().Be(120);
        (c.Start, c.End).Should().Be((SimTime.Parse("08:06"), SimTime.Parse("08:13")));
        c.PointIndex.Should().Be(1);
        c.KnockOnSeconds.Should().Be(120);
        c.Summary.Should().Be("1F01 too close behind 2S01");
        (c.HeadwaySeconds, c.HeadwaySource).Should().Be((180, (HeadwaySource?)HeadwaySource.Default), "the section's headway is named (#7)");
        conflicts.Involving(Stopper).Should().ContainSingle();
        conflicts.Find(c.Id).Should().BeSameAs(c);
    }

    [Fact]
    public void Headway_from_the_wtt_judges_the_clash_and_is_named()
    {
        // As above, on a line the WTT books 2 min apart: 1F01 must leave C 2 min after 2S01 (08:14), so 1 min too close.
        var timetable = LoopTimetable();
        var line = Line("main", 180, single: false, "A", "B", "C");
        var wtt = line with { Sections = [.. line.Sections.Select(s => (s with { BookedHeadwaySeconds = 120 }).ResolveHeadway(180))] };
        var conflicts = Detect(WorkingPlan.FromTimetable(timetable), ForecastNetwork.Build([wtt], timetable), "08:05", live:
        [
            Running("10", "1F01", Passed("A", "08:05")),
            Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")),
        ]);

        var c = conflicts.All.Should().ContainSingle().Subject;
        (c.Kind, c.SeveritySeconds).Should().Be((ConflictKind.Headway, 60));
        (c.HeadwaySeconds, c.HeadwaySource).Should().Be((120, (HeadwaySource?)HeadwaySource.Wtt));
    }

    [Fact]
    public void Overtake_a_less_late_fast_train_would_pass_the_stopper_between_stations()
    {
        var timetable = LoopTimetable();
        var conflicts = Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:03:30", live:
        [
            Running("10", "1F01", Passed("A", "08:03:30")),
            Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")),
        ]);

        // 1F01 would run B–C 08:07:30–08:11:30, into C before 2S01 (08:12) on the one Down track: it must follow it,
        // leaving C the headway after it (08:15), 3½ min later.
        var c = conflicts.All.Should().ContainSingle().Subject;
        (c.Kind, c.A, c.B).Should().Be((ConflictKind.Overtake, Fast, (TrainId?)Stopper));
        c.SeveritySeconds.Should().Be(210);
        c.Summary.Should().Be("1F01 catches up 2S01");
    }

    [Fact]
    public void Booked_spacing_tighter_than_the_headway_is_no_conflict()
    {
        // 2S01 is booked 2 min behind 1F01 into B–C, less than the 3 min headway: the WTT's own spacing is allowed.
        var timetable = LoopTimetable();
        Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable, headway: 300)).Count.Should().Be(0);
    }

    [Fact]
    public void A_train_already_on_its_way_into_a_section_is_not_reported()
    {
        // 1F01 has left B, 5 min late (B–C 08:09–08:13), behind 2S01 (08:06–08:12): nothing can be re-planned there.
        var timetable = LoopTimetable();
        var conflicts = Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:09", live:
        [
            Running("10", "1F01", Passed("A", "08:05"), Passed("B", "08:09")),
            Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59"), Dep("B", "08:06")),
        ]);

        conflicts.Count.Should().Be(0);
    }

    [Fact]
    public void Clashes_in_sections_one_after_another_are_one_conflict()
    {
        // A–B–C–D with a 4 min headway: 2S01 waits at B then runs to D; 1F01, 5 min late, follows it 1 min too close
        // in B–C and in C–D.
        var timetable = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08"), Pass("D", "08:12")]),
            Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Pass("C", "08:10"), Call("D", "08:14", null)]));
        var network = ForecastNetwork.Build([Line("main", 240, single: false, "A", "B", "C", "D")], timetable);
        var conflicts = Detect(WorkingPlan.FromTimetable(timetable), network, "08:05", live:
        [
            Running("10", "1F01", Passed("A", "08:05")),
            Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")),
        ]);

        var c = conflicts.All.Should().ContainSingle().Subject;
        (c.Kind, c.Where, c.To, c.SeveritySeconds).Should().Be((ConflictKind.Headway, new Tiploc("B"), (Tiploc?)new Tiploc("D"), 60));
        (c.Start, c.End).Should().Be((SimTime.Parse("08:06"), SimTime.Parse("08:17")));
    }

    [Fact]
    public void Small_clashes_are_left_out()
    {
        // 1F01 passes A at 08:06:40, so C at 08:14:40: 20 s too soon after 2S01 (08:12, plus the headway), under 30 s.
        var timetable = LoopTimetable();
        LiveTrainsAt("08:06:40", out var live);
        Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:06:40", live: live).Count.Should().Be(0);
        Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:06:40", new ConflictOptions { MinSeveritySeconds = 0 }, live)
            .All.Should().ContainSingle(c => c.SeveritySeconds == 20);
    }

    private static void LiveTrainsAt(string fastPassesA, out Core.Live.LiveTrain[] live) => live =
    [
        Running("10", "1F01", Passed("A", fastPassesA)),
        Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")),
    ];

    // ---- Single line A–B–C with a crossing loop at B (headway 2 min) ----

    private static WorkingTimetable CrossingTimetable() => Of(
        Train("2D01", points: [Call("A", null, "09:00"), Call("B", "09:10", "09:11"), Call("C", "09:21", null)]),
        Train("2U01", points: [Call("C", null, "09:00"), Call("B", "09:10", "09:11"), Call("A", "09:21", null)]));

    private static ForecastNetwork SingleLine(WorkingTimetable timetable) =>
        ForecastNetwork.Build([Line("main", 120, single: true, "A", "B", "C")], timetable);

    [Fact]
    public void Single_line_trains_crossing_as_booked_have_no_conflicts()
    {
        var timetable = CrossingTimetable();
        Detect(WorkingPlan.FromTimetable(timetable), SingleLine(timetable)).Count.Should().Be(0);
    }

    [Fact]
    public void Single_line_the_opposing_train_would_meet_a_late_train_head_on()
    {
        var timetable = CrossingTimetable();
        var conflicts = Detect(WorkingPlan.FromTimetable(timetable), SingleLine(timetable), "09:10", live:
        [
            Running("10", "2D01", Dep("A", "09:10")),
            Running("11", "2U01", Dep("C", "09:00")),
        ]);

        // 2D01 is 10 min late through A–B (09:10–09:20). 2U01 would enter B–A at 09:11, so it must wait in the loop
        // until 2D01 is in, plus the booked minute: 10 min. 2D01 is on its way and is not the one to give way.
        var c = conflicts.All.Should().ContainSingle().Subject;
        (c.Kind, c.A, c.B).Should().Be((ConflictKind.SingleLine, new TrainId("2U01"), (TrainId?)new TrainId("2D01")));
        (c.Where, c.To, c.When, c.SeveritySeconds).Should().Be((new Tiploc("B"), (Tiploc?)new Tiploc("A"), SimTime.Parse("09:11"), 600));
        c.KnockOnSeconds.Should().Be(600);
        c.Summary.Should().Be("2U01 meets 2D01 on the single line");
    }

    // ---- Junction: a branch joins the main line at J ----

    [Fact]
    public void Junction_a_late_branch_train_clashes_with_a_main_line_train_where_they_join()
    {
        // The branch (X–J) and the main line (A–J–C) share J–C. 2X01 is booked onto it ahead of 1M01, but leaves X 4½
        // min late, so it would follow 1M01 through J–C only 30 s behind: 2½ min short of the 3 min headway.
        var timetable = Of(
            Train("1M01", points: [Pass("A", "10:00"), Pass("J", "10:05"), Pass("C", "10:10")]),
            Train("2X01", points: [Call("X", null, "09:55"), Pass("J", "10:01"), Call("C", "10:06", null)]));
        var network = ForecastNetwork.Build([Line("main", 180, single: false, "A", "J", "C"), Line("branch", 180, single: false, "X", "J", "C")], timetable);
        var conflicts = Detect(WorkingPlan.FromTimetable(timetable), network, "09:59:30", live: Running("20", "2X01", Dep("X", "09:59:30")));

        var c = conflicts.All.Should().ContainSingle().Subject;
        (c.Kind, c.A, c.B).Should().Be((ConflictKind.Headway, new TrainId("2X01"), (TrainId?)new TrainId("1M01")));
        (c.Where, c.To).Should().Be((new Tiploc("J"), (Tiploc?)new Tiploc("C")));
        c.SeveritySeconds.Should().Be(150);
    }

    // ---- Platforms ----

    private static TimingPoint At(TimingPoint point, string platform) => point with { Platform = platform };

    private static WorkingTimetable PlatformTimetable(string secondPlatform = "1") => Of(
        Train("2A01", points: [At(Call("A", null, "10:00"), "1"), At(Call("S", "10:05", "10:10"), "1"), Call("C", "10:20", null)]),
        Train("2B01", points: [At(Call("A", null, "10:02"), "2"), At(Call("S", "10:12", "10:14"), secondPlatform), Call("C", "10:24", null)]));

    [Fact]
    public void Platform_trains_booked_into_one_platform_in_turn_are_fine()
    {
        Detect(WorkingPlan.FromTimetable(PlatformTimetable())).Count.Should().Be(0);
    }

    [Fact]
    public void Platform_a_late_train_still_in_the_platform_when_the_next_is_due()
    {
        // 2A01 leaves A 6½ min late, so it is at S 10:11:30–10:12 (the shortest dwell); 2B01 is due in at 10:12. It
        // must wait until a minute (the margin; the WTT books them 2 min apart) after 2A01 leaves.
        var conflicts = Detect(WorkingPlan.FromTimetable(PlatformTimetable()), now: "10:06:30", live:
        [
            Running("30", "2A01", Dep("A", "10:06:30")),
            Running("31", "2B01", Dep("A", "10:02")),
        ]);

        var c = conflicts.All.Should().ContainSingle().Subject;
        (c.Kind, c.A, c.B, c.Platform).Should().Be((ConflictKind.Platform, new TrainId("2B01"), (TrainId?)new TrainId("2A01"), "1"));
        (c.Where, c.When, c.To).Should().Be((new Tiploc("S"), SimTime.Parse("10:12"), (Tiploc?)null));
        c.SeveritySeconds.Should().Be(60);
        c.Summary.Should().Be("2B01 due in platform 1 before 2A01 clears it");
    }

    [Fact]
    public void Platform_a_platform_change_into_an_occupied_platform_uses_the_full_margin()
    {
        // 2B01 is booked into platform 2 at S, re-planned into platform 1 at 10:10:30, just after 2A01 leaves it.
        var timetable = PlatformTimetable(secondPlatform: "2");
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new ChangePlatform(new TrainId("2B01"), 1, "1"));
        plan = Applied(plan, new ShiftTrain(new TrainId("2B01"), 0, -90));

        var c = Detect(plan).All.Should().ContainSingle().Subject;
        (c.Kind, c.A, c.SeveritySeconds).Should().Be((ConflictKind.Platform, new TrainId("2B01"), 30));
    }

    [Fact]
    public void Platform_a_next_working_formed_in_the_platform_is_not_a_clash_with_its_own_train()
    {
        // 2A01 terminates in platform 1 at S and forms 2A02 there; 2Z01 is booked in after 2A02 has gone.
        var timetable = Of(
            Train("2A01", points: [Call("A", null, "10:00"), At(Call("S", "10:05", null, Next("2A02")), "1")]),
            Train("2A02", formedFrom: new TrainId("2A01"), points: [At(Call("S", null, "10:20"), "1"), Call("A", "10:25", null)]),
            Train("2Z01", points: [Call("C", null, "10:15"), At(Call("S", "10:22", "10:23"), "1"), Call("A", "10:28", null)]));

        Detect(WorkingPlan.FromTimetable(timetable)).Count.Should().Be(0);

        // The next working is held in the platform until 10:22: now 2Z01 cannot get in.
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new ShiftTrain(new TrainId("2A02"), 0, 120));
        var c = Detect(plan).All.Should().ContainSingle().Subject;
        (c.Kind, c.A, c.B, c.SeveritySeconds).Should().Be((ConflictKind.Platform, new TrainId("2Z01"), (TrainId?)new TrainId("2A02"), 60));
    }

    // ---- Disruptions and run times ----

    private static Disruption Block(string from, string to, string start, string end) =>
        new("B1", DisruptionKind.Block, new Tiploc(from), new Tiploc(to), null, SimTime.Parse(start), SimTime.Parse(end));

    [Fact]
    public void Disruption_a_train_planned_through_a_block_is_held_until_it_ends()
    {
        var timetable = LoopTimetable();
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new AddDisruption(Block("B", "C", "08:00", "08:30")));

        var conflicts = Detect(plan);
        conflicts.All.Select(c => (c.Kind, c.A)).Should().Equal((ConflictKind.Disruption, Fast), (ConflictKind.Disruption, Stopper));
        var c = conflicts.All[0];
        (c.Where, c.To, c.When, c.SeveritySeconds, c.DisruptionId).Should().Be((new Tiploc("B"), (Tiploc?)new Tiploc("C"), SimTime.Parse("08:04"), 26 * 60, "B1"));
        (c.B, c.Start, c.End).Should().Be(((TrainId?)null, SimTime.Parse("08:04"), SimTime.Parse("08:30")));
        c.Summary.Should().Be("1F01 runs into block B1");
    }

    [Fact]
    public void Disruption_a_train_held_before_the_block_in_the_plan_is_fine()
    {
        // 2S01 is booked out of B at 08:06, into a block until 08:08; holding it in the loop to 08:08 clears it.
        var timetable = Of(Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]));
        var plan = Applied(WorkingPlan.FromTimetable(timetable), new AddDisruption(Block("B", "C", "07:50", "08:08")));
        Detect(plan).All.Should().ContainSingle(c => c.Kind == ConflictKind.Disruption && c.SeveritySeconds == 120);

        plan = Applied(plan, new SetDwell(Stopper, 1, SimTime.Parse("08:08")));
        Detect(plan).Count.Should().Be(0);
    }

    [Fact]
    public void Unrealistic_a_run_planned_faster_than_a_speed_restriction_allows()
    {
        // A speed restriction ×2 on A–B is added after the plan was made: 1F01's 4 min run now takes 8.
        var restriction = new Disruption("SR1", DisruptionKind.SpeedRestriction, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("07:00"), SimTime.Parse("09:00"), RunTimeFactor: 2);
        var plan = Applied(WorkingPlan.FromTimetable(LoopTimetable()), new AddDisruption(restriction));

        var conflicts = Detect(plan);
        var fast = conflicts.All.Single(c => c.A == Fast);
        (fast.Kind, fast.Where, fast.To, fast.When, fast.SeveritySeconds).Should().Be((ConflictKind.Unrealistic, new Tiploc("A"), (Tiploc?)new Tiploc("B"), SimTime.Parse("08:00"), 240));
        fast.Summary.Should().Be("1F01 planned faster than it can run");
        conflicts.All.Should().OnlyContain(c => c.Kind == ConflictKind.Unrealistic);
    }

    [Fact]
    public void Unrealistic_a_booked_run_is_never_unrealistic_as_booked()
    {
        // 1Z01 runs A–C non-stop past B, untimed there. Its minimum is summed over A–B and B–C (from 2S01's stops):
        // 8 min, longer than its booked 6.
        var timetable = Of(
            Train("1Z01", points: [Pass("A", "08:00"), new TimingPoint { Index = 0, Location = new Tiploc("B") }, Call("C", "08:06", null)]),
            Train("2S01", points: [Call("A", null, "07:00"), Call("B", "07:04", "07:05"), Call("C", "07:09", null)]));
        var plan = WorkingPlan.FromTimetable(timetable);
        plan.MinRuns.Along(plan.Get(new TrainId("1Z01")).Points, 0, 2).Should().Be(480);

        Detect(plan).Count.Should().Be(0);
    }

    [Fact]
    public void Detection_is_deterministic()
    {
        var timetable = LoopTimetable();
        var live = new[] { Running("10", "1F01", Passed("A", "08:05")), Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59")) };
        var a = Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", live: live);
        var b = Detect(WorkingPlan.FromTimetable(timetable), LoopNetwork(timetable), "08:05", live: live);
        a.All.Should().Equal(b.All);
    }

    // ---- Catching up at a signal (S39) ----

    /// <summary>
    /// 2S01 starts from A at 08:00 and 1F01 runs through A at 08:05, both through B ten minutes later: 5 min apart at
    /// both ends. Starting from a stand, 2S01 is slow at first and passes S1 8/10 of the way (08:08); 1F01, running
    /// through, passes it 2/10 of the way (08:07).
    /// </summary>
    private static (WorkingTimetable Timetable, ForecastNetwork Plain, ForecastNetwork Signals) CatchingUp()
    {
        var timetable = Of(
            Train("1F01", points: [Pass("A", "08:05"), Pass("B", "08:15")]),
            Train("2S01", points: [Call("A", null, "08:00"), Pass("B", "08:10")]));
        var plain = ForecastNetwork.Build([CatchingLine], timetable);
        return (timetable, plain, WithS1(plain));
    }

    private static readonly Corridor CatchingLine = Line("main", 180, single: false, "A", "B");

    /// <summary>The network with S1 placed between A and B: 0.2 of the way running through, 0.8 starting.</summary>
    private static ForecastNetwork WithS1(ForecastNetwork network)
    {
        var places = Network.Profiles.SignallingPlaces.Empty
            .SetByHand(Network.Profiles.SignallingKind.Signal, "S1", new Tiploc("A"), new Tiploc("B"), 0.2)
            .SetByHand(Network.Profiles.SignallingKind.Signal, "S1", new Tiploc("A"), new Tiploc("B"), 0.8, Network.Profiles.RunKind.Starting);
        return network.WithBlocks(Network.Profiles.BlockHeadways.Measure([CatchingLine], Network.Profiles.UsablePlaces.Of(places)));
    }

    [Fact]
    public void Headway_a_train_running_through_catches_one_starting_at_a_signal_between_the_ends()
    {
        var (timetable, plain, signals) = CatchingUp();
        Detect(WorkingPlan.FromTimetable(timetable), plain).Count.Should().Be(0, "the ends are 5 min apart");

        // At S1 1F01 must pass 3 min after 2S01 (08:11), and would pass at 08:07: 4 min too close.
        var c = Detect(WorkingPlan.FromTimetable(timetable), signals).All.Should().ContainSingle().Subject;
        c.Kind.Should().Be(ConflictKind.Headway);
        (c.HeadcodeA, c.HeadcodeB).Should().Be((new Headcode("1F01"), (Headcode?)new Headcode("2S01")));
        (c.Where, c.To).Should().Be((new Tiploc("A"), (Tiploc?)new Tiploc("B")));
        c.Signal.Should().Be("S1");
        c.SeveritySeconds.Should().Be(240);
        c.Summary.Should().Be("1F01 catches 2S01 at S1");
        (c.HeadwaySeconds, c.HeadwaySource).Should().Be((180, (HeadwaySource?)HeadwaySource.Default), "no block headway is measured, so the section's applies");
    }

    [Fact]
    public void The_knock_on_pass_holds_a_train_that_would_catch_another_at_a_signal()
    {
        var (timetable, plain, signals) = CatchingUp();
        var plan = WorkingPlan.FromTimetable(timetable);
        Run(plan, network: plain).Find(Fast)!.KnockOnSeconds.Should().Be(0);
        Run(plan, network: signals).Find(Fast)!.KnockOnSeconds.Should().Be(240);
    }

    [Fact]
    public void Two_trains_running_through_are_checked_only_at_the_ends()
    {
        var timetable = Of(
            Train("1F01", points: [Pass("A", "08:05"), Pass("B", "08:15")]),
            Train("2S01", points: [Pass("A", "08:00"), Pass("B", "08:10")]));
        var network = WithS1(ForecastNetwork.Build([CatchingLine], timetable));
        network.FindSection(new Tiploc("A"), new Tiploc("B"))!.DownSignals.Should().ContainSingle();
        Detect(WorkingPlan.FromTimetable(timetable), network).Count.Should().Be(0, "both pass S1 at the same share of their runs");
    }
}
