using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Timetable.Live;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Timetable.Tests.Live;

public sealed class LiveStateTests
{
    /// <summary>5P09 runs in empty and forms 2P09; two 1A01s; 2X01 at 07:00.</summary>
    private static WorkingTimetable Timetable() => Of(
        Train("5P09", start: TrainStart.EntryPoint, entry: "05:50", points: [Pass("VAUX", "05:55"), Call("WLOO", "06:00", null, Next("2P09"))]),
        Train("2P09", formedFrom: new TrainId("5P09"), points: [Call("WLOO", null, "06:10"), Pass("VAUX", "06:14")]),
        Train("1A01", points: [Call("WLOO", null, "06:00"), Pass("VAUX", "06:04")]),
        Train("1A01", id: "1A01#2", points: [Call("WLOO", null, "06:30"), Pass("VAUX", "06:34")]),
        Train("2X01", points: [Call("WLOO", null, "07:00"), Pass("VAUX", "07:04")]));

    private static LiveState State(params GatewayEvent[] events)
    {
        var state = new LiveState(Timetable());
        foreach (var e in events)
        {
            state.Apply(e);
        }

        return state;
    }

    private static TrainId Id(string id) => new(id);

    [Fact]
    public void Starts_empty()
    {
        var state = State();

        state.TrainCount.Should().Be(0);
        state.SessionStart.Should().BeNull();
        state.Clock.HasTime.Should().BeFalse();
        state.Version.Should().Be(0);
        state.Summarise().Should().BeEquivalentTo(new MatchSummary(0, 0, 0, 0, 0, new Dictionary<MatchMethod, int>()));
        state.Summarise().MatchedShare.Should().Be(1);
    }

    [Fact]
    public void Takes_the_clock_and_the_session_start()
    {
        var state = State(Clock("05:50:00"));

        state.Clock.LastReported.Should().Be(SimTime.Parse("05:50"));
        state.Clock.AreaId.Should().Be("test");
        state.SessionStart.Should().Be(SimTime.Parse("05:50"));
    }

    [Fact]
    public void Ignores_the_placeholder_clock_before_a_sim_starts()
    {
        var state = new LiveState(Timetable());

        state.Apply(new ClockEvent(T0, "test", SimTime.Zero, 1000, false)).Should().BeFalse();

        state.Clock.HasTime.Should().BeFalse();
        state.SessionStart.Should().BeNull();
    }

    [Fact]
    public void A_new_uid_becomes_a_live_train_with_its_actuals()
    {
        var state = State(
            Clock("05:50"),
            Location("31", "5P09", "S12", "05:52"),
            Location("31", "5P09", "VAUX", "05:55"),
            Location("31", "5P09", "WLOO", "06:00", TrainLocationAction.Arrive));

        var train = state.Find("31")!;
        train.SimUid.Should().Be("31");
        train.Headcode.Should().Be(new Headcode("5P09"));
        train.Actuals.Select(a => (a.Location.Code, a.AtTimingPoint)).Should().Equal(("S12", false), ("VAUX", true), ("WLOO", true));
        train.FirstSeen.Should().Be(SimTime.Parse("05:52"));
        train.LastSeen.Should().Be(SimTime.Parse("06:00"));
        train.Last.Action.Should().Be(TrainLocationAction.Arrive);
        train.MatchedTrain.Should().Be(Id("5P09"));
        train.Current.Method.Should().Be(MatchMethod.HeadcodeAndTime);
        state.FindByTrain(Id("5P09")).Should().Be((train, 0));
        state.Find("99").Should().BeNull();
        state.FindByTrain(Id("2X01")).Should().BeNull();
    }

    [Fact]
    public void A_headcode_change_starts_the_next_working()
    {
        var state = State(
            Clock("05:50"),
            Location("31", "5P09", "VAUX", "05:55"),
            Location("31", "5P09", "WLOO", "06:00", TrainLocationAction.Arrive),
            Location("31", "2P09", "WLOO", "06:10", TrainLocationAction.Depart, "11"),
            Location("31", "2P09", "VAUX", "06:14"));

        var train = state.Find("31")!;
        train.Workings.Should().HaveCount(2);
        train.Workings[0].Should().Be(new LiveWorking(new Headcode("5P09"), 0, MatchStatus.Matched, Id("5P09"), MatchMethod.HeadcodeAndTime, 0));
        train.Workings[1].Should().Be(new LiveWorking(new Headcode("2P09"), 2, MatchStatus.Matched, Id("2P09"), MatchMethod.NextWorking));
        train.ActualsOf(0).Select(a => a.Location.Code).Should().Equal("VAUX", "WLOO");
        train.ActualsOf(1).Select(a => a.Location.Code).Should().Equal("WLOO", "VAUX");
        train.ActualsOf(1).First().Platform.Should().Be("11");
        train.WorkingOf(Id("2P09")).Should().Be(1);
        train.WorkingOf(Id("2X01")).Should().Be(-1);
        state.FindByTrain(Id("5P09"))!.Value.Working.Should().Be(0);
        state.FindByTrain(Id("2P09"))!.Value.Working.Should().Be(1);
        train.ToString().Should().Be("uid 31 5P09→2P09");
    }

    [Fact]
    public void A_movement_without_a_headcode_stays_on_the_current_working()
    {
        var state = State(Clock("05:50"), Location("31", "5P09", "VAUX", "05:55"), Location("31", "", "WLOO", "06:00"));

        state.Find("31")!.Workings.Should().ContainSingle();
        state.Find("31")!.Actuals.Should().HaveCount(2);
    }

    [Fact]
    public void Ignores_movements_without_a_uid()
    {
        var state = new LiveState(Timetable());

        state.Apply(Location("", "5P09", "VAUX", "05:55")).Should().BeFalse();

        state.TrainCount.Should().Be(0);
    }

    [Fact]
    public void Pending_workings_match_as_evidence_arrives()
    {
        var state = State(Clock("05:50"), Location("40", "1A01", "S7", "06:25"));

        state.Find("40")!.Current.Status.Should().Be(MatchStatus.Pending);

        state.Apply(Location("40", "1A01", "WLOO", "06:31", TrainLocationAction.Depart));

        state.Find("40")!.MatchedTrain.Should().Be(Id("1A01#2"));
        state.Find("40")!.Current.DeviationSeconds.Should().Be(60);
    }

    [Fact]
    public void Two_uids_with_one_headcode_get_different_trains()
    {
        var state = State(
            Clock("05:50"),
            Location("40", "1A01", "WLOO", "06:00", TrainLocationAction.Depart),
            Location("41", "1A01", "WLOO", "06:05", TrainLocationAction.Depart));

        state.Find("40")!.MatchedTrain.Should().Be(Id("1A01"));
        state.Find("41")!.MatchedTrain.Should().Be(Id("1A01#2"), "1A01 is taken");
    }

    [Fact]
    public void Trains_first_seen_near_the_start_were_present_at_the_start()
    {
        var state = State(Clock("05:50"), Location("1", "5P09", "VAUX", "05:52"), Location("2", "2X01", "WLOO", "07:00", TrainLocationAction.Depart));

        state.Find("1")!.PresentAtStart.Should().BeTrue();
        state.Find("2")!.PresentAtStart.Should().BeFalse();
    }

    [Fact]
    public void Without_a_clock_the_first_movement_starts_the_session()
    {
        var state = State(Location("1", "5P09", "VAUX", "05:55"));

        state.SessionStart.Should().Be(SimTime.Parse("05:55"));
        state.Find("1")!.PresentAtStart.Should().BeTrue();
    }

    [Fact]
    public void Movements_move_the_clock_on()
    {
        var state = State(Clock("05:55"), Location("1", "5P09", "VAUX", "05:55:40"));

        state.Clock.NowAt(T0).Should().Be(SimTime.Parse("05:55:40"));
    }

    [Fact]
    public void Lists_trains_in_the_order_first_seen()
    {
        var state = State(Clock("05:50"), Location("9", "2X01", "WLOO", "07:00"), Location("3", "5P09", "VAUX", "05:55"), Location("9", "2X01", "VAUX", "07:04"));

        state.Trains.Select(t => t.SimUid).Should().Equal("9", "3");
    }

    [Fact]
    public void Summarises_matching()
    {
        var state = State(
            Clock("05:50"),
            Location("31", "5P09", "VAUX", "05:55"),
            Location("31", "2P09", "WLOO", "06:10", TrainLocationAction.Depart),
            Location("40", "1A01", "S7", "06:15"),
            Location("50", "9Z99", "WLOO", "06:00"));

        var summary = state.Summarise();

        summary.Trains.Should().Be(3);
        summary.Workings.Should().Be(4);
        summary.Matched.Should().Be(2);
        summary.Pending.Should().Be(1);
        summary.Unmatched.Should().Be(1);
        summary.MatchedShare.Should().Be(0.5);
        summary.ByMethod.Should().BeEquivalentTo(new Dictionary<MatchMethod, int> { [MatchMethod.HeadcodeAndTime] = 1, [MatchMethod.NextWorking] = 1 });
        state.Find("50")!.Current.Reason.Should().Be("No train 9Z99 in the timetable");
    }

    [Fact]
    public void Links_a_working_by_hand()
    {
        var state = State(Clock("05:50"), Location("50", "9Z99", "WLOO", "06:00"));

        state.Link("50", Id("2X01"));

        var working = state.Find("50")!.Current;
        working.Status.Should().Be(MatchStatus.Matched);
        working.Train.Should().Be(Id("2X01"));
        working.Method.Should().Be(MatchMethod.Manual);
        state.FindByTrain(Id("2X01"))!.Value.Train.SimUid.Should().Be("50");
        state.ManualLinks.Should().ContainKey(("50", new Headcode("9Z99")));
    }

    [Fact]
    public void A_manual_link_takes_the_train_from_another_working()
    {
        var state = State(Clock("05:50"), Location("1", "2X01", "WLOO", "07:00", TrainLocationAction.Depart), Location("50", "9Z99", "WLOO", "07:00"));

        state.Link("50", Id("2X01"));

        state.Find("1")!.Current.Status.Should().Be(MatchStatus.Unmatched);
        state.Find("1")!.Current.Reason.Should().Be("2X01 linked by hand to uid 50");
        state.FindByTrain(Id("2X01"))!.Value.Train.SimUid.Should().Be("50");

        state.Apply(Location("1", "2X01", "VAUX", "07:04"));
        state.Find("1")!.Current.Status.Should().Be(MatchStatus.Unmatched, "it is not matched again automatically");
    }

    [Fact]
    public void A_manual_link_replaces_an_automatic_match()
    {
        var state = State(Clock("05:50"), Location("40", "1A01", "WLOO", "06:00", TrainLocationAction.Depart));

        state.Link("40", Id("1A01#2"));

        state.Find("40")!.MatchedTrain.Should().Be(Id("1A01#2"));
        state.FindByTrain(Id("1A01")).Should().BeNull();
    }

    [Fact]
    public void Unlinking_by_hand_leaves_it_unmatched()
    {
        var state = State(Clock("05:50"), Location("40", "1A01", "WLOO", "06:00", TrainLocationAction.Depart));

        state.Link("40", null);
        state.Apply(Location("40", "1A01", "VAUX", "06:04"));

        state.Find("40")!.Current.Status.Should().Be(MatchStatus.Unmatched);
        state.Find("40")!.Current.Reason.Should().Be("Unlinked by hand");
        state.FindByTrain(Id("1A01")).Should().BeNull();
    }

    [Fact]
    public void A_link_made_before_the_train_appears_applies_when_it_does()
    {
        var state = new LiveState(Timetable());
        state.Link("50", new Headcode("9Z99"), Id("2X01"));

        state.Apply(Location("50", "9Z99", "WLOO", "07:00"));

        state.Find("50")!.Current.Method.Should().Be(MatchMethod.Manual);
        state.Find("50")!.MatchedTrain.Should().Be(Id("2X01"));
    }

    [Fact]
    public void Clearing_a_link_matches_automatically_again()
    {
        var state = State(Clock("05:50"), Location("40", "1A01", "WLOO", "06:00", TrainLocationAction.Depart));
        state.Link("40", Id("1A01#2"));

        state.ClearLink("40", new Headcode("1A01"));

        state.ManualLinks.Should().BeEmpty();
        state.Find("40")!.MatchedTrain.Should().Be(Id("1A01"));
        state.Find("40")!.Current.Method.Should().Be(MatchMethod.HeadcodeAndTime);
    }

    [Fact]
    public void Clearing_a_link_that_does_not_exist_does_nothing()
    {
        var state = State(Clock("05:50"));
        var version = state.Version;

        state.ClearLink("40", new Headcode("1A01"));

        state.Version.Should().Be(version);
    }

    [Fact]
    public void Linking_rejects_unknown_trains_and_uids()
    {
        var state = State(Clock("05:50"), Location("40", "1A01", "WLOO", "06:00"));

        state.Invoking(s => s.Link("40", Id("NOPE"))).Should().Throw<ArgumentException>();
        state.Invoking(s => s.Link("41", Id("2X01"))).Should().Throw<ArgumentException>();
        state.Invoking(s => s.Link("", new Headcode("1A01"), Id("2X01"))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_rewind_drops_everything_but_the_manual_links()
    {
        var state = State(Clock("05:50"), Location("50", "9Z99", "WLOO", "07:00"), new TdStepEvent(T0, TdStepKind.Interpose, "test", "", "0101", "9Z99", null));
        state.Link("50", Id("2X01"));

        state.Apply(new ReplayRewoundEvent(T0, SimTime.Parse("05:00"))).Should().BeTrue();

        state.TrainCount.Should().Be(0);
        state.Clock.HasTime.Should().BeFalse();
        state.SessionStart.Should().BeNull();
        state.Berths.Should().BeEmpty();
        state.FindByTrain(Id("2X01")).Should().BeNull();
        state.ManualLinks.Should().ContainSingle();

        state.Apply(Location("50", "9Z99", "WLOO", "07:00"));
        state.Find("50")!.MatchedTrain.Should().Be(Id("2X01"));
    }

    [Fact]
    public void Reset_clears_the_state()
    {
        var state = State(Clock("05:50"), Location("50", "9Z99", "WLOO", "07:00"));
        var version = state.Version;

        state.Reset();

        state.TrainCount.Should().Be(0);
        state.Version.Should().BeGreaterThan(version);
    }

    [Fact]
    public void Keeps_a_berth_map_from_td_steps()
    {
        var state = State(
            new TdStepEvent(T0, TdStepKind.Interpose, "test", "", "0101", "1A01", null),
            new TdStepEvent(T0, TdStepKind.Interpose, "test", "", "0200", "2X01", null),
            new TdStepEvent(T0, TdStepKind.Step, "test", "0101", "0103", "1A01", null),
            new TdStepEvent(T0, TdStepKind.Cancel, "test", "0200", "", "", null),
            new TdStepEvent(T0, TdStepKind.Interpose, "test", "", "", "5Z99", null));

        state.Berths.Should().BeEquivalentTo(new Dictionary<string, string> { ["0103"] = "1A01" });
    }

    [Fact]
    public void An_interpose_without_a_description_clears_the_berth()
    {
        var state = State(
            new TdStepEvent(T0, TdStepKind.Interpose, "test", "", "0101", "1A01", null),
            new TdStepEvent(T0, TdStepKind.Interpose, "test", "", "0101", "", null));

        state.Berths.Should().BeEmpty();
    }

    [Fact]
    public void Keeps_the_latest_entry_delay_per_headcode()
    {
        var state = State(new TrainDelayEvent(T0, new Headcode("2F02"), "", 0), new TrainDelayEvent(T0, new Headcode("2F02"), "", -173));

        state.EntryDelayHint(new Headcode("2F02")).Should().Be(-173);
        state.EntryDelayHint(new Headcode("1A01")).Should().BeNull();
        state.Apply(new TrainDelayEvent(T0, default, "", 60)).Should().BeFalse();
    }

    [Fact]
    public void Keeps_the_latest_timetable_summary()
    {
        var summary = new TimetableSummaryEvent(T0, "/topic/TRAIN_MVT_SUMMARY", []);

        State(summary).LastSummary.Should().BeSameAs(summary);
    }

    [Fact]
    public void Other_events_change_nothing()
    {
        var state = new LiveState(Timetable());

        state.Apply(new UnknownEvent(T0, "/topic/x", "foo", "{}", "unknown")).Should().BeFalse();
        state.Apply(new SignallingStateEvent(T0, "test", SignallingObjectType.Track, "track", "T1", null, new Dictionary<string, string>())).Should().BeFalse();

        state.Version.Should().Be(0);
    }

    [Fact]
    public void The_version_goes_up_on_each_change()
    {
        var state = new LiveState(Timetable());

        state.Apply(Clock("05:50"));
        state.Apply(Location("1", "5P09", "VAUX", "05:55"));

        state.Version.Should().Be(2);
    }

    [Fact]
    public void Live_trains_are_immutable_snapshots()
    {
        var state = State(Clock("05:50"), Location("31", "5P09", "VAUX", "05:55"));
        var before = state.Find("31")!;

        state.Apply(Location("31", "5P09", "WLOO", "06:00", TrainLocationAction.Arrive));

        before.Actuals.Should().HaveCount(1);
        state.Find("31")!.Actuals.Should().HaveCount(2);
    }
}
