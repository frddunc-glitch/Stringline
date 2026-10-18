using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S12's re-planning in the shell: the detail panel, history, undo and redo, drag edits and the right-click menu.</summary>
public sealed class PlanEditingTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    /// <summary>Loads Waterloo's WTT with no session: the graph shows the timetable from its start.</summary>
    private static async Task<MainViewModel> LoadWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true && vm.Snapshot.Plan.Count > 0, "the WTT on the graph");
        await app.Ui.Run(() => vm.Tick());
        return vm;
    }

    /// <summary>A sloping stretch of some train's WTT line in view, and the press that hits it there.</summary>
    private static (GraphHit Hit, ScreenPoint At) FindLine(MainViewModel vm, Func<GraphPath, bool>? where = null)
    {
        var scene = vm.Scene!;
        var view = vm.Navigator.Viewport;
        foreach (var path in scene.InLayer(GraphLayer.Wtt).Where(where ?? (_ => true)))
        {
            for (var i = 0; i + 1 < path.Vertices.Count; i++)
            {
                var (a, b) = (path.Vertices[i], path.Vertices[i + 1]);
                var at = view.ToScreen((a.Time + b.Time) / 2, (a.Axis + b.Axis) / 2);
                if (a.Axis == b.Axis || b.Time - a.Time < 120 || !view.IsInPlot(at) || at.X > view.PlotRight - 100)
                {
                    continue;
                }

                if (GraphHitTester.HitTest(scene, view, at) is { } hit && hit.Path.Key == path.Key && hit.Path.PointAt(i) is { } p && vm.Snapshot.Plan.Find(path.Train!.Value)!.Points[p].IsTimed)
                {
                    return (hit, at);
                }
            }
        }

        throw new InvalidOperationException("No train line in view to press on.");
    }

    private static ScreenPoint Later(MainViewModel vm, ScreenPoint at, int seconds) => new(at.X + (seconds * vm.Navigator.Viewport.PixelsPerSecond), at.Y);

    [Fact]
    public async Task Selecting_a_train_shows_its_schedule()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            vm.Detail.HasTrain.Should().BeFalse();
            var (hit, _) = FindLine(vm);
            vm.SelectPath(hit);

            var plan = vm.Snapshot.Plan.Get(hit.Path.Train!.Value);
            vm.Detail.HasTrain.Should().BeTrue();
            vm.Detail.Heading.Should().Be(plan.Headcode.ToString());
            vm.Detail.Rows.Should().NotBeEmpty();
            vm.Detail.Rows.Should().OnlyContain(r => !r.TimesChanged && r.Runs);
            vm.Detail.Rows.Should().Contain(r => r.WttText.Length > 0 && r.PlanDepartText.Length > 0);
            vm.Detail.CanReset.Should().BeFalse();
            vm.CancelSelectedText.Should().Be("Cancel train");
            vm.ResetSelectedCommand.CanExecute(null).Should().BeFalse();
            vm.CancelSelectedCommand.CanExecute(null).Should().BeTrue();
            vm.UndoCommand.CanExecute(null).Should().BeFalse();
            vm.History.Summary.Should().Be("No edits");

            vm.SelectPath(null);
            vm.Detail.HasTrain.Should().BeFalse();
            vm.Detail.Rows.Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Edits_go_into_the_history_and_undo_and_redo()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var train = await app.Ui.Run(() =>
        {
            var (hit, _) = FindLine(vm);
            vm.SelectPath(hit);
            return hit.Path.Train!.Value;
        });

        // Typing a later departure into the origin's plan cell retimes the train.
        await app.Ui.Run(() =>
        {
            var origin = vm.Detail.Rows[0];
            var depart = vm.Snapshot.Plan.Get(train).Points[origin.Index].Depart!.Value;
            origin.PlanDepartText = (depart + 300).ToString();
        });
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 1 && vm.Scene!.Paths.Any(p => p.Layer == GraphLayer.Plan && p.Train == train), "the edit applied and drawn");

        await app.Ui.Run(() =>
        {
            vm.Snapshot.Plan.Get(train).IsModified.Should().BeTrue();
            vm.Detail.Rows[0].TimesChanged.Should().BeTrue();
            vm.Detail.CanReset.Should().BeTrue();
            vm.History.Entries.Should().ContainSingle().Which.Description.Should().StartWith("Shift ").And.Contain("by +5 min");
            vm.History.Summary.Should().Be("1 edit");
            vm.UndoHeader.Should().StartWith("_Undo Shift ");
            vm.UndoCommand.CanExecute(null).Should().BeTrue();
            vm.EditStatus.Should().StartWith("Done: Shift ");
        });

        await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 0, "the undo");
        await app.Ui.Run(() =>
        {
            vm.Snapshot.Plan.Get(train).IsModified.Should().BeFalse();
            vm.History.Entries.Should().ContainSingle().Which.IsUndone.Should().BeTrue();
            vm.History.Summary.Should().Be("0 edits, 1 undone");
            vm.RedoCommand.CanExecute(null).Should().BeTrue();
            vm.EditStatus.Should().StartWith("Undone: Shift ");
        });

        await app.Ui.RunAsync(() => vm.RedoCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 1, "the redo");

        await app.Ui.RunAsync(() => vm.ResetSelectedCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 2, "the reset");
        (await app.Ui.Run(() => vm.Snapshot.Plan.Get(train).IsModified)).Should().BeFalse();
    }

    [Fact]
    public async Task A_bad_time_or_a_refused_edit_is_shown_in_the_info_bar()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            var (hit, _) = FindLine(vm);
            vm.SelectPath(hit);
            vm.Detail.Rows[0].PlanDepartText = "soon";
            vm.InfoSeverity.Should().Be(InfoSeverity.Error);
            vm.InfoMessage.Should().Be("'soon' is not a time; enter one such as 10:25 or 10:25:30.");
        });

        var result = await app.Ui.Run(() => vm.ExecuteAsync(new ResetTrain(vm.Selection!.Train!.Value)));
        (await result).Applied.Should().BeFalse();
        (await app.Ui.Run(() => vm.InfoMessage)).Should().EndWith("not done. " + (await result).Error);
        (await app.Ui.Run(() => vm.History.Entries.Count)).Should().Be(0);
    }

    [Fact]
    public async Task Dragging_the_selected_train_previews_a_ghost_and_retimes_it_on_release()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (train, at) = await app.Ui.Run(() =>
        {
            var (hit, at) = FindLine(vm);
            vm.BeginEdit(hit, at, fromVertex: false).Should().BeFalse("only the selected train is dragged; this one pans");
            vm.SelectPath(hit);
            vm.BeginEdit(hit, at, fromVertex: false).Should().BeTrue();
            vm.IsDragging.Should().BeTrue();
            vm.UndoCommand.CanExecute(null).Should().BeFalse("not while dragging");

            vm.MoveEdit(Later(vm, at, 180), fine: false);
            vm.Ghosts.Should().NotBeNull().And.OnlyContain(g => g.Key.StartsWith("ghost:", StringComparison.Ordinal) && g.Train == hit.Path.Train);
            vm.EditStatus.Should().StartWith("Shift ").And.Contain("by +3 min");
            return (hit.Path.Train!.Value, at);
        });

        await app.Ui.RunAsync(() => vm.EndEditAsync(commit: true));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 1 && vm.Ghosts is null, "the edit applied and drawn");

        await app.Ui.Run(() =>
        {
            vm.IsDragging.Should().BeFalse();
            vm.Snapshot.History.Done[0].Command.Should().BeOfType<ShiftTrain>().Which.DeltaSeconds.Should().Be(180);
            vm.Snapshot.Plan.Get(train).IsModified.Should().BeTrue();
            vm.Scene!.Paths.Should().Contain(p => p.Layer == GraphLayer.Plan && p.Train == train);
        });

        // A drag dropped with Esc changes nothing.
        await app.Ui.Run(() =>
        {
            var plan = vm.Scene!.Paths.First(p => p.Layer == GraphLayer.Plan && p.Train == train);
            var hit = GraphHitTester.HitTest(vm.Scene, vm.Navigator.Viewport, vm.Navigator.Viewport.ToScreen(
                (plan.Vertices[0].Time + plan.Vertices[1].Time) / 2, (plan.Vertices[0].Axis + plan.Vertices[1].Axis) / 2))!;
            vm.BeginEdit(hit, at, false).Should().BeTrue();
            vm.MoveEdit(Later(vm, at, 600), false);
        });
        await app.Ui.RunAsync(() => vm.EndEditAsync(commit: false));
        await app.Ui.Run(() =>
        {
            vm.Ghosts.Should().BeNull();
            vm.EditStatus.Should().Be("Edit cancelled.");
            vm.Snapshot.History.Done.Should().HaveCount(1);
        });
    }

    [Fact]
    public async Task The_right_click_menu_cancels_holds_and_clones()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (hit, train) = await app.Ui.Run(() =>
        {
            var (h, _) = FindLine(vm, p => p.Points!.Count >= 3);
            return (h, h.Path.Train!.Value);
        });

        var menu = await app.Ui.Run(() => vm.MenuFor(hit));
        await app.Ui.Run(() =>
        {
            vm.Selection!.Train.Should().Be(train, "a right click selects the train");
            menu[0].Command.Should().BeNull("the first item names the train and place");
            menu.Select(m => m.Header).Should().Contain(["_Cancel train", "Cl_one as ad-hoc…", "Change _platform…", "_Hold here…"]);
            vm.MenuFor(null).Should().BeEmpty();
        });

        // Cancel, then reinstate from the detail panel.
        await app.Ui.Run(() => menu.First(m => m.Header == "_Cancel train").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Get(train).Cancelled && vm.Scene!.Paths.Any(p => p.Train == train && p.IsCancelled), "the cancellation drawn");
        await app.Ui.Run(() =>
        {
            vm.CancelSelectedText.Should().Be("Reinstate");
            vm.Detail.StatusText.Should().StartWith("Cancelled");
        });
        await app.Ui.RunAsync(() => vm.CancelSelectedCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => !vm.Snapshot.Plan.Get(train).Cancelled, "the reinstatement");

        // Hold here asks until when.
        var plan = await app.Ui.Run(() => vm.Snapshot.Plan.Get(train));
        var index = hit.Path.PointAt(hit.NearestVertex)!.Value;
        var point = plan.Points[index];
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Value = ((point.Depart ?? point.Arrive)!.Value + 600).ToString();
            return true;
        });
        menu = await app.Ui.Run(() => vm.MenuFor(hit));
        await app.Ui.Run(() => menu.First(m => m.Header == "_Hold here…").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.History.Done.Count == 3, "the hold");
        (await app.Ui.Run(() => vm.Snapshot.History.Done[^1].Command)).Should().BeOfType(point.IsPass ? typeof(AddStop) : typeof(SetDwell));

        // Clone asks for a headcode and an offset, and selects the copy.
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Value = "1z99";
            p.Fields[1].Value = "30";
            return true;
        });
        await app.Ui.RunAsync(() => vm.CloneSelectedCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Find(new TrainId("1Z99#c1")) is not null && vm.Detail.IsClone, "the clone selected");
        await app.Ui.Run(() =>
        {
            vm.Selection!.Train.Should().Be(new TrainId("1Z99#c1"));
            vm.Detail.Heading.Should().Be("1Z99");
            vm.Detail.CanReset.Should().BeFalse();
        });
    }

    [Fact]
    public async Task A_prompt_that_is_cancelled_or_invalid_changes_nothing()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        await app.Ui.Run(() => vm.SelectPath(FindLine(vm).Hit));

        app.Dialogs.Prompts.Enqueue(_ => false);
        await app.Ui.RunAsync(() => vm.CloneSelectedCommand.ExecuteAsync(null));
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Value = "";
            return true;
        });
        await app.Ui.RunAsync(() => vm.CloneSelectedCommand.ExecuteAsync(null));

        app.Dialogs.Prompted.Should().HaveCount(2);
        app.Dialogs.Prompted[1].Error.Should().Be("Enter a headcode, such as 1Z99.");
        (await app.Ui.Run(() => vm.Snapshot.History.Done.Count)).Should().Be(0);
    }

    [Fact]
    public async Task A_live_train_can_be_linked_by_hand_from_its_menu()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), Fixtures.Path(Waterloo), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Matches.Matched > 100 && vm.Scene?.InLayer(GraphLayer.Actual).Any(p => p.Train is not null) == true, "matched trains on the graph");

        var (hit, uid, headcode) = await app.Ui.Run(() =>
        {
            var path = vm.Scene!.InLayer(GraphLayer.Actual).First(p => p.Train is not null);
            var live = vm.Snapshot.FindByUid(path.LiveUid!)!;
            return (new GraphHit(path, 0, 0, 1, path.Vertices[1].Time), path.LiveUid!, live.Workings[path.Working!.Value].Headcode);
        });
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Options.Should().Contain("(do not match)");
            p.Fields[0].Value = "(do not match)";
            return true;
        });

        var menu = await app.Ui.Run(() => vm.MenuFor(hit));
        await app.Ui.Run(() => menu.First(m => m.Header == "_Link to WTT train…").Command!.Execute(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.FindLink(uid, headcode) is { Train: null }, "the working marked not to match");

        menu = await app.Ui.Run(() => vm.MenuFor(new GraphHit(hit.Path, 0, 0, 1, hit.Time)));
        menu.Select(m => m.Header).Should().Contain("_Match automatically");
    }

    [Theory]
    [InlineData("07:30", "07:00", "07:30:00")]
    [InlineData("7:30:15", "07:00", "07:30:15")]
    [InlineData("07:30½", "07:00", "07:30:30")]
    [InlineData("00:10", "23:55", "00:10:00+1")]
    [InlineData("23:50", "00:05+1", "23:50:00")]
    [InlineData("10:00 pass", "10:00", "10:00:00")]
    [InlineData("00:10+1", "06:00", "00:10:00+1")]
    public void Times_are_read_on_the_nearest_day(string text, string near, string expected)
    {
        PlanInput.TryParseTime(text, SimTime.Parse(near), out var time).Should().BeTrue();
        time.Should().Be(SimTime.Parse(expected));
    }

    [Theory]
    [InlineData("2", 120)]
    [InlineData("1.5", 90)]
    [InlineData("0,5", 30)]
    [InlineData("-3", -180)]
    public void Minutes_are_read_as_seconds(string text, int seconds)
    {
        PlanInput.TryParseMinutes(text, out var s).Should().BeTrue();
        s.Should().Be(seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("1e9")]
    public void Nonsense_is_not_read(string text)
    {
        PlanInput.TryParseMinutes(text, out _).Should().BeFalse();
        PlanInput.TryParseTime(text, SimTime.Zero, out _).Should().BeFalse();
    }
}
