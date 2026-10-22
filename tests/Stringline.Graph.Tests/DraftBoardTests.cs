using System.Xml.Linq;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Gateway.Tests;
using Stringline.Planning;
using Stringline.Timetable.Editing;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>
/// The draft board (S44, <c>08-…md</c> §7): the booking only, booked conflicts found against the original's bookings,
/// ghosts of the original and of a recording, the trains no corridor draws, and drags that make plan commands on a
/// session board and timetable edits on a draft board, in half minutes, held at the least run known unless Shift.
/// </summary>
public sealed class DraftBoardTests
{
    private const string Title = "Draft – Test 1.0.1";

    private static readonly TrainId Express1 = new("1A01");
    private static readonly TrainId Express3 = new("1A03");

    private static double T(string clock) => SimTime.Parse(clock).Seconds;

    /// <summary>1A01 and, ten minutes behind it, 1A03, both A→D passing B and C; 2B02 Up.</summary>
    private static WorkingTimetable Original() => Tt(
        Train("1A01", "XP", Call("A", null, "07:00"), Pass("B", "07:04"), Pass("C", "07:08"), Call("D", "07:12", null)),
        Train("1A03", "XP", Call("A", null, "07:10"), Pass("B", "07:14"), Pass("C", "07:18"), Call("D", "07:22", null)),
        Train("2B02", "OO", Call("D", null, "07:05"), Call("C", "07:10", "07:12"), Pass("B", "07:16"), Call("A", "07:21", null)));

    /// <summary>The draft: 1A03 retimed to run a minute behind 1A01, and 2B02 deleted.</summary>
    private static WorkingTimetable Draft() => Tt(
        Train("1A01", "XP", Call("A", null, "07:00"), Pass("B", "07:04"), Pass("C", "07:08"), Call("D", "07:12", null)),
        Train("1A03", "XP", Call("A", null, "07:01"), Pass("B", "07:05"), Pass("C", "07:09"), Call("D", "07:13", null)));

    private static ForecastNetwork Network(WorkingTimetable tt) => ForecastNetwork.Build(Profile.Corridors, tt);

    private static GraphScene Scene(PlanSnapshot plan, IReadOnlyList<DraftGhost>? originals = null, params LiveTrain[] actuals) =>
        DraftBoard.Build(new DraftBoardRequest(Profile, TestCorridor, plan, Title) { Originals = originals ?? [], Actuals = actuals });

    [Fact]
    public void The_board_draws_the_booking_only_at_full_strength_under_the_drafts_title()
    {
        var tt = Original();
        var plan = DraftBoard.Plan(tt, Network(tt), tt);

        var scene = Scene(plan);

        scene.Title.Should().Be("Draft – Test 1.0.1 — A – D");
        scene.Paths.Should().OnlyContain(p => p.Layer == GraphLayer.Plan && p.Key.StartsWith("draft:", StringComparison.Ordinal) && p.Points != null,
            "the draft is the plan being made: no WTT, actual or forecast lines");
        scene.Paths.Select(p => p.Train).Should().BeEquivalentTo([Express1, Express3, new TrainId("2B02")]);
        scene.LiveHeads.Should().BeEmpty();
        scene.Now.Should().BeNull("a draft has no now line");
        plan.Trains.Should().BeEmpty("a draft has no live data");
        plan.Conflicts.All.Should().BeEmpty("a timetable left as it was is accepted as booked");
    }

    [Fact]
    public void A_train_retimed_close_behind_another_is_a_booked_conflict_against_the_originals_bookings()
    {
        var draft = Draft();

        var plan = DraftBoard.Plan(draft, Network(Original()), Original());

        var conflict = plan.Conflicts.All.Should().ContainSingle(c => c.Kind == ConflictKind.Headway).Subject;
        (conflict.A, conflict.B).Should().Be((Express3, (TrainId?)Express1), "1A03 must give way to 1A01");
        Scene(plan).Conflicts.Should().ContainSingle(c => c.Id == conflict.Id, "it is ringed on the board");

        DraftBoard.Plan(draft, Network(draft)).Conflicts.All.Should().NotContain(c => c.Kind == ConflictKind.Headway,
            "checked against its own bookings, a draft would accept any spacing it books");
    }

    [Fact]
    public void A_run_made_faster_than_the_original_books_any_is_unrealistic()
    {
        var original = Original();
        var draft = Tt(
            Train("1A01", "XP", Call("A", null, "07:00"), Pass("B", "07:02"), Pass("C", "07:08"), Call("D", "07:12", null)),
            original.Trains[1],
            original.Trains[2]);

        var plan = DraftBoard.Plan(draft, Network(original), original, MinRunTable.Build(original));

        plan.Conflicts.All.Should().ContainSingle(c => c.Kind == ConflictKind.Unrealistic && c.A == Express1 && c.Where == new Tiploc("A"));
    }

    [Fact]
    public void Ghosts_show_the_original_of_changed_and_deleted_trains_and_a_recordings_actuals_faintly()
    {
        var original = Original();
        var draft = Draft();
        var changed = DraftBoard.Changed(original, draft);
        changed.Select(g => (g.Original.Id.ToString(), g.Draft?.ToString())).Should().Equal(("1A03", "1A03"), ("2B02", null));

        var actual = Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:01"), ("B", TrainLocationAction.Pass, "07:05"));
        var scene = Scene(DraftBoard.Plan(draft, Network(original), original), changed, actual);

        var ghosts = scene.Paths.Where(p => p.Key.StartsWith("orig:", StringComparison.Ordinal)).ToList();
        ghosts.Should().HaveCount(2).And.OnlyContain(p => p.Layer == GraphLayer.Wtt && p.Points == null, "faint, and never dragged");
        ghosts.Select(p => p.Train).Should().BeEquivalentTo(new TrainId?[] { Express3, null }, "a ghost picks the train it became");
        ghosts.Single(p => p.Train == Express3).Vertices[0].Time.Should().Be(T("07:10"), "where 1A03 ran before");

        var drawn = scene.Paths.Single(p => p.Layer == GraphLayer.Actual);
        drawn.Train.Should().Be(Express1);
        drawn.Colour.A.Should().Be((byte)Math.Round(Express.A * DraftBoard.ActualGhostAlpha));
        scene.Paths.Count(p => p.Layer == GraphLayer.Plan).Should().Be(2);
    }

    [Fact]
    public void Trains_no_corridor_draws_are_listed()
    {
        var tt = Tt(
            Original().Trains[0],
            Train("6X01", null, Call("X", null, "08:00"), Call("Y", "08:20", null)),
            Train("6X03", null, Call("A", null, "08:00"), Call("Y", "08:20", null)));

        DraftBoard.OffCorridors(Profile.Corridors, tt).Select(t => t.Headcode.Value).Should().Equal("6X01", "6X03");
    }

    // ---- Drag targets ----

    [Fact]
    public void A_session_boards_drag_is_a_plan_command()
    {
        var plan = WorkingPlan.FromTimetable(Original());
        var snapshot = new PlanSnapshot(1, plan.Timetable, [], SimClockReading.None, PlanSnapshot.Empty.Matches, 0, null, plan);
        var target = new SessionDragTarget(snapshot, null);

        var preview = Start(target, Express1)!.Nudge(20);

        target.EditFor(preview, out _).Should().Be(new ShiftTrain(Express1, 1, 15), "a session board snaps to 15 s");
    }

    [Fact]
    public void A_draft_boards_drag_is_one_timetable_edit_in_half_minutes_that_undoes_to_the_same_tree()
    {
        var document = WttDocument.Load(Fixtures.Path("waterloo 2010.WTT"));
        var before = document.Root.ToString(SaveOptions.DisableFormatting);
        var import = document.Import();
        var train = import.Timetable.Trains.First(t => t.Points.Count >= 4 && t.Points.All(p => p.IsTimed) && t.EntryTime is null);
        var target = new DraftDragTarget(Snapshot(import.Timetable), import);

        var preview = target.Start(train.Id, PlanDragKind.ShiftFrom, 2, out var refusal)!.Nudge(50);
        refusal.Should().BeNull();
        preview.DeltaSeconds.Should().Be(60, "a draft board snaps to half minutes");
        var edit = target.EditFor(preview, out refusal)!;
        refusal.Should().BeNull();
        edit.Description.Should().Be($"{train.Headcode}: retime from {train.Points[2].Location} by +1 min");

        var undo = edit.Apply(document);
        var after = document.Import().Timetable.Get(train.Id);
        after.Points.Take(2).Select(p => (p.Arrive, p.Depart)).Should().Equal(train.Points.Take(2).Select(p => (p.Arrive, p.Depart)), "points before the grip stay");
        after.Points.Skip(2).Select(p => (p.Arrive, p.Depart)).Should().Equal(train.Points.Skip(2).Select(p => (p.Arrive + 60, p.Depart + 60)));

        target.EditFor(preview, out refusal).Should().BeNull("the draft has changed since the drag began");
        refusal.Should().Contain("changed");

        undo.Apply(document);
        document.Root.ToString(SaveOptions.DisableFormatting).Should().Be(before);
    }

    [Fact]
    public void Moving_a_whole_run_moves_its_entry_time_and_a_dwell_drag_sets_the_departure()
    {
        var document = WttDocument.Load(Fixtures.Path("waterloo 2010.WTT"));
        var import = document.Import();
        var entering = import.Timetable.Trains.First(t => t.EntryTime is not null && t.Points.Count >= 3 && t.Points[0].FirstTime is not null);
        var target = new DraftDragTarget(Snapshot(import.Timetable), import);

        var whole = target.Start(entering.Id, PlanDragKind.Shift, -1, out _)!.Nudge(-30);
        target.EditFor(whole, out _)!.Apply(document);

        var moved = document.Import().Timetable.Get(entering.Id);
        moved.EntryTime.Should().Be(entering.EntryTime - 30);
        moved.Points[0].FirstTime.Should().Be(entering.Points[0].FirstTime - 30);

        import = document.Import();
        static bool IsCall(TimingPoint p) => !p.IsPass && p.Arrive is not null && p.Depart is not null;
        var stopper = import.Timetable.Trains.First(t => t.Points.Take(t.Points.Count - 1).Skip(1).Any(IsCall));
        var stop = stopper.Points.Take(stopper.Points.Count - 1).Skip(1).First(IsCall);
        target = new DraftDragTarget(Snapshot(import.Timetable), import);
        var dwell = target.Start(stopper.Id, PlanDragKind.Dwell, stop.Index, out _)!.Nudge(90);
        target.EditFor(dwell, out _)!.Apply(document);

        document.Import().Timetable.Get(stopper.Id).Points[stop.Index].Depart.Should().Be(stop.Depart + 90);
    }

    [Fact]
    public void A_drag_stops_at_the_least_run_known_and_Shift_lets_it_past_with_a_warning()
    {
        var tt = Original();
        var plan = WorkingPlan.FromTimetable(tt);
        var snapshot = new PlanSnapshot(1, tt, [], SimClockReading.None, PlanSnapshot.Empty.Matches, 0, null, plan);
        var rules = PlanDragRules.Draft with { MinRun = (_, from, to) => from == 0 && to == 1 ? 4 * 60 : null };
        var scene = GraphSceneBuilder.Build(GraphSceneRequest.For(Profile, TestCorridor, snapshot));
        var grips = GraphGrips.For(scene, snapshot, new GraphSelection(Express1, null), rules)!;
        var atB = grips.Grips.Single(g => g.Kind == GraphGripKind.Point && g.Location == new Tiploc("B"));
        var view = GraphViewport.Create(T("06:50"), T("07:30"), 1000, 600);
        var press = view.ToScreen(atB.At.Time, atB.At.Axis);
        var earlier = new ScreenPoint(press.X - (60 * view.PixelsPerSecond), press.Y);

        var held = PlanDrag.Begin(atB, snapshot, view, press, null, out _, rules)!.Update(view, earlier, fine: false);
        (held.DeltaSeconds, held.Limited, held.Command).Should().Be((0, true, null));
        held.Label.Should().EndWith("stopped: minimum run");
        held.Status.Should().Contain("needs at least 4 min");

        var past = PlanDrag.Begin(atB, snapshot, view, press, null, out _, rules)!.Update(view, earlier, fine: true);
        (past.DeltaSeconds, past.Limited).Should().Be((-60, false));
        past.Command.Should().Be(new ShiftTrain(Express1, 1, -60));
        past.Label.Should().EndWith("under minimum run");
        past.Status.Should().Contain("faster than known");
    }

    [Fact]
    public void A_train_SimSig_would_not_run_can_still_be_moved_on_a_draft()
    {
        var tt = Original();
        var plan = WorkingPlan.FromTimetable(tt);
        var notRunning = new Forecast(null, false, [.. plan.Trains.Select(t => new TrainForecast(t.Id, t.Headcode, TrainForecastStatus.NotRunning, [])
        {
            NotRunning = new NotRunningReason(NotRunningKind.OtherSeedGroup, "seeded in group B"),
        })]);
        var snapshot = new PlanSnapshot(1, tt, [], SimClockReading.None, PlanSnapshot.Empty.Matches, 0, null, plan, Forecast: notRunning);

        PlanDrag.Start(snapshot, Express1, PlanDragKind.ShiftFrom, 1, null, out var refusal).Should().BeNull();
        refusal.Should().Contain("not running");
        PlanDrag.Start(snapshot, Express1, PlanDragKind.ShiftFrom, 1, null, out _, PlanDragRules.Draft).Should().NotBeNull();
    }

    /// <summary>A drag from point 1, through the abstraction both kinds of board use.</summary>
    private static PlanDrag? Start<TEdit>(IDragTarget<TEdit> target, TrainId train)
        where TEdit : class => target.Start(train, PlanDragKind.ShiftFrom, 1, out _);

    private static PlanSnapshot Snapshot(WorkingTimetable tt) =>
        new(1, tt, [], SimClockReading.None, PlanSnapshot.Empty.Matches, 0, null, WorkingPlan.FromTimetable(tt));
}
