using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Network;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S13 in the shell: blocks, speed restrictions and notes from the right-click menu and B+drag, with their dialogs.</summary>
public sealed class DisruptionEditingTests
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

    /// <summary>A point in the plot: <paramref name="x"/> and <paramref name="y"/> as fractions across and down it.</summary>
    private static ScreenPoint In(MainViewModel vm, double x, double y)
    {
        var view = vm.Navigator.Viewport;
        return new ScreenPoint(view.PlotLeft + (x * view.PlotWidth), view.PlotTop + (y * view.PlotHeight));
    }

    private static PlanMenuItem Item(IReadOnlyList<PlanMenuItem> menu, string header) =>
        menu.FirstOrDefault(m => m.Header == header) ?? throw new InvalidOperationException($"No '{header}' in: {string.Join(", ", menu.Select(m => m.Header))}");

    [Fact]
    public async Task A_block_is_added_from_the_background_menu_then_edited_removed_and_undone()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var at = await app.Ui.Run(() => In(vm, 0.5, 0.5));

        var menu = await app.Ui.Run(() => vm.MenuFor(null, at));
        menu.Select(m => m.Header).Should().Equal("Add _block/possession here…", "Add _speed restriction here…", "Add _note here…");

        string[]? offered = null;
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Title.Should().Be("Add block/possession");
            p.Fields.Select(f => f.Label).Should().Equal("From", "To", "Track", "From time", "Until", "Note");
            offered = [.. p.Fields[2].Options!];
            p.Fields[2].Value = "Down (Down trains)";
            p.Fields[5].Value = "Engineering";
            return true;
        });
        await app.Ui.Run(() => Item(menu, "Add _block/possession here…").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Scene!.Disruptions.Count == 1, "the block drawn");

        var (block, corridor) = await app.Ui.Run(() => (vm.Snapshot.Plan.Disruptions.Single(), vm.SelectedCorridor!.Corridor));
        offered.Should().StartWith(["All lines"]).And.Contain("Down (Down trains)");
        block.Id.Should().Be("B1");
        block.Kind.Should().Be(DisruptionKind.Block);
        corridor.IndexOf(block.From).Should().BeLessThan(corridor.IndexOf(block.To), "the block spans the section around the click, Up end first");
        (block.Track, block.Direction, block.Note).Should().Be(("Down", TrackDirection.Down, "Engineering"));
        (block.End - block.Start).Should().Be(3600);
        block.Start.Seconds.Should().Be(block.Start.Seconds / 300 * 300);
        (await app.Ui.Run(() => vm.History.Entries.Single().Description)).Should().StartWith("Add block ").And.Contain("(Down)");

        // Its tooltip, and its own menu: edit the end time, then remove it.
        await app.Ui.Run(() => vm.DescribeAt(at)!.Heading.Should().Be("Block B1"));
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Title.Should().Be("Edit block/possession");
            p.Fields[2].Value.Should().Be("Down (Down trains)");
            p.Fields[4].Value = PlanInput.Format(block.End + 1800);
            return true;
        });
        menu = await app.Ui.Run(() => vm.MenuFor(null, at));
        menu[0].Command.Should().BeNull("the first item names the block");
        await app.Ui.Run(() => Item(menu, "_Edit block…").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.FindDisruption("B1")?.End == block.End + 1800, "the block made longer");

        await app.Ui.Run(() => Item(menu, "_Remove block").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Disruptions.Count == 0 && vm.Scene!.Disruptions.Count == 0, "the block removed");

        await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Scene!.Disruptions.Count == 1, "the removal undone");
    }

    [Fact]
    public async Task A_speed_restriction_must_slow_trains_down()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var at = await app.Ui.Run(() => In(vm, 0.3, 0.4));

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields.Select(f => f.Label).Should().Contain(["Running-time factor", "Added (minutes)"]);
            p.Fields[5].Value.Should().Be("1.5");
            p.Fields[5].Value = "1";
            p.Fields[6].Value = "0";
            return true;
        });
        var menu = await app.Ui.Run(() => vm.MenuFor(null, at));
        await app.Ui.Run(() => Item(menu, "Add _speed restriction here…").Command!.Execute(null));
        app.Dialogs.Prompted.Should().ContainSingle().Which.Error.Should().Be("A speed restriction must make running slower: a factor over 1, or added minutes.");

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[5].Value = "1.25";
            p.Fields[6].Value = "0.5";
            p.Fields[2].Value = "UF";
            return true;
        });
        await app.Ui.Run(() => Item(menu, "Add _speed restriction here…").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Disruptions.Count == 1, "the restriction");

        var restriction = await app.Ui.Run(() => vm.Snapshot.Plan.Disruptions.Single());
        (restriction.Id, restriction.Kind, restriction.RunTimeFactor, restriction.AddedSeconds).Should().Be(("SR1", DisruptionKind.SpeedRestriction, 1.25, 30));
        (restriction.Track, restriction.Direction).Should().Be(("UF", (TrackDirection?)null), "a typed line code is a WTT line");
        await app.Ui.WaitUntil(() => vm.Scene!.Disruptions.SingleOrDefault()?.Label == "SR ×1.25 +30 s · UF", "the restriction drawn");
    }

    [Fact]
    public async Task B_drag_draws_a_block_and_opens_its_dialog_filled_in()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (from, to) = await app.Ui.Run(() => (In(vm, 0.2, 0.15), In(vm, 0.45, 0.62)));

        var draft = await app.Ui.Run(() =>
        {
            vm.BeginBlock(from).Should().BeTrue();
            vm.MoveBlock(to);
            return vm.DraftDisruption;
        });
        draft.Should().NotBeNull();
        draft!.IsDraft.Should().BeTrue();
        (await app.Ui.Run(() => vm.EditStatus)).Should().StartWith("Block ").And.EndWith("release to set it up, Esc to cancel.");
        (await app.Ui.Run(() => vm.MenuFor(null, to))).Should().BeEmpty("no menu while drawing");

        string[]? values = null;
        app.Dialogs.Prompts.Enqueue(p =>
        {
            values = [.. p.Fields.Select(f => f.Value)];
            return true;
        });
        await app.Ui.RunAsync(() => vm.EndBlockAsync(commit: true));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Disruptions.Count == 1, "the block");

        var block = await app.Ui.Run(() => vm.Snapshot.Plan.Disruptions.Single());
        (block.From, block.To, block.Start, block.End).Should().Be((draft.Disruption.From, draft.Disruption.To, draft.Disruption.Start, draft.Disruption.End));
        values![2].Should().Be("All lines");
        values[3].Should().Be(PlanInput.Format(draft.Disruption.Start));
        (await app.Ui.Run(() => vm.DraftDisruption)).Should().BeNull();

        // Esc drops a drawing without asking.
        await app.Ui.Run(() =>
        {
            vm.BeginBlock(from).Should().BeTrue();
            vm.MoveBlock(to);
        });
        await app.Ui.RunAsync(() => vm.EndBlockAsync(commit: false));
        await app.Ui.Run(() =>
        {
            vm.DraftDisruption.Should().BeNull();
            vm.EditStatus.Should().Be("Block cancelled.");
        });
        app.Dialogs.Prompted.Should().HaveCount(1);
    }

    [Fact]
    public async Task Notes_are_added_edited_and_removed_from_the_graph()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var at = await app.Ui.Run(() => In(vm, 0.6, 0.3));

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields.Select(f => f.Label).Should().Equal("At", "Time", "Note");
            p.Fields[2].Value = "Driver relief for 2L45";
            return true;
        });
        var menu = await app.Ui.Run(() => vm.MenuFor(null, at));
        await app.Ui.Run(() => Item(menu, "Add _note here…").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Scene!.Notes.Count == 1, "the note drawn");

        var marker = await app.Ui.Run(() => GraphAnnotations.MarkerOf(vm.Scene!.Notes[0], vm.Navigator.Viewport));
        await app.Ui.Run(() =>
        {
            var note = vm.Snapshot.Plan.Notes.Single();
            (note.Id, note.Text, note.Time.Second).Should().Be(("N1", "Driver relief for 2L45", 0));
            vm.DescribeAt(marker)!.ValueOf("Note").Should().Be("Driver relief for 2L45");
        });

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Title.Should().Be("Edit note");
            p.Fields[2].Value = "Relief cancelled";
            return true;
        });
        menu = await app.Ui.Run(() => vm.MenuFor(null, marker));
        menu.Select(m => m.Header).Skip(1).Should().Equal("_Edit note…", "_Remove note");
        await app.Ui.Run(() => Item(menu, "_Edit note…").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.FindNote("N1")?.Text == "Relief cancelled", "the note changed");

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[2].Value = " ";
            return true;
        });
        await app.Ui.Run(() => Item(menu, "_Edit note…").Command!.Execute(null));
        app.Dialogs.Prompted[^1].Error.Should().Be("A note needs some text.");

        await app.Ui.Run(() => Item(menu, "_Remove note").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Scene!.Notes.Count == 0, "the note removed");
    }

    [Fact]
    public async Task Disruptions_can_be_hidden()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        await app.Ui.Run(() =>
        {
            var c = vm.SelectedCorridor!.Corridor;
            return vm.ExecuteAsync(new AddDisruption(new Disruption("B1", DisruptionKind.Block, c.Locations[0].Tiploc, c.Locations[1].Tiploc, null,
                vm.Snapshot.Plan.Timetable.Header.StartTime, vm.Snapshot.Plan.Timetable.Header.StartTime + 3600)));
        });
        await app.Ui.WaitUntil(() => vm.Scene!.Disruptions.Count == 1, "the block drawn");

        await app.Ui.Run(() => vm.ShowDisruptions = false);
        await app.Ui.WaitUntil(() => vm.Scene!.Disruptions.Count == 0, "the block hidden");
    }
}
