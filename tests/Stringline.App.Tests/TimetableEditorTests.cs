using System.Xml.Linq;
using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Editing.Drafts;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;

namespace Stringline.App.Tests;

/// <summary>
/// The timetable editor in the shell (S43, <c>08-…md</c> §6–7), driven through its view models on Waterloo: a draft,
/// typing in the trip grid, inserting and filling, undo, saving as a new version and reopening, the replace-or-new
/// question, the autosave after a crash, and the session's timetable left alone.
/// </summary>
public sealed class TimetableEditorTests : IDisposable
{
    private const string Waterloo = "waterloo 2010.WTT";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-editor-app-");

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

    /// <summary>The sim's other timetables are the fixtures (Waterloo's own file among them); the draft's source is a copy elsewhere.</summary>
    private TestApp App() => new(s => s with { TimetableFolder = Path.GetDirectoryName(Fixtures.Path(Waterloo)) }, root: _root.FullName);

    private string Source()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "timetables")).FullName;
        var path = Path.Combine(folder, Waterloo);
        if (!File.Exists(path))
        {
            File.Copy(Fixtures.Path(Waterloo), path);
        }

        return path;
    }

    private static string Tree(WttDocument document) => document.Root.ToString(SaveOptions.DisableFormatting);

    /// <summary>Loads the copy of Waterloo and makes a draft of it, with the sim's knowledge in.</summary>
    private static async Task<TimetableEditorViewModel> DraftOfLoaded(TestApp app, string source)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(source));
        await app.Ui.WaitUntil(() => vm.NewDraftFromLoadedCommand.CanExecute(null), "the timetable loaded");
        await app.Ui.Run(() => vm.NewDraftFromLoadedCommand.Execute(null));
        var draft = await app.Ui.Run(() => vm.Draft!);
        await app.Ui.WaitUntil(() => draft.Editor.Knowledge is not null, "the sim's knowledge");
        return draft;
    }

    [Fact]
    public async Task A_draft_of_Waterloo_takes_typed_times_inserted_points_and_fill_down_undoes_and_saves_as_a_new_version()
    {
        var source = Source();
        var sourceBytes = File.ReadAllBytes(source);
        await using var app = App();
        var vm = app.ViewModel;
        var draft = await DraftOfLoaded(app, source);
        var sessionTimetable = await app.Ui.Run(() => vm.LoadedTimetable!.Timetable);
        var planTimetable = await app.Ui.Run(() => vm.Snapshot.Timetable);
        var w51Before = sessionTimetable.Get(new TrainId("1W51"));

        app.Dialogs.EditorsShown.Should().ContainSingle().Which.Should().BeSameAs(draft);
        await app.Ui.Run(() =>
        {
            draft.KnowledgeSummary.Should().Contain("Checked against 1 timetable");
            draft.SelectedTrainRow = draft.Trains.Single(r => r.Headcode == "1W51");
            var train = draft.Train!;
            train.Headcode.Should().Be("1W51");
            train.Trips.Should().HaveCountGreaterThanOrEqualTo(3);

            // Typing 1234H style: the first departure half a minute later.
            var first = train.Trips[0];
            var departure = new TripNode(draft.Document, first.Element).DepPassTime!.Value;
            first.Departure = SimTimeNotation.Format(departure, compact: true) + "H";
            new TripNode(draft.Document, first.Element).DepPassTime.Should().Be(departure + 30);
            first.Departure.Should().Be(SimTimeNotation.Format(departure + 30));
            draft.Error.Should().BeNull();

            // A time that cannot be read is refused, and the cell shows its value again.
            first.Arrival = "25x";
            draft.Error.Should().Contain("not a time");
            first.Arrival.Should().BeEmpty();

            // Insert a point above the third, and name it.
            train.CurrentTrip = train.Trips[2];
            var count = train.Trips.Count;
            train.InsertPointCommand.Execute(null);
            train.Trips.Should().HaveCount(count + 1);
            train.CurrentTrip!.Index.Should().Be(2);
            train.CurrentTrip.Location = "wimbldn";
            train.Trips[2].Location.Should().Be("WIMBLDN");

            // Fill the platform down from the second row over the next two, as one edit, then undo it.
            var rows = new[] { train.Trips[1], train.Trips[2], train.Trips[3] };
            var platforms = rows.Select(r => r.Platform).ToList();
            train.FillDown(nameof(TripRow.Platform), rows);
            rows.Select(r => r.Platform).Should().AllBe(platforms[0]);
            draft.UndoHeader.Should().Contain("fill Platform down");
            draft.UndoCommand.Execute(null);
            rows.Select(r => r.Platform).Should().Equal(platforms);
            draft.Snapshot.IsDirty.Should().BeTrue();
            draft.Title.Should().EndWith("*");
        });

        // The session's timetable and its file are untouched.
        (await app.Ui.Run(() => vm.LoadedTimetable!.Timetable)).Should().BeSameAs(sessionTimetable);
        sessionTimetable.Get(new TrainId("1W51")).Should().BeSameAs(w51Before);
        (await app.Ui.Run(() => vm.Snapshot.Timetable)).Should().BeSameAs(planTimetable, "the plan keeps the session's timetable");
        File.ReadAllBytes(source).Should().Equal(sourceBytes);

        // Save: a new file beside the source, named by the convention, at the next build.
        var saved = Path.Combine(Path.GetDirectoryName(source)!, "Waterloo 2010 Stringline.wtt");
        app.Dialogs.Choices.Enqueue(0);
        app.Dialogs.Choices.Enqueue(0);
        app.Dialogs.SaveFiles.Enqueue(saved);
        await app.Ui.Run(() => vm.SaveDraftCommand.Execute(null));

        app.Dialogs.Asked[0].Should().Contain("Save it as a new timetable beside it").And.Contain("waterloo 2010.WTT");
        app.Dialogs.SaveSuggestions.Should().ContainSingle().Which.Should().Be(saved);
        File.ReadAllBytes(source).Should().Equal(sourceBytes, "the shipped timetable stays as it is");
        var (tree, savedPath, dirty) = await app.Ui.Run(() => (Tree(draft.Document), draft.Editor.TargetPath, draft.Snapshot.IsDirty));
        savedPath.Should().Be(saved);
        dirty.Should().BeFalse();
        var reopened = WttDocument.Load(saved);
        Tree(reopened).Should().Be(tree);
        reopened.Header.Version.Should().Be(new WttVersion(1, 0, 3));

        // Close it, and open the draft again: the same document, with its undo history.
        await app.Ui.Run(() => vm.CloseDraftCommand.Execute(null));
        (await app.Ui.Run(() => vm.Draft)).Should().BeNull();
        await app.Ui.Run(() => vm.RestoreDraft(draft.DraftPath));
        var again = await app.Ui.Run(() => vm.Draft!);
        await app.Ui.Run(() =>
        {
            Tree(again.Document).Should().Be(tree);
            again.Snapshot.IsDirty.Should().BeFalse();
            again.Editor.TargetPath.Should().Be(saved);
            again.Snapshot.CanUndo.Should().BeTrue();
        });
    }

    [Fact]
    public async Task The_first_save_of_a_SimSig_timetable_asks_to_replace_it_or_save_a_new_file()
    {
        var source = Source();
        await using var app = App();
        var vm = app.ViewModel;
        await app.Ui.Run(() => vm.OpenNewDraft(source));
        var draft = await app.Ui.Run(() => vm.Draft!);
        await app.Ui.Run(() =>
        {
            draft.SelectedTrainRow = draft.Trains.Single(r => r.Headcode == "1W51");
            draft.Train!.Notes = "Changed";
        });

        // Cancel: nothing saved.
        app.Dialogs.Choices.Enqueue(2);
        await app.Ui.Run(() => draft.SaveDraftCommand.Execute(null));
        (await app.Ui.Run(() => draft.Editor.TargetPath)).Should().BeNull();

        // Replace, but not confirmed: nothing saved.
        app.Dialogs.Choices.Enqueue(1);
        app.Dialogs.Choices.Enqueue(1);
        await app.Ui.Run(() => draft.SaveDraftCommand.Execute(null));
        (await app.Ui.Run(() => draft.Editor.TargetPath)).Should().BeNull();
        app.Dialogs.Asked[^1].Should().Contain("Replace").And.Contain("backups");

        // Replace, confirmed: the source is replaced at the next build, and backed up first.
        app.Dialogs.Choices.Enqueue(1);
        app.Dialogs.Choices.Enqueue(0);
        await app.Ui.Run(() => draft.SaveDraftCommand.Execute(null));
        (await app.Ui.Run(() => draft.Editor.TargetPath)).Should().Be(source);
        WttDocument.Load(source).Trains.First(t => t.Headcode == "1W51").Notes.Should().Be("Changed");
        Directory.EnumerateFiles(app.Paths.TimetableBackups, "*.wtt", SearchOption.AllDirectories).Should().ContainSingle()
            .Which.Should().Contain("1.0.2");
        app.Dialogs.SaveSuggestions.Should().BeEmpty("replacing asks for no file");

        // Later saves go to the same file without asking.
        var asked = app.Dialogs.Asked.Count;
        await app.Ui.Run(() => draft.Train!.Notes = "Again");
        await app.Ui.Run(() => draft.SaveDraftCommand.Execute(null));
        app.Dialogs.Asked.Should().HaveCount(asked);
        WttDocument.Load(source).Header.Version.Should().Be(new WttVersion(1, 0, 4));

        // Release: the minor number goes up and the build goes back to 0.
        app.Dialogs.Choices.Enqueue(0);
        await app.Ui.Run(() => vm.ReleaseDraftCommand.Execute(null));
        WttDocument.Load(source).Header.Version.Should().Be(new WttVersion(1, 1, 0));
    }

    [Fact]
    public async Task Unsaved_changes_are_asked_about_on_close_and_on_exit()
    {
        var source = Source();
        await using var app = App();
        var vm = app.ViewModel;
        await app.Ui.Run(() => vm.OpenNewDraft(source));
        var draft = await app.Ui.Run(() => vm.Draft!);

        // No changes: closes without asking.
        (await app.Ui.Run(() => vm.ConfirmCloseDraft())).Should().BeTrue();
        app.Dialogs.Asked.Should().BeEmpty();

        await app.Ui.Run(() => vm.OpenNewDraft(source));
        draft = await app.Ui.Run(() => vm.Draft!);
        await app.Ui.Run(() => draft.Header.Name = "Changed name");

        app.Dialogs.Choices.Enqueue(3);
        (await app.Ui.Run(() => vm.ConfirmCloseDraft())).Should().BeFalse("Cancel keeps the app open");
        app.Dialogs.Asked[^1].Should().Contain("changes not saved");
        (await app.Ui.Run(() => vm.Draft)).Should().BeSameAs(draft);

        // Keep as a draft: closed, kept in the folder, not offered at the next start.
        app.Dialogs.Choices.Enqueue(1);
        (await app.Ui.Run(() => vm.ConfirmCloseDraft())).Should().BeTrue();
        (await app.Ui.Run(() => vm.Draft)).Should().BeNull();
        File.Exists(draft.DraftPath).Should().BeTrue();
        new DraftStore(app.Paths.Drafts).FindUnclosed().Should().BeNull();
        WttDocument.Load(draft.DraftPath).Header.Name.Should().Be("Changed name");

        // Discard: the draft's files go.
        await app.Ui.Run(() => vm.RestoreDraft(draft.DraftPath));
        draft = await app.Ui.Run(() => vm.Draft!);
        app.Dialogs.Choices.Enqueue(2);
        app.Dialogs.Choices.Enqueue(0);
        await app.Ui.Run(() => vm.CloseDraftCommand.Execute(null));
        File.Exists(draft.DraftPath).Should().BeFalse();
        File.Exists(DraftStore.StatePath(draft.DraftPath)).Should().BeFalse();
    }

    [Fact]
    public async Task A_draft_open_at_a_crash_is_autosaved_and_offered_back_with_its_history()
    {
        var source = Source();
        string draftPath;
        string tree;
        await using (var first = App())
        {
            var vm = first.ViewModel;
            await first.Ui.Run(() => vm.OpenNewDraft(source));
            var draft = await first.Ui.Run(() => vm.Draft!);
            await first.Ui.Run(() =>
            {
                draft.SelectedTrainRow = draft.Trains.Single(r => r.Headcode == "1W51");
                draft.Train!.Trips[0].Platform = "9";
            });
            draftPath = draft.DraftPath;
            tree = await first.Ui.Run(() => Tree(draft.Document));

            // The autosave a moment after the edit, with nothing closed: then the "crash" (the second app starts over the same files).
            await first.Ui.WaitUntil(() => DraftStore.ReadState(draftPath)?.Undo.Count == 1, "the autosave", seconds: 15);
            await using var second = App();
            second.Dialogs.Choices.Enqueue(0);
            await second.Ui.Run(() => second.ViewModel.OfferRestoreDraft());

            second.Dialogs.Asked.Should().ContainSingle().Which.Should().Contain("was open when Stringline last stopped");
            var restored = await second.Ui.Run(() => second.ViewModel.Draft!);
            await second.Ui.Run(() =>
            {
                Tree(restored.Document).Should().Be(tree);
                restored.Snapshot.IsDirty.Should().BeTrue();
                restored.UndoHeader.Should().Contain("platform 9");
                restored.UndoCommand.Execute(null);
                restored.Document.Trains.First(t => t.Headcode == "1W51").Trips.First().Platform.Should().NotBe("9");
                restored.Snapshot.IsDirty.Should().BeFalse();
            });
        }

        // Declined at a start: not offered again.
        await using var third = App();
        DraftStore.MarkClosed(draftPath);
        await third.Ui.Run(() => third.ViewModel.OfferRestoreDraft());
        third.Dialogs.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task Issues_take_you_to_the_train_and_point_and_quick_fixes_are_undoable_edits()
    {
        var source = Source();
        await using var app = App();
        var draft = await DraftOfLoaded(app, source);
        await app.Ui.Run(() =>
        {
            // A departure before the one before it: an Error on that point.
            draft.SelectedTrainRow = draft.Trains.Single(r => r.Headcode == "1W51");
            var train = draft.Train!;
            var first = new TripNode(draft.Document, train.Trips[0].Element).DepPassTime!.Value;
            var point = train.Trips[2];
            point.Arrival = string.Empty;
            point.Departure = SimTimeNotation.Format(first - 600);
            draft.Issues.Should().Contain(i => i.Train == "1W51" && i.Issue.Element == point.Element);
            draft.SelectedTrainRow.Worst.Should().Be(Editing.Validation.IssueSeverity.Error);
            point.Worst.Should().Be(Editing.Validation.IssueSeverity.Error);
            point.IssueTip.Should().Contain("earlier");

            // Clicking the issue goes to the train and the point.
            draft.SelectedTrainRow = draft.Trains.Single(r => r.Headcode == "2P05");
            draft.SelectedIssue = draft.Issues.First(i => i.Train == "1W51" && i.Issue.Element == point.Element);
            draft.Train!.Headcode.Should().Be("1W51");
            draft.Train.CurrentTrip!.Element.Should().BeSameAs(point.Element);

            // A seeded train given an arrival at its seed location: a note whose quick fix clears it, undone like any edit.
            var seeded = draft.Document.Trains.First(t => t.SeedPoint is not null && t.Trips.Any());
            var start = seeded.Trips.First();
            if (start.ArrTime is null)
            {
                draft.TryApply(TimetableEdit.Change(start, t => t.ArrTime = t.DepPassTime - 60)).Should().BeTrue();
            }

            draft.IssueLevel = IssueLevel.All;
            var issue = draft.Issues.First(i => i.Code == Editing.Validation.IssueCodes.SeedArrivalTime && i.Issue.TrainElement == seeded.Element);
            issue.HasFix.Should().BeTrue();
            var before = Tree(draft.Document);
            draft.SelectedIssue = issue;
            draft.ApplyFixCommand.Execute(null);
            draft.Error.Should().BeNull();
            start.ArrTime.Should().BeNull();
            draft.Issues.Should().NotContain(i => i.Code == Editing.Validation.IssueCodes.SeedArrivalTime && i.Issue.TrainElement == seeded.Element);
            draft.UndoCommand.Execute(null);
            Tree(draft.Document).Should().Be(before);
        });
    }

    [Fact]
    public async Task A_blank_draft_takes_a_new_train()
    {
        var source = Source();
        await using var app = App();
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(source));
        await app.Ui.WaitUntil(() => vm.NewBlankDraftCommand.CanExecute(null), "the timetable loaded");
        await app.Ui.Run(() => vm.NewBlankDraftCommand.Execute(null));
        var draft = await app.Ui.Run(() => vm.Draft!);

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Value = "2C01";
            return true;
        });
        await app.Ui.Run(() =>
        {
            draft.Trains.Should().BeEmpty();
            draft.Document.Header.AreaId.Should().Be("waterloo");
            draft.Document.Categories.Should().NotBeEmpty();
            draft.NewTrainCommand.Execute(null);
            draft.SelectedTrainRow!.Headcode.Should().Be("2C01");
            draft.Train!.Trips.Should().ContainSingle();
            draft.Train.Trips[0].Location = "WLOO";
            draft.Train.Trips[0].Departure = "0600";
            draft.Train.InsertPointAfterCommand.Execute(null);
            draft.Train.Trips[1].Location = "VAUXHLM";
            draft.Train.Trips[1].Arrival = "+3";
            new TripNode(draft.Document, draft.Train.Trips[1].Element).ArrTime.Should().Be(new SimTime(6 * 3600 + 180));
            draft.Snapshot.Timetable.Trains.Should().ContainSingle(t => t.Id.Value == "2C01");
        });
    }
}
