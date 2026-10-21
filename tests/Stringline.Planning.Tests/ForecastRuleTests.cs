using Stringline.Core;
using Stringline.Core.Timetables;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// WTT rules in the forecast (S32, <c>06-…md</c> §2): a train held until another enters, leaves, arrives, joins,
/// divides or forms, plus the rule's time in seconds.
/// </summary>
public sealed class ForecastRuleTests
{
    private static readonly TrainId X = new("2X01");
    private static readonly TrainId Y = new("2Y01");

    /// <summary>2Y01 runs A–B and leaves the area at B at 08:05; 2X01 comes back B–A from 08:10.</summary>
    private static Train[] Shuttle() =>
    [
        Train("2Y01", points: [Pass("A", "08:00"), Pass("B", "08:05")]),
        Train("2X01", points: [Pass("B", "08:10"), Pass("A", "08:15")]),
    ];

    private static TimetableRule Rule(RuleKind kind, int seconds, string x = "2X01", string y = "2Y01", string? location = null) =>
        new(kind, (int)kind, x, y, new TrainId(x), new TrainId(y), seconds, location is null ? null : new Tiploc(location));

    private static WorkingPlan Plan(Train[] trains, params TimetableRule[] rules)
    {
        var t = Of(trains);
        return WorkingPlan.FromTimetable(new WorkingTimetable(t.Header, t.Categories, t.Trains, rules));
    }

    private static Forecast Forecast(WorkingPlan plan, string? now = null, bool rules = true, params Core.Live.LiveTrain[] live) =>
        Forecaster.Run(new ForecastRequest(plan)
        {
            LiveTrains = live,
            Now = now is null ? null : SimTime.Parse(now),
            Options = new ForecastOptions { Interaction = false, TimetableRules = rules },
        });

    [Fact]
    public void X_appears_after_Y_leaves_plus_the_rules_seconds()
    {
        var plan = Plan(Shuttle(), Rule(RuleKind.XAppearsAfterYLeaves, 600));

        var forecast = Forecast(plan);

        var x = forecast.Find(X)!;
        Times(x).Should().Equal("B pass 08:15:00 F", "A 08:20:00/- F");
        var hold = x.RuleHolds.Should().ContainSingle().Subject;
        hold.Index.Should().Be(0);
        hold.From.Should().Be(SimTime.Parse("08:10"));
        hold.Until.Should().Be(SimTime.Parse("08:15"));
        hold.Seconds.Should().Be(300);
        hold.ToString().Should().Be("held by rule: after 2Y01 leaves (+10 min)");
        forecast.Find(Y)!.RuleHolds.Should().BeEmpty();
    }

    [Fact]
    public void A_rule_with_room_to_spare_changes_nothing()
    {
        var plan = Plan(Shuttle(), Rule(RuleKind.XAppearsAfterYLeaves, 60));

        var x = Forecast(plan).Find(X)!;

        Times(x).Should().Equal("B pass 08:10:00 F", "A 08:15:00/- F");
        x.RuleHolds.Should().BeEmpty();
    }

    [Fact]
    public void A_late_Y_holds_X_from_its_forecast_and_the_option_turns_rules_off()
    {
        var plan = Plan(Shuttle(), Rule(RuleKind.XAppearsAfterYLeaves, 300));
        var late = Running("10", "2Y01", Passed("A", "08:07"));

        Times(Forecast(plan, "08:07:30", live: late).Find(X)!).Should().Equal("B pass 08:17:00 F", "A 08:22:00/- F");
        Times(Forecast(plan, "08:07:30", rules: false, live: late).Find(X)!).Should().Equal("B pass 08:10:00 F", "A 08:15:00/- F");
    }

    [Fact]
    public void Y_gone_already_holds_X_from_its_actual()
    {
        var plan = Plan(Shuttle(), Rule(RuleKind.XAppearsAfterYLeaves, 600));

        var forecast = Forecast(plan, "08:09", live: Running("10", "2Y01", Passed("A", "08:01"), Passed("B", "08:06")));

        Times(forecast.Find(X)!).Should().Equal("B pass 08:16:00 F", "A 08:21:00/- F");
    }

    [Fact]
    public void Appearance_after_Y_enters_and_after_Y_arrives()
    {
        var trains = new[]
        {
            Train("2Y01", points: [Call("A", null, "08:00"), Call("B", "08:05", null)]),
            Train("2X01", points: [Call("C", null, "08:01"), Call("D", "08:06", null)]),
        };

        Times(Forecast(Plan(trains, Rule(RuleKind.XAppearsAfterYEnters, 120))).Find(X)!).Should().Equal("C -/08:02:00 F", "D 08:07:00/- F");
        Times(Forecast(Plan(trains, Rule(RuleKind.XAppearsAfterYArrives, 0))).Find(X)!).Should().Equal("C -/08:05:00 F", "D 08:10:00/- F");
    }

    [Theory]
    [InlineData(RuleKind.XAppearsAfterYForms, ActivityKind.Next)]
    [InlineData(RuleKind.XAppearsAfterYJoins, ActivityKind.Join)]
    [InlineData(RuleKind.XAppearsAfterYDivides, ActivityKind.DivideRear)]
    public void Appearance_after_Y_forms_joins_or_divides_counts_from_its_arrival_there(RuleKind kind, ActivityKind activity)
    {
        var trains = new[]
        {
            Train("2Y01", points: [Call("A", null, "08:00"), Call("B", "08:05", "08:10", Forms(activity, "9Z99")), Call("C", "08:15", null)]),
            Train("2X01", points: [Call("C", null, "08:02"), Call("D", "08:07", null)]),
        };

        var x = Forecast(Plan(trains, Rule(kind, 60))).Find(X)!;

        Times(x).Should().Equal("C -/08:06:00 F", "D 08:11:00/- F");
        x.RuleHolds.Single().Rule.Description.Should().Be($"after 2Y01 {TimetableRules.VerbOf(kind)} (+1 min)");
    }

    [Fact]
    public void X_departs_after_Y_arrives_at_the_rules_location()
    {
        // A crossing on a single line at B: 2X01 waits at B for 2Y01 to arrive there.
        var trains = new[]
        {
            Train("2X01", points: [Call("A", null, "08:00"), Call("B", "08:05", "08:06"), Call("C", "08:12", null)]),
            Train("2Y01", points: [Call("C", null, "08:02"), Call("B", "08:08", "08:09"), Call("A", "08:14", null)]),
        };

        var x = Forecast(Plan(trains, Rule(RuleKind.XDepartsAfterYArrives, 30, location: "B"))).Find(X)!;

        Times(x).Should().Equal("A -/08:00:00 F", "B 08:05:00/08:08:30 F", "C 08:14:30/- F");
        var hold = x.RuleHolds.Should().ContainSingle().Subject;
        (hold.Index, hold.From, hold.Until).Should().Be((1, SimTime.Parse("08:06"), SimTime.Parse("08:08:30")));
        hold.Rule.Description.Should().Be("after 2Y01 arrives (+30 s)");
    }

    [Fact]
    public void A_departure_rule_with_no_location_holds_X_at_its_first_stop_for_Y_there()
    {
        var trains = new[]
        {
            Train("2X01", points: [Pass("E", "07:58"), Call("A", "08:00", "08:01"), Call("C", "08:12", null)]),
            Train("2Y01", points: [Call("C", null, "08:00"), Pass("B", "08:05"), Call("A", "08:09", null)]),
        };

        var x = Forecast(Plan(trains, Rule(RuleKind.XDepartsAfterYArrives, 0))).Find(X)!;

        Times(x).Should().Equal("E pass 07:58:00 F", "A 08:00:00/08:09:00 F", "C 08:20:00/- F");
        x.RuleHolds.Single().Index.Should().Be(1);
    }

    [Fact]
    public void X_departs_after_Y_leaves_a_pass_there_counts()
    {
        var trains = new[]
        {
            Train("2X01", points: [Call("A", null, "08:00"), Call("B", "08:05", "08:06"), Call("C", "08:12", null)]),
            Train("2Y01", points: [Pass("D", "08:04"), Pass("B", "08:07"), Pass("E", "08:10")]),
        };

        Times(Forecast(Plan(trains, Rule(RuleKind.XDepartsAfterYLeaves, 60, location: "B"))).Find(X)!)
            .Should().Equal("A -/08:00:00 F", "B 08:05:00/08:08:00 F", "C 08:14:00/- F");
    }

    [Fact]
    public void A_train_standing_at_its_hold_point_waits_for_the_rule()
    {
        var trains = new[]
        {
            Train("2X01", points: [Call("A", null, "08:00"), Call("B", "08:05", "08:06"), Call("C", "08:12", null)]),
            Train("2Y01", points: [Call("C", null, "08:02"), Call("B", "08:08", "08:09"), Call("A", "08:14", null)]),
        };
        var plan = Plan(trains, Rule(RuleKind.XDepartsAfterYArrives, 0, location: "B"));

        var forecast = Forecast(plan, "08:06:30", live:
            [Running("10", "2X01", Dep("A", "08:00"), Arr("B", "08:05")),
            Running("11", "2Y01", Dep("C", "08:04"))]);

        Times(forecast.Find(X)!).Should().Equal("A -/08:00:00 A", "B 08:05:00/08:10:00 R", "C 08:16:00/- F");
    }

    [Fact]
    public void Rules_chain_in_order()
    {
        var trains = new[]
        {
            Train("2Y01", points: [Pass("A", "08:00"), Pass("B", "08:05")]),
            Train("2X01", points: [Pass("B", "08:06"), Pass("A", "08:11")]),
            Train("2W01", points: [Pass("A", "08:12"), Pass("B", "08:17")]),
        };
        var plan = Plan(trains, Rule(RuleKind.XAppearsAfterYLeaves, 300), Rule(RuleKind.XAppearsAfterYLeaves, 300, x: "2W01", y: "2X01"));

        var forecast = Forecast(plan);

        Times(forecast.Find(X)!).Should().Equal("B pass 08:10:00 F", "A 08:15:00/- F");
        Times(forecast.Find(new TrainId("2W01"))!).Should().Equal("A pass 08:20:00 F", "B 08:25:00/- F");
    }

    [Fact]
    public void A_loop_of_rules_is_broken_and_noted_not_waited_on()
    {
        var plan = Plan(Shuttle(), Rule(RuleKind.XAppearsAfterYLeaves, 600), Rule(RuleKind.XAppearsAfterYLeaves, 600, x: "2Y01", y: "2X01"));

        var rules = TimetableRules.For(plan);
        rules.Rules.Should().ContainSingle().Which.Train.Should().Be(X);
        rules.Notes.Should().ContainSingle().Which.Should().Contain("loop");

        var forecast = Forecast(plan);
        Times(forecast.Find(Y)!).Should().Equal("A pass 08:00:00 F", "B 08:05:00/- F");
        Times(forecast.Find(X)!).Should().Equal("B pass 08:15:00 F", "A 08:20:00/- F");
    }

    [Fact]
    public void A_loop_through_a_formed_train_is_let_go()
    {
        // 2X01 waits (by rule) for 2Y01 to leave; 2Y01 is formed from 2X01. Neither could start: the forecast lets
        // 2X01 go rather than wait for ever.
        var trains = new[]
        {
            Train("2X01", points: [Call("A", null, "08:00"), Call("B", "08:05", null, Next("2Y01"))]),
            Train("2Y01", formedFrom: X, points: [Call("B", null, "08:10"), Call("C", "08:15", null)]),
        };

        var forecast = Forecast(Plan(trains, Rule(RuleKind.XAppearsAfterYLeaves, 60)));

        forecast.Find(X)!.Status.Should().Be(TrainForecastStatus.NotStarted);
        Times(forecast.Find(X)!).Should().Equal("A -/08:00:00 F", "B 08:05:00/- F");
        Times(forecast.Find(Y)!).Should().Equal("B -/08:10:00 F", "C 08:15:00/- F");
    }

    [Fact]
    public void Unresolved_rules_and_rules_whose_train_will_not_run_are_skipped()
    {
        var unresolved = new TimetableRule(RuleKind.XAppearsAfterYLeaves, 1, "2X01", "9Q99", X, null, 600, null);
        Times(Forecast(Plan(Shuttle(), unresolved)).Find(X)!).Should().Equal("B pass 08:10:00 F", "A 08:15:00/- F");
        TimetableRules.For(Plan(Shuttle(), unresolved)).Rules.Should().BeEmpty();

        var (cancelled, result) = Plan(Shuttle(), Rule(RuleKind.XAppearsAfterYLeaves, 600)).Apply(new CancelTrain(Y));
        result.Applied.Should().BeTrue();
        Times(Forecast(cancelled).Find(X)!).Should().Equal("B pass 08:10:00 F", "A 08:15:00/- F");
    }

    [Fact]
    public void A_place_a_train_does_not_go_is_noted_and_the_rule_left_out()
    {
        // 2X01 only passes (no stop to hold it at), and 2Y01 does not go to Q.
        var noStop = TimetableRules.For(Plan(Shuttle(), Rule(RuleKind.XDepartsAfterYArrives, 0, location: "Q")));
        noStop.Rules.Should().BeEmpty();
        noStop.Notes.Should().ContainSingle().Which.Should().Contain("2X01 has no stop to hold it at");

        var trains = new[]
        {
            Train("2X01", points: [Call("A", null, "08:00"), Call("B", "08:05", null)]),
            Train("2Y01", points: [Call("C", null, "08:00"), Call("D", "08:05", null)]),
        };
        var elsewhere = TimetableRules.For(Plan(trains, Rule(RuleKind.XDepartsAfterYArrives, 0, location: "B")));
        elsewhere.Rules.Should().BeEmpty();
        elsewhere.Notes.Should().ContainSingle().Which.Should().Contain("2Y01 does not arrive at B");
    }

    [Fact]
    public void A_train_that_joins_at_the_rules_location_is_held_at_its_start()
    {
        // As Basingstoke's 5L78: it leaves the depot to join 2L65 at the station, not before 2L65 has arrived there.
        var trains = new[]
        {
            Train("2X01", points: [Call("DEPOT", null, "08:00"), Call("STN", "08:02", null, Forms(ActivityKind.Join, "2Y01"))]),
            Train("2Y01", points: [Call("A", null, "07:50"), Call("STN", "08:01", "08:10")]),
        };
        var plan = Plan(trains, Rule(RuleKind.XDepartsAfterYArrives, 0, location: "STN"));

        var x = Forecast(plan, "07:52", live: Running("10", "2Y01", Dep("A", "07:53"))).Find(X)!;

        TimetableRules.For(plan).Rules.Should().ContainSingle().Which.Point.Should().Be(0);
        Times(x).Should().Equal("DEPOT -/08:04:00 F", "STN 08:06:00/- F");
    }

    [Fact]
    public void A_rule_on_the_train_itself_is_a_least_dwell()
    {
        // As Kings Cross's "4S39 departs after 4S39 arrives" at HITCHIN, 2 min: it stops there 2 min even when late.
        var trains = new[]
        {
            Train("2X01", points: [Call("A", null, "08:00"), Pass("B", "08:05"), Call("C", "08:10", null)]),
        };
        var plan = Plan(trains, Rule(RuleKind.XDepartsAfterYArrives, 120, x: "2X01", y: "2X01", location: "B"));

        var x = Forecast(plan).Find(X)!;

        TimetableRules.For(plan).Notes.Should().BeEmpty();
        Times(x).Should().Equal("A -/08:00:00 F", "B 08:05:00/08:07:00 F", "C 08:12:00/- F");
        x.RuleHolds.Single().Rule.Description.Should().Be("after 2X01 arrives (+2 min)");
        TimetableRules.For(Plan(trains, Rule(RuleKind.XAppearsAfterYLeaves, 0, x: "2X01", y: "2X01"))).Notes.Should().ContainSingle().Which.Should().Contain("names one train twice");
    }

    [Fact]
    public void A_train_with_no_booked_times_is_noted()
    {
        var trains = new[]
        {
            Train("2X01", points: [Call("A", null, null), Call("B", null, null)]),
            Train("2Y01", points: [Call("A", null, "08:00"), Call("B", "08:05", null)]),
        };

        TimetableRules.For(Plan(trains, Rule(RuleKind.XDepartsAfterYArrives, 0, location: "A"))).Notes.Should().ContainSingle()
            .Which.Should().Contain("2X01 has no booked times");
    }

    [Fact]
    public void Exclusion_rules_are_not_forecast_but_kept_for_the_detail()
    {
        var plan = Plan(Shuttle(), Rule(RuleKind.XNotIfY, 0), Rule(RuleKind.MutuallyExclusive, 0), Rule(RuleKind.Alternatives, 0));

        var rules = TimetableRules.For(plan);

        rules.Rules.Should().BeEmpty();
        Times(Forecast(plan).Find(X)!).Should().Equal("B pass 08:10:00 F", "A 08:15:00/- F");
        rules.ExclusionsOf(X).Select(r => TimetableRules.DescribeExclusion(r, X, plan.NameOf))
            .Should().Equal("does not run if 2Y01 runs", "runs or 2Y01 runs, not both", "an alternative to 2Y01");
        rules.ExclusionsOf(Y).Select(r => TimetableRules.DescribeExclusion(r, Y, plan.NameOf)).First()
            .Should().Be("2X01 does not run if this one runs");
    }

    [Fact]
    public void A_rule_held_train_is_not_a_conflict_in_itself()
    {
        // 2Z01 follows 2X01 B–A on the same line, 5 min behind the time 2X01's rule lets it go.
        var trains = new[]
        {
            Train("2Y01", points: [Pass("A", "08:00"), Pass("B", "08:05")]),
            Train("2X01", points: [Pass("B", "08:10"), Pass("A", "08:15")]),
            Train("2Z01", points: [Pass("B", "08:20"), Pass("A", "08:25")]),
        };
        var plan = Plan(trains, Rule(RuleKind.XAppearsAfterYLeaves, 600));
        var network = ForecastNetwork.Build([Line("main", 180, single: false, "B", "A")], plan.Timetable);

        var forecast = Forecaster.Run(new ForecastRequest(plan) { Network = network });
        var conflicts = ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network });

        Times(forecast.Find(X)!).Should().Equal("B pass 08:15:00 F", "A 08:20:00/- F");
        forecast.Find(X)!.RuleHolds.Should().ContainSingle();
        forecast.Trains.Should().OnlyContain(t => t.KnockOnSeconds == 0);
        conflicts.All.Should().BeEmpty();
    }

    [Fact]
    public void Durations_read_as_people_say_them()
    {
        TimetableRules.Duration(60).Should().Be("1 min");
        TimetableRules.Duration(45).Should().Be("45 s");
        TimetableRules.Duration(90).Should().Be("90 s");
        TimetableRules.Duration(150).Should().Be("2 min 30 s");
        TimetableRules.Duration(1500).Should().Be("25 min");
    }
}
