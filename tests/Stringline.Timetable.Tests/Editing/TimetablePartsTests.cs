using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Timetable.Editing;

namespace Stringline.Timetable.Tests.Editing;

/// <summary>S45: activities, rules, seed groups, categories and lists, each an edit whose undo restores the tree.</summary>
public sealed class TimetablePartsTests
{
    /// <summary>1A01 AAA→BBB, then 2A01 BBB→AAA; by headcode, or by UID (U1, U2) with one activity naming a UID.</summary>
    private static WttDocument Two(bool uids = false) => WttDocument.Parse(Wtt.Document(
        Wtt.SimpleTrain("1A01", uid: uids ? "U1" : null, toExtra: uids ? Wtt.Activity(9, uid: "U2") : "") + Wtt.SimpleTrain("2A01", dep: 37000, uid: uids ? "U2" : null, from: "BBB", to: "AAA"),
        header: "<SeedGroupSummary></SeedGroupSummary><ScenarioOptions></ScenarioOptions>"));

    private static TrainNode Train(WttDocument d, string headcode) => d.Trains.Single(t => t.Headcode == headcode);

    private static void ApplyUndo(WttDocument document, TimetableEdit edit, Action applied)
    {
        var before = RoundTrip.Snapshot(document);
        var undo = document.Apply(edit);
        applied();
        document.Apply(undo);
        document.Root.ShouldEqualTree(before);
        document.Apply(edit);
        applied();
    }

    [Fact]
    public void An_activity_is_added_named_as_the_file_names_trains_and_removed_with_its_list()
    {
        var d = Two();
        var last = Train(d, "1A01").Trips.Last();
        var next = TimetableParts.NewActivity(d, ActivityKind.Next, Train(d, "2A01"));

        ApplyUndo(d, TimetableParts.AddActivity(last, next), () =>
        {
            last.Activities.Should().ContainSingle().Which.Should().Match<ActivityNode>(a => a.Code == 0 && a.AssociatedTrain == "2A01" && a.AssociatedUid == null);
            d.ToWorkingTimetable().Find(new TrainId("1A01"))!.Points[^1].Activities.Should().ContainSingle(a => a.Kind == ActivityKind.Next);
        });

        ApplyUndo(d, TimetableParts.RemoveActivity(last.Activities.Single()), () => last.ActivitiesElement.Should().BeNull("SimSig writes no empty Activities"));

        var u = Two(uids: true);
        TimetableParts.UsesUids(u).Should().BeTrue();
        var formed = new ActivityNode(u, TimetableParts.NewActivity(u, ActivityKind.Join, Train(u, "2A01")));
        (formed.AssociatedTrain, formed.AssociatedUid).Should().Be(((string?)null, "U2"));
    }

    [Fact]
    public void A_next_working_stays_last()
    {
        var d = Two();
        var (first, last) = (Train(d, "1A01").Trips.First(), Train(d, "1A01").Trips.Last());
        var other = Train(d, "2A01");

        FluentActions.Invoking(() => TimetableParts.AddActivity(first, TimetableParts.NewActivity(d, ActivityKind.Next, other)))
            .Should().Throw<InvalidOperationException>().WithMessage("*last location*");

        d.Apply(TimetableParts.AddActivity(last, TimetableParts.NewActivity(d, ActivityKind.Next, other)));
        FluentActions.Invoking(() => TimetableParts.AddActivity(last, TimetableParts.NewActivity(d, ActivityKind.CrewChange, null, new SimTime(36400))))
            .Should().Throw<InvalidOperationException>().WithMessage("*last activity*");

        var crew = TimetableParts.AddActivity(last, TimetableParts.NewActivity(d, ActivityKind.CrewChange, null, new SimTime(36400), 120), index: 0);
        ApplyUndo(d, crew, () => last.Activities.Select(a => a.Code).Should().Equal(10, 0));
        var crewNode = last.Activities.First();
        crewNode.ActivityDuration.Should().Be(120);
        FluentActions.Invoking(() => TimetableParts.MoveActivity(crewNode, 1)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => TimetableParts.ChangeActivity(last.Activities.Last(), a => a.Code = (int)ActivityKind.Join)).Should().NotThrow();
        FluentActions.Invoking(() => TimetableParts.ChangeActivity(crewNode, a => a.Code = (int)ActivityKind.Next)).Should().NotThrow("alternative next workings are allowed");
        FluentActions.Invoking(() => TimetableParts.NewActivity(d, ActivityKind.Join, null)).Should().Throw<InvalidOperationException>().WithMessage("*needs the train*");
    }

    [Fact]
    public void Rules_are_added_with_their_list_and_deleted_with_it()
    {
        var d = Two();
        var edit = TimetableParts.AddRule(d, RuleKind.XDepartsAfterYArrives, Train(d, "2A01"), Train(d, "1A01"), "bbb", 120);

        ApplyUndo(d, edit, () =>
        {
            var rule = d.Rules.Should().ContainSingle().Subject;
            (rule.Code, rule.Train, rule.Train2, rule.Location, rule.Time).Should().Be((4, "2A01", "1A01", "BBB", 120));
            d.ToWorkingTimetable().Rules.Should().ContainSingle(r => r.Kind == RuleKind.XDepartsAfterYArrives);
        });

        FluentActions.Invoking(() => TimetableParts.AddRule(d, RuleKind.XDepartsAfterYArrives, Train(d, "2A01"), Train(d, "1A01")))
            .Should().Throw<InvalidOperationException>().WithMessage("*location Z*");
        FluentActions.Invoking(() => TimetableParts.AddRule(d, RuleKind.XNotIfY, Train(d, "2A01"), Train(d, "2A01")))
            .Should().Throw<InvalidOperationException>().WithMessage("*two different*");

        ApplyUndo(d, TimetableParts.SetRuleTrain(d.Rules.Single(), second: false, Train(d, "1A01")), () => d.Rules.Single().Train.Should().Be("1A01"));
        ApplyUndo(d, TimetableParts.RemoveRule(d.Rules.Single()), () => d.Root.Element("TimetableRules").Should().BeNull());
    }

    [Fact]
    public void Seed_groups_keep_the_header_summary_and_a_deleted_group_seeds_its_trains_at_the_start()
    {
        var d = Two();
        ApplyUndo(d, TimetableParts.AddSeedGroup(d, "Midday Start", SimTime.Parse("12:00"), Guid.Parse("aa53e1c9-3b10-42d8-b5f4-32dee44284e6")), () =>
            d.Header.GetText("SeedGroupSummary").Should().Be("{AA53E1C9-3B10-42D8-B5F4-32DEE44284E6},\"12:00 Midday Start\""));
        var group = d.SeedGroups.Single();
        ApplyUndo(d, TimetableParts.ChangeSeedGroup(group, "Afternoon", SimTime.Parse("14:30")), () =>
            d.Header.GetText("SeedGroupSummary").Should().EndWith("\"14:30 Afternoon\""));

        foreach (var t in d.Trains)
        {
            d.Apply(TimetableEdit.Change(t, n => { n.SeedPoint = "S1"; n.SeedGroup = group.Id; }));
        }

        TimetableParts.TrainsIn(d, group).Should().HaveCount(2);
        TimetableParts.SeedClashes(d, group).Should().ContainSingle().Which.SeedPoint.Should().Be("S1");
        TimetableParts.SeededAtStart(d).Should().BeEmpty();

        ApplyUndo(d, TimetableParts.RemoveSeedGroup(group), () =>
        {
            d.SeedGroups.Should().BeEmpty();
            d.Trains.Should().OnlyContain(t => t.SeedGroup == null);
            TimetableParts.SeededAtStart(d).Should().HaveCount(2);
            d.Root.Element("SeedGroupSummary")!.Value.Should().BeEmpty("the file keeps its empty summary");
        });
    }

    [Fact]
    public void A_category_is_added_renamed_with_its_trains_and_deleted_only_once_its_trains_move()
    {
        var d = Two();
        var original = d.Categories.Single();
        ApplyUndo(d, TimetableParts.AddCategory(d, "Copy of 999", original, "CAT2"), () =>
            d.Categories.Select(c => (c.Id, c.Description, c.MaxSpeed)).Should().Equal(("CAT1", "Class 999", 100), ("CAT2", "Copy of 999", 100)));

        d.Apply(TimetableEdit.Change(Train(d, "2A01").Trips.Last(), p => p.NewCategory = "CAT1"));
        ApplyUndo(d, TimetableParts.RenameCategory(original, "CAT9"), () =>
        {
            d.Trains.Should().OnlyContain(t => t.Category == "CAT9");
            Train(d, "2A01").Trips.Last().NewCategory.Should().Be("CAT9");
        });

        TimetableParts.TrainsOf(d, "CAT9").Should().HaveCount(2);
        FluentActions.Invoking(() => TimetableParts.RemoveCategory(original)).Should().Throw<InvalidOperationException>().WithMessage("2 trains use*1A01, 2A01*");
        var copy = d.Categories.Single(c => c.Id == "CAT2");
        ApplyUndo(d, TimetableParts.RemoveCategory(original, copy), () =>
        {
            d.Categories.Should().ContainSingle().Which.Id.Should().Be("CAT2");
            d.Trains.Should().OnlyContain(t => t.Category == "CAT2");
        });

        var fresh = TimetableParts.AddCategory(d, "New type");
        d.Apply(fresh);
        d.Categories.Last().Should().Match<CategoryNode>(c => c.Id!.Length == 8 && c.GetDwell("StationForward") == 45);
        TimetableParts.SpeedClassBits(8 | 512).Should().Equal(4, 10);
        TimetableParts.WithSpeedClassBit(8, 10, true).Should().Be(520);
        TimetableParts.WithSpeedClassBit(520, 4, false).Should().Be(512);
    }

    [Fact]
    public void Lists_and_scenario_options_are_edited_by_field()
    {
        var d = Two();
        ApplyUndo(d, TimetableParts.AddListItem(d, "Decisions", TimetableParts.NewDecision("DOTW", "Day of the week", "MON", "TUE")), () =>
            d.Decisions!.Items.Should().ContainSingle().Which.Element.Element("Choices")!.Elements().Should().HaveCount(2));
        var decision = d.Decisions!.Items.Single();
        ApplyUndo(d, TimetableParts.AddChoice(decision, "WED"), () => decision.Element.Element("Choices")!.Elements().Should().HaveCount(3));
        FluentActions.Invoking(() => TimetableParts.AddChoice(decision, "mon")).Should().Throw<InvalidOperationException>();
        ApplyUndo(d, TimetableParts.SetItemField(decision, "@ID", "DAY"), () => ((string?)decision.Element.Attribute("ID")).Should().Be("DAY"));
        ApplyUndo(d, TimetableParts.SetItemField(decision, "Description", "Which day"), () => decision.GetText("Description").Should().Be("Which day"));

        ApplyUndo(d, TimetableParts.SetScenarioOptions(d, [("NERA", "2"), ("NENETEL", "1")]), () =>
            TimetableParts.ScenarioOptions(d).Should().Equal(("NERA", "2"), ("NENETEL", "1")));
        ApplyUndo(d, TimetableParts.SetScenarioOptions(d, []), () => d.Root.Element("ScenarioOptions")!.Value.Should().BeEmpty());
    }

    [Fact]
    public void Candidates_for_an_activity_are_the_trains_there_then_the_rest()
    {
        var d = WttDocument.Parse(Wtt.Document(
            Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("2A01", dep: 36600, from: "BBB", to: "AAA") + Wtt.SimpleTrain("0Z01", dep: 36400, from: "CCC", to: "BBB")
            + Wtt.SimpleTrain("9X99", dep: 80000, from: "BBB", to: "AAA")));
        var import = d.Import();

        var at = Train(d, "1A01").Trips.Last();
        // 1A01 reaches BBB at 10:05; 2A01 starts there at 10:10, 0Z01 arrives at 10:11:40, 9X99 is hours later.
        TimetableParts.Candidates(import, at, ActivityKind.Next).Select(t => t.Headcode).Should().Equal("2A01", "0Z01", "9X99");
        var later = WttDocument.Parse(Wtt.Document(
            Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("2A01", dep: 36900, from: "BBB", to: "AAA") + Wtt.SimpleTrain("0Z01", dep: 35900, from: "CCC", to: "BBB")));
        TimetableParts.Candidates(later.Import(), Train(later, "1A01").Trips.Last(), ActivityKind.Next).Select(t => t.Headcode)
            .Should().Equal(["2A01", "0Z01"], "a train formed there comes first for a next working");
        TimetableParts.Candidates(later.Import(), Train(later, "1A01").Trips.Last(), ActivityKind.Join).Select(t => t.Headcode)
            .Should().Equal(["0Z01", "2A01"], "otherwise the nearest in time");
    }
}
