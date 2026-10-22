using System.Diagnostics;
using System.Xml.Linq;
using Stringline.Core;
using Stringline.Editing.Drafts;
using Stringline.Editing.Validation;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;
using Xunit.Abstractions;
using static Stringline.Editing.Tests.Draft;

namespace Stringline.Editing.Tests;

/// <summary>The draft's single writer (S43): edits, undo and redo, checks, saving, and the autosave with its history.</summary>
public sealed class TimetableEditorTests(ITestOutputHelper output) : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("stringline-editor-");

    public void Dispose() => _folder.Delete(recursive: true);

    private static WttDocument TwoTrains() => Doc(
        Simple("1A01"),
        Train("1B01", Trips(Stop("A", null, "11:00"), Stop("B", "11:10", "11:10"))));

    private static string Tree(WttDocument document) => document.Root.ToString(SaveOptions.DisableFormatting);

    private static TrainNode Node(WttDocument document, string headcode) => document.Trains.First(t => t.Headcode == headcode);

    [Fact]
    public void Edits_undo_and_redo_publish_snapshots_with_the_issues_and_the_draft_state()
    {
        var document = TwoTrains();
        var editor = new TimetableEditor(document);
        var published = new List<DraftSnapshot>();
        editor.SnapshotPublished += (_, s) => published.Add(s);
        var original = Tree(document);
        editor.Current.Validation.Codes().Should().NotContain(IssueCodes.ArrivalBeforePrevious);
        editor.Current.IsDirty.Should().BeFalse();

        var arrival = Node(document, "1B01").Trips.Last();
        editor.Apply(TimetableEdit.Change(arrival, t => t.ArrTime = new SimTime(T("10:50")), "Arrive B 10:50"));

        editor.Current.Validation.Codes().Should().Contain(IssueCodes.ArrivalBeforePrevious);
        editor.Current.IsDirty.Should().BeTrue();
        editor.Current.UndoDescription.Should().Be("Arrive B 10:50");
        editor.Current.Timetable.Get(new TrainId("1B01")).Points[1].Arrive.Should().Be(new SimTime(T("10:50")));

        editor.Undo();
        Tree(document).Should().Be(original);
        editor.Current.Validation.Codes().Should().NotContain(IssueCodes.ArrivalBeforePrevious);
        editor.Current.IsDirty.Should().BeFalse();
        editor.Current.RedoDescription.Should().Be("Arrive B 10:50");

        editor.Redo();
        editor.Current.Validation.Codes().Should().Contain(IssueCodes.ArrivalBeforePrevious);
        published.Select(s => s.Sequence).Should().BeInAscendingOrder().And.HaveCount(3);
        published[^1].Revision.Should().Be(document.Revision);
    }

    [Fact]
    public void A_new_edit_clears_redo_and_an_edit_that_does_not_fit_changes_nothing()
    {
        var document = TwoTrains();
        var editor = new TimetableEditor(document);
        editor.Apply(TimetableEdit.SetField(Node(document, "1A01").Element, "Notes", "one"));
        editor.Undo();
        editor.Apply(TimetableEdit.SetField(Node(document, "1A01").Element, "Notes", "two"));

        editor.Current.CanRedo.Should().BeFalse();
        var before = Tree(document);
        var elsewhere = TwoTrains().Trains.First().Element;
        var act = () => editor.Apply(TimetableEdit.SetField(elsewhere, "Notes", "x"));

        act.Should().Throw<InvalidOperationException>();
        Tree(document).Should().Be(before);
        editor.Current.UndoDescription.Should().NotBeNull();
    }

    [Fact]
    public void Saving_raises_the_version_sets_the_file_and_clears_the_unsaved_mark()
    {
        var document = TwoTrains();
        var editor = new TimetableEditor(document, sourcePath: "source.wtt");
        editor.Apply(TimetableEdit.SetField(Node(document, "1A01").Element, "Notes", "x"));
        var path = Path.Combine(_folder.FullName, "Test 2026-10-22 Stringline.wtt");

        var saved = editor.Save(path, new WttSaveOptions(BackupFolder: Path.Combine(_folder.FullName, "backups")));

        saved.Version.Should().Be(new WttVersion(1, 0, 1));
        editor.TargetPath.Should().Be(path);
        editor.Current.IsDirty.Should().BeFalse();
        editor.Current.Version.Should().Be(new WttVersion(1, 0, 1));
        WttDocument.Load(path).Trains.First().Notes.Should().Be("x");

        editor.Undo();
        editor.Current.IsDirty.Should().BeTrue("the file has the note");
        editor.Current.Version.Should().Be(new WttVersion(1, 0, 1), "undo never lowers the version");
        editor.Redo();
        editor.Current.IsDirty.Should().BeFalse();

        editor.Save(path, new WttSaveOptions(Bump: VersionBump.Minor, BackupFolder: Path.Combine(_folder.FullName, "backups")));
        editor.Current.Version.Should().Be(new WttVersion(1, 1, 0));
        Directory.EnumerateFiles(Path.Combine(_folder.FullName, "backups"), "*.wtt", SearchOption.AllDirectories).Should().ContainSingle();
    }

    [Fact]
    public void An_autosaved_draft_comes_back_with_its_undo_history_and_unsaved_mark()
    {
        var store = new DraftStore(_folder.FullName);
        var document = TwoTrains();
        var sourcePath = Path.Combine(_folder.FullName, "Timetables", "test.wtt");
        var editor = new TimetableEditor(document, sourcePath);
        var states = new List<string> { Tree(document) };
        editor.Apply(TimetableEdit.SetField(Node(document, "1A01").Element, "Notes", "x"));
        states.Add(Tree(document));
        editor.Apply(TimetableEdit.Insert(Node(document, "1B01").TripsElement!, new XElement("Trip", new XElement("Location", "C"), new XElement("ArrTime", T("11:20"))), 2));
        states.Add(Tree(document));
        editor.Apply(TimetableEdit.Renumber(document, Node(document, "1B01"), "1B09"));
        states.Add(Tree(document));
        editor.Undo();

        var path = store.NewPath(editor.SourcePath, "test");
        Path.GetFileName(path).Should().Be("test.wtt");
        DraftStore.Write(path, document, editor.ToState(open: true));

        var opened = DraftStore.Open(path);
        opened.Problem.Should().BeNull();
        var restored = TimetableEditor.Restore(opened.Document, opened.State);

        restored.SourcePath.Should().Be(sourcePath);
        restored.Current.IsDirty.Should().BeTrue();
        restored.Current.RedoDescription.Should().Be("Renumber 1B01 as 1B09");
        restored.Redo();
        Tree(opened.Document).Should().Be(states[3]);
        restored.Undo();
        restored.Undo();
        Tree(opened.Document).Should().Be(states[1]);
        restored.Undo();
        Tree(opened.Document).Should().Be(states[0]);
        restored.Current.IsDirty.Should().BeFalse();
        restored.Current.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void A_history_that_does_not_match_its_timetable_is_left_out()
    {
        var store = new DraftStore(_folder.FullName);
        var document = TwoTrains();
        var editor = new TimetableEditor(document);
        editor.Apply(TimetableEdit.SetField(Node(document, "1A01").Element, "Notes", "x"));
        var path = store.NewPath(null, "test");
        DraftStore.Write(path, document, editor.ToState(open: true));
        File.WriteAllBytes(path, WttWriter.ToBytes(TwoTrains()));

        var opened = DraftStore.Open(path);

        opened.Problem.Should().Contain("does not match");
        var restored = TimetableEditor.Restore(opened.Document, opened.State);
        restored.Current.CanUndo.Should().BeFalse();
        restored.Current.IsDirty.Should().BeTrue("what the file holds is not known to be saved");
    }

    [Fact]
    public void The_draft_open_when_the_app_stopped_is_found_until_it_is_closed()
    {
        var store = new DraftStore(_folder.FullName);
        store.FindUnclosed().Should().BeNull();
        var editor = new TimetableEditor(TwoTrains());
        var closed = store.NewPath(null, "test");
        DraftStore.Write(closed, editor.Document, editor.ToState(open: false));
        var open = store.NewPath(null, "test");
        DraftStore.Write(open, editor.Document, editor.ToState(open: true));

        Path.GetFileName(open).Should().Be("test new (2).wtt");
        store.List().Should().HaveCount(2);
        store.FindUnclosed()!.Path.Should().Be(open);

        DraftStore.MarkClosed(open);
        store.FindUnclosed().Should().BeNull();
        DraftStore.Delete(open);
        store.List().Should().ContainSingle();
    }

    [Fact]
    public void Points_Stringline_inserts_are_greyed_until_the_user_edits_them_and_stay_so_through_an_autosave()
    {
        var document = TwoTrains();
        var editor = new TimetableEditor(document);
        var trips = Node(document, "1B01").TripsElement!;
        var added = new XElement("Trip", new XElement("Location", "M"), new XElement("DepPassTime", T("11:05")), new XElement("IsPassTime", "-1"));
        var mine = new XElement("Trip", new XElement("Location", "N"));

        editor.Apply(TimetableEdit.Composite("Insert the chain", [TimetableEdit.Insert(trips, added, 1)]), EditOrigin.Stringline);
        editor.Apply(TimetableEdit.Insert(trips, mine, 3));

        editor.Current.AddedPoints.Should().BeEquivalentTo([added]);
        var path = new DraftStore(_folder.FullName).NewPath(null, "test");
        DraftStore.Write(path, document, editor.ToState(open: true));
        var opened = DraftStore.Open(path);
        var restored = TimetableEditor.Restore(opened.Document, opened.State);
        restored.Current.AddedPoints.Select(e => e.Element("Location")!.Value).Should().Equal("M");

        editor.Apply(TimetableEdit.SetField(added, "Platform", "2"));
        editor.Current.AddedPoints.Should().BeEmpty();
        editor.IsAdded(added).Should().BeFalse();
    }

    [Fact]
    public void A_blank_timetable_takes_new_trains_and_copies()
    {
        var categories = Doc(Category("CAT1"), Simple("1A01"));
        var document = DraftDocuments.Blank("test", "New timetable", new SimTime(T("04:00")), categories);
        var editor = new TimetableEditor(document);
        editor.Current.Timetable.Trains.Should().BeEmpty();
        document.Header.Version.Should().Be(new WttVersion(1, 0, 0));
        document.Categories.Select(c => c.Id).Should().Equal("CAT1");

        var (container, insert) = DraftDocuments.TrainsContainer(document);
        insert.Should().BeNull();
        editor.Apply(TimetableEdit.Insert(container, DraftDocuments.NewTrain("2C01", "CAT1", "A")));
        var train = Node(document, "2C01");
        editor.Apply(TimetableEdit.Insert(train.TripsElement!, new XElement("Trip", new XElement("Location", "B"))));
        editor.Apply(TimetableEdit.Insert(container, DraftDocuments.CopyTrain(train, "2C03")));

        document.Trains.Select(t => (t.Headcode, t.Category, t.Trips.Count())).Should().Equal(("2C01", "CAT1", 2), ("2C03", "CAT1", 2));
        editor.Current.Validation.Codes().Should().Contain(IssueCodes.NeverUsed);
    }

    [Theory]
    [InlineData("waterloo 2010.wtt", "Waterloo 2010 Stringline.wtt")]
    [InlineData("Waterloo 2019-05-20 ASC.wtt", "Waterloo 2019-05-20 Stringline.wtt")]
    [InlineData("custom.wtt", "Waterloo 2026-10-22 Stringline.wtt")]
    [InlineData(null, "Waterloo 2026-10-22 Stringline.wtt")]
    public void A_new_file_is_named_by_the_convention_with_the_original_names_date(string? original, string expected)
    {
        var name = TimetableNames.SimName(null, "waterloo");

        TimetableNames.Suggest(name, original, new DateOnly(2026, 10, 22)).Should().Be(expected);
    }

    [Fact]
    public void Editing_Waterloo_keeps_the_checks_up_to_date_quickly()
    {
        var document = WttDocument.Load(Fixtures.Path("waterloo 2010.WTT"));
        var editor = new TimetableEditor(document);
        var train = Node(document, "1W51");

        var clock = Stopwatch.StartNew();
        editor.Apply(TimetableEdit.Change(train.Trips.ElementAt(1), t => t.DepPassTime -= 3600));
        output.WriteLine($"Edit, import and check: {clock.ElapsedMilliseconds} ms");

        var full = new TimetableValidator().Validate(document);
        editor.Current.Validation.Issues.Select(i => i.ToString()).Should().Equal(full.Issues.Select(i => i.ToString()));
        editor.Current.Validation.For(train.Element).Select(i => i.Code).Should().Contain(IssueCodes.DepartureBeforePrevious);
    }
}
