using Stringline.Core;
using Stringline.Core.Timetables;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>Every plan command, with its validation and clamping (<c>06-…md</c> §1).</summary>
public sealed class WorkingPlanTests
{
    private static readonly TrainId Fast = new("1A01");
    private static readonly TrainId Slow = new("2B01");

    /// <summary>
    /// A–B–C–D. 1A01 runs at the minimums (A–B 5 min, B–C 4 min, C–D 5 min); 2B01 has a minute's slack on A–B and C–D.
    /// </summary>
    internal static WorkingTimetable Timetable() => Of(
        Train("1A01", points: [Call("A", null, "06:00"), Call("B", "06:05", "06:06"), Pass("C", "06:10"), Call("D", "06:15", null)]),
        Train("2B01", points: [Call("A", null, "07:00"), Call("B", "07:06", "07:07"), Pass("C", "07:11"), Call("D", "07:17", null)]));

    internal static WorkingPlan Plan() => WorkingPlan.FromTimetable(Timetable());

    private static WorkingPlan Applied(WorkingPlan plan, IPlanCommand command, out PlanCommandResult result)
    {
        (var after, result) = plan.Apply(command);
        result.Applied.Should().BeTrue(result.Error);
        return after;
    }

    private static WorkingPlan Applied(WorkingPlan plan, IPlanCommand command) => Applied(plan, command, out _);

    private static string Refused(WorkingPlan plan, IPlanCommand command)
    {
        var (after, result) = plan.Apply(command);
        result.Applied.Should().BeFalse();
        after.Should().BeSameAs(plan);
        return result.Error!;
    }

    private static string[] Times(TrainPlan train) =>
        train.RunningPoints.Select(p => $"{p.Location} {p.Arrive?.ToString() ?? "-"}/{p.Depart?.ToString() ?? "-"}{(p.IsPass ? " pass" : string.Empty)}").ToArray();

    [Fact]
    public void The_plan_of_a_timetable_is_the_WTT_as_booked()
    {
        var plan = Plan();

        plan.Count.Should().Be(2);
        plan.Trains.Select(t => t.Id).Should().Equal(Fast, Slow);
        plan.Modified.Should().BeEmpty();
        Times(plan.Get(Fast)).Should().Equal("A -/06:00:00", "B 06:05:00/06:06:00", "C -/06:10:00 pass", "D 06:15:00/-");
        plan.Get(Fast).Origin.Should().Be(PlanOrigin.Wtt);
        plan.Get(Fast).Source.Should().Be(Fast);
        plan.MinRuns.Between(new Tiploc("A"), new Tiploc("B")).Should().Be(300);
    }

    [Fact]
    public void The_empty_plan_has_no_trains()
    {
        WorkingPlan.Empty.Count.Should().Be(0);
        Refused(WorkingPlan.Empty, new CancelTrain(Fast)).Should().Be("No train 1A01 in the plan.");
    }

    // ShiftTrain

    [Fact]
    public void Shift_moves_the_train_from_a_point_to_the_end()
    {
        var plan = Applied(Plan(), new ShiftTrain(Fast, 1, 300), out var result);

        result.Warnings.Should().BeEmpty();
        Times(plan.Get(Fast)).Should().Equal("A -/06:00:00", "B 06:10:00/06:11:00", "C -/06:15:00 pass", "D 06:20:00/-");
        plan.Get(Fast).IsModified.Should().BeTrue();
        plan.Get(Slow).IsModified.Should().BeFalse();
        plan.Modified.Select(t => t.Id).Should().Equal(Fast);
    }

    [Fact]
    public void Shift_of_the_whole_train_earlier_needs_no_clamp()
    {
        var plan = Applied(Plan(), new ShiftTrain(Slow, 0, -600), out var result);

        result.Warnings.Should().BeEmpty();
        Times(plan.Get(Slow)).Should().Equal("A -/06:50:00", "B 06:56:00/06:57:00", "C -/07:01:00 pass", "D 07:07:00/-");
    }

    [Fact]
    public void Shift_earlier_is_clamped_to_the_minimum_run_and_pushes_on_where_needed()
    {
        // B two minutes earlier: A–B would take 4 min against a minimum of 5, so B comes back 1 min. C, two minutes
        // earlier too, is then only 3 min from B against 4, so it comes back 1 min. D then has 5 min from C: enough.
        var plan = Applied(Plan(), new ShiftTrain(Slow, 1, -120), out var result);

        Times(plan.Get(Slow)).Should().Equal("A -/07:00:00", "B 07:05:00/07:06:00", "C -/07:10:00 pass", "D 07:15:00/-");
        result.Warnings.Select(w => (w.Kind, w.Location.Code, w.Seconds, w.Index)).Should().Equal(
            (PlanWarningKind.RunTimeClamped, "B", 60, 1),
            (PlanWarningKind.RunTimeClamped, "C", 60, 2));
        result.Warnings[0].Message.Should().Be("2B01 needs at least 5 min from A to B; B moved 1 min later.");
        result.Warnings[0].Train.Should().Be(Slow);
    }

    [Fact]
    public void Shift_that_clamps_back_to_the_booking_leaves_the_train_unmodified()
    {
        var plan = Applied(Plan(), new ShiftTrain(Fast, 1, -60), out var result);

        result.Warnings.Should().HaveCount(3);
        plan.Get(Fast).SameAs(Plan().Get(Fast)).Should().BeTrue();
        plan.Get(Fast).IsModified.Should().BeFalse();
    }

    [Fact]
    public void Shift_is_refused_for_nothing_or_a_bad_point()
    {
        Refused(Plan(), new ShiftTrain(Fast, 1, 0)).Should().Be("A shift of zero makes no change to the plan.");
        Refused(Plan(), new ShiftTrain(Fast, 4, 60)).Should().Be("1A01 has no point 4.");
        Refused(Plan(), new ShiftTrain(Fast, -1, 60)).Should().Be("1A01 has no point -1.");
        Refused(Plan(), new ShiftTrain(new TrainId("9Z99"), 0, 60)).Should().Be("No train 9Z99 in the plan.");
    }

    // SetDwell

    [Fact]
    public void A_longer_dwell_pushes_the_train_on_only_as_far_as_it_must()
    {
        var plan = Applied(Plan(), new SetDwell(Slow, 1, SimTime.Parse("07:09")), out var result);

        // B–C needs 4 min: C at 07:13 (+2). C–D needs 5 min: D at 07:18 (+1), using up a minute of slack.
        Times(plan.Get(Slow)).Should().Equal("A -/07:00:00", "B 07:06:00/07:09:00", "C -/07:13:00 pass", "D 07:18:00/-");
        result.Warnings.Select(w => (w.Location.Code, w.Seconds)).Should().Equal(("C", 120), ("D", 60));
    }

    [Fact]
    public void A_shorter_dwell_does_not_move_later_points()
    {
        var plan = Applied(Plan(), new SetDwell(Slow, 1, SimTime.Parse("07:06:30")), out var result);

        result.Warnings.Should().BeEmpty();
        Times(plan.Get(Slow)).Should().Equal("A -/07:00:00", "B 07:06:00/07:06:30", "C -/07:11:00 pass", "D 07:17:00/-");
    }

    [Fact]
    public void A_departure_before_the_arrival_is_clamped_to_the_arrival()
    {
        var plan = Applied(Plan(), new SetDwell(Fast, 1, SimTime.Parse("06:04")), out var result);

        Times(plan.Get(Fast))[1].Should().Be("B 06:05:00/06:05:00");
        result.Warnings.Should().ContainSingle().Which.Should().Match<PlanWarning>(w =>
            w.Kind == PlanWarningKind.DwellClamped && w.Seconds == 60 && w.Message == "1A01 cannot leave B before it arrives; departure set to 06:05:00.");
    }

    [Fact]
    public void The_origin_departure_can_be_set()
    {
        var plan = Applied(Plan(), new SetDwell(Fast, 0, SimTime.Parse("06:02")), out var result);

        // Each point moves 2 min later, keeping its dwell, because 1A01 already runs at the minimums.
        Times(plan.Get(Fast)).Should().Equal("A -/06:02:00", "B 06:07:00/06:08:00", "C -/06:12:00 pass", "D 06:17:00/-");
        result.Warnings.Select(w => (w.Kind, w.Seconds)).Should().AllBeEquivalentTo((PlanWarningKind.RunTimeClamped, 120));
        result.Warnings.Should().HaveCount(3);
    }

    [Fact]
    public void A_dwell_is_refused_at_a_pass_or_the_destination()
    {
        Refused(Plan(), new SetDwell(Fast, 2, SimTime.Parse("06:12"))).Should().Be("1A01 passes C; add a stop there first.");
        Refused(Plan(), new SetDwell(Fast, 3, SimTime.Parse("06:20"))).Should().Be("1A01 does not leave D.");
        Refused(Plan(), new SetDwell(Fast, 1, SimTime.Parse("06:06"))).Should().Be("This makes no change to the plan.");
    }

    // ChangePlatform / ChangeLine

    [Fact]
    public void Platform_and_line_can_be_changed_and_cleared()
    {
        var plan = Applied(Plan(), new ChangePlatform(Fast, 1, " 3 "));
        plan = Applied(plan, new ChangeLine(Fast, 1, "UF"));

        plan.Get(Fast).Points[1].Platform.Should().Be("3");
        plan.Get(Fast).Points[1].Line.Should().Be("UF");
        plan.Get(Fast).IsModified.Should().BeTrue();

        plan = Applied(plan, new ChangePlatform(Fast, 1, ""));
        plan = Applied(plan, new ChangeLine(Fast, 1, null));
        plan.Get(Fast).Points[1].Platform.Should().BeNull();
        plan.Get(Fast).IsModified.Should().BeFalse();
        Refused(plan, new ChangePlatform(Fast, 1, null)).Should().Be("This makes no change to the plan.");
    }

    // SkipStop / AddStop

    [Fact]
    public void A_skipped_stop_becomes_a_pass_at_the_arrival_time()
    {
        var plan = Applied(Plan(), new SkipStop(Fast, 1), out var result);

        result.Warnings.Should().BeEmpty();
        Times(plan.Get(Fast)).Should().Equal("A -/06:00:00", "B -/06:05:00 pass", "C -/06:10:00 pass", "D 06:15:00/-");
    }

    [Fact]
    public void Only_an_intermediate_stop_can_be_skipped()
    {
        Refused(Plan(), new SkipStop(Fast, 0)).Should().Be("1A01 starts or ends at A; only an intermediate stop can be skipped.");
        Refused(Plan(), new SkipStop(Fast, 3)).Should().Be("1A01 starts or ends at D; only an intermediate stop can be skipped.");
        Refused(Plan(), new SkipStop(Fast, 2)).Should().Be("1A01 does not stop at C.");
    }

    [Fact]
    public void An_added_stop_arrives_at_the_pass_time_and_pushes_on()
    {
        var plan = Applied(Plan(), new AddStop(Fast, 2, 120), out var result);

        Times(plan.Get(Fast)).Should().Equal("A -/06:00:00", "B 06:05:00/06:06:00", "C 06:10:00/06:12:00", "D 06:17:00/-");
        result.Warnings.Should().ContainSingle().Which.Seconds.Should().Be(120);
    }

    [Fact]
    public void A_stop_can_only_be_added_at_a_pass_with_a_sensible_dwell()
    {
        Refused(Plan(), new AddStop(Fast, 1, 60)).Should().Be("1A01 already stops at B.");
        Refused(Plan(), new AddStop(Fast, 2, 0)).Should().Be("A dwell must be between 1 s and 6 h.");
        Refused(Plan(), new AddStop(Fast, 2, AddStop.MaxDwellSeconds + 1)).Should().Be("A dwell must be between 1 s and 6 h.");
    }

    // TerminateAt / CancelTrain / ReinstateTrain

    [Fact]
    public void A_train_terminated_short_stops_at_that_point()
    {
        var plan = Applied(Plan(), new TerminateAt(Fast, 2));
        var train = plan.Get(Fast);

        train.TerminatesAt.Should().Be(2);
        train.LastIndex.Should().Be(2);
        train.Points.Should().HaveCount(4, "the points it no longer runs to are kept for a reinstatement");
        Times(train).Should().Equal("A -/06:00:00", "B 06:05:00/06:06:00", "C 06:10:00/-");
        train.IsModified.Should().BeTrue();

        Refused(plan, new ShiftTrain(Fast, 3, 60)).Should().Be("1A01 is terminated short and no longer runs to D.");
        Refused(plan, new TerminateAt(Fast, 2)).Should().Be("1A01 already terminates at C.");
        Applied(plan, new TerminateAt(Fast, 1)).Get(Fast).TerminatesAt.Should().Be(1);
    }

    [Fact]
    public void A_train_cannot_be_terminated_at_its_origin_or_destination()
    {
        Refused(Plan(), new TerminateAt(Fast, 0)).Should().Be("1A01 starts at A; cancel it instead.");
        Refused(Plan(), new TerminateAt(Fast, 3)).Should().Be("1A01 already terminates at D.");
    }

    [Fact]
    public void Cancel_and_reinstate()
    {
        var plan = Applied(Plan(), new CancelTrain(Fast));
        plan.Get(Fast).Cancelled.Should().BeTrue();
        plan.Get(Fast).IsModified.Should().BeTrue();
        Refused(plan, new CancelTrain(Fast)).Should().Be("1A01 is already cancelled.");

        plan = Applied(plan, new TerminateAt(Fast, 1));
        plan = Applied(plan, new ReinstateTrain(Fast));
        plan.Get(Fast).Cancelled.Should().BeFalse();
        plan.Get(Fast).TerminatesAt.Should().BeNull();
        plan.Get(Fast).IsModified.Should().BeFalse();
        Refused(plan, new ReinstateTrain(Fast)).Should().Be("1A01 is neither cancelled nor terminated short.");
    }

    // CloneTrain

    [Fact]
    public void A_clone_copies_the_current_plan_with_a_new_headcode()
    {
        var plan = Applied(Plan(), new ShiftTrain(Fast, 1, 60));
        plan = Applied(plan, new CancelTrain(Fast));
        var id = plan.NewCloneId(new Headcode("1A51"));
        id.Value.Should().Be("1A51#c1");

        plan = Applied(plan, new CloneTrain(Fast, new Headcode("1A51"), 3600, id));
        var clone = plan.Get(id);

        clone.Headcode.Value.Should().Be("1A51");
        clone.Origin.Should().Be(PlanOrigin.Clone);
        clone.Source.Should().Be(Fast);
        clone.Cancelled.Should().BeFalse();
        clone.IsModified.Should().BeTrue();
        Times(clone).Should().Equal("A -/07:00:00", "B 07:06:00/07:07:00", "C -/07:11:00 pass", "D 07:16:00/-");
        plan.Trains.Select(t => t.Id).Should().Equal(Fast, Slow, id);
        plan.NewCloneId(new Headcode("1A51")).Value.Should().Be("1A51#c2");

        Refused(plan, new CloneTrain(Slow, new Headcode("1A51"), 0, id)).Should().Be("The train ID 1A51#c1 is already used.");
    }

    [Fact]
    public void A_clone_can_be_re_planned_like_any_train()
    {
        var id = new TrainId("5Z01#c1");
        var plan = Applied(Plan(), new CloneTrain(Slow, new Headcode("5Z01"), 0, id));
        plan = Applied(plan, new ShiftTrain(id, 0, 600));

        plan.Get(id).Points[0].Depart.Should().Be(SimTime.Parse("07:10"));
        plan.Get(id).IsModified.Should().BeTrue();
        plan.Get(Slow).IsModified.Should().BeFalse();
    }

    // Disruptions

    private static Disruption Restriction(string id = "SR1", string from = "A", string to = "B", string start = "06:00", string end = "07:00", double factor = 1.5, int added = 0) =>
        new(id, DisruptionKind.SpeedRestriction, new Tiploc(from), new Tiploc(to), null, SimTime.Parse(start), SimTime.Parse(end), factor, added);

    [Fact]
    public void Disruptions_are_added_edited_and_removed()
    {
        var plan = Applied(Plan(), new AddDisruption(Restriction()));
        plan.Disruptions.Should().ContainSingle().Which.RunTimeFactor.Should().Be(1.5);
        Refused(plan, new AddDisruption(Restriction())).Should().Be("There is already a disruption SR1.");

        plan = Applied(plan, new EditDisruption(Restriction(factor: 2)));
        plan.FindDisruption("SR1")!.RunTimeFactor.Should().Be(2);
        Refused(plan, new EditDisruption(Restriction(factor: 2))).Should().Be("This makes no change to the disruption.");
        Refused(plan, new EditDisruption(Restriction(id: "X"))).Should().Be("No disruption X.");

        plan = Applied(plan, new RemoveDisruption("SR1"));
        plan.Disruptions.Should().BeEmpty();
        Refused(plan, new RemoveDisruption("SR1")).Should().Be("No disruption SR1.");
    }

    [Fact]
    public void Invalid_disruptions_are_refused()
    {
        Refused(Plan(), new AddDisruption(Restriction(start: "07:00", end: "07:00"))).Should().Be("A disruption must end after it starts.");
        Refused(Plan(), new AddDisruption(Restriction(factor: 0.5))).Should().Be("The running-time factor must be between 1 and 100.");
        Refused(Plan(), new AddDisruption(Restriction(added: -1))).Should().Be("Added seconds cannot be negative.");
        Refused(Plan(), new AddDisruption(Restriction(id: " "))).Should().Be("A disruption needs an ID.");
    }

    [Fact]
    public void Adding_a_disruption_does_not_re_plan_trains()
    {
        var plan = Applied(Plan(), new AddDisruption(Restriction(factor: 3)));

        plan.Modified.Should().BeEmpty();
    }

    [Fact]
    public void A_speed_restriction_lengthens_the_minimum_run_of_an_edited_train()
    {
        var plan = Applied(Plan(), new AddDisruption(Restriction(factor: 1.5, added: 30)));
        plan = Applied(plan, new SetDwell(Fast, 0, SimTime.Parse("06:01")), out var result);

        // A–B: 5 min × 1.5 + 30 s = 8 min. B was 4 min after the new departure, so it moves 4 min later.
        result.Warnings[0].Message.Should().Be("1A01 needs at least 8 min from A to B; B moved 4 min later.");
        Times(plan.Get(Fast))[1].Should().Be("B 06:09:00/06:10:00");
    }

    [Fact]
    public void A_speed_restriction_applies_only_to_runs_departing_while_it_is_in_force()
    {
        var plan = Applied(Plan(), new AddDisruption(Restriction(start: "05:00", end: "06:00")));
        plan = Applied(plan, new SetDwell(Fast, 0, SimTime.Parse("06:01")), out var result);

        result.Warnings[0].Message.Should().Be("1A01 needs at least 5 min from A to B; B moved 1 min later.");
    }

    [Fact]
    public void A_block_does_not_clamp()
    {
        var block = new Disruption("B1", DisruptionKind.Block, new Tiploc("A"), new Tiploc("B"), null, SimTime.Parse("06:00"), SimTime.Parse("07:00"));
        var plan = Applied(Plan(), new AddDisruption(block));
        plan = Applied(plan, new SetDwell(Fast, 0, SimTime.Parse("06:01")), out var result);

        result.Warnings[0].Message.Should().Be("1A01 needs at least 5 min from A to B; B moved 1 min later.");
    }

    // Live links

    [Fact]
    public void Live_links_are_kept_by_working_and_replaced()
    {
        var plan = Applied(Plan(), new LinkLiveTrain("10", new Headcode("1A01"), Slow));
        plan = Applied(plan, new LinkLiveTrain("11", new Headcode("2B01"), null));
        plan = Applied(plan, new LinkLiveTrain("10", new Headcode("1A01"), Fast));

        plan.Links.Should().Equal(new LiveLink("11", new Headcode("2B01"), null), new LiveLink("10", new Headcode("1A01"), Fast));
        plan.FindLink("10", new Headcode("1A01"))!.Train.Should().Be(Fast);
        plan.Modified.Should().BeEmpty("a link is not a change to the plan");
        Refused(plan, new LinkLiveTrain("10", new Headcode("1A01"), Fast)).Should().Be("That working is already linked so.");

        plan = Applied(plan, new UnlinkLiveTrain("10", new Headcode("1A01")));
        plan.Links.Should().ContainSingle();
        Refused(plan, new UnlinkLiveTrain("10", new Headcode("1A01"))).Should().Be("1A01 (uid 10) has no manual link.");
    }

    [Fact]
    public void Only_WTT_trains_can_be_linked()
    {
        var id = new TrainId("1A51#c1");
        var plan = Applied(Plan(), new CloneTrain(Fast, new Headcode("1A51"), 60, id));

        Refused(plan, new LinkLiveTrain("10", new Headcode("1A51"), id)).Should().Be("Only WTT trains can be linked; 1A51#c1 is not in the timetable.");
        Refused(plan, new LinkLiveTrain("", new Headcode("1A51"), Fast)).Should().Be("A live link needs a sim uid.");
    }

    // AcceptSuggestion

    [Fact]
    public void A_suggestion_applies_all_its_steps_with_their_warnings()
    {
        var plan = Applied(Plan(), new AcceptSuggestion("S1", [new SetDwell(Slow, 1, SimTime.Parse("07:09")), new ChangePlatform(Fast, 1, "2")]), out var result);

        plan.Get(Slow).Points[1].Depart.Should().Be(SimTime.Parse("07:09"));
        plan.Get(Fast).Points[1].Platform.Should().Be("2");
        result.Warnings.Should().HaveCount(2);
    }

    [Fact]
    public void A_suggestion_with_a_failing_step_changes_nothing()
    {
        var plan = Plan();
        Refused(plan, new AcceptSuggestion("S1", [new CancelTrain(Fast), new SkipStop(Fast, 2)]))
            .Should().Be("Step 2 of the suggestion failed: 1A01 does not stop at C.");
        Refused(plan, new AcceptSuggestion("S2", [])).Should().Be("The suggestion has no steps.");
    }

    [Fact]
    public void A_command_that_throws_changes_nothing()
    {
        var plan = Plan();

        var act = () => plan.Apply(new AcceptSuggestion("S1", [new CancelTrain(Fast), new Throwing()]));

        act.Should().Throw<InvalidOperationException>();
        plan.Get(Fast).Cancelled.Should().BeFalse();
    }

    private sealed record Throwing : IPlanCommand
    {
        public PlanCommandResult Apply(WorkingPlanBuilder b) => throw new InvalidOperationException("boom");

        public string Describe(WorkingPlan plan) => "Throw";
    }

    // Descriptions

    [Fact]
    public void Commands_describe_themselves_for_the_history()
    {
        var plan = Plan();
        var cases = new (IPlanCommand Command, string Text)[]
        {
            (new ShiftTrain(Fast, 1, 300), "Shift 1A01 by +5 min from B"),
            (new ShiftTrain(Fast, 0, -90), "Shift 1A01 by −1 min 30 s from A"),
            (new SetDwell(Fast, 1, SimTime.Parse("06:08")), "1A01 departs B at 06:08:00"),
            (new ChangePlatform(Fast, 1, "3"), "1A01 to platform 3 at B"),
            (new ChangeLine(Fast, 1, null), "1A01 out of B on line (none)"),
            (new SkipStop(Fast, 1), "1A01 runs through B without stopping"),
            (new AddStop(Fast, 2, 45), "1A01 stops at C for 45 s"),
            (new TerminateAt(Fast, 2), "Terminate 1A01 short at C"),
            (new CancelTrain(Fast), "Cancel 1A01"),
            (new ReinstateTrain(Fast), "Reinstate 1A01"),
            (new CloneTrain(Fast, new Headcode("1A51"), 7200, new TrainId("1A51#c1")), "Clone 1A01 as 1A51 +2 h"),
            (new AddDisruption(Restriction()), "Add speed restriction A–B 06:00:00–07:00:00"),
            (new RemoveDisruption("SR9"), "Remove disruption SR9"),
            (new LinkLiveTrain("10", new Headcode("1A01"), Slow), "Link live 1A01 to 2B01"),
            (new LinkLiveTrain("10", new Headcode("1A01"), null), "Do not match live 1A01"),
            (new UnlinkLiveTrain("10", new Headcode("1A01")), "Match live 1A01 automatically"),
            (new AcceptSuggestion("S1", [new CancelTrain(Fast), new SkipStop(Slow, 1)]), "Accept suggestion: Cancel 1A01; 2B01 runs through B without stopping"),
        };

        foreach (var (command, text) in cases)
        {
            command.Describe(plan).Should().Be(text);
        }
    }

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(300, "5 min")]
    [InlineData(90, "1 min 30 s")]
    [InlineData(7500, "2 h 5 min")]
    [InlineData(-61, "1 min 1 s")]
    public void Durations_read_naturally(int seconds, string text) => PlanText.Duration(seconds).Should().Be(text);
}
