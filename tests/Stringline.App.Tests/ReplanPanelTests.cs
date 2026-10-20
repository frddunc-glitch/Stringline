using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S20 in the shell: Plan ▸ Re-plan shows the optimiser's plan in the suggestions panel to preview and apply.</summary>
public sealed class ReplanPanelTests
{
    private static async Task<MainViewModel> LoadWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path("waterloo 2010.WTT")));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true && vm.Snapshot.Plan.Count > 0 && vm.Snapshot.Forecast.Interaction, "the WTT on the graph");
        await app.Ui.Run(() => vm.Tick());
        return vm;
    }

    [Fact]
    public async Task Re_plan_the_next_hour_preview_and_apply()
    {
        // More time than the default, so a busy machine running the other tests still finds a plan.
        await using var app = new TestApp(s => s with { Planning = s.Planning with { ReplanTimeLimitSeconds = 20 } });
        var vm = await LoadWaterloo(app);

        // A copy of the train in the first conflict, a minute behind it, clashes with it all the way.
        var (source, version) = await app.Ui.Run(() => (vm.Snapshot.Conflicts.All[0].A, vm.Snapshot.PlanVersion));
        var clone = await app.Ui.Run(() => vm.Snapshot.Plan.NewCloneId(new Headcode("9Z99")));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new CloneTrain(source, new Headcode("9Z99"), 60, clone)));
        await app.Ui.WaitUntil(() => vm.Snapshot.PlanVersion > version && vm.Snapshot.Conflicts.All.Any(c => c.Involves(clone)), "the copy's conflicts");
        (await app.Ui.Run(() => vm.ReplanHeader)).Should().Be("Re-_plan the next 60 min…");

        await app.Ui.Run(() => vm.ReplanCommand.Execute(null));
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.IsReplan && vm.SuggestionPanel.Rows.Count > 0, "the re-plan worked out", 60);

        var (heading, row, visible, tab) = await app.Ui.Run(() => (vm.SuggestionPanel.Heading, vm.SuggestionPanel.Rows.Single(), vm.SuggestionPanel.IsVisible, vm.SideTab));
        (visible, tab).Should().Be((true, 0));
        heading.Should().StartWith("Re-plan to ");
        row.Replan!.Outcome.Should().Be(ReplanOutcome.Improved);
        row.Replan.Method.Should().Be("optimiser");
        row.Title.Should().StartWith("Re-plan to ").And.Contain("hold");
        row.Detail.Should().StartWith("Conflicts to ");
        row.Steps.Should().StartWith("• ");
        row.Tip.Should().Contain("Worked out by the optimiser");
        row.Changed.Should().NotBeEmpty();

        // The panel keeps it as the sim moves on, until a conflict is picked.
        await app.Ui.Run(() => vm.Tick());
        (await app.Ui.Run(() => vm.SuggestionPanel.IsReplan && vm.SuggestionPanel.Rows.Count == 1)).Should().BeTrue();

        // Preview draws the trains it changes as ghosts.
        await app.Ui.Run(() => vm.PreviewSuggestionCommand.Execute(row));
        var ghosts = await app.Ui.Run(() => vm.Ghosts);
        ghosts.Should().NotBeNullOrEmpty();
        ghosts!.Should().OnlyContain(g => row.Changed.Any(id => id == g.Train));

        // Apply: one edit, named after it; the panel says so and the copy's clashes are gone.
        var before = await app.Ui.Run(() => vm.Snapshot.PlanVersion);
        await app.Ui.RunAsync(() => vm.ApplySuggestionCommand.ExecuteAsync(row));
        await app.Ui.WaitUntil(() => vm.Snapshot.PlanVersion > before, "the re-plan applied");
        (await app.Ui.Run(() => vm.Snapshot.History.UndoDescription)).Should().Be($"Suggestion: {row.Replan.Describe()}");
        (await app.Ui.Run(() => vm.SuggestionPanel.Status)).Should().StartWith("Applied: Re-plan to ");
        (await app.Ui.Run(() => vm.SuggestionPanel.Rows.Count)).Should().Be(0);
        (await app.Ui.Run(() => vm.Snapshot.Conflicts.All.Count(c => c.Involves(clone) && c.When <= row.Replan.Until)))
            .Should().BeLessThan(row.Replan.Before.Count(c => c.Involves(clone)));

        // Picking a conflict shows its suggestions instead; Close shuts a re-plan.
        if (await app.Ui.Run(() => vm.ConflictList.Rows.FirstOrDefault()) is { } pick)
        {
            await app.Ui.Run(() => vm.ConflictList.Selected = pick);
            await app.Ui.WaitUntil(() => !vm.SuggestionPanel.IsReplan && vm.SuggestionPanel.Heading.StartsWith("Resolve: ", StringComparison.Ordinal), "the conflict's suggestions");
        }

        await app.Ui.Run(() => vm.ConflictList.Selected = null);
        await app.Ui.Run(() => vm.ReplanCommand.Execute(null));
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.IsReplan && !vm.SuggestionPanel.Status.StartsWith("Working", StringComparison.Ordinal), "a second re-plan", 60);
        await app.Ui.Run(() => vm.CloseReplanCommand.Execute(null));
        (await app.Ui.Run(() => (vm.SuggestionPanel.IsVisible, vm.SuggestionPanel.IsReplan))).Should().Be((false, false));
    }

    [Fact]
    public async Task A_re_plan_worked_out_on_an_older_plan_is_not_applied()
    {
        // The rules: quick, and the same however busy the machine is.
        await using var app = new TestApp(s => s with { Planning = s.Planning with { ReplanMethod = ReplanMethod.Rules } });
        var vm = await LoadWaterloo(app);
        var source = await app.Ui.Run(() => vm.Snapshot.Conflicts.All[0].A);
        var clone = await app.Ui.Run(() => vm.Snapshot.Plan.NewCloneId(new Headcode("9Z99")));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new CloneTrain(source, new Headcode("9Z99"), 60, clone)));
        await app.Ui.Run(() => vm.ReplanCommand.Execute(null));
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.IsReplan && vm.SuggestionPanel.Rows.Count > 0, "the re-plan worked out", 60);
        var row = await app.Ui.Run(() => vm.SuggestionPanel.Rows.Single());

        // Another edit first: the re-plan is for the plan before it.
        var version = await app.Ui.Run(() => vm.Snapshot.PlanVersion);
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new CancelTrain(clone)));
        await app.Ui.WaitUntil(() => vm.Snapshot.PlanVersion > version, "the edit made");
        var after = await app.Ui.Run(() => vm.Snapshot.PlanVersion);

        await app.Ui.RunAsync(() => vm.ApplySuggestionCommand.ExecuteAsync(row));

        (await app.Ui.Run(() => vm.Snapshot.PlanVersion)).Should().Be(after);
        (await app.Ui.Run(() => vm.EditStatus)).Should().Contain("re-plan again");
    }
}
