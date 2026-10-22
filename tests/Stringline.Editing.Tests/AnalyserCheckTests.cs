using Stringline.Editing.Validation;
using static Stringline.Editing.Tests.Draft;

namespace Stringline.Editing.Tests;

/// <summary>The Analyser's offline checks (<c>08-…md</c> §5), each firing and its near miss, on small timetables.</summary>
public sealed class AnalyserCheckTests
{
    [Fact]
    public void A_simple_train_has_no_issues()
    {
        Check(Doc(Simple("1A01"))).Issues.Should().BeEmpty();
    }

    [Fact]
    public void Times_out_of_sequence_are_errors_but_times_past_midnight_are_not()
    {
        var result = Check(Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:05"), Stop("C", "10:20", "10:21"))),
            Train("1A02", Trips(Stop("A", null, "10:00"), Stop("B", "09:50", "09:55"), Stop("C", "10:20", "10:21"))),
            Train("1A03", Trips(Stop("A", null, "10:00"), Pass("B", "09:58"), Stop("C", "10:20", "10:21"))),
            Train("1A04", Trips(Stop("A", null, "23:58"), Pass("B", "24:01"), Stop("C", "24:10", "24:11")))));

        result.Of(IssueCodes.DepartureBeforeArrival).Should().ContainSingle()
            .Which.Should().Match<ValidationIssue>(i => i.Headcode == "1A01" && i.PointIndex == 1 && i.Message == "Departure time at B earlier than arrival time" && i.Severity == IssueSeverity.Error);
        result.Of(IssueCodes.DepartureBeforePrevious).Should().ContainSingle().Which.Headcode.Should().Be("1A03", "1A01's late departure is reported once");
        result.Of(IssueCodes.ArrivalBeforePrevious).Should().ContainSingle().Which.Message.Should().Be("Arrival time at B earlier than previous location time");
        result.Issues.Should().NotContain(i => i.Headcode == "1A04");
    }

    [Fact]
    public void Reversing_on_a_pass_is_an_error_with_a_fix_to_stop_there()
    {
        var document = Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Pass("B", "10:05"), Stop("A", "10:10", "10:11"))),
            Train("1A02", Trips(Stop("A", null, "10:00"), Pass("B", "10:05"), Stop("C", "10:10", "10:11"))));
        var issue = Check(document).Of(IssueCodes.ReversesWithPassTime).Should().ContainSingle().Subject;

        issue.Message.Should().Be("Reverses at B but has \"passing time\" ticked");
        var after = issue.Fix(document);
        after.Codes().Should().NotContain(IssueCodes.ReversesWithPassTime);
        var b = document.Trains.First().Trips.ElementAt(1);
        (b.IsPassTime, b.ArrTime, b.DepPassTime).Should().Be((false, b.DepPassTime, b.DepPassTime));
    }

    [Fact]
    public void An_associated_train_must_exist_but_a_crew_change_may_name_a_crew()
    {
        var result = Check(Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, "2B99"))))),
            Train("1A02", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:11", Activities(Activity(10, "CREW7")))))));

        result.Of(IssueCodes.AssociatedNotDefined).Should().ContainSingle().Which.Message.Should().Be("Associated train 2B99 not defined (next working at B)");
    }

    [Fact]
    public void An_associated_train_must_start_where_it_is_formed_on_the_same_platform()
    {
        var result = Check(Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Field("Platform", "1"), Activities(Activity(0, "1B01"))))),
            Train("1B01", NoEntry, Trips(Stop("C", null, "10:20"), Stop("A", "10:30", "10:31"))),
            Train("1A02", Trips(Stop("A", null, "11:00"), Stop("B", "11:10", "11:10", Field("Platform", "1"), Activities(Activity(0, "1B02"))))),
            Train("1B02", NoEntry, Trips(Stop("B", null, "11:20", Field("Platform", "2")), Stop("A", "11:30", "11:31"))),
            Train("1A03", Trips(Stop("A", null, "12:00"), Stop("B", "12:10", "12:10", Field("Platform", "1"), Activities(Activity(0, "1B03"))))),
            Train("1B03", NoEntry, Trips(Stop("B", null, "12:20", Field("Platform", "1")), Stop("A", "12:30", "12:31")))));

        result.Of(IssueCodes.AssociatedDifferentLocation).Should().ContainSingle().Which.Message.Should().Be("Associated train 1B01 is at different location (starts at C)");
        result.Of(IssueCodes.AssociatedDifferentPlatform).Should().ContainSingle().Which.Message.Should().Be("Associated train 1B02 is on different platform (2 at B, not 1)");
        result.Issues.Should().NotContain(i => i.Headcode == "1A03" && i.Code.StartsWith("associated", StringComparison.Ordinal));
    }

    [Fact]
    public void A_next_train_used_twice_is_an_error_unless_the_two_never_run_in_one_game()
    {
        static object[] Forming(string id, string next, params object[] extra) =>
            [Train(id, [.. extra, Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, next))))])];

        var result = Check(Doc(
            Forming("1A01", "1B01"),
            Forming("1A02", "1B01"),
            Train("1B01", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),

            // Days of the week: one decision, different choices.
            Forming("1A03", "1B03", Field("EntryDecision", "DOTW"), Field("EntryChoice", "MON")),
            Forming("1A04", "1B03", Field("EntryDecision", "DOTW"), Field("EntryChoice", "[not] MON")),
            Train("1B03", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),

            // A seed group's copy, and the working that enters before the group's start.
            Forming("1A05", "1B05", Seeded("S1", "{G}")),
            Forming("1A05", "1B05"),
            Train("1B05", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),
            SeedGroup("{G}", "10:05"),

            // Alternatives by rule: one runs if 5A07 does not, the other after it.
            Forming("1A07", "1B07"),
            Forming("1A08", "1B07"),
            Train("1B07", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),
            Simple("5A07"),
            Rule(3, "1A07", "5A07"),
            Rule(1, "1A08", "5A07")));

        result.Of(IssueCodes.NextAlreadyUsed).Should().ContainSingle()
            .Which.Should().Match<ValidationIssue>(i => i.Headcode == "1A02" && i.Message == "Next train 1B01 already used (by 1A01)");
    }

    [Fact]
    public void A_divided_portion_formed_twice_is_an_error()
    {
        var result = Check(Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:15", Activities(Activity(1, "2B01"))), Stop("C", "10:20", "10:21"))),
            Train("1A02", Trips(Stop("A", null, "11:00"), Stop("B", "11:10", "11:15", Activities(Activity(1, "2B01"))), Stop("C", "11:20", "11:21"))),
            Train("2B01", NoEntry, Field("TrainLength", 40), Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31")))));

        result.Of(IssueCodes.DividedPortionAlreadyUsed).Should().ContainSingle().Which.Message.Should().Be("Divided portion 2B01 already used (by 1A01)");
    }

    [Fact]
    public void Next_must_be_the_final_activity_but_alternative_nexts_may_follow_it()
    {
        var document = Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, "1B01"), Activity(10, "CREW"))))),
            Train("1B01", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),
            Train("1A02", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(
                Activity(0, "1B02", Field("Decision", "DOTW"), Field("Choice", "[any] MON | TUE")),
                Activity(0, "1B03", Field("Decision", "DOTW"), Field("Choice", "[any] WED | THU")))))),
            Train("1B02", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),
            Train("1B03", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),
            Train("1A04", Trips(Stop("A", null, "10:00", Activities(Activity(0, "1B04"))), Stop("B", "10:10", "10:11"))),
            Train("1B04", NoEntry, Trips(Stop("A", null, "10:20"), Stop("B", "10:30", "10:31"))));
        var issues = Check(document).Of(IssueCodes.NextNotFinal).ToList();

        issues.Select(i => i.Headcode).Should().Equal("1A01", "1A04");
        issues[0].Message.Should().Be("Next at B not the final activity");
        issues[1].QuickFix.Should().BeNull("a next working before the last location cannot be moved there");
        issues[0].Fix(document).Of(IssueCodes.NextNotFinal).Select(i => i.Headcode).Should().Equal("1A04");
    }

    [Fact]
    public void An_unknown_activity_is_a_warning()
    {
        Check(Doc(Train("1A01", Trips(Stop("A", null, "10:00", Activities(Activity(99, null))), Stop("B", "10:10", "10:11")))))
            .Of(IssueCodes.UnknownActivity).Should().ContainSingle().Which.Message.Should().Be("Unknown activity at A (code 99)");
    }

    [Fact]
    public void Trains_never_used_never_run_and_lost_joins()
    {
        var result = Check(Doc(
            Train("5A01", NoEntry, Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, "5A02"))))),
            Train("5A02", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:30", Activities(Activity(3, "1A03"))))),
            Train("1A03", Trips(Stop("C", null, "10:20"), Stop("A", "10:25", "10:40", Activities(Activity(8, "5A02"))), Stop("B", "10:50", "10:51"))),
            Train("1A04", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, "1A05"))))),
            Train("1A05", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31")))));

        result.Of(IssueCodes.NeverUsed).Should().ContainSingle().Which.Headcode.Should().Be("5A01");
        result.Of(IssueCodes.NeverRuns).Should().ContainSingle().Which.Message.Should().Be("Never runs, referred to by 5A01");
        result.Of(IssueCodes.JoiningTrainLost).Should().BeEmpty("5A02 joins 1A03, which runs");
        result.Issues.Should().NotContain(i => i.Headcode == "1A05" && i.Code.StartsWith("never", StringComparison.Ordinal));

        var lost = Check(Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(3, "5B01"))))),
            Train("5B01", NoEntry, Trips(Stop("C", null, "10:00"), Stop("B", "10:05", "10:30")))));
        lost.Of(IssueCodes.JoiningTrainLost).Should().ContainSingle().Which.Message.Should().Be("Joining train 5B01 got lost (it never runs)");
    }

    [Fact]
    public void A_train_that_terminates_needs_an_onward_working_unless_it_leaves_or_berths()
    {
        var result = Check(Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", null))),
            Train("1A02", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10"))),
            Train("1A03", Trips(Stop("A", null, "10:00"), Stop("C", "10:10", "10:12"))),
            Train("1A04", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Field("BerthsHere", -1)))),
            Train("1A05", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(8, "1A06"))))),
            Train("1A06", Trips(Stop("A", null, "10:01"), Stop("B", "10:09", "10:09", Activities(Activity(3, "1A05"))))),
            Train("1A07", Trips(Stop("A", null, "10:00"), Pass("D", "10:10"))),
            Train("1A08", Trips(Stop("A", null, "10:00"), Stop("D", "10:10", "10:10")))));

        result.Of(IssueCodes.TerminatesWithNoOnward).Select(i => i.Headcode).Should().Equal(["1A01", "1A02"], "1A03 departs later, so it leaves; 1A08 stops where 1A07 leaves the area");
        result.Of(IssueCodes.TerminatesWithNoOnward).First().Message.Should().Be("Terminates at B with no onward working");
        result.Of(IssueCodes.NoFurtherActionAfterJoining).Should().ContainSingle().Which.Message.Should().Be("No further action after joining 1A06");
    }

    [Fact]
    public void Lengths_follow_the_train_through_joins_and_divides()
    {
        var result = Check(Doc(
            Train("1A01", Field("TrainLength", 6), Trips(Stop("A", null, "10:00"), Pass("B", "10:10"))),
            Train("1A02", Field("TrainLength", 40), Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:15", Activities(Activity(1, "2B02"))), Pass("C", "10:20"))),
            Train("2B02", NoEntry, Field("TrainLength", 60), Trips(Stop("B", null, "10:20"), Pass("C", "10:30"))),
            Train("1A03", Field("TrainLength", 64), Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:15", Activities(Activity(1, "2B03"))), Pass("C", "10:20"))),
            Train("2B03", NoEntry, Field("TrainLength", 60), Trips(Stop("B", null, "10:20"), Pass("C", "10:30"))),
            Train("1A04", Field("TrainLength", 80), Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(8, "2B04"), Activity(0, "1B04"))))),
            Train("2B04", Field("TrainLength", 80), Trips(Stop("C", null, "10:00"), Stop("B", "10:05", "10:05", Activities(Activity(3, "1A04"))))),
            Train("1B04", NoEntry, Field("TrainLength", 160), Trips(Stop("B", null, "10:20"), Pass("C", "10:30"))),
            Train("1A05", Field("TrainLength", 80), Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, "1B05"))))),
            Train("1B05", NoEntry, Field("TrainLength", 100), Trips(Stop("B", null, "10:20"), Pass("C", "10:30")))));

        result.Of(IssueCodes.TooShort).Should().ContainSingle().Which.Message.Should().Be("Train shorter than minimum allowed length (6 m)");
        result.Of(IssueCodes.ShorterThanDividedPortion).Should().ContainSingle().Which.Message.Should().Be("Length 40m shorter than divided portion 2B02 (60 m)");
        result.Of(IssueCodes.RemainderTooShort).Should().ContainSingle().Which.Message.Should().Be("Remainder too short after dividing 2B03 (4 m)");
        result.Of(IssueCodes.LengthDiffersFromNext).Should().ContainSingle().Which.Message.Should().Be("Length 80m not the same as next working 1B05 (100 m)", "1A04 is 160 m once 2B04 has joined it");
    }

    [Fact]
    public void A_train_needs_a_traction_power_its_own_or_its_categorys()
    {
        var result = Check(Doc(
            Category("C1", Field("Electrification", "O")),
            Train("1A01", Field("Electrification", " "), Trips(Stop("A", null, "10:00"), Pass("B", "10:10"))),
            Train("1A02", Field("Electrification", " "), Field("Category", "C1"), Trips(Stop("A", null, "10:00"), Pass("B", "10:10")))));

        result.Of(IssueCodes.NoTractionPower).Should().ContainSingle().Which.Headcode.Should().Be("1A01");
    }

    [Fact]
    public void Seeding_has_no_arrival_at_the_start_and_one_train_per_signal_per_group()
    {
        var document = Doc(
            SeedGroup("{G}", "12:00"),
            Train("1A01", Seeded("S1"), Trips(Stop("A", "09:59", "10:00"), Pass("B", "10:10"))),
            Train("1A02", Seeded("S2"), Trips(Stop("A", null, "10:00"), Pass("B", "10:10"))),
            Train("1A03", Seeded("S2"), Trips(Stop("A", null, "10:00"), Pass("B", "10:10"))),
            Train("1A04", Seeded("S2", "{G}"), Trips(Stop("A", null, "12:00"), Pass("B", "12:10"))));
        var result = Check(document);

        result.Of(IssueCodes.SeedSignalShared).Should().ContainSingle().Which.Message.Should().Be("Seeded at S2 with 1A02, at the timetable's start");
        var arrival = result.Of(IssueCodes.SeedArrivalTime).Should().ContainSingle().Subject;
        arrival.Message.Should().Be("Arrival time at seed start location A");
        arrival.Fix(document).Codes().Should().NotContain(IssueCodes.SeedArrivalTime);
        document.Trains.First().Trips.First().ArrTime.Should().BeNull();
    }

    [Fact]
    public void A_train_needs_two_locations_or_an_entry_seed_or_formation_and_one()
    {
        var result = Check(Doc(
            Train("1A01", NoEntry, Trips(Stop("A", null, "10:00"))),
            Train("1A02", Trips(Pass("A", "10:00"))),
            Train("1A03", Seeded("S1"), Trips(Stop("A", null, "10:00"))),
            Train("1A04", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, "1A05"))))),
            Train("1A05", NoEntry, Trips(Stop("B", null, "10:20"))),
            Train("1A06", Trips())));

        result.Of(IssueCodes.NoTrips).Select(i => i.Headcode).Should().Equal("1A01", "1A06");
    }

    [Fact]
    public void Document_checks_name_a_missing_sim_and_unknown_categories_and_groups()
    {
        var result = Check(Doc(
            null,
            Category("C1"),
            Category("C1"),
            Simple("1A01", Field("Category", "C9")),
            Simple("1A02", Seeded("S1", "{X}"))));

        result.Of(IssueCodes.NoAreaId).Should().ContainSingle();
        result.Of(IssueCodes.DuplicateCategory).Should().ContainSingle().Which.Message.Should().Be("Category C1 (C1) is defined twice");
        result.Of(IssueCodes.UnknownCategory).Should().ContainSingle().Which.Message.Should().Be("Uses category C9, which is not defined");
        result.Of(IssueCodes.UnknownSeedGroup).Should().ContainSingle();
        result.BlocksSave.Should().BeTrue("there is no sim ID");
        Check(Doc(Category("C1"), Category("C1"), Simple("1A01"))).BlocksSave.Should().BeFalse("released timetables define categories twice and SimSig reads them");
    }

    [Fact]
    public void Every_issue_has_its_catalogue_severity_and_hint()
    {
        var result = Check(Doc(Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:05")))));

        result.Issues.Should().OnlyContain(i => i.Severity == IssueCatalogue.Of(i.Code).Severity && i.Hint == IssueCatalogue.Of(i.Code).Hint && i.TrainElement != null && i.Element != null);
        IssueCatalogue.Codes.Should().OnlyHaveUniqueItems().And.HaveCountGreaterThan(40);
    }
}
