using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Timetable;
using Stringline.Timetable.Live;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// Which trains SimSig will run from the session's start (S34, #16): seed groups, trains booked before the start,
/// formation chains, a late connection, and "not seen". Modelled on Penrith (groups at 04:00 and 14:00).
/// </summary>
public sealed class StartingTrainsTests
{
    private static readonly SeedGroup Early = new("{G04}", SimTime.FromClock(4, 0), "04:00");
    private static readonly SeedGroup Afternoon = new("{G14}", SimTime.FromClock(14, 0), "14:00");

    private static readonly HashSet<TrainId> None = [];

    private static WorkingTimetable Penrithish() => Of(
        Seeded("175BS", Early, "04:02", "04:20"),
        Seeded("178HS", Afternoon, "14:00", "14:14", Next("178J")),
        Seeded("W595TU", null, "04:00:30", "04:30"),
        Train("W552", start: TrainStart.EntryPoint, entry: "13:58:30", points: [Call("WARIMO", null, "14:01"), Call("BLAKTWN", "14:39", null)]),
        Train("AR85", start: TrainStart.EntryPoint, entry: "14:09", points: [Call("STMARYS", null, "14:10"), Call("VLYHGTS", "14:40", null)]),
        Train("178H", start: TrainStart.EntryPoint, entry: "13:53:48", points: [Call("DONSIDE", null, "13:55"), Call("PENRITH", "14:14:30", null, Next("178J"))]),
        Train("178J", points: [Call("PENRITH", null, "14:20"), Call("EMUPLNS", "14:30", null)]),
        Train("186K", start: TrainStart.EntryPoint, entry: "13:35:48", points: [Call("DONSIDE", null, "13:40"), Call("EMUPLNS", "14:00", null, Next("186L"))]),
        Train("186L", points: [Call("EMUPLNS", null, "14:13:01"), Call("BLAKTWN", "14:41", null, Next("186M"))]),
        Train("186M", points: [Call("BLAKTWN", null, "14:50"), Call("EMUPLNS", "15:20", null)]),
        Train("161J", points: [Call("PENRITH", null, "13:30:01"), Call("BLAKTWN", "13:54", null)]));

    private static Train Seeded(string id, SeedGroup? group, string first, string last, params TrainActivity[] atEnd) =>
        Train(id[..4], id, TrainStart.Seeded, points: [Call("AAA", null, first), Call("BBB", last, null, atEnd)]) with { SeedGroup = group?.Id, Started = true };

    private static WorkingTimetable WithGroups(WorkingTimetable timetable) =>
        new(timetable.Header, timetable.Categories, timetable.Trains, timetable.Rules) { SeedGroups = [Early, Afternoon] };

    private static StartingTrains Work(string? start, string? now = null, params string[] matched) => StartingTrains.Work(
        WithGroups(Penrithish()),
        start is null ? null : SimTime.Parse(start),
        matched.Select(m => new TrainId(m)).ToHashSet(),
        now is null ? null : SimTime.Parse(now));

    private static Dictionary<string, string> Reasons(StartingTrains starting) =>
        starting.NotRunning.ToDictionary(r => r.Key.Value, r => r.Value.Text);

    [Fact]
    public void A_fresh_start_at_a_seed_group_leaves_out_other_groups_and_trains_booked_before_it()
    {
        var starting = Work("14:00:00");

        starting.StartGroup.Should().Be(Afternoon);
        starting.FreshStart.Should().BeTrue();
        Reasons(starting).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["175BS"] = "seed group 04:00",
            ["W595TU"] = "seeded at 04:00",
            ["W552"] = "not seeded", // its entry, 13:58:30, is before the start, though its first point is after
            ["178H"] = "not seeded",
            ["186K"] = "not seeded",
            ["186L"] = "formed from 186K", // booked after the start, but its stock never arrives
            ["186M"] = "formed from 186L", // and so on down the chain
            ["161J"] = "not seeded",
        });
        starting.NotRunning[new TrainId("186L")].Kind.Should().Be(NotRunningKind.FormedFromNotRunning);
        starting.NotRunning[new TrainId("175BS")].Kind.Should().Be(NotRunningKind.OtherSeedGroup);
        starting.ReasonFor(new TrainId("178J")).Should().BeNull("178HS, seeded in the start group, forms it as well as 178H");
        starting.ReasonFor(new TrainId("AR85")).Should().BeNull();
        starting.ReasonFor(new TrainId("178HS")).Should().BeNull();
    }

    [Fact]
    public void A_start_a_few_seconds_after_the_group_is_still_a_fresh_start()
    {
        var starting = Work("14:00:40");
        starting.FreshStart.Should().BeTrue();
        starting.StartTime.Should().Be(SimTime.FromClock(14, 0));
        starting.ReasonFor(new TrainId("161J")).Should().NotBeNull();
    }

    [Fact]
    public void A_train_matched_live_always_runs_and_so_does_what_it_forms()
    {
        var starting = Work("14:00:00", null, "186K", "W552");

        starting.ReasonFor(new TrainId("186K")).Should().BeNull();
        starting.ReasonFor(new TrainId("186L")).Should().BeNull("a running train forms it, whatever its own time");
        starting.ReasonFor(new TrainId("186M")).Should().BeNull();
        starting.ReasonFor(new TrainId("W552")).Should().BeNull();
    }

    [Fact]
    public void A_late_connection_only_leaves_out_other_groups_seeded_trains()
    {
        var starting = Work("16:00:00");

        starting.FreshStart.Should().BeFalse();
        starting.StartGroup.Should().Be(Afternoon, "the latest group before it");
        Reasons(starting).Keys.Should().BeEquivalentTo("175BS", "W595TU");
    }

    [Fact]
    public void A_start_at_the_timetables_start_seeds_the_trains_with_no_group()
    {
        // The test timetable starts at 04:00, as the early group does: a group at the timetable's start seeds with it.
        var starting = Work("04:00:00");

        starting.StartGroup.Should().Be(Early);
        starting.StartTime.Should().Be(SimTime.FromClock(4, 0));
        starting.ReasonFor(new TrainId("W595TU")).Should().BeNull("seeded with no group, at the timetable's start");
        starting.ReasonFor(new TrainId("175BS")).Should().BeNull();
        Reasons(starting)["178HS"].Should().Be("seed group 14:00");
    }

    [Fact]
    public void A_timetable_with_no_groups_started_at_its_start_changes_nothing()
    {
        var timetable = Penrithish(); // no seed groups; the timetable starts at 04:00 and every train is booked after it
        var starting = StartingTrains.Work(timetable, SimTime.FromClock(4, 0), None, SimTime.FromClock(5, 0));

        starting.NotRunning.Should().BeEmpty();
        starting.FreshStart.Should().BeTrue();
    }

    [Fact]
    public void Without_a_session_start_everything_runs()
    {
        var starting = Work(null);
        starting.Should().BeSameAs(StartingTrains.Unknown);
        starting.NotRunning.Should().BeEmpty();
    }

    [Fact]
    public void A_seeded_train_of_the_start_group_not_seen_soon_after_the_start_is_flagged()
    {
        var id = new TrainId("178HS");
        Work("14:00:00", "14:01:00").IsNotSeen(id).Should().BeFalse("it is too soon to tell");
        Work("14:00:00", "14:02:30").IsNotSeen(id).Should().BeTrue();
        Work("14:00:00", "14:02:30", "178HS").IsNotSeen(id).Should().BeFalse("it was seen");
        Work("16:00:00", "16:05:00").IsNotSeen(id).Should().BeFalse("a late connection cannot tell");
    }

    [Fact]
    public void A_formation_loop_is_not_followed_round()
    {
        var timetable = Of(
            Train("1A01", points: [Call("AAA", null, "05:00"), Call("BBB", "05:10", null, Next("1A02"))]),
            Train("1A02", points: [Call("BBB", null, "05:20"), Call("AAA", "05:30", null, Next("1A01"))]));

        var starting = StartingTrains.Work(timetable, SimTime.FromClock(4, 0), None, null);

        starting.NotRunning.Should().BeEmpty();
    }

    [Fact]
    public void The_forecaster_marks_them_not_running_with_no_points_and_run_anyway_brings_one_back()
    {
        var plan = WorkingPlan.FromTimetable(WithGroups(Penrithish()));
        var request = new ForecastRequest(plan) { SessionStart = SimTime.FromClock(14, 0), Now = SimTime.FromClock(14, 0, 30) };

        var forecast = Forecaster.Run(request);

        var w552 = forecast.Find(new TrainId("W552"))!;
        w552.Status.Should().Be(TrainForecastStatus.NotRunning);
        w552.NotRunning!.Text.Should().Be("not seeded");
        w552.Points.Should().BeEmpty();
        forecast.Drawn.Should().NotContain(w552);
        forecast.Find(new TrainId("AR85"))!.Status.Should().Be(TrainForecastStatus.NotStarted);
        forecast.Starting.FreshStart.Should().BeTrue();

        var (after, result) = plan.Apply(new ExpectToRun(new TrainId("186K")));
        result.Applied.Should().BeTrue(result.Error);
        after.RunsAnyway(new TrainId("186K")).Should().BeTrue();
        after.Modified.Should().BeEmpty("running a train anyway does not change its plan");
        var again = Forecaster.Run(request with { Plan = after });
        again.Find(new TrainId("186K"))!.Status.Should().Be(TrainForecastStatus.NotStarted);
        again.Find(new TrainId("186L"))!.Status.Should().Be(TrainForecastStatus.NotStarted, "the train it forms runs too");
        again.Find(new TrainId("W552"))!.Status.Should().Be(TrainForecastStatus.NotRunning);
    }

    [Fact]
    public void Not_running_trains_are_left_out_of_conflicts_and_the_regulation_list()
    {
        // The conflict scenarios' loop at B: 1F01, running late, catches the stopper 2S01, unless 2S01 will not run
        // (it is seeded in the 04:00 group, and the sim started at 14:00).
        var timetable = new WorkingTimetable(Of().Header, [], [
            Train("1F01", points: [Pass("A", "14:10"), Pass("B", "14:14"), Pass("C", "14:18")]),
            Train("2S01", "2S01S", TrainStart.Seeded, points: [Call("A", null, "14:05"), Call("B", "14:09", "14:16"), Call("C", "14:22", null)]) with { SeedGroup = Early.Id },
        ], []) { SeedGroups = [Early, Afternoon] };
        var plan = WorkingPlan.FromTimetable(timetable);
        var network = ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C")], timetable);
        var request = new ForecastRequest(plan)
        {
            Now = SimTime.FromClock(14, 15),
            Network = network,
            LiveTrains = [Running("10", "1F01", Passed("A", "14:15"))],
        };

        var everything = Forecaster.Run(request);
        ConflictDetector.Detect(new ConflictRequest(plan, everything) { Network = network }).Count.Should().BePositive("with both running they clash");
        RegulationList.For(plan, everything, new Tiploc("B"), request.Now).Select(r => r.Train.Value).Should().Contain("2S01S");

        var forecast = Forecaster.Run(request with { SessionStart = SimTime.FromClock(14, 0) });
        forecast.Find(new TrainId("2S01S"))!.Status.Should().Be(TrainForecastStatus.NotRunning);
        ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network }).All.Should().BeEmpty();
        RegulationList.For(plan, forecast, new Tiploc("B"), request.Now).Select(r => r.Train.Value).Should().Equal("1F01");
    }

    [Fact]
    public void Expect_to_run_round_trips_as_json_and_undoes()
    {
        var command = new ExpectToRun(new TrainId("W552"));
        var json = PlanCommandJson.Serialize(command);
        json.Should().Contain("\"type\":\"expectToRun\"");
        PlanCommandJson.Deserialize(json).Should().Be(command);
        PlanCommandJson.Deserialize("""{"type":"expectToRun","train":"W552"}""").Should().Be(command, "run defaults to true");
        PlanCommandJson.Deserialize(PlanCommandJson.Serialize(command with { Run = false })).Should().Be(command with { Run = false });

        var plan = WorkingPlan.FromTimetable(WithGroups(Penrithish()));
        var (on, _) = plan.Apply(command);
        command.Describe(on).Should().Be("Run W552 anyway");
        on.Apply(command).Result.Applied.Should().BeFalse("it is already expected to run");
        var (off, result) = on.Apply(command with { Run = false });
        result.Applied.Should().BeTrue();
        off.RunsAnyway(new TrainId("W552")).Should().BeFalse();
        plan.Apply(new ExpectToRun(new TrainId("NOPE"))).Result.Applied.Should().BeFalse();
    }

    [Fact]
    public void Penriths_14_00_recording_forecasts_none_of_the_04_00_group_and_marks_nothing_that_ran()
    {
        if (Fixtures.LocalPath("Penrith 2017-11-01.WTT") is not { } wtt || Fixtures.LocalPath("penrith-payware.sslog") is not { } log)
        {
            return; // payware: only on Fred's machine
        }

        var timetable = new WttReader().Read(wtt).Timetable;
        timetable.SeedGroups.Select(g => (g.Title, g.StartTime)).Should().Equal(("04:00", SimTime.FromClock(4, 0)), ("14:00", SimTime.FromClock(14, 0)));
        timetable.Get(new TrainId("175BS")).SeedGroup.Should().Be(timetable.SeedGroups[0].Id);
        timetable.Get(new TrainId("175BS")).Started.Should().BeTrue();

        var parser = new MessageParser();
        var state = new LiveState(timetable);
        foreach (var e in SslogRecording.Load(log).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())))
        {
            state.Apply(e);
        }

        var forecast = Forecaster.Run(new ForecastRequest(WorkingPlan.FromTimetable(timetable))
        {
            LiveTrains = state.Trains.ToArray(),
            SessionStart = state.SessionStart,
            Now = state.Clock.Reading.LastReported,
        });

        forecast.Starting.StartGroup!.Title.Should().Be("14:00");
        forecast.Starting.FreshStart.Should().BeTrue();
        var notRunning = forecast.Trains.Where(t => t.Status == TrainForecastStatus.NotRunning).Select(t => t.Train.Value).ToList();
        notRunning.Should().Contain(["W552", "W551", "178H", "186K", "186L", "105J", "1127", "286H"]);
        foreach (var early in new[] { "175BS", "W504S", "W595TU", "3SP7" })
        {
            // Over before 14:00, so left out of the forecast altogether; never forecast either way.
            forecast.Find(new TrainId(early))?.Status.Should().Be(TrainForecastStatus.NotRunning, early);
        }

        var ran = state.Trains.SelectMany(t => t.Workings).Where(w => w.IsMatched).Select(w => w.Train!.Value.Value).ToList();
        ran.Should().HaveCount(9);
        notRunning.Should().NotIntersectWith(ran);
        forecast.Find(new TrainId("AR85"))!.Status.Should().NotBe(TrainForecastStatus.NotRunning);
        forecast.Find(new TrainId("178J"))!.Status.Should().Be(TrainForecastStatus.NotStarted, "178HS forms it");
        forecast.Trains.Should().NotContain(t => t.NotSeen, "every 14:00 seeded train was seen");
    }
}
