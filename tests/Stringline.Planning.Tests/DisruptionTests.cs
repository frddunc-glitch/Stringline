using Stringline.Core;
using Stringline.Network;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// S13: which trains a disruption's track applies to (Fred: a corridor track by direction, or a WTT line code), notes
/// as plan commands, and the track choices offered when making a disruption.
/// </summary>
public sealed class DisruptionTests
{
    private static readonly TrainId Down = new("2D01");
    private static readonly TrainId Up = new("2U01");

    /// <summary>A–B–C: 2D01 runs Down (A to C) on line DF out of A, 2U01 Up (C to A) on UF out of C.</summary>
    private static WorkingPlan Plan() => WorkingPlan.FromTimetable(Of(
        Train("2D01", points: [Call("A", null, "07:00") with { Line = "DF" }, Call("B", "07:05", "07:06") with { Line = "DF" }, Call("C", "07:10", null)]),
        Train("2U01", points: [Call("C", null, "07:00") with { Line = "UF" }, Call("B", "07:04", "07:05") with { Line = "US" }, Call("A", "07:10", null)])));

    private static WorkingPlan Applied(WorkingPlan plan, IPlanCommand command)
    {
        var (after, result) = plan.Apply(command);
        result.Applied.Should().BeTrue(result.Error);
        return after;
    }

    private static string Refused(WorkingPlan plan, IPlanCommand command)
    {
        var (after, result) = plan.Apply(command);
        result.Applied.Should().BeFalse();
        after.Should().BeSameAs(plan);
        return result.Error!;
    }

    private static Disruption Block(string? track = null, TrackDirection? direction = null, string from = "A", string to = "C") =>
        new("B1", DisruptionKind.Block, new Tiploc(from), new Tiploc(to), track, SimTime.Parse("06:50"), SimTime.Parse("07:30"), Direction: direction);

    /// <summary>The forecast departure from each train's origin: held to 07:30 by the block, or not.</summary>
    private static (string Down, string Up) Departures(Disruption block)
    {
        var forecast = Run(Applied(Plan(), new AddDisruption(block)));
        return (forecast.Find(Down)!.Points[0].Depart!.Value.ToString(), forecast.Find(Up)!.Points[0].Depart!.Value.ToString());
    }

    [Fact]
    public void A_block_on_all_lines_holds_trains_both_ways() =>
        Departures(Block()).Should().Be(("07:30:00", "07:30:00"));

    [Fact]
    public void A_block_on_a_corridor_track_holds_only_trains_running_its_way()
    {
        Departures(Block("Down", TrackDirection.Down)).Should().Be(("07:30:00", "07:00:00"));
        Departures(Block("Up", TrackDirection.Up)).Should().Be(("07:00:00", "07:30:00"));
        Departures(Block("Single", TrackDirection.BiDirectional)).Should().Be(("07:30:00", "07:30:00"));
    }

    [Fact]
    public void A_block_on_a_WTT_line_holds_only_trains_planned_on_that_line()
    {
        Departures(Block("df")).Should().Be(("07:30:00", "07:00:00"), "line codes compare without case");
        Departures(Block("UF")).Should().Be(("07:00:00", "07:30:00"));
        Departures(Block("DS")).Should().Be(("07:00:00", "07:00:00"));
    }

    [Fact]
    public void A_line_code_is_the_line_out_of_the_point_the_run_starts_from()
    {
        // 2U01 leaves B on US: a block on US between B and A holds it there, not at C.
        var forecast = Run(Applied(Plan(), new AddDisruption(Block("US", from: "A", to: "B"))));

        var up = forecast.Find(Up)!;
        up.Points[0].Depart.Should().Be(SimTime.Parse("07:00"));
        up.Points[1].Depart.Should().Be(SimTime.Parse("07:30"));
    }

    [Fact]
    public void A_direction_at_one_location_applies_to_every_train()
    {
        var b = Block("Down", TrackDirection.Down, from: "B", to: "B");

        Departures(b).Should().Be(("07:30:00", "07:30:00"));
    }

    [Fact]
    public void A_speed_restriction_on_a_track_lengthens_only_runs_on_it()
    {
        var restriction = new Disruption("SR1", DisruptionKind.SpeedRestriction, new Tiploc("A"), new Tiploc("C"), "Up", SimTime.Parse("06:00"),
            SimTime.Parse("08:00"), RunTimeFactor: 3, Direction: TrackDirection.Up);
        var plan = Applied(Plan(), new AddDisruption(restriction));
        var points = plan.Get(Up).Points;

        plan.MinRuns.Along(plan.Get(Down).Points, 0, 1, SimTime.Parse("07:00"), plan.Disruptions).Should().Be(plan.MinRuns.Along(plan.Get(Down).Points, 0, 1));
        plan.MinRuns.Along(points, 0, 1, SimTime.Parse("07:00"), plan.Disruptions).Should().Be(3 * plan.MinRuns.Along(points, 0, 1));

        var forecast = Run(plan);
        forecast.Find(Down)!.Points[1].Arrive.Should().Be(SimTime.Parse("07:05"));
        forecast.Find(Up)!.Points[1].Arrive.Should().Be(SimTime.Parse("07:12"), "C–B at three times its 4 min minimum");
    }

    [Fact]
    public void A_track_must_have_a_name_and_a_direction_needs_one()
    {
        Refused(Plan(), new AddDisruption(Block(" "))).Should().Be("Give the track a name, or none for all lines.");
        Refused(Plan(), new AddDisruption(Block(direction: TrackDirection.Down))).Should().Be("A track direction needs the track's name.");
    }

    [Fact]
    public void Disruptions_describe_their_track()
    {
        new AddDisruption(Block("Down", TrackDirection.Down)).Describe(Plan()).Should().Be("Add block A–C (Down) 06:50:00–07:30:00");
        Block("UF").ToString().Should().Be("Block A–C UF 06:50:00–07:30:00");
    }

    [Fact]
    public void New_IDs_are_the_first_free_number()
    {
        var plan = Applied(Plan(), new AddDisruption(Block()));
        plan.NewDisruptionId(DisruptionKind.Block).Should().Be("B2");
        plan.NewDisruptionId(DisruptionKind.SpeedRestriction).Should().Be("SR1");
        plan.NewNoteId().Should().Be("N1");
        Applied(plan, new AddNote(Note("N1"))).NewNoteId().Should().Be("N2");
    }

    // Notes

    private static PlanNote Note(string id = "N1", string text = "Driver relieved here") => new(id, new Tiploc("B"), SimTime.Parse("07:05"), text);

    [Fact]
    public void Notes_are_added_changed_and_removed()
    {
        var plan = Applied(Plan(), new AddNote(Note()));
        plan.Notes.Should().ContainSingle().Which.Text.Should().Be("Driver relieved here");
        plan.FindNote("N1").Should().Be(Note());
        Refused(plan, new AddNote(Note())).Should().Be("There is already a note N1.");

        plan = Applied(plan, new EditNote(Note(text: "Relief cancelled")));
        plan.FindNote("N1")!.Text.Should().Be("Relief cancelled");
        Refused(plan, new EditNote(Note(text: "Relief cancelled"))).Should().Be("This makes no change to the note.");
        Refused(plan, new EditNote(Note("N9"))).Should().Be("No note N9.");

        plan = Applied(plan, new RemoveNote("N1"));
        plan.Notes.Should().BeEmpty();
        Refused(plan, new RemoveNote("N1")).Should().Be("No note N1.");
    }

    [Fact]
    public void A_note_needs_text_of_a_sensible_length()
    {
        Refused(Plan(), new AddNote(Note(text: "  "))).Should().Be("A note needs some text.");
        Refused(Plan(), new AddNote(Note(id: ""))).Should().Be("A note needs an ID.");
        Refused(Plan(), new AddNote(Note(text: new string('x', PlanNote.MaxLength + 1)))).Should().Be("A note can be at most 500 characters.");
    }

    [Fact]
    public void Notes_do_not_change_trains_or_the_forecast()
    {
        var plan = Applied(Plan(), new AddNote(Note()));

        plan.Modified.Should().BeEmpty();
        Times(Run(plan).Find(Down)!).Should().Equal(Times(Run(Plan()).Find(Down)!));
    }

    [Fact]
    public void Notes_describe_themselves_by_their_first_line()
    {
        var plan = Applied(Plan(), new AddNote(Note(text: "A rather long note about the relief arrangements\nand a second line")));

        new RemoveNote("N1").Describe(plan).Should().Be("Remove note at B 07:05:00: “A rather long note about the…”");
        new RemoveNote("N7").Describe(plan).Should().Be("Remove note N7");
        Note().Summary(10).Should().Be("Driver re…");
    }

    // Track choices

    [Fact]
    public void Track_choices_are_all_lines_then_the_corridor_tracks_then_WTT_lines_most_used_first()
    {
        var corridor = Line("main", 120, false, "A", "B", "C");

        var choices = DisruptionTracks.For(corridor, new Tiploc("C"), new Tiploc("A"), Plan());

        choices.Select(c => c.Label).Should().Equal("All lines", "Up (Up trains)", "Down (Down trains)", "DF (WTT line)", "UF (WTT line)", "US (WTT line)");
        choices[1].Should().Be(new TrackChoice("Up", TrackDirection.Up, "Up (Up trains)"));
        choices[3].Direction.Should().BeNull();
    }

    [Fact]
    public void Track_choices_cover_only_lines_used_between_the_ends()
    {
        var corridor = Line("single", 120, true, "A", "B", "C");

        DisruptionTracks.For(corridor, new Tiploc("A"), new Tiploc("B"), Plan()).Select(c => c.Label)
            .Should().Equal("All lines", "Single (both ways)", "DF (WTT line)", "US (WTT line)");
        DisruptionTracks.For(corridor, new Tiploc("A"), new Tiploc("X"), Plan()).Should().Equal(TrackChoice.AllLines);
        TrackChoice.Of("UF", null).Label.Should().Be("UF (WTT line)");
    }
}
