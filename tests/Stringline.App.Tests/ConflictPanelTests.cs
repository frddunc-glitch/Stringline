using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S14 in the shell: the conflicts panel, the count, the rings' tooltips and clicks, and focusing a conflict.</summary>
public sealed class ConflictPanelTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static async Task<MainViewModel> LoadWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true && vm.Snapshot.Plan.Count > 0, "the WTT on the graph");
        await app.Ui.Run(() => vm.Tick());
        return vm;
    }

    private static Disruption Block(MainViewModel vm)
    {
        var corridor = vm.SelectedCorridor!.Corridor;
        return new Disruption("B1", DisruptionKind.Block, corridor.Locations[0].Tiploc, corridor.Locations[2].Tiploc, null, SimTime.Parse("07:00"), SimTime.Parse("07:20"));
    }

    [Fact]
    public async Task A_block_lists_its_conflicts_and_picking_one_focuses_the_graph()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        await app.Ui.WaitUntil(() => vm.Snapshot.Forecast.Interaction && vm.ConflictsText.Length > 0, "the conflicts counted over the corridors' sections");
        var asBooked = await app.Ui.Run(() => vm.ConflictList.Rows.Count);
        asBooked.Should().BeLessThan(8, "the WTT's own clashes are mostly allowed as booked or under the threshold");

        var block = await app.Ui.Run(() => Block(vm));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new AddDisruption(block)));
        await app.Ui.WaitUntil(() => vm.ConflictList.Rows.Any(r => r.Conflict.Kind == ConflictKind.Disruption) && vm.Scene!.Conflicts.Count > 0, "the block's conflicts");

        var (rows, text, header) = await app.Ui.Run(() => (vm.ConflictList.Rows.ToList(), vm.ConflictsText, vm.ConflictList.Header));
        rows.Count.Should().BeGreaterThan(asBooked);
        text.Should().Be($"{rows.Count} conflicts");
        header.Should().Be($"Conflicts ({rows.Count})");
        rows.Select(r => r.Conflict.When).Should().BeInAscendingOrder();
        var held = rows.First(r => r.Conflict.Kind == ConflictKind.Disruption);
        (held.Badge, held.Kind).Should().Be(("BK", "Block"));
        held.Trains.Should().EndWith(" / B1");
        held.Tip.Should().StartWith("Block: ").And.Contain("runs into block B1").And.Contain("Resolve: delay ");
        held.Severity.Should().EndWith(" min");

        // Pick it: the train is selected with the conflict, and the view stops following and centres on it.
        await app.Ui.Run(() => vm.ConflictList.Selected = held);
        var (selection, following, view) = await app.Ui.Run(() => (vm.Selection, vm.Navigator.IsFollowing, vm.Navigator.Viewport));
        (selection!.Train, selection.ConflictId).Should().Be(((TrainId?)held.Conflict.A, held.Id));
        following.Should().BeFalse();
        ((view.TimeStart + view.TimeEnd) / 2).Should().BeApproximately(held.Conflict.When.Seconds, 1);
        (await app.Ui.Run(() => vm.Detail.Heading)).Should().Contain(held.Conflict.HeadcodeA.Value);

        // Its ring on the graph: the tooltip, and a click picks it again after the selection is cleared.
        await app.Ui.WaitUntil(() => vm.Scene!.Conflicts.Any(c => c.Id == held.Id), "its ring drawn");
        var ring = await app.Ui.Run(() => GraphConflicts.RingOf(vm.Scene!.Conflicts.First(c => c.Id == held.Id), vm.Navigator.Viewport));
        (await app.Ui.Run(() => vm.DescribeConflictAt(ring)))!.Heading.Should().StartWith("Block: ");
        await app.Ui.Run(() => vm.SelectPath(null));
        (await app.Ui.Run(() => (vm.Selection, vm.ConflictList.Selected))).Should().Be(((GraphSelection?)null, (ConflictRowViewModel?)null));
        (await app.Ui.Run(() => vm.ClickConflictAt(ring))).Should().BeTrue();
        (await app.Ui.Run(() => (vm.Selection?.ConflictId, vm.ConflictList.Selected?.Id))).Should().Be((held.Id, held.Id));
        (await app.Ui.Run(() => vm.ClickConflictAt(new ScreenPoint(ring.X + 40, ring.Y + 40)))).Should().BeFalse();

        // Hidden from the graph, still listed.
        await app.Ui.Run(() => vm.ShowConflicts = false);
        await app.Ui.WaitUntil(() => vm.Scene!.Conflicts.Count == 0, "the conflicts hidden");
        (await app.Ui.Run(() => vm.ConflictList.Rows.Count)).Should().Be(rows.Count);
        await app.Ui.Run(() => vm.ShowConflicts = true);

        // Undo the block: they go.
        await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.ConflictList.Rows.Count == asBooked && vm.ConflictList.Rows.All(r => r.Conflict.Kind != ConflictKind.Disruption), "the block's conflicts gone");
    }

    [Fact]
    public async Task A_conflict_on_another_corridor_switches_to_it()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (first, other, where) = await app.Ui.Run(() =>
        {
            var main = vm.Corridors[0].Corridor;
            var tab = vm.Corridors.Skip(1).First(t => t.Corridor.Locations.Any(l => main.PositionOf(l.Tiploc) is null));
            return (vm.Corridors[0], tab, tab.Corridor.Locations.First(l => main.PositionOf(l.Tiploc) is null).Tiploc);
        });
        await app.Ui.Run(() => vm.SelectedCorridor = first);
        var train = await app.Ui.Run(() => vm.Snapshot.Plan.Trains[0]);
        var conflict = new Conflict("P:test", ConflictKind.Platform, train.Id, null, where, SimTime.Parse("09:00"), 120) { HeadcodeA = train.Headcode, Platform = "1" };

        await app.Ui.Run(() => vm.FocusConflict(conflict));

        (await app.Ui.Run(() => vm.SelectedCorridor)).Should().Be(other);
        (await app.Ui.Run(() => vm.Selection?.ConflictId)).Should().Be("P:test");
        var view = await app.Ui.Run(() => vm.Navigator.Viewport);
        ((view.TimeStart + view.TimeEnd) / 2).Should().BeApproximately(SimTime.Parse("09:00").Seconds, 1);
    }

    [Fact]
    public async Task A_conflict_on_no_corridor_says_so()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var train = await app.Ui.Run(() => vm.Snapshot.Plan.Trains[0]);
        var conflict = new Conflict("P:nowhere", ConflictKind.Platform, train.Id, null, new Tiploc("NOWHERE"), SimTime.Parse("09:00"), 120) { HeadcodeA = train.Headcode, Platform = "1" };

        await app.Ui.Run(() => vm.FocusConflict(conflict));

        (await app.Ui.Run(() => vm.InfoMessage)).Should().EndWith("not on any corridor's graph.");
        (await app.Ui.Run(() => vm.Selection?.Train)).Should().Be(train.Id);
    }
}
