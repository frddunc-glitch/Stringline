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
    public async Task A_trains_details_show_its_category_by_name_and_offer_the_sims_entry_and_seed_points_names_and_codes()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.WaitUntil(() => draft.Editor.Knowledge is not null, "the sim's knowledge");
        await app.Ui.Run(() =>
        {
            Select(draft, "1A11");
            var train = draft.Train!;
            var node = train.Node;

            // #27: the category by its name, picked by name, a trip's new category too; a name no category has is refused.
            var own = draft.Document.Categories.Single(c => c.Id == node.Category);
            train.Category.Should().Be(own.Description);
            var other = train.CategoryChoices.First(c => c.Code != node.Category);
            train.Category = other.Label;
            train.Node.Category.Should().Be(other.Code);
            train.Category.Should().Be(other.Label);
            train.Category = "No such train type";
            draft.Error.Should().Contain("no category 'No such train type'");
            train.Node.Category.Should().Be(other.Code);
            train.Trips[^1].NewCategory = own.Description!;
            new TripNode(draft.Document, train.Trips[^1].Element).NewCategory.Should().Be(own.Id);
            train.Trips[^1].NewCategory.Should().Be(own.Description);

            // #26: entry and seed points the sim's timetables use, most used first.
            train.EntryPointChoices.Select(c => c.Code).Should().Contain(draft.Document.Trains.Select(t => t.EntryPoint).OfType<string>().Distinct());
            train.EntryPointChoices.Select(c => c.Code).Should().OnlyHaveUniqueItems();
            train.SeedPointChoices.Should().NotBeEmpty();

            // #28: origin and destination names, this draft's among them.
            train.OriginName = "Woking Up Yard";
            train.JourneyNameChoices.Should().Contain("Woking Up Yard");

            // #33: a code typed at a location is offered there, and remembered for the sim after it is undone.
            var row = train.Trips[1];
            row.Platform = "Z9";
            var platforms = draft.ChoicesAt(row.Location).Platforms;
            platforms.Should().Contain(c => c.Code == "Z9" && c.Label.Contains("this draft"));
            draft.UndoCommand.Execute(null);
            draft.ChoicesAt(row.Location).Platforms.Should().Contain(c => c.Code == "Z9" && c.Label.Contains("typed before"));
            new Editing.Knowledge.TypedCodesStore(app.Paths.Profiles).Load(draft.Document.Header.AreaId!).At(new Core.Tiploc(row.Location))!.Platforms.Should().Contain("Z9");
        });
    }

    [Fact]
    public async Task Caution_speed_sets_and_their_rules_are_made_ordered_chosen_by_a_category_and_deleted()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.Run(() =>
        {
            // #31: a set, named when it is made.
            var lists = draft.Lists;
            app.Dialogs.Prompts.Enqueue(p =>
            {
                p.Fields[0].Value = "Passenger";
                return true;
            });
            lists.AddCautionSetCommand.Execute(null);
            var set = lists.CautionSets.Should().ContainSingle().Subject;
            lists.SelectedCautionSet.Should().Be(set);

            // Rules by aspect and kind of value; a new one goes below the selected one, and they move.
            lists.AddCautionSpeedCommand.Execute(null);
            var first = lists.CautionSpeeds.Should().ContainSingle().Subject;
            first.Aspect.Should().Be("Y");
            first.NowType.Should().Be("Not applicable");
            first.Aspect = "YY";
            first.ReduceNow = "67";
            first.NowType = "Percent of line speed";
            first.Element.Element("AspectPassed")!.Value.Should().Be("4");
            first.Element.Element("NowValueType")!.Value.Should().Be("2");
            first.Aspect = "Q";
            draft.Error.Should().Contain("'Q' is not an aspect");
            lists.SelectedCautionSpeed = first;
            lists.AddCautionSpeedCommand.Execute(null);
            lists.CautionSpeeds.Select(r => r.Aspect).Should().Equal("YY", "Y");
            lists.SelectedCautionSpeed = lists.CautionSpeeds[1];
            lists.MoveCautionSpeedUpCommand.Execute(null);
            lists.CautionSpeeds.Select(r => r.Aspect).Should().Equal(["Y", "YY"], "SimSig tries them from the top");

            // A category chooses it by name, and goes back to SimSig's rules when it is deleted (asked first).
            var category = draft.Categories.Rows[0];
            draft.Categories.CautionSetNames.Should().Equal(CautionSetChoice.Standard, "Passenger");
            category.CautionSpeedSet = "Passenger";
            new CategoryNode(draft.Document, category.Element).CautionSpeedSet.Should().Be(set["@ID"]);
            app.Dialogs.Choices.Enqueue(0);
            lists.RemoveCautionSetCommand.Execute(null);
            app.Dialogs.Asked[^1].Should().Contain("Passenger").And.Contain("standard driving rules");
            lists.CautionSets.Should().BeEmpty();
            category.CautionSpeedSet.Should().Be(CautionSetChoice.Standard);
        });
    }

    [Fact]
    public async Task Filling_in_timing_points_adds_the_locations_other_timetables_book_greyed_as_one_edit()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.WaitUntil(() => draft.Editor.Knowledge is not null, "the sim's knowledge");
        await app.Ui.Run(() =>
        {
            // #32: a train with a booked pass taken out has it back, as a pass between its neighbours' times.
            var (train, middle) = draft.Trains.Select(r =>
            {
                draft.SelectedTrainRow = r;
                var t = draft.Train!;
                return (Train: t, Middle: t.Trips.Skip(1).SkipLast(1).FirstOrDefault(p => p.Departure.Contains('/', StringComparison.Ordinal)
                    && t.Trips[p.Index - 1].Departure.Length > 0 && t.Trips[p.Index + 1].Departure.Length > 0));
            }).First(x => x.Middle is not null);
            var location = middle!.Location;
            var (before, after) = (train.Trips[middle.Index - 1].Location, train.Trips[middle.Index + 1].Location);
            train.DeletePoints([middle]);
            train.Trips.Should().NotContain(r => r.Location == location);

            app.Dialogs.Choices.Enqueue(0);
            draft.FillTimingPointsCommand.Execute(null);
            draft.Error.Should().BeNull();
            var back = train.Trips.Should().ContainSingle(r => r.Location == location).Subject;
            train.Trips.Select(r => r.Location).Should().ContainInOrder(before, location, after);
            back.IsAdded.Should().BeTrue("Stringline added it");
            back.Departure.Should().Contain("/");
            draft.UndoHeader.Should().Contain("Fill in timing points");
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

    [Fact]
    public async Task Bulk_tools_preview_their_changes_as_ghosts_then_apply_as_one_edit_or_nothing()
    {
        await using var app = App();
        var draft = await Draft(app);
        await app.Ui.WaitUntil(() => draft.Board.IsReady, "the draft board");
        await app.Ui.Run(() =>
        {
            var before = Tree(draft.Document);
            var picked = draft.Trains.Where(r => new TrainNode(draft.Document, r.Element).Headcode is "1A11" or "1A12").ToList();
            draft.SelectedTrainRow = picked[0];
            draft.SelectedTrainRows = picked;
            var departs = picked.Select(r => new TrainNode(draft.Document, r.Element).Trips.First().DepPassTime).ToList();

            // Shift +2H, cancelled at the preview: nothing changes, and the ghosts go.
            app.Dialogs.Prompts.Enqueue(p => { p.Fields[0].Value = "+2H"; return true; });
            app.Dialogs.Choices.Enqueue(1);
            draft.ShiftTimesCommand.Execute(null);
            app.Dialogs.Asked[^1].Should().Contain("1A11: +2½ min").And.Contain("drawn on the board");
            Tree(draft.Document).Should().Be(before);
            draft.Board.Board.Ghosts.Should().BeNull();

            // Applied: one edit, one undo.
            app.Dialogs.Prompts.Enqueue(p => { p.Fields[0].Value = "+2H"; return true; });
            app.Dialogs.Choices.Enqueue(0);
            draft.ShiftTimesCommand.Execute(null);
            picked.Select(r => new TrainNode(draft.Document, r.Element).Trips.First().DepPassTime).Should().Equal(departs.Select(d => d + 150));
            draft.UndoHeader.Should().Contain("Shift 2 trains");
            draft.UndoCommand.Execute(null);
            Tree(draft.Document).Should().Be(before);

            // Repeat one train as a pattern: the copies are added after it.
            draft.SelectedTrainRows = [picked[0]];
            var count = draft.Document.Trains.Count();
            app.Dialogs.Prompts.Enqueue(p => { p.Fields[0].Value = "60"; p.Fields[1].Value = DraftText.Time(new TrainNode(draft.Document, picked[0].Element).Trips.First().DepPassTime! + 7200); p.Fields[2].Value = "0"; return true; });
            app.Dialogs.Choices.Enqueue(0);
            draft.RepeatPatternCommand.Execute(null);
            draft.Document.Trains.Count().Should().Be(count + 2);

            // A tool with nothing to do says so.
            draft.SelectedTrainRows = [];
            draft.SelectedTrainRow = null;
            draft.AutoFillCommand.Execute(null);
            draft.Error.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task A_draft_from_the_working_plan_has_the_sessions_re_plans_and_lists_the_rest()
    {
        await using var app = App();
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "timetables")).FullName;
        var source = Path.Combine(folder, Waterloo);
        File.Copy(Fixtures.Path(Waterloo), source, overwrite: true);
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(source));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Count > 0, "the plan");
        var (retimed, cancelled) = await app.Ui.Run(() => (vm.Snapshot.Plan.Trains[0], vm.Snapshot.Plan.Trains[1]));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new Planning.ShiftTrain(retimed.Id, 0, 300)));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new Planning.CancelTrain(cancelled.Id)));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new Planning.AddNote(new Planning.PlanNote("N1", retimed.Points[0].Location, Core.SimTime.Parse("08:00"), "Fog"))));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Modified.Count() == 2 && vm.Snapshot.Plan.Notes.Count == 1, "the re-plans");

        app.Dialogs.Choices.Enqueue(0);
        await app.Ui.Run(() =>
        {
            vm.NewDraftFromPlanCommand.Execute(null);
            app.Dialogs.Asked[^1].Should().Contain("1 train is cancelled");
            var draft = vm.Draft!;
            var after = draft.Snapshot.Timetable;
            after.Find(retimed.Id)!.Points[0].Depart.Should().Be(retimed.Points[0].Depart + 300);
            after.Find(cancelled.Id).Should().BeNull();
            draft.UndoHeader.Should().Contain("Draft from the working plan");
            app.Dialogs.Lists[^1].Lines.Should().Contain(l => l.Contains("Fog", StringComparison.Ordinal));
            vm.Snapshot.Timetable!.Find(cancelled.Id).Should().NotBeNull("the session's timetable is untouched");
        });
    }

    [Fact]
    public async Task Trains_are_exported_as_a_sheet_and_a_changed_sheet_is_imported_as_one_edit()
    {
        await using var app = App();
        var draft = await Draft(app);
        var csv = Path.Combine(_root.FullName, "trains.csv");
        app.Dialogs.SaveFiles.Enqueue(csv);
        await app.Ui.Run(() => draft.ExportCsvCommand.Execute(null));
        File.Exists(csv).Should().BeTrue();

        var lines = File.ReadAllLines(csv).ToList();
        var row = lines.FindIndex(l => l.StartsWith("1A11,", StringComparison.Ordinal) && l.Contains(",1,", StringComparison.Ordinal));
        var fields = lines[row].Split(',');
        fields[9] = "9";
        lines[row] = string.Join(",", fields);
        File.WriteAllLines(csv, lines);

        app.Dialogs.Files.Enqueue(csv);
        app.Dialogs.Choices.Enqueue(0);
        await app.Ui.Run(() =>
        {
            draft.ImportCsvCommand.Execute(null);
            app.Dialogs.Asked[^1].Should().Contain("1A11: 1 point changed");
            new TrainNode(draft.Document, draft.Trains.First(r => r.Headcode == "1A11").Element).Trips.First().Platform.Should().Be("9");
            draft.UndoHeader.Should().Contain("Import");
        });
    }

    [Fact]
    public async Task A_test_run_read_back_puts_its_findings_in_the_issues_panel_and_a_fix_clears_its_issue()
    {
        await using var app = App();
        var draft = await Draft(app);
        app.Dialogs.Files.Enqueue(Fixtures.Path("waterloo-4h.sslog"));

        await app.Ui.RunAsync(() => draft.TestReportCommand.ExecuteAsync(null));

        await app.Ui.Run(() =>
        {
            draft.TestIssues.Should().Contain(i => i.Code == Editing.Validation.IssueCodes.TestSlowSection);
            draft.IssueLevel = IssueLevel.All;
            var row = draft.Issues.First(r => r.Code == Editing.Validation.IssueCodes.TestSlowSection);
            var count = draft.TestIssues.Count;
            draft.SelectedIssue = row;
            draft.ApplyFixCommand.Execute(null);
            draft.Error.Should().BeNull();
            draft.TestIssues.Should().HaveCount(count - 1, "the fixed one goes");
            draft.UndoHeader.Should().Contain("by +1 min from VAUX");

            draft.ClearTestReportCommand.Execute(null);
            draft.TestIssues.Should().BeEmpty();
            draft.Issues.Should().NotContain(r => r.Code.StartsWith("test-", StringComparison.Ordinal));
        });
    }
}
