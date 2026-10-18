using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Tests;

public sealed class TrainLinkerTests
{
    private static List<string> Ids(WttImportResult result) => result.Timetable.Trains.Select(t => t.Id.Value).ToList();

    [Fact]
    public void Ids_are_the_uid_else_the_headcode_with_repeats_numbered()
    {
        var trains =
            Wtt.SimpleTrain("1A01", uid: "U1") +
            Wtt.SimpleTrain("1A02") +
            Wtt.SimpleTrain("1A02") +
            Wtt.SimpleTrain("1A03", uid: "U1") +
            Wtt.SimpleTrain("1A04", uid: "1A02") +
            Wtt.SimpleTrain("1A05", uid: "  ");

        Ids(Wtt.Read(Wtt.Document(trains))).Should().Equal("U1", "1A02", "1A02#2", "U1#2", "1A02#3", "1A05");
    }

    [Fact]
    public void A_generated_id_never_clashes_with_a_real_one()
    {
        var trains = Wtt.SimpleTrain("1A01", uid: "X") + Wtt.SimpleTrain("1A02", uid: "X#2") + Wtt.SimpleTrain("1A03", uid: "X");

        Ids(Wtt.Read(Wtt.Document(trains))).Should().Equal("X", "X#2", "X#3");
    }

    [Fact]
    public void Ids_are_stable_across_imports()
    {
        var xml = Wtt.Document(Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("2B02", uid: "U"));

        Ids(Wtt.Read(xml)).Should().Equal(Ids(Wtt.Read(xml)));
    }

    [Fact]
    public void Resolves_a_next_working_by_headcode_and_sets_formed_from()
    {
        var trains =
            Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000) + Wtt.Trip("BBB", arr: 1300, extra: Wtt.Activity(0, train: "2B01"))) +
            Wtt.Train("2B01", Wtt.Trip("BBB", dep: 1800) + Wtt.Trip("AAA", arr: 2100));

        var tt = Wtt.Read(Wtt.Document(trains)).Timetable;

        var first = tt.Get(new TrainId("1A01"));
        var activity = first.Points[1].Activities.Should().ContainSingle().Subject;
        activity.Should().Be(new TrainActivity(ActivityKind.Next, 0, new Headcode("2B01"), null, new TrainId("2B01")));
        first.NextWorking.Should().Be(new TrainId("2B01"));
        tt.Get(new TrainId("2B01")).FormedFrom.Should().Be(new TrainId("1A01"));
        first.FormedFrom.Should().BeNull();
    }

    [Fact]
    public void Resolves_by_uid_and_picks_the_alternative_that_starts_here_nearest_in_time()
    {
        var trains =
            Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000) + Wtt.Trip("BBB", arr: 1300, extra: Wtt.Activity(0, uid: "U2"))) +
            Wtt.Train("2B01", Wtt.Trip("CCC", dep: 1500) + Wtt.Trip("BBB", arr: 1600), uid: "U2") +
            Wtt.Train("2B01", Wtt.Trip("BBB", dep: 9000) + Wtt.Trip("AAA", arr: 9300), uid: "U2") +
            Wtt.Train("2B01", Wtt.Trip("BBB", dep: 1800) + Wtt.Trip("AAA", arr: 2100), uid: "U2");

        var tt = Wtt.Read(Wtt.Document(trains)).Timetable;

        tt.Get(new TrainId("1A01")).NextWorking.Should().Be(new TrainId("U2#3"));
        tt.Get(new TrainId("U2#3")).FormedFrom.Should().Be(new TrainId("1A01"));
        tt.Get(new TrainId("U2")).FormedFrom.Should().BeNull();
    }

    [Fact]
    public void A_join_resolves_to_the_train_that_is_at_that_location_nearest_in_time()
    {
        var trains =
            Wtt.Train("0C02", Wtt.Trip("CARROG", arr: 5000, extra: Wtt.Activity(3, train: "1C02"))) +
            Wtt.Train("1C02", Wtt.Trip("LLANGOLLEN", dep: 1000) + Wtt.Trip("CARROG", arr: 9000)) +
            Wtt.Train("1C02", Wtt.Trip("LLANGOLLEN", dep: 2000) + Wtt.Trip("CARROG", arr: 5100)) +
            Wtt.Train("1C02", Wtt.Trip("LLANGOLLEN", dep: 5000) + Wtt.Trip("BERWYN", arr: 5100));

        var tt = Wtt.Read(Wtt.Document(trains)).Timetable;

        var join = tt.Get(new TrainId("0C02")).Points[0].Activities[0];
        join.Kind.Should().Be(ActivityKind.Join);
        join.FormsAssociatedTrain.Should().BeFalse();
        join.Train.Should().Be(new TrainId("1C02#2"));
        tt.Trains.Should().OnlyContain(t => t.FormedFrom == null, "a join forms nothing");
    }

    [Fact]
    public void Without_a_location_match_the_nearest_start_time_wins()
    {
        var trains =
            Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000, extra: Wtt.Activity(9, train: "2B01"))) +
            Wtt.Train("2B01", Wtt.Trip("XXX", dep: 5000)) +
            Wtt.Train("2B01", Wtt.Trip("YYY", dep: 1200)) +
            Wtt.Train("2B01", Wtt.Trip("ZZZ"));

        Wtt.Read(Wtt.Document(trains)).Timetable.Get(new TrainId("1A01")).Points[0].Activities[0].Train.Should().Be(new TrainId("2B01#2"));
    }

    [Fact]
    public void A_uid_that_is_really_a_headcode_still_resolves()
    {
        var trains = Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000, extra: Wtt.Activity(0, uid: "2B01"))) + Wtt.Train("2B01", Wtt.Trip("AAA", dep: 1500), uid: "W123");

        Wtt.Read(Wtt.Document(trains)).Timetable.Get(new TrainId("1A01")).NextWorking.Should().Be(new TrainId("W123"));
    }

    [Fact]
    public void A_train_never_links_to_itself()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000, extra: Wtt.Activity(0, train: "1A01")))));

        result.Timetable.Trains[0].NextWorking.Should().BeNull();
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Warning && w.Message == "Next at AAA names 1A01, which is not in the timetable; the link is lost.");
    }

    [Fact]
    public void A_missing_crew_change_target_is_not_a_warning()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000, extra: Wtt.Activity(10, uid: "42")))));

        var activity = result.Timetable.Trains[0].Points[0].Activities[0];
        activity.Kind.Should().Be(ActivityKind.CrewChange);
        activity.Train.Should().BeNull();
        activity.AssociatedText.Should().Be("42");
        result.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ActivityKind.Next, true)]
    [InlineData(ActivityKind.DivideRear, true)]
    [InlineData(ActivityKind.DivideFront, true)]
    [InlineData(ActivityKind.DetachEngineRear, true)]
    [InlineData(ActivityKind.DetachEngineFront, true)]
    [InlineData(ActivityKind.DropCoachesRear, true)]
    [InlineData(ActivityKind.DropCoachesFront, true)]
    [InlineData(ActivityKind.Join, false)]
    [InlineData(ActivityKind.JoinedBy, false)]
    [InlineData(ActivityKind.PlatformShare, false)]
    [InlineData(ActivityKind.CrewChange, false)]
    [InlineData(ActivityKind.Unknown, false)]
    public void Only_forming_activities_set_formed_from(ActivityKind kind, bool forms)
    {
        var code = kind == ActivityKind.Unknown ? 77 : (int)kind;
        var trains = Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000, extra: Wtt.Activity(code, train: "2B01"))) + Wtt.Train("2B01", Wtt.Trip("AAA", dep: 1500));

        var tt = Wtt.Read(Wtt.Document(trains)).Timetable;

        tt.Get(new TrainId("1A01")).Points[0].Activities[0].Train.Should().Be(new TrainId("2B01"));
        tt.Get(new TrainId("2B01")).FormedFrom.Should().Be(forms ? new TrainId("1A01") : null);
    }

    [Fact]
    public void Rules_resolve_only_unique_trains()
    {
        var rules = "<TimetableRule><Rule>14</Rule><Train>1A01</Train><Train2>1A02</Train2></TimetableRule>";

        var rule = Wtt.Read(Wtt.Document(Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("1A02"), rules: rules)).Timetable.Rules.Single();

        rule.Kind.Should().Be(RuleKind.Alternatives);
        rule.Train.Should().BeNull("1A01 is ambiguous");
        rule.TrainText.Should().Be("1A01");
        rule.Train2.Should().Be(new TrainId("1A02"));
    }
}
