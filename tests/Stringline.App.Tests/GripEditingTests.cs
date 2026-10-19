using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S26's grips on Waterloo (#10): select a train, drag or nudge a grip, see the command, and undo it.</summary>
public sealed class GripEditingTests
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

    /// <summary>Selects the first train in view with a movable grip of this kind there (away from the plot's right edge), and returns that grip.</summary>
    private static GraphGrip SelectTrainWith(MainViewModel vm, GraphGripKind kind)
    {
        var view = vm.Navigator.Viewport;
        foreach (var path in vm.Scene!.InLayer(GraphLayer.Wtt))
        {
            for (var v = 1; v < path.Vertices.Count; v++)
            {
                var at = view.ToScreen(path.Vertices[v].Time, path.Vertices[v].Axis);
                var fits = view.IsInPlot(at) && at.X < view.PlotRight - 150 && at.X > view.PlotLeft + 50;
                if (!fits || (kind == GraphGripKind.Dwell) != path.IsDwellEnd(v))
                {
                    continue;
                }

                vm.SelectPath(new GraphHit(path, v - 1, 0, v, path.Vertices[v].Time));
                var grip = vm.Grips?.InView(view).FirstOrDefault(g => g.Kind == kind && !g.IsLocked && g.Index > 0 && view.ToScreen(g.At.Time, g.At.Axis).X < view.PlotRight - 150);
                if (grip is not null && vm.GripAt(view.ToScreen(grip.At.Time, grip.At.Axis)) is { } hit && hit.SameAs(grip))
                {
                    return grip;
                }
            }
        }

        throw new InvalidOperationException($"No train in view with a {kind} grip.");
    }

    private static ScreenPoint ScreenOf(MainViewModel vm, GraphGrip grip) => vm.Navigator.Viewport.ToScreen(grip.At.Time, grip.At.Axis);

    private static ScreenPoint Later(MainViewModel vm, ScreenPoint at, int seconds) => vm.Navigator.Viewport.IsVertical
        ? new(at.X, at.Y + (seconds * vm.Navigator.Viewport.PixelsPerSecond))
        : new(at.X + (seconds * vm.Navigator.Viewport.PixelsPerSecond), at.Y);

    [Fact]
    public async Task Selecting_a_train_draws_its_grips_and_hovering_one_names_its_action()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            vm.Grips.Should().BeNull("no train is selected");
            var grip = SelectTrainWith(vm, GraphGripKind.Point);
            vm.Grips!.Train.Should().Be(vm.Selection!.Train!.Value);
            vm.Grips.Grips.Should().OnlyContain(g => g.Train == vm.Grips.Train && !g.IsLocked, "the sim has not started");

            vm.HoverGrip(grip, overLine: false);
            vm.Grips.Hot.Should().Be(grip);
            vm.GripHint.Should().Be($"{grip.Action}. Shift: 1 s steps; Esc cancels.");
            vm.EditStatusText.Should().Be(vm.GripHint);
            vm.DescribeGrip(grip).ValueOf("Drag").Should().StartWith("retime ");

            vm.HoverGrip(null, overLine: true);
            vm.GripHint.Should().StartWith("Drag to retime the rest of ");
            vm.HoverGrip(null, overLine: false);
            vm.GripHint.Should().BeNull();
            vm.EditStatusText.Should().Be(vm.EditStatus);

            vm.SelectPath(null);
            vm.Grips.Should().BeNull();
        });
    }

    [Fact]
    public async Task Dragging_a_square_retimes_from_its_point_and_undoes()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (grip, train) = await app.Ui.Run(() =>
        {
            var g = SelectTrainWith(vm, GraphGripKind.Point);
            var at = ScreenOf(vm, g);
            vm.BeginGrip(g, at).Should().BeTrue();
            vm.IsDragging.Should().BeTrue();
            vm.Grips!.Drag!.Grip.Should().Be(g);

            vm.MoveEdit(Later(vm, at, 180), fine: false);
            vm.Ghosts.Should().NotBeNull();
            vm.Grips.Drag!.Label.Should().EndWith("(+3 min)");
            vm.Grips.Drag.At.Time.Should().Be(g.At.Time + 180, "the grip is drawn where it has got to");
            vm.EditStatus.Should().StartWith("Shift ").And.Contain("by +3 min");
            return (g, g.Train);
        });

        await app.Ui.RunAsync(() => vm.EndEditAsync(commit: true));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 1 && vm.Grips?.Paths[0].Layer == GraphLayer.Plan, "the edit applied and the grips on the plan line");
        await app.Ui.Run(() =>
        {
            vm.Snapshot.History.Done[0].Command.Should().Be(new ShiftTrain(train, grip.Index, 180));
            vm.Grips!.Drag.Should().BeNull();
            vm.Grips.Selected.Should().NotBeNull().And.Match<GraphGrip>(g => g.SameAs(grip), "the grip dragged stays picked for the arrow keys");
            vm.Grips.Selected!.At.Time.Should().Be(grip.At.Time + 180);
        });

        await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 0 && vm.Grips?.Paths[0].Layer == GraphLayer.Wtt, "the undo");
        (await app.Ui.Run(() => vm.Snapshot.Plan.Get(train).IsModified)).Should().BeFalse();
    }

    [Fact]
    public async Task Dragging_a_bar_changes_the_departure()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (grip, depart) = await app.Ui.Run(() =>
        {
            var g = SelectTrainWith(vm, GraphGripKind.Dwell);
            var at = ScreenOf(vm, g);
            vm.BeginGrip(g, at).Should().BeTrue();
            vm.MoveEdit(Later(vm, at, 120), fine: false);
            return (g, vm.Snapshot.Plan.Get(g.Train).Points[g.Index].Depart!.Value);
        });

        await app.Ui.RunAsync(() => vm.EndEditAsync(commit: true));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 1, "the edit");
        (await app.Ui.Run(() => vm.Snapshot.History.Done[0].Command)).Should().Be(new SetDwell(grip.Train, grip.Index, depart + 120));
    }

    [Fact]
    public async Task The_arrow_keys_nudge_the_picked_grip()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var grip = await app.Ui.Run(() =>
        {
            var g = SelectTrainWith(vm, GraphGripKind.Point);
            vm.Nudge(15).Should().BeFalse("no grip is picked yet");
            vm.SelectGrip(g);
            vm.Grips!.Selected.Should().Be(g);
            vm.EditStatus.Should().Contain("nudge it with");
            vm.Nudge(15).Should().BeTrue();
            return g;
        });

        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 1, "the first nudge");
        await app.Ui.Run(() => vm.Nudge(60).Should().BeTrue());
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 2 && vm.Grips?.Selected?.At.Time == grip.At.Time + 75, "the second nudge, and the grip still picked where it went");
        await app.Ui.Run(() =>
        {
            vm.Snapshot.History.Done.Select(d => d.Command).Should().Equal(new ShiftTrain(grip.Train, grip.Index, 15), new ShiftTrain(grip.Train, grip.Index, 60));
            vm.EditStatus.Should().StartWith("Done: Shift ");
        });

        await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
        await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 0, "both undone");
        (await app.Ui.Run(() => vm.Snapshot.Plan.Get(grip.Train).IsModified)).Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_trains_grips_are_locked_and_say_why()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var grip = await app.Ui.Run(() => SelectTrainWith(vm, GraphGripKind.Point));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new CancelTrain(grip.Train)));
        await app.Ui.WaitUntil(() => vm.Grips?.Grips.All(g => g.IsLocked) == true && vm.Grips.Paths[0].Layer == GraphLayer.Plan, "the grips locked");

        await app.Ui.Run(() =>
        {
            var locked = vm.Grips!.Find(grip)!;
            var reason = $"{vm.Snapshot.Plan.Get(grip.Train).Headcode} is cancelled; reinstate it to retime it.";
            locked.LockReason.Should().Be(reason);
            vm.BeginGrip(locked, ScreenOf(vm, locked)).Should().BeFalse();
            vm.IsDragging.Should().BeFalse();
            vm.EditStatus.Should().Be(reason);
            vm.SelectGrip(locked);
            vm.Grips!.Selected.Should().BeNull();
            vm.EditStatus.Should().Be($"Locked: {reason}");
            vm.Nudge(15).Should().BeFalse();
            vm.HoverGrip(locked, overLine: false);
            vm.GripHint.Should().Be($"Locked: {reason}");
        });
    }

    [Fact]
    public async Task Pressing_the_selected_line_drags_its_body_and_labels_the_time()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            var grip = SelectTrainWith(vm, GraphGripKind.Point);
            var path = vm.Grips!.Paths[0];
            var view = vm.Navigator.Viewport;
            var i = Enumerable.Range(0, path.Vertices.Count - 1).First(k =>
                path.Vertices[k].Axis != path.Vertices[k + 1].Axis && view.IsInPlot(view.ToScreen(path.Vertices[k].Time, path.Vertices[k].Axis)) &&
                view.IsInPlot(view.ToScreen(path.Vertices[k + 1].Time, path.Vertices[k + 1].Axis)));
            var (a, b) = (path.Vertices[i], path.Vertices[i + 1]);
            var at = view.ToScreen((a.Time + b.Time) / 2, (a.Axis + b.Axis) / 2);
            var hit = vm.SelectedLineAt(at);
            hit.Should().NotBeNull();
            hit!.Path.Train.Should().Be(grip.Train);

            vm.BeginEdit(hit, at, fromVertex: false).Should().BeTrue();
            vm.Grips!.Drag.Should().NotBeNull();
            vm.MoveEdit(Later(vm, at, 60), fine: false);
            vm.Grips.Drag!.Label.Should().EndWith("(+1 min)");
        });
        await app.Ui.RunAsync(() => vm.EndEditAsync(commit: false));
        (await app.Ui.Run(() => vm.Grips!.Drag)).Should().BeNull();
    }
}
