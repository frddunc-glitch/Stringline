using System.Text.Json;
using System.Xml.Linq;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;

namespace Stringline.Timetable.Tests.Editing;

/// <summary>
/// Edits recorded by element path (S43, a draft's undo history in <c>.draft.json</c>): replayed on the same timetable
/// read again from disk, the backward records undo every edit and the forward records redo them.
/// </summary>
public sealed class EditRecordTests
{
    private static WttDocument Simple() => WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("1A02", dep: 40000)));

    private static WttDocument Reopen(WttDocument document) => WttDocument.Load(new MemoryStream(WttWriter.ToBytes(document)));

    /// <summary>Applies the edits recording each, then checks that the records undo and redo them on the reopened document.</summary>
    private static void CheckReplay(WttDocument document, params Func<WttDocument, TimetableEdit>[] edits)
    {
        var states = new List<XElement> { RoundTrip.Snapshot(document) };
        var changes = new List<RecordedChange>();
        foreach (var edit in edits)
        {
            changes.Add(edit(document).ApplyRecorded(document));
            states.Add(RoundTrip.Snapshot(document));
        }

        var reopened = Reopen(document);
        for (var i = changes.Count - 1; i >= 0; i--)
        {
            reopened.Apply(changes[i].Backward.ToEdit());
            reopened.Root.ShouldEqualTree(states[i]);
        }

        for (var i = 0; i < changes.Count; i++)
        {
            reopened.Apply(changes[i].Forward.ToEdit());
            reopened.Root.ShouldEqualTree(states[i + 1]);
        }
    }

    [Fact]
    public void Paths_name_each_level_by_its_place_among_siblings_of_its_name()
    {
        var document = Simple();
        var trip = document.Trains.Last().Trips.Last();

        var path = ElementPath.Of(document, trip.Element);

        path.Should().Be("Timetables[0]/Timetable[1]/Trips[0]/Trip[1]");
        ElementPath.Resolve(document, path).Should().BeSameAs(trip.Element);
        ElementPath.Of(document, document.Root).Should().BeEmpty();
        ElementPath.Resolve(document, "Timetables[0]/Timetable[7]").Should().BeNull();
        ElementPath.Resolve(document, "Timetables[x]").Should().BeNull();
    }

    [Fact]
    public void Field_insert_move_remove_and_composite_edits_replay_on_the_reopened_timetable()
    {
        CheckReplay(
            Simple(),
            d => TimetableEdit.SetField(d.Trains.First().Trips.Last().Element, "Platform", "3"),
            d => TimetableEdit.Insert(d.Trains.First().TripsElement!, new XElement("Trip", new XElement("Location", "MMM"), new XElement("Notes", " ")), 1),
            d => TimetableEdit.Move(d.Trains.First().Trips.Last().Element, 0),
            d => TimetableEdit.SetField(d.Trains.First().Trips.First().Element, "Line", null),
            d => TimetableEdit.SetAttribute(d.Root, "ID", "elsewhere"),
            d => TimetableEdit.Composite("Both", [TimetableEdit.Remove(d.Trains.First().Element), TimetableEdit.SetField(d.Trains.Last().Element, "Notes", "kept")]),
            d => TimetableEdit.Change(d.Trains.Single(), t => t.Trips.First().DepPassTime += 60));
    }

    [Fact]
    public void A_renumber_and_a_shift_on_Waterloo_replay()
    {
        var document = WttDocument.Load(Fixtures.Path(WttFixtureTests.Waterloo));

        CheckReplay(
            document,
            d => TimetableEdit.Renumber(d, d.Trains.First(t => t.Headcode == "2P05"), "2P07"),
            d => TimetableEdit.Change(d.Trains.First(t => t.Headcode == "1W51"), t =>
            {
                foreach (var trip in t.Trips)
                {
                    trip.DepPassTime += 300;
                }
            }));
    }

    [Fact]
    public void A_self_closing_element_replays_self_closing()
    {
        var document = WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01"), Wtt.Category.Replace(
            "<DwellTimes><RedSignalMoveOff>12</RedSignalMoveOff><StationForward>30</StationForward><Join>240</Join></DwellTimes>", "<DwellTimes/>", StringComparison.Ordinal)));

        CheckReplay(document, d => TimetableEdit.Change(d.Categories.Single(), c => c.SetDwell("Join", 90)));
    }

    [Fact]
    public void A_version_written_into_the_header_leaves_the_paths_where_they_were()
    {
        var document = Simple();
        document.Header.Version = null;
        var change = TimetableEdit.SetField(document.Trains.Last().Element, "Notes", "x").ApplyRecorded(document);

        WttWriter.BumpVersion(document, VersionBump.Build);
        document.Header.Version.Should().Be(new WttVersion(1, 0, 1));
        document.Apply(change.Backward.ToEdit());

        document.Trains.Last().Notes.Should().BeNull();
    }

    [Fact]
    public void Records_come_back_from_JSON()
    {
        var document = Simple();
        var change = TimetableEdit.Composite("Edit", [
            TimetableEdit.SetAttribute(document.Root, "ID", "x"),
            TimetableEdit.Insert(document.Trains.First().TripsElement!, new XElement("Trip", new XElement("Location", "Q&A <1>"))),
        ]).ApplyRecorded(document);

        var json = JsonSerializer.Serialize(change.Backward);
        var back = JsonSerializer.Deserialize<EditRecord>(json)!;

        back.Description.Should().Be(change.Backward.Description);
        back.Steps.Should().BeEquivalentTo(change.Backward.Steps, o => o.WithStrictOrdering());
    }

    [Fact]
    public void A_record_that_does_not_fit_changes_nothing()
    {
        var document = Simple();
        var before = RoundTrip.Snapshot(document);
        var record = new EditRecord("Two", [
            EditStep.SetField("Timetables[0]/Timetable[0]", "Notes", "x"),
            EditStep.Remove("Timetables[0]/Timetable[5]"),
        ]);

        var act = () => document.Apply(record.ToEdit());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Timetable[5]*not in the timetable*");
        document.Root.ShouldEqualTree(before);
    }

    [Fact]
    public void A_failed_composite_records_nothing()
    {
        var document = Simple();
        var elsewhere = Simple().Trains.First().Element;

        var act = () => TimetableEdit.Composite("Two", [TimetableEdit.SetField(document.Trains.First().Element, "Notes", "x"), TimetableEdit.SetField(elsewhere, "Notes", "y")])
            .ApplyRecorded(document);

        act.Should().Throw<InvalidOperationException>();
        document.Trains.First().Notes.Should().BeNull();
    }
}
