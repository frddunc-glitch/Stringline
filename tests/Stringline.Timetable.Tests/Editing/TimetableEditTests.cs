using System.Xml.Linq;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;

namespace Stringline.Timetable.Tests.Editing;

/// <summary>Edits apply, and their undo restores the exact tree (<c>08-…md</c> §3.2).</summary>
public sealed class TimetableEditTests
{
    private static readonly Lazy<byte[]> WaterlooBytes = new(() => File.ReadAllBytes(Fixtures.Path(WttFixtureTests.Waterloo)));

    private static WttDocument Waterloo() => WttDocument.Load(new MemoryStream(WaterlooBytes.Value));

    private static WttDocument Simple() => WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("1A02", dep: 40000)));

    /// <summary>Applies, checks <paramref name="applied"/>, undoes and checks the tree is as before, then redoes.</summary>
    private static void ApplyUndoRedo(WttDocument document, TimetableEdit edit, Action applied)
    {
        var before = RoundTrip.Snapshot(document);
        var undo = document.Apply(edit);
        applied();
        var after = RoundTrip.Snapshot(document);

        var redo = document.Apply(undo);
        document.Root.ShouldEqualTree(before);

        document.Apply(redo);
        document.Root.ShouldEqualTree(after);
    }

    [Fact]
    public void Setting_a_field_makes_changes_or_removes_it_and_undo_restores_the_tree()
    {
        var document = Simple();
        var trip = document.Trains.First().Trips.Last();

        ApplyUndoRedo(document, TimetableEdit.SetField(trip.Element, "Platform", "3"), () => trip.Platform.Should().Be("3"));
        ApplyUndoRedo(document, TimetableEdit.SetField(trip.Element, "DepPassTime", "36360"), () => trip.DepPassTime.Should().Be(new SimTime(36360)));
        ApplyUndoRedo(document, TimetableEdit.SetField(trip.Element, "Location", null), () => trip.Location.Should().BeNull());
        ApplyUndoRedo(document, TimetableEdit.SetField(trip.Element, "Line", null), () => trip.Line.Should().BeNull());
    }

    [Fact]
    public void Undo_keeps_the_elements_identity()
    {
        var document = Simple();
        var trip = document.Trains.First().Trips.Last();
        var arrival = trip.Element.Element("ArrTime")!;

        var undo = document.Apply(TimetableEdit.SetField(trip.Element, "ArrTime", null));
        document.Apply(undo);

        trip.Element.Element("ArrTime").Should().BeSameAs(arrival);
    }

    [Fact]
    public void A_self_closing_element_is_self_closing_again_after_undo()
    {
        var document = WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01"), Wtt.Category.Replace(
            "<DwellTimes><RedSignalMoveOff>12</RedSignalMoveOff><StationForward>30</StationForward><Join>240</Join></DwellTimes>", "<DwellTimes/>", StringComparison.Ordinal)));
        var category = document.Categories.Single();

        var undo = document.Apply(TimetableEdit.Change(category, c => c.SetDwell("Join", 90)));
        category.GetDwell("Join").Should().Be(90);
        document.Apply(undo);

        category.Element.Element("DwellTimes")!.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Insert_remove_and_move_undo_exactly()
    {
        var document = Simple();
        var trips = document.Trains.First().TripsElement!;
        var middle = new XElement("Trip", new XElement("Location", "MMM"), new XElement("DepPassTime", "36150"), new XElement("IsPassTime", "True"));

        ApplyUndoRedo(document, TimetableEdit.Insert(trips, middle, 1), () => trips.Elements().Select(t => (string?)t.Element("Location")).Should().Equal("AAA", "MMM", "BBB"));
        ApplyUndoRedo(document, TimetableEdit.Move(middle, 0), () => trips.Elements().Select(t => (string?)t.Element("Location")).Should().Equal("MMM", "AAA", "BBB"));
        ApplyUndoRedo(document, TimetableEdit.Remove(trips.Elements().Last()), () => trips.Elements().Should().HaveCount(2));
        ApplyUndoRedo(document, TimetableEdit.Remove(document.Trains.Last().Element), () => document.Trains.Should().ContainSingle());
    }

    [Fact]
    public void A_new_trip_with_no_place_goes_after_the_last_trip()
    {
        var document = Simple();
        var trips = document.Trains.First().TripsElement!;

        document.Apply(TimetableEdit.Insert(trips, new XElement("Trip", new XElement("Location", "CCC"))));

        trips.Elements().Select(t => (string?)t.Element("Location")).Should().Equal("AAA", "BBB", "CCC");
    }

    [Fact]
    public void Shifting_a_Waterloo_train_and_undoing_it_restores_the_whole_tree()
    {
        var document = Waterloo();
        var train = document.Trains.First(t => t.Headcode == "1W51");
        var first = train.Trips.First().DepPassTime!.Value;

        ApplyUndoRedo(
            document,
            TimetableEdit.Change(train, t =>
            {
                t.DepartTime = t.DepartTime + 300;
                foreach (var trip in t.Trips)
                {
                    trip.ArrTime = trip.ArrTime + 300;
                    trip.DepPassTime = trip.DepPassTime + 300;
                }
            }),
            () => train.Trips.First().DepPassTime.Should().Be(first + 300));
    }

    [Fact]
    public void A_change_keeps_the_trip_elements()
    {
        var document = Simple();
        var train = document.Trains.First();
        var trips = train.Trips.Select(t => t.Element).ToList();

        document.Apply(TimetableEdit.Change(train, t => t.Trips.Last().Platform = "4"));

        train.Trips.Select(t => t.Element).Should().Equal(trips);
        train.Trips.Last().Platform.Should().Be("4");
    }

    [Fact]
    public void A_change_that_adds_trips_is_refused()
    {
        var document = Simple();
        var train = document.Trains.First();
        train.TripsElement!.Add(new XElement("Trip", new XElement("Location", "XXX")));

        var act = () => TimetableEdit.Change(train, t => t.TripsElement!.Elements().Last().Remove());

        act.Should().Throw<InvalidOperationException>().WithMessage("*Insert or Remove*");
    }

    [Fact]
    public void Attributes_keep_their_order_through_undo()
    {
        var document = Simple();
        var before = RoundTrip.Snapshot(document);

        var undo = document.Apply(TimetableEdit.SetAttribute(document.Root, "ID", null));
        document.Header.AreaId.Should().BeNull();
        document.Apply(undo);

        document.Root.ShouldEqualTree(before);
        document.Root.Attributes().Select(a => a.Name.LocalName).Should().Equal("ID", "Version");
    }

    [Fact]
    public void A_composite_that_fails_part_way_changes_nothing()
    {
        var document = Simple();
        var before = RoundTrip.Snapshot(document);
        var trip = document.Trains.First().Trips.First();
        var elsewhere = Simple().Trains.First().Trips.First();

        var act = () => document.Apply(TimetableEdit.Composite("Two", [TimetableEdit.SetField(trip.Element, "Platform", "1"), TimetableEdit.SetField(elsewhere.Element, "Platform", "2")]));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not in this document*");
        document.Root.ShouldEqualTree(before);
    }

    [Fact]
    public void Edits_count_as_revisions_and_a_clone_is_separate()
    {
        var document = Simple();
        var clone = document.Clone();
        var revision = document.Revision;

        document.Apply(TimetableEdit.SetField(document.Trains.First().Element, "Notes", "x"));

        document.Revision.Should().BeGreaterThan(revision);
        clone.Trains.First().Notes.Should().BeNull();
        clone.Revision.Should().Be(0);
    }

    [Fact]
    public void An_import_knows_its_elements_and_goes_stale_after_an_edit()
    {
        var document = Waterloo();
        var import = document.Import();
        var w51 = import.Timetable.Get(new TrainId("1W51"));

        var node = import.NodeOf(w51);
        node.Headcode.Should().Be("1W51");
        import.TrainOf(node).Should().BeSameAs(w51);
        import.TripOf(w51, 0).Location.Should().Be(w51.Points[0].Location.ToString());
        var (point, activity) = w51.Points.SelectMany(p => p.Activities.Select((a, i) => (p, i))).First();
        import.ActivityOf(w51, point.Index, activity).Code.Should().Be(point.Activities[activity].Code);
        import.RuleOf(0).Code.Should().Be(import.Timetable.Rules[0].Code);

        document.Apply(TimetableEdit.SetField(node.Element, "Notes", "x"));
        var act = () => import.NodeOf(w51);
        act.Should().Throw<InvalidOperationException>().WithMessage("*import it again*");
    }

    [Fact]
    public void Renumbering_keeps_activity_links_by_headcode_and_leaves_other_trains_with_that_headcode()
    {
        // 1A01 forms 2B01 at BBB; another 2B01 runs later from elsewhere.
        var document = WttDocument.Parse(Wtt.Document(
            Wtt.SimpleTrain("1A01", dep: 36000, toExtra: Wtt.Activity(0, "2B01"))
            + Wtt.SimpleTrain("2B01", dep: 36900, from: "BBB", to: "CCC")
            + Wtt.SimpleTrain("2B01", dep: 50000, from: "DDD", to: "EEE")));
        var later = document.Trains.Last();
        var linked = document.Trains.ElementAt(1);

        document.Apply(TimetableEdit.Renumber(document, later, "2B09"));
        document.Trains.First().Trips.Last().Activities.Single().AssociatedTrain.Should().Be("2B01", "the link is to the other 2B01");

        var before = RoundTrip.Snapshot(document);
        var edit = TimetableEdit.Renumber(document, linked, "2B05");
        edit.Description.Should().Be("Renumber 2B01 as 2B05");
        var undo = document.Apply(edit);

        document.Trains.First().Trips.Last().Activities.Single().AssociatedTrain.Should().Be("2B05");
        var tt = document.ToWorkingTimetable();
        tt.Trains[0].NextWorking.Should().Be(tt.Trains[1].Id);
        tt.Trains[1].Headcode.Value.Should().Be("2B05");
        tt.Trains[2].Headcode.Value.Should().Be("2B09");

        document.Apply(undo);
        document.Root.ShouldEqualTree(before);
    }

    [Fact]
    public void Renumbering_follows_a_uid_link_when_the_uid_changes()
    {
        var document = WttDocument.Parse(Wtt.Document(
            Wtt.SimpleTrain("1A01", toExtra: Wtt.Activity(0, train: "2B01", uid: "W2B01"))
            + Wtt.SimpleTrain("2B01", dep: 36900, uid: "W2B01", from: "BBB", to: "CCC"),
            rules: "<TimetableRule><TrainUID>W2B01</TrainUID><Rule>1</Rule><Train2>1A01</Train2><Time>60</Time></TimetableRule>"));
        var train = document.Trains.Last();

        document.Apply(TimetableEdit.Renumber(document, train, "2B03"));
        var activity = document.Trains.First().Trips.Last().Activities.Single();
        activity.AssociatedUid.Should().Be("W2B01", "the UID did not change");
        activity.AssociatedTrain.Should().Be("2B03");

        document.Apply(TimetableEdit.Renumber(document, train, "2B04", uid: "W2B04"));
        activity.AssociatedUid.Should().Be("W2B04");
        activity.AssociatedTrain.Should().Be("2B04");
        document.Rules.Single().TrainUid.Should().Be("W2B04");
        document.Rules.Single().Train2.Should().Be("1A01");
        var tt = document.ToWorkingTimetable();
        tt.Rules.Single().Train.Should().Be(tt.Trains[1].Id);
        tt.Trains[0].NextWorking.Should().Be(tt.Trains[1].Id);
    }

    [Fact]
    public void Renumbering_a_Waterloo_train_keeps_its_links_and_rules()
    {
        var document = Waterloo();
        var import = document.Import();
        var tt = import.Timetable;
        var target = tt.Trains.First(t => t.FormedFrom is not null && tt.Rules.Any(r => r.Train == t.Id || r.Train2 == t.Id));
        var former = tt.Get(target.FormedFrom!.Value);
        var rules = tt.Rules.Count(r => r.Train == target.Id || r.Train2 == target.Id);
        var sameHeadcode = tt.Trains.Count(t => t.Headcode == target.Headcode);

        document.Apply(TimetableEdit.Renumber(document, import.NodeOf(target), "9Z99"));

        var after = document.ToWorkingTimetable();
        var renumbered = after.Trains.Single(t => t.Headcode.Value == "9Z99");
        after.Trains.Single(t => t.Points.SequenceEqual(former.Points, PointComparer.Instance) && t.Headcode == former.Headcode).NextWorking.Should().Be(renumbered.Id);
        after.Rules.Count(r => r.Train == renumbered.Id || r.Train2 == renumbered.Id).Should().Be(rules);
        after.Trains.Count(t => t.Headcode == target.Headcode).Should().Be(sameHeadcode - 1);
    }

    /// <summary>Points compared by place and time only (activities name trains by ID, which a renumber may change).</summary>
    private sealed class PointComparer : IEqualityComparer<TimingPoint>
    {
        public static readonly PointComparer Instance = new();

        public bool Equals(TimingPoint? x, TimingPoint? y) => x?.Location == y?.Location && x?.Arrive == y?.Arrive && x?.Depart == y?.Depart;

        public int GetHashCode(TimingPoint obj) => obj.Location.GetHashCode();
    }
}
