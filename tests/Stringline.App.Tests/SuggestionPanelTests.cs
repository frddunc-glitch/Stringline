using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S15 in the shell: suggestions for the picked conflict (preview, apply, cancellations) and the regulation list.</summary>
public sealed class SuggestionPanelTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static async Task<MainViewModel> LoadWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true && vm.Snapshot.Plan.Count > 0 && vm.Snapshot.Forecast.Interaction, "the WTT on the graph");
        await app.Ui.Run(() => vm.Tick());
        return vm;
    }

    /// <summary>A block over the main corridor's first sections, and the conflict it makes for its first train.</summary>
    private static async Task<ConflictRowViewModel> Blocked(TestApp app, MainViewModel vm)
    {
        var block = await app.Ui.Run(() =>
        {
            var corridor = vm.SelectedCorridor!.Corridor;
            return new Disruption("B1", DisruptionKind.Block, corridor.Locations[0].Tiploc, corridor.Locations[2].Tiploc, null, SimTime.Parse("07:00"), SimTime.Parse("07:20"));
        });
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new AddDisruption(block)));
        await app.Ui.WaitUntil(() => vm.ConflictList.Rows.Any(r => r.Conflict.Kind == ConflictKind.Disruption), "the block's conflicts");
        return await app.Ui.Run(() => vm.ConflictList.Rows.First(r => r.Conflict.Kind == ConflictKind.Disruption));
    }

    [Fact]
    public async Task Picking_a_conflict_offers_suggestions_to_preview_and_apply()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        (await app.Ui.Run(() => vm.SuggestionPanel.IsVisible)).Should().BeFalse("no conflict is picked");
        var held = await Blocked(app, vm);

        await app.Ui.Run(() => vm.ConflictList.Selected = held);
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.Rows.Count > 0, "the suggestions worked out");

        var (heading, status, rows) = await app.Ui.Run(() => (vm.SuggestionPanel.Heading, vm.SuggestionPanel.Status, vm.SuggestionPanel.Rows.ToList()));
        heading.Should().Be($"Resolve: {held.Conflict.Summary}");
        status.Should().BeEmpty();
        rows.Count.Should().BeLessThanOrEqualTo(3);
        var best = rows[0];
        best.Suggestion!.Kind.Should().Be(SuggestionKind.Hold, "planning the hold costs nothing more than the block already does");
        best.Suggestion!.Train.Should().Be(held.Conflict.A);
        best.Title.Should().StartWith($"Hold {held.Conflict.HeadcodeA} at ").And.Contain(" until ");
        best.Detail.Should().Contain("clears");
        best.Tip.Should().NotBeEmpty();
        rows.Should().NotContain(r => r.Suggestion!.Kind == SuggestionKind.Cancel || r.Suggestion!.Kind == SuggestionKind.TerminateShort, "cancelling is off by default");

        // The conflict's tooltip on the graph gives the best one (#12).
        await app.Ui.WaitUntil(() => vm.Scene!.Conflicts.Any(c => c.Id == held.Id), "its ring drawn");
        var tip = await app.Ui.Run(() => vm.DescribeAt(GraphConflicts.RingOf(vm.Scene!.Conflicts.First(c => c.Id == held.Id), vm.Navigator.Viewport)));
        (tip!.ValueOf("Suggest"), tip.ValueOf("Tip")).Should().Be((best.Title, (string?)null));

        // Preview draws its train's new plan as a ghost; a second click hides it.
        await app.Ui.Run(() => vm.PreviewSuggestionCommand.Execute(best));
        var (ghosts, previewed) = await app.Ui.Run(() => (vm.Ghosts, best.IsPreviewed));
        ghosts.Should().NotBeNullOrEmpty();
        ghosts!.Should().OnlyContain(g => g.Train == best.Suggestion!.Train);
        previewed.Should().BeTrue();
        await app.Ui.Run(() => vm.PreviewSuggestionCommand.Execute(best));
        (await app.Ui.Run(() => (vm.Ghosts, best.IsPreviewed))).Should().Be(((IReadOnlyList<GraphPath>?)null, false));

        // Apply: one edit, named after it, and the conflict goes; the panel closes with it.
        var version = await app.Ui.Run(() => vm.Snapshot.PlanVersion);
        await app.Ui.RunAsync(() => vm.ApplySuggestionCommand.ExecuteAsync(best));
        await app.Ui.WaitUntil(() => vm.Snapshot.PlanVersion > version && vm.ConflictList.Rows.All(r => r.Id != held.Id), "the suggestion applied");
        (await app.Ui.Run(() => vm.Snapshot.History.UndoDescription)).Should().Be($"Suggestion: {best.Suggestion!.Describe()}");
        (await app.Ui.Run(() => vm.EditStatus)).Should().StartWith("Done: Suggestion: Hold ");
        await app.Ui.WaitUntil(() => !vm.SuggestionPanel.IsVisible, "the panel closed with its conflict");
        await app.Ui.WaitUntil(() => vm.Ghosts is null, "the ghost dropped once the graph shows the edit");

        // Undo brings it back.
        await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.ConflictList.Rows.Any(r => r.Id == held.Id), "the conflict back");
    }

    [Fact]
    public async Task Cancelling_can_be_allowed_and_is_remembered()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var held = await Blocked(app, vm);
        await app.Ui.Run(() => vm.ConflictList.Selected = held);
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.Rows.Count > 0, "the suggestions worked out");

        await app.Ui.Run(() => vm.SuggestionPanel.AllowCancellation = true);

        // No train has started (no sim is running), so each can be cancelled.
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.Rows.Any(r => r.Suggestion!.Kind == SuggestionKind.Cancel), "a cancellation offered");
        var cancel = await app.Ui.Run(() => vm.SuggestionPanel.Rows.First(r => r.Suggestion!.Kind == SuggestionKind.Cancel));
        cancel.Title.Should().Be($"Cancel {held.Conflict.HeadcodeA}");
        cancel.Tip.Should().Contain("Counted for the cancelling: 1 h");
        app.Settings.Load().SuggestCancellations.Should().BeTrue();

        await app.Ui.Run(() => vm.SuggestionPanel.AllowCancellation = false);
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.Rows.Count > 0 && vm.SuggestionPanel.Rows.All(r => r.Suggestion!.Kind != SuggestionKind.Cancel), "cancellations gone");
        app.Settings.Load().SuggestCancellations.Should().BeFalse();
    }

    [Fact]
    public async Task A_suggestion_worked_out_on_an_older_plan_is_not_applied()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var held = await Blocked(app, vm);
        await app.Ui.Run(() => vm.ConflictList.Selected = held);
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.Rows.Count > 0, "the suggestions worked out");
        var (conflict, suggestions, version) = await app.Ui.Run(() => (held.Conflict, vm.SuggestionPanel.Rows.Select(r => r.Suggestion!).ToList(), vm.Snapshot.PlanVersion));

        // As if they had been worked out before the last edit.
        await app.Ui.Run(() => vm.SuggestionPanel.Show(conflict, suggestions, version - 1, vm.Profile));
        var stale = await app.Ui.Run(() => vm.SuggestionPanel.Rows[0]);
        await app.Ui.RunAsync(() => vm.ApplySuggestionCommand.ExecuteAsync(stale));

        (await app.Ui.Run(() => vm.EditStatus)).Should().StartWith("The plan has changed since");
        (await app.Ui.Run(() => vm.Snapshot.PlanVersion)).Should().Be(version);
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.PlanVersion == version, "worked out again on the plan as it is");
    }

    [Fact]
    public async Task The_regulation_list_shows_a_location_in_plan_order_and_marks_trains_out_of_wtt_order()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (locations, first) = await app.Ui.Run(() => (vm.Regulation.Locations.Count, vm.SelectedCorridor!.Corridor.Locations[0].Tiploc));
        locations.Should().BeGreaterThan(10, "every corridor location");

        await app.Ui.Run(() => vm.ShowRegulation(first));

        (await app.Ui.Run(() => (vm.SideTab, vm.Regulation.Location?.Tiploc))).Should().Be((MainViewModel.RegulationTab, (Tiploc?)first));
        await app.Ui.WaitUntil(() => vm.Regulation.Rows.Count > 1, "movements listed");
        var rows = await app.Ui.Run(() => vm.Regulation.Rows.ToList());
        rows.Should().OnlyContain(r => !r.OutOfOrder, "the plan is the WTT");
        (await app.Ui.Run(() => vm.Regulation.Summary)).Should().MatchRegex(@"^\d+ movements planned$");

        // Hold the first train until after the second: it now goes behind it.
        var listed = await app.Ui.Run(() => RegulationList.For(vm.Snapshot.Plan, vm.Snapshot.Forecast, first, null));
        var (a, b) = (listed[0], listed.First(r => r.Train != listed[0].Train && r.Planned > listed[0].Planned));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new ShiftTrain(a.Train, a.Index, b.Planned - a.Planned + 60)));

        await app.Ui.WaitUntil(() => vm.Regulation.Rows.Any(r => r.OutOfOrder), "the change of order marked");
        var after = await app.Ui.Run(() => vm.Regulation.Rows.ToList());
        after.Single(r => r.Key == $"{a.Train}:{a.Index}").Order.Should().StartWith("Behind ").And.Contain(b.Headcode.Value);
        after.Single(r => r.Key == $"{b.Train}:{b.Index}").Order.Should().StartWith("Ahead of ").And.Contain(a.Headcode.Value);
        (await app.Ui.Run(() => vm.Regulation.Summary)).Should().EndWith(" out of WTT order");
    }

    [Fact]
    public async Task The_graph_menu_offers_the_regulation_list_for_a_train_s_location()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (hit, location) = await app.Ui.Run(() =>
        {
            var path = vm.Scene!.InLayer(GraphLayer.Wtt).First(p => p.Vertices.Count >= 2 && p.Train is not null && p.PointAt(0) is not null);
            var hit = new GraphHit(path, 0, 0, 0, path.Vertices[0].Time);
            return (hit, vm.Snapshot.Plan.Get(path.Train!.Value).Points[path.PointAt(0)!.Value].Location);
        });

        var item = await app.Ui.Run(() => vm.MenuFor(hit).Single(i => i.Header?.StartsWith("Re_gulation list at ", StringComparison.Ordinal) == true));
        await app.Ui.Run(() => item.Command!.Execute(null));

        (await app.Ui.Run(() => (vm.SideTab, vm.Regulation.Location?.Tiploc))).Should().Be((MainViewModel.RegulationTab, (Tiploc?)location));
    }
}
