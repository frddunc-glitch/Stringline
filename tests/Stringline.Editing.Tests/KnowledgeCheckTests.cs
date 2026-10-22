using Stringline.Core.Timetables;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using static Stringline.Editing.Tests.Draft;
using Ref = Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Editing.Tests;

/// <summary>
/// The checks against sim knowledge (<c>08-…md</c> §5), learned from a reference timetable: trains enter at EIN on
/// diesel; A (platforms 1, 2) → B (passed) → C (platform 2, a key location); lines F and S from A to B; D → E2 → F booked
/// beside D → F; X where trains leave the area; S1 a seed point.
/// </summary>
public sealed class KnowledgeCheckTests
{
    private static readonly SimKnowledge Knowledge = Build();

    private static SimKnowledge Build()
    {
        static Train Enters(string headcode, params TimingPoint[] points) =>
            Ref.Train(headcode, start: TrainStart.EntryPoint, entry: "09:59", points: points) with { Electrification = "D" };

        var builder = new SimKnowledgeBuilder("test");
        builder.AddTimetable(Ref.Of(
            Enters("1R01", Ref.Call("A", null, "10:00") with { Platform = "1", Line = "F" }, Ref.Pass("B", "10:05"), Ref.Call("C", "10:10", "10:12", Ref.Next("2R01")) with { Platform = "2", Path = "P" }),
            Ref.Train("2R01", formedFrom: new Core.TrainId("1R01"), points: [Ref.Call("C", null, "10:20") with { Platform = "2" }, Ref.Pass("B", "10:25"), Ref.Call("A", "10:30", null) with { Platform = "2" }]),
            Enters("1R03", Ref.Call("A", null, "11:00") with { Platform = "2", Line = "S" }, Ref.Pass("B", "11:06"), Ref.Call("C", "11:12", "11:13") with { Platform = "2" }),
            Enters("1R04", Ref.Call("A", null, "12:00") with { Platform = "1", Line = "F" }, Ref.Pass("X", "12:05")),
            Enters("1R05", Ref.Call("D", null, "12:00"), Ref.Pass("E2", "12:05"), Ref.Call("F", "12:10", "12:11")),
            Enters("1R06", Ref.Call("D", null, "13:00"), Ref.Call("F", "13:10", "13:11")),
            Ref.Train("1R07", start: TrainStart.Seeded, points: [Ref.Call("A", null, "10:00") with { Platform = "1", Line = "F" }, Ref.Pass("B", "10:05"), Ref.Call("C", "10:10", "10:11")]) with { Electrification = "D" }));
        return builder.Build();
    }

    private static object[] In => [Field("EntryPoint", "EIN")];

    [Fact]
    public void A_train_like_the_sims_own_has_no_knowledge_issues()
    {
        Check(Doc(Train("1A01", [.. In, Trips(Stop("A", null, "10:00", Field("Platform", "1"), Field("Line", "F")), Pass("B", "10:05"), Stop("C", "10:10", "10:12", Field("Platform", "2"), Field("Path", "P")))])), Knowledge)
            .Issues.Where(i => IssueCatalogue.Of(i.Code).FromKnowledge).Should().BeEmpty();
    }

    [Fact]
    public void Locations_and_codes_never_seen_are_warnings_that_say_how_much_was_seen()
    {
        var result = Check(Doc(Train("1A01", [.. In, Trips(
            Stop("A", null, "10:00", Field("Platform", "9"), Field("Line", "Q")),
            Pass("B", "10:05"),
            Stop("C", "10:10", "10:12", Field("Platform", "2"), Field("Path", "Q")),
            Pass("ZZZ", "10:20"))])), Knowledge);

        result.Of(IssueCodes.UnknownLocation).Should().ContainSingle().Which.Message.Should().Be("ZZZ never seen in 1 timetable (7 trains)");
        result.Of(IssueCodes.InvalidPlatform).Should().ContainSingle().Which.Message.Should().Be("Invalid platform 9 at A (never seen in 1 timetable (7 trains) there)");
        result.Of(IssueCodes.InvalidLine).Should().ContainSingle().Which.Message.Should().StartWith("Invalid line code Q at A");
        result.Of(IssueCodes.InvalidPath).Should().ContainSingle().Which.Message.Should().StartWith("Invalid path code Q at C");
        result.Issues.Where(i => IssueCatalogue.Of(i.Code).FromKnowledge).Should().OnlyContain(i => i.Severity <= IssueSeverity.Warning, "absence of evidence is not proof");
    }

    [Fact]
    public void No_known_path_inserts_the_booked_chain_with_pass_times_from_the_run_times()
    {
        var document = Doc(Train("1A01", [.. In, Trips(Stop("A", null, "10:00", Field("Platform", "1"), Field("Line", "F"), Field("DownDirection", -1)), Stop("C", "10:10", "10:12", Field("Platform", "2"), Field("DownDirection", -1)))]));
        var issue = Check(document, Knowledge).Of(IssueCodes.NoKnownPath).Should().ContainSingle().Subject;

        issue.Message.Should().Be("Invalid timetable data: no known path from A to C (never seen in 1 timetable (7 trains) straight after each other)");
        issue.QuickFix!.Title.Should().Be("Insert B");
        var after = issue.Fix(document, Knowledge);

        after.Codes().Should().NotContain(IssueCodes.NoKnownPath);
        var trips = document.Trains.Single().Trips.ToList();
        trips.Select(t => t.Location).Should().Equal("A", "B", "C");
        (trips[1].IsPassTime, trips[1].DepPassTime?.Seconds, trips[1].DownDirection).Should().Be((true, T("10:05"), true), "A → B and B → C both usually take 5 min");
    }

    [Fact]
    public void An_ambiguous_line_is_set_to_the_most_used_but_not_where_others_leave_it_out()
    {
        var document = Doc(Train("1A01", [.. In, Trips(Stop("A", null, "10:00", Field("Platform", "1")), Pass("B", "10:05"), Stop("C", "10:10", "10:12", Field("Platform", "2")))]));
        var issue = Check(document, Knowledge).Of(IssueCodes.AmbiguousLine).Should().ContainSingle().Subject;

        issue.Message.Should().Be("Ambiguous line from A to B: F (2), S (1)");
        issue.Fix(document, Knowledge).Codes().Should().NotContain(IssueCodes.AmbiguousLine);
        document.Trains.Single().Trips.First().Line.Should().Be("F");
        Knowledge.Link(new("B"), new("C"))!.Lines.Should().BeNull("B → C is booked with no line, so it is never ambiguous");
    }

    [Fact]
    public void A_run_quicker_than_any_seen_a_stop_at_a_non_station_and_activities_where_none_happen()
    {
        var result = Check(Doc(
            Train("1A01", [.. In, Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("B", "10:02"), Stop("C", "10:10", "10:12"))]),
            Train("1A02", [.. In, Trips(Stop("A", null, "10:00", Field("Line", "F")), Stop("B", "10:05", "10:06", Activities(Activity(10, "CREW"))), Stop("C", "10:11", "10:12"))])), Knowledge);

        result.Of(IssueCodes.RunTooShort).Should().ContainSingle().Which.Message.Should().Be("Runs A to B in 2:00; the least seen for unknown trains is 5:00 (3 runs)");
        result.Of(IssueCodes.StopsAtNonStation).Should().ContainSingle().Which.Message.Should().Be("Stops at non-station B");
        result.Of(IssueCodes.NotSafeForActivities).Should().ContainSingle().Which.Message.Should().Be("Not safe to have activities at B");
    }

    [Fact]
    public void Entry_and_seed_points_and_traction_power_are_checked_against_what_entered()
    {
        var result = Check(Doc(
            Train("1A01", Field("EntryPoint", "ENEW"), Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("B", "10:05"))),
            Train("1A02", [.. In, Field("Electrification", "O"), Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("B", "10:05"))]),
            Train("1A03", [.. In, Field("Electrification", "OD"), Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("B", "10:05"))]),
            Train("1A04", Seeded("S7"), Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("B", "10:05"))),
            Train("1A05", Seeded("S1"), Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("B", "10:05")))), Knowledge);

        result.Of(IssueCodes.UnknownEntryPoint).Should().ContainSingle().Which.Message.Should().StartWith("ENEW not seen as an entry point");
        result.Of(IssueCodes.WrongTractionPower).Should().ContainSingle().Which.Message.Should().Be("Wrong traction power for EIN: no O train enters there (seen: D)");
        result.Of(IssueCodes.UnknownSeedPoint).Should().ContainSingle().Which.Message.Should().StartWith("Seed point S7 is not a known signal");
    }

    [Fact]
    public void Endings_exits_and_order_are_judged_by_where_the_sims_trains_go()
    {
        var result = Check(Doc(
            Train("1A01", [.. In, Trips(Stop("C", null, "10:00"), Pass("B", "10:05"), Stop("A", "10:10", null))]),
            Train("1A02", [.. In, Trips(Stop("C", null, "10:00"), Pass("B", "10:05"), Stop("ZZ", "10:10", null))]),
            Train("1A03", [.. In, Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("X", "10:05"), Stop("C", "10:10", "10:11"))]),
            Train("1A04", [.. In, Trips(Stop("D", null, "10:00"), Stop("F", "10:10", "10:11"), Pass("E2", "10:15"))]),
            Train("1A05", [.. In, Trips(Stop("A", null, "10:00", Field("Line", "F")), Pass("B", "10:05"), Stop("C", "10:10", "10:10"))])), Knowledge);

        result.Of(IssueCodes.NotSensibleEnding).Should().BeEmpty("A and C are where the sim's trains end; ZZ is never seen at all");
        result.Of(IssueCodes.ExitsAndReenters).Should().ContainSingle().Which.Message.Should().Be("Exits after X and then re-enters the area (trains leave the area there)");
        result.Of(IssueCodes.OutOfOrder).Should().ContainSingle().Which.Message.Should().Be("E2 out of order in timetable (other timetables book it between D and F)");

        var ending = Check(Doc(Train("1A01", [.. In, Trips(Stop("A", null, "10:00", Field("Line", "F")), Stop("B", "10:05", null))])), Knowledge);
        ending.Of(IssueCodes.NotSensibleEnding).Should().ContainSingle().Which.Message.Should().Be("B not a sensible ending point (no train of the sim ends there)");
    }

    [Fact]
    public void Knowledge_of_another_sim_or_of_no_timetables_is_not_used()
    {
        var document = Doc("other", Train("1A01", Trips(Stop("A", null, "10:00", Field("Platform", "9")), Pass("ZZZ", "10:05"))));
        Check(document, Knowledge).Issues.Should().NotContain(i => IssueCatalogue.Of(i.Code).FromKnowledge);
        Check(Doc(Train("1A01", Trips(Stop("A", null, "10:00"), Pass("ZZZ", "10:05")))), new SimKnowledgeBuilder("test").Build())
            .Issues.Should().NotContain(i => IssueCatalogue.Of(i.Code).FromKnowledge);
    }
}
