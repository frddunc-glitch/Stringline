using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Timetable.Live;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Timetable.Tests.Live;

public sealed class TrainMatcherTests
{
    private static readonly HashSet<TrainId> None = [];

    private static MatchDecision Match(WorkingTimetable tt, string headcode, params ActualEvent[] actuals) =>
        new TrainMatcher(tt).Match(new MatchRequest(new Headcode(headcode), actuals, None));

    /// <summary>Two 1A01s on the same path, at 06:00 and 09:00.</summary>
    private static WorkingTimetable TwoServices() => Of(
        Train("1A01", points: [Call("AAA", null, "06:00"), Pass("BBB", "06:10"), Call("CCC", "06:20", null)]),
        Train("1A01", id: "1A01#2", points: [Call("AAA", null, "09:00"), Pass("BBB", "09:10"), Call("CCC", "09:20", null)]));

    [Fact]
    public void Knows_timing_points_from_signals()
    {
        var matcher = new TrainMatcher(TwoServices());

        matcher.IsTimingPoint(new Tiploc("BBB")).Should().BeTrue();
        matcher.IsTimingPoint(new Tiploc("S123")).Should().BeFalse();
    }

    [Fact]
    public void Without_a_headcode_nothing_matches()
    {
        var decision = new TrainMatcher(TwoServices()).Match(new MatchRequest(default, [Actual("AAA", "06:00")], None));

        decision.Status.Should().Be(MatchStatus.Unmatched);
        decision.Reason.Should().Be("No headcode");
    }

    [Fact]
    public void An_unknown_headcode_is_unmatched()
    {
        var decision = Match(TwoServices(), "2B02", Actual("AAA", "06:00"));

        decision.Status.Should().Be(MatchStatus.Unmatched);
        decision.Reason.Should().Be("No train 2B02 in the timetable");
    }

    [Fact]
    public void Headcodes_match_exactly_including_suffixes()
    {
        var tt = Of(Train("2O03", points: [Pass("AAA", "06:00")]), Train("2O03-1", points: [Pass("AAA", "06:01")]));

        Match(tt, "2O03-1", Actual("AAA", "06:00")).Train.Should().Be(new TrainId("2O03-1"));
    }

    [Fact]
    public void Taken_trains_are_not_candidates()
    {
        var tt = Of(Train("1A01", points: [Pass("AAA", "06:00")]));
        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("1A01"), [Actual("AAA", "06:00")], new HashSet<TrainId> { new("1A01") }));

        decision.Status.Should().Be(MatchStatus.Unmatched);
        decision.Reason.Should().Be("Every 1A01 in the timetable is already matched");
    }

    [Fact]
    public void No_movements_is_pending()
    {
        Match(TwoServices(), "1A01").Status.Should().Be(MatchStatus.Pending);
    }

    [Fact]
    public void Picks_the_service_nearest_in_time()
    {
        var decision = Match(TwoServices(), "1A01", Actual("BBB", "09:12"));

        decision.Status.Should().Be(MatchStatus.Matched);
        decision.Train.Should().Be(new TrainId("1A01#2"));
        decision.Method.Should().Be(MatchMethod.HeadcodeAndTime);
        decision.DeviationSeconds.Should().Be(120);
        decision.Candidates.Select(c => c.Train.Value).Should().Equal("1A01#2", "1A01");
    }

    [Fact]
    public void A_single_candidate_matches_on_a_signal_alone()
    {
        var tt = Of(Train("1A01", start: TrainStart.EntryPoint, entry: "05:55", points: [Pass("BBB", "06:10")]));

        var decision = Match(tt, "1A01", Actual("S12", "05:56", atTimingPoint: false));

        decision.Status.Should().Be(MatchStatus.Matched);
        decision.Train.Should().Be(new TrainId("1A01"));
        decision.DeviationSeconds.Should().BeNull("no timing point has been reported");
    }

    [Fact]
    public void Several_candidates_wait_for_a_timing_point()
    {
        var tt = Of(
            Train("1A01", points: [Call("AAA", null, "06:00"), Pass("BBB", "06:10")]),
            Train("1A01", id: "1A01#2", points: [Call("AAA", null, "06:40"), Pass("BBB", "06:50")]));

        var decision = Match(tt, "1A01", Actual("S12", "06:25", atTimingPoint: false));

        decision.Status.Should().Be(MatchStatus.Pending);
        decision.Reason.Should().Be("2 candidates; waiting for a timing point");
    }

    [Fact]
    public void A_signal_far_outside_every_booked_span_is_unmatched()
    {
        var decision = Match(TwoServices(), "1A01", Actual("S12", "15:00", atTimingPoint: false));

        decision.Status.Should().Be(MatchStatus.Unmatched);
    }

    [Fact]
    public void Outside_the_window_is_unmatched()
    {
        var decision = Match(TwoServices(), "1A01", Actual("BBB", "11:00"));

        decision.Status.Should().Be(MatchStatus.Unmatched);
        decision.Reason.Should().Be("No 1A01 booked within 60 min (nearest 110 min off)");
        decision.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public void The_window_is_configurable()
    {
        var tt = TwoServices();
        var decision = new TrainMatcher(tt, new MatcherOptions { WindowSeconds = 600 }).Match(new MatchRequest(new Headcode("1A01"), [Actual("BBB", "06:25")], None));

        decision.Status.Should().Be(MatchStatus.Unmatched);
    }

    [Fact]
    public void Close_candidates_stay_pending_then_the_best_is_taken()
    {
        var tt = Of(
            Train("1A01", points: [Call("AAA", null, "06:00"), Pass("BBB", "06:10"), Call("CCC", "06:20", null)]),
            Train("1A01", id: "1A01#2", points: [Call("AAA", null, "06:00:30"), Pass("BBB", "06:10:30"), Call("CCC", "06:20:30", null)]));

        var one = Match(tt, "1A01", Actual("AAA", "06:00", TrainLocationAction.Depart));
        one.Status.Should().Be(MatchStatus.Pending);
        one.Reason.Should().Be("2 candidates within 60 s of each other");

        var three = Match(tt, "1A01", Actual("AAA", "06:00", TrainLocationAction.Depart), Actual("BBB", "06:10"), Actual("CCC", "06:20", TrainLocationAction.Arrive));
        three.Status.Should().Be(MatchStatus.Matched);
        three.Train.Should().Be(new TrainId("1A01"));
        three.Reason.Should().Be("Close call: 1A01#2 scored within 30 s");
    }

    [Fact]
    public void Ties_go_to_file_order()
    {
        var tt = Of(
            Train("1A01", id: "X", points: [Pass("AAA", "06:00")]),
            Train("1A01", id: "Y", points: [Pass("AAA", "06:00")]));

        Match(tt, "1A01", Actual("AAA", "06:00"), Actual("AAA", "06:00"), Actual("AAA", "06:00")).Train.Should().Be(new TrainId("X"));
    }

    [Fact]
    public void Alternative_paths_are_told_apart_by_location()
    {
        var tt = Of(
            Train("1A01", id: "MAIN", points: [Pass("AAA", "06:00"), Pass("BBB", "06:10")]),
            Train("1A01", id: "SLOW", points: [Pass("AAA", "06:00"), Pass("SSS", "06:12")]));

        var decision = Match(tt, "1A01", Actual("AAA", "06:00"), Actual("SSS", "06:12"));

        decision.Train.Should().Be(new TrainId("SLOW"));
        decision.Candidates.Single(c => c.Train.Value == "MAIN").Should().BeEquivalentTo(new { OffPath = 1, Score = 120 });
    }

    [Fact]
    public void An_early_arrival_at_an_origin_costs_nothing()
    {
        // Trains in the sim at the start report arrive at their origin long before they are due away.
        var tt = Of(Train("2D03", points: [Call("WLOO", null, "05:47"), Pass("VAUX", "05:51")]));

        var decision = Match(tt, "2D03", Actual("WLOO", "04:30:10", TrainLocationAction.Arrive));

        decision.Status.Should().Be(MatchStatus.Matched);
        decision.DeviationSeconds.Should().Be(0);
    }

    [Fact]
    public void A_late_arrival_at_an_origin_counts()
    {
        var tt = Of(Train("2D03", points: [Call("WLOO", null, "05:47")]));

        Match(tt, "2D03", Actual("WLOO", "05:50", TrainLocationAction.Arrive)).DeviationSeconds.Should().Be(180);
    }

    [Fact]
    public void Uses_the_arrival_or_departure_to_suit_the_action()
    {
        var tt = Of(Train("1A01", points: [Call("AAA", "06:00", "06:10")]));

        Match(tt, "1A01", Actual("AAA", "06:01", TrainLocationAction.Arrive)).DeviationSeconds.Should().Be(60);
        Match(tt, "1A01", Actual("AAA", "06:11", TrainLocationAction.Depart)).DeviationSeconds.Should().Be(60);
    }

    [Fact]
    public void A_second_call_at_the_same_place_uses_the_nearer_one()
    {
        var tt = Of(Train("1A01", points: [Call("AAA", null, "06:00"), Pass("BBB", "06:10"), Call("AAA", "06:30", null)]));

        Match(tt, "1A01", Actual("AAA", "06:31", TrainLocationAction.Arrive)).DeviationSeconds.Should().Be(60);
    }

    [Fact]
    public void Untimed_points_are_not_evidence()
    {
        var tt = Of(Train("1A01", start: TrainStart.EntryPoint, entry: "06:00", points: [Call("AAA", null, null), Pass("BBB", "06:10")]));

        var decision = Match(tt, "1A01", Actual("AAA", "06:05"));

        decision.Status.Should().Be(MatchStatus.Matched);
        decision.Candidates.Single().TimingPoints.Should().Be(0);
        decision.Candidates.Single().OffPath.Should().Be(0);
    }

    [Theory]
    [InlineData(true, "SEEDED")]
    [InlineData(false, "LATER")]
    public void Seeded_versions_suit_trains_there_from_the_start(bool presentAtStart, string expected)
    {
        // Penrith: a seeded and an unseeded 105J, booked away from PENRITH 29 s apart, for different start times.
        var tt = Of(
            Train("105J", id: "LATER", points: [Call("PENRITH", null, "14:00:01"), Pass("EMUPLNS", "14:05")]),
            Train("105J", id: "SEEDED", start: TrainStart.Seeded, points: [Call("PENRITH", null, "14:00:30"), Pass("EMUPLNS", "14:05:30")]));

        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("105J"), [Actual("PENRITH", "14:00:15", TrainLocationAction.Depart)], None, presentAtStart));

        decision.Train.Should().Be(new TrainId(expected));
    }

    [Fact]
    public void A_new_headcode_on_the_same_uid_follows_the_next_working()
    {
        var tt = Of(
            Train("5P09", points: [Call("WLOO", "06:00", null, Next("2P09"))]),
            Train("2P09", formedFrom: new TrainId("5P09"), points: [Call("WLOO", null, "06:10")]),
            Train("2P09", id: "2P09#2", points: [Call("WLOO", null, "06:10")]));

        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("2P09"), [Actual("WLOO", "06:11", TrainLocationAction.Depart)], new HashSet<TrainId> { new("5P09") }, PreviousTrain: new TrainId("5P09")));

        decision.Status.Should().Be(MatchStatus.Matched);
        decision.Train.Should().Be(new TrainId("2P09"));
        decision.Method.Should().Be(MatchMethod.NextWorking);
    }

    [Fact]
    public void A_new_headcode_that_is_not_the_next_working_is_matched_normally()
    {
        var tt = Of(
            Train("5P09", points: [Call("WLOO", "06:00", null, Next("2P09"))]),
            Train("2P09", points: [Call("WLOO", null, "06:10")]),
            Train("2X99", points: [Call("WLOO", null, "06:15")]));

        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("2X99"), [Actual("WLOO", "06:15", TrainLocationAction.Depart)], None, PreviousTrain: new TrainId("5P09")));

        decision.Train.Should().Be(new TrainId("2X99"));
        decision.Method.Should().Be(MatchMethod.HeadcodeAndTime);
    }

    [Fact]
    public void A_taken_next_working_is_not_followed()
    {
        var tt = Of(
            Train("5P09", points: [Call("WLOO", "06:00", null, Next("2P09"))]),
            Train("2P09", points: [Call("WLOO", null, "06:10")]));

        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("2P09"), [Actual("WLOO", "06:10", TrainLocationAction.Depart)], new HashSet<TrainId> { new("2P09") }, PreviousTrain: new TrainId("5P09")));

        decision.Status.Should().Be(MatchStatus.Unmatched);
    }

    [Fact]
    public void The_same_uid_can_continue_as_a_divided_portion()
    {
        var tt = Of(
            Train("1W51", points: [Call("WLOO", "05:00", null, Forms(ActivityKind.DivideRear, "2P05"))]),
            Train("2P05", formedFrom: new TrainId("1W51"), points: [Call("WLOO", null, "05:10")]));

        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("2P05"), [Actual("WLOO", "05:10", TrainLocationAction.Depart)], None, PreviousTrain: new TrainId("1W51")));

        decision.Train.Should().Be(new TrainId("2P05"));
        decision.Method.Should().Be(MatchMethod.Formed);
    }

    [Fact]
    public void A_new_uid_formed_from_a_matched_train_is_marked_formed()
    {
        var tt = Of(
            Train("1W51", points: [Call("WLOO", null, "05:30", Forms(ActivityKind.DivideFront, "2P05"))]),
            Train("2P05", formedFrom: new TrainId("1W51"), points: [Call("WLOO", null, "05:00")]));

        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("2P05"), [Actual("WLOO", "05:01", TrainLocationAction.Depart)], new HashSet<TrainId> { new("1W51") }));

        decision.Train.Should().Be(new TrainId("2P05"));
        decision.Method.Should().Be(MatchMethod.Formed);
    }

    [Fact]
    public void An_unknown_previous_train_is_ignored()
    {
        var tt = Of(Train("2P09", points: [Call("WLOO", null, "06:10")]));

        var decision = new TrainMatcher(tt).Match(new MatchRequest(new Headcode("2P09"), [Actual("WLOO", "06:10", TrainLocationAction.Depart)], None, PreviousTrain: new TrainId("NOPE")));

        decision.Method.Should().Be(MatchMethod.HeadcodeAndTime);
    }

    [Fact]
    public void Is_pure()
    {
        var tt = TwoServices();
        var request = new MatchRequest(new Headcode("1A01"), [Actual("BBB", "06:12")], None);

        new TrainMatcher(tt).Match(request).Should().BeEquivalentTo(new TrainMatcher(tt).Match(request));
    }

    /// <summary>An untimed point, as the WTT books a train the signaller brings on when wanted.</summary>
    private static TimingPoint Untimed(string location) => new() { Index = 0, Location = new Tiploc(location) };

    [Fact]
    public void An_untimed_train_is_matched_by_headcode_alone()
    {
        // Moss Vale's 3332 (#21): a grain train booked with no times at all.
        var tt = Of(Train("3332", start: TrainStart.EntryPoint, points: [Untimed("JINDLEE"), Untimed("HARDEN"), Untimed("YASSJ")]));

        var decision = Match(tt, "3332", Actual("S12", "15:59", atTimingPoint: false));

        decision.Status.Should().Be(MatchStatus.Matched);
        decision.Train.Should().Be(new TrainId("3332"));
        decision.Method.Should().Be(MatchMethod.Untimed);
        decision.DeviationSeconds.Should().BeNull();
    }

    [Fact]
    public void A_timed_train_within_the_window_wins_over_an_untimed_one()
    {
        var tt = Of(
            Train("3332", id: "3332U", points: [Untimed("AAA"), Untimed("BBB")]),
            Train("3332", points: [Pass("AAA", "10:00"), Pass("BBB", "10:10")]));

        Match(tt, "3332", Actual("AAA", "10:20")).Should().BeEquivalentTo(new { Train = new TrainId("3332"), Method = MatchMethod.HeadcodeAndTime });
        Match(tt, "3332", Actual("AAA", "15:00")).Should().BeEquivalentTo(new { Train = new TrainId("3332U"), Method = MatchMethod.Untimed }, "the timed one is hours away");
    }

    [Fact]
    public void Untimed_trains_alike_wait_for_their_path_to_tell_them_apart()
    {
        // Moss Vale's two 2SSRs: one runs into Yass Junction yard, the other out of it.
        var tt = Of(
            Train("2SSR", id: "2SSRA", points: [Untimed("YASSJ"), Untimed("YASSJS"), Untimed("YASSJYD")]),
            Train("2SSR", id: "2SSRB", points: [Untimed("YASSJS"), Untimed("YASSJ"), Untimed("HARDEN")]));

        Match(tt, "2SSR", Actual("S40", "12:00", atTimingPoint: false)).Status.Should().Be(MatchStatus.Pending);
        Match(tt, "2SSR", Actual("S40", "12:00", atTimingPoint: false), Actual("HARDEN", "12:20"))
            .Should().BeEquivalentTo(new { Status = MatchStatus.Matched, Train = new TrainId("2SSRB") }, "only 2SSRB runs to Harden");
    }
}
