using System.Xml.Linq;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;

namespace Stringline.App.Tests;

/// <summary>
/// S45: the activities panel and the Rules, Seed groups, Categories and Lists tabs, driven through their view models
/// on a draft of Waterloo. Each change is an edit that undo takes back to the same tree.
/// </summary>
public sealed class DraftPartsTests : IDisposable
{
    private const string Waterloo = "waterloo 2010.WTT";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-parts-app-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Cleaned up eventually.
        }
    }

    private TestApp App() => new(s => s with { TimetableFolder = Path.GetDirectoryName(Fixtures.Path(Waterloo)) }, root: _root.FullName);

    private async Task<TimetableEditorViewModel> Draft(TestApp app)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "timetables")).FullName;
        var source = Path.Combine(folder, Waterloo);
        File.Copy(Fixtures.Path(Waterloo), source, overwrite: true);
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(source));
        await app.Ui.WaitUntil(() => vm.NewDraftFromLoadedCommand.CanExecute(null), "the timetable loaded");
        await app.Ui.Run(() => vm.NewDraftFromLoadedCommand.Execute(null));
        return await app.Ui.Run(() => vm.Draft!);
    }

    private static string Tree(WttDocument document) => document.Root.ToString(SaveOptions.DisableFormatting);

    private static void Select(TimetableEditorViewModel draft, string headcode) =>
        draft.SelectedTrainRow = draft.Trains.First(r => new Timetable.Editing.TrainNode(draft.Document, r.Element).Headcode == headcode);

    [Fact]
    public async Task Activities_are_added_at_the_grids_point_refused_where_SimSig_would_and_undone()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.Run(() =>
        {
            var before = Tree(draft.Document);
            Select(draft, "1A11");
            var train = draft.Train!;
            train.CurrentTrip = train.Trips[0];
            train.ActivitiesHeading.Should().StartWith("Activities at ");

            // A next working at the first point is refused, and says why.
            train.NewActivityKind = TimetableParts.Name(Core.Timetables.ActivityKind.Next);
            train.ActivityCandidates.Should().NotBeEmpty("trains booked there near the time are offered");
            train.NewActivityTrain = train.ActivityCandidates[0].Label;
            train.AddActivityCommand.Execute(null);
            draft.Error.Should().Contain("last location");

            // A crew change there is fine, timed at the point.
            train.NewActivityKind = TimetableParts.Name(Core.Timetables.ActivityKind.CrewChange);
            train.AddActivityCommand.Execute(null);
            draft.Error.Should().BeNull();
            train.Activities.Should().ContainSingle().Which.KindName.Should().Be("Crew change");
            train.Activities[0].CrewTime.Should().NotBeEmpty();
            train.Activities[0].Duration = "90";
            new ActivityNode(draft.Document, train.Activities[0].Element).ActivityDuration.Should().Be(90);
            train.Trips[0].Activities.Should().Contain("CC", "the grid's column shows it");

            train.SelectedActivity = train.Activities[0];
            train.RemoveActivityCommand.Execute(null);
            train.Activities.Should().BeEmpty();
            draft.UndoCommand.Execute(null);
            draft.UndoCommand.Execute(null);
            draft.UndoCommand.Execute(null);
            Tree(draft.Document).Should().Be(before);
        });
    }

    [Fact]
    public async Task Rules_are_listed_filtered_by_train_added_edited_and_deleted()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.Run(() =>
        {
            var rules = draft.Rules;
            var all = rules.Rows.Count;
            all.Should().Be(draft.Document.Rules.Count());
            var named = rules.Rows[0].X;

            Select(draft, named);
            rules.ThisTrainOnly = true;
            rules.Rows.Should().NotBeEmpty().And.HaveCountLessThan(all).And.OnlyContain(r => r.X == named || r.Y == named);
            rules.ThisTrainOnly = false;

            rules.NewKind = TimetableParts.Wording(Core.Timetables.RuleKind.XAppearsAfterYLeaves);
            rules.NewX = "1A11";
            rules.NewY = "1A12";
            rules.NewMinutes = "2H";
            rules.AddCommand.Execute(null);
            draft.Error.Should().BeNull();
            var added = rules.Rows.Should().HaveCount(all + 1).And.Subject.Last();
            (added.X, added.Y, added.Minutes).Should().Be(("1A11", "1A12", "2H"));
            new RuleNode(draft.Document, added.Element).Time.Should().Be(150);

            added.Minutes = "5";
            new RuleNode(draft.Document, added.Element).Time.Should().Be(300);
            added.Y = "no such train";
            draft.Error.Should().Contain("no train");
            added.Y.Should().Be("1A12", "a refused cell shows its value again");

            rules.Selected = added;
            rules.RemoveCommand.Execute(null);
            rules.Rows.Should().HaveCount(all);
        });
    }

    [Fact]
    public async Task A_seed_group_is_made_used_and_deleted_seeding_its_trains_at_the_start_after_asking()
    {
        await using var app = App();
        var draft = await Draft(app);
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Value = "Midday";
            p.Fields[1].Value = "1200";
            return true;
        });
        await app.Ui.Run(() =>
        {
            var groups = draft.SeedGroups;
            groups.AddCommand.Execute(null);
            var group = groups.Rows.Should().ContainSingle().Subject;
            (group.Title, group.Start).Should().Be(("Midday", "12:00"));
            draft.Document.Header.GetText("SeedGroupSummary").Should().Be($"{group.Id},\"12:00 Midday\"");

            Select(draft, "1A11");
            draft.Train!.SeedPoint = "WLOO";
            draft.Train.SeedGroup = group.Id;
            group.Trains.Should().Be("1 train: 1A11");

            group.Title = "Noon";
            draft.Document.Header.GetText("SeedGroupSummary").Should().EndWith("\"12:00 Noon\"");

            groups.Selected = group;
            app.Dialogs.Choices.Enqueue(1);
            groups.RemoveCommand.Execute(null);
            groups.Rows.Should().ContainSingle("cancelled");
            app.Dialogs.Choices.Enqueue(0);
            groups.RemoveCommand.Execute(null);
            groups.Rows.Should().BeEmpty();
            app.Dialogs.Asked[^1].Should().Contain("1A11").And.Contain("start");
            draft.Train.SeedGroup.Should().BeEmpty();
            groups.AtStart.Should().Contain("1A11");
        });
    }

    [Fact]
    public async Task Categories_are_renamed_with_their_trains_ticked_and_deleted_once_their_trains_move()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.Run(() =>
        {
            var categories = draft.Categories;
            var row = categories.Rows.First(r => r.UsedBy != "0");
            var users = int.Parse(row.UsedBy, System.Globalization.CultureInfo.CurrentCulture);
            var old = row.Id;

            row.Id = "NEWID001";
            draft.Error.Should().BeNull();
            TimetableParts.TrainsOf(draft.Document, "NEWID001").Should().HaveCount(users);
            TimetableParts.TrainsOf(draft.Document, old).Should().BeEmpty();

            var bit = row.Bits[9];
            var was = bit.IsSet;
            bit.IsSet = !was;
            row.Bits[9].IsSet.Should().Be(!was);
            row["StationForward"] = "50";
            new CategoryNode(draft.Document, row.Element).GetDwell("StationForward").Should().Be(50);

            var other = categories.Rows.First(r => r != row);
            app.Dialogs.Prompts.Enqueue(p =>
            {
                p.Fields[0].Value = new CategoryNode(draft.Document, other.Element).Description ?? other.Id;
                return true;
            });
            categories.Selected = row;
            categories.RemoveCommand.Execute(null);
            draft.Error.Should().BeNull();
            categories.Rows.Should().NotContain(row);
            TimetableParts.TrainsOf(draft.Document, other.Id).Should().HaveCountGreaterThanOrEqualTo(users);
        });
    }

    [Fact]
    public async Task Decisions_choices_and_scenario_options_are_edited_as_lists()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.Run(() =>
        {
            var lists = draft.Lists;
            lists.AddDecisionCommand.Execute(null);
            var decision = lists.Decisions.Should().ContainSingle().Subject;
            decision["@ID"].Should().Be("D1");
            lists.SelectedDecision = decision;
            lists.Choices.Should().HaveCount(2);
            lists.AddChoiceCommand.Execute(null);
            lists.Choices.Should().HaveCount(3);
            lists.Choices[2]["Announcement"] = "Today is Wednesday";
            lists.Choices[2].Element.Element("Announcement")!.Value.Should().Be("Today is Wednesday");
            decision["Description"] = "Day of the week";
            draft.Document.Decisions!.Items.Single().GetText("Description").Should().Be("Day of the week");

            lists.AddScenarioOptionCommand.Execute(null);
            lists.Scenario.Should().ContainSingle();
            lists.Scenario[0].Option = "NERA";
            lists.Scenario[0].Value = "2";
            draft.Document.Header.ScenarioOptions.Should().Be("NERA,2");
            lists.RemoveScenarioOptionCommand.Execute(lists.Scenario[0]);
            lists.Scenario.Should().BeEmpty();
        });
    }
}
