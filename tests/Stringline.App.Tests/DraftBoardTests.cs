using Stringline.App.Controls;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Timetable.Editing;

namespace Stringline.App.Tests;

/// <summary>
/// The draft board in the editor (S44, <c>08-…md</c> §7) on a Waterloo draft: it draws the draft on the profile's
/// corridors with its booked conflicts, shares the selection with the trains list and trip grid, a grip's drag retimes
/// the draft (the grid shows it, undo takes it back) and leaves a ghost of the original, a recording's actuals come
/// in as ghosts, and the board exports.
/// </summary>
public sealed class DraftBoardTests : IDisposable
{
    private const string Waterloo = "waterloo 2010.WTT";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-draft-board-");

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

    /// <summary>Loads the copy of Waterloo, makes a draft of it, and waits for its board to draw.</summary>
    private async Task<TimetableEditorViewModel> DraftOfLoaded(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Source()));
        await app.Ui.WaitUntil(() => vm.NewDraftFromLoadedCommand.CanExecute(null), "the timetable loaded");
        await app.Ui.Run(() => vm.NewDraftFromLoadedCommand.Execute(null));
        var draft = await app.Ui.Run(() => vm.Draft!);
        await app.Ui.WaitUntil(() => draft.Editor.Knowledge is not null, "the sim's knowledge");
        await app.Ui.WaitUntil(() => draft.Board.Board.Scene is not null && draft.Board.Plan?.Sequence == draft.Snapshot.Sequence, "the draft board");
        return draft;
    }

    /// <summary>Picks the first train in the list with a movable timing point's grip after its first on the board.</summary>
    private static GraphGrip PickTrainWithGrip(TimetableEditorViewModel draft)
    {
        foreach (var row in draft.Trains.ToList())
        {
            draft.SelectedTrainRow = row;
            if (draft.Board.Board.Grips?.Grips.FirstOrDefault(g => g.Kind == GraphGripKind.Point && g.Index >= 1 && !g.IsLocked) is { } grip)
            {
                return grip;
            }
        }

        throw new InvalidOperationException("No train on the board has a grip.");
    }

    [Fact]
    public async Task The_board_draws_the_draft_shares_the_selection_and_a_drag_retimes_the_grid_and_undoes()
    {
        await using var app = App();
        var draft = await DraftOfLoaded(app);
        var editor = (IGraphEditor)draft.Board;

        var (grip, before, trip) = await app.Ui.Run(() =>
        {
            var board = draft.Board.Board;
            board.Corridor.Should().NotBeNull();
            board.Scene!.Title.Should().StartWith("Draft – ").And.Contain(board.Corridor!.Name);
            board.Scene.Paths.Should().OnlyContain(p => p.Layer == GraphLayer.Plan, "the booking only, with no ghosts yet");
            draft.Board.Status.Should().MatchRegex("booked conflict|No booked conflicts");
            editor.NudgeSeconds.Should().Be(30);

            // The grid's train is the board's, and the grid's row its grip.
            var grip = PickTrainWithGrip(draft);
            var train = draft.Snapshot.Import.Timetable.Get(grip.Train);
            board.Selection!.Train.Should().Be(grip.Train);
            var trip = draft.Snapshot.Import.TripOf(train, grip.Index).Element;
            draft.Train!.CurrentTrip = draft.Train.Trips.Single(r => r.Element == trip);
            board.Grips!.Selected.Should().NotBeNull();
            board.Grips.Selected!.Index.Should().Be(grip.Index);

            // Drag the grip a minute later: one edit of the draft, which the grid shows.
            var view = board.Navigator.Viewport;
            var at = view.ToScreen(grip.At.Time, grip.At.Axis);
            var before = new TripNode(draft.Document, trip).DepPassTime ?? new TripNode(draft.Document, trip).ArrTime;
            editor.BeginGrip(grip, at).Should().BeTrue(draft.Board.EditStatus);
            editor.MoveEdit(view.IsVertical ? new ScreenPoint(at.X, at.Y + (60 * view.PixelsPerSecond)) : new ScreenPoint(at.X + (60 * view.PixelsPerSecond), at.Y), fine: false);
            board.Ghosts.Should().NotBeNullOrEmpty("the drag shows where the train would run");
            editor.EndEditAsync(commit: true).IsCompleted.Should().BeTrue();
            draft.Error.Should().BeNull();
            draft.UndoHeader.Should().Contain("retime from");
            var after = new TripNode(draft.Document, trip).DepPassTime ?? new TripNode(draft.Document, trip).ArrTime;
            after.Should().Be(before + 60);
            draft.Train!.CurrentTrip!.Element.Should().Be(trip, "the grid goes to the point dragged");
            return (grip, before, trip);
        });

        // The board draws the edited draft, with the original's line as a ghost.
        await app.Ui.WaitUntil(() => draft.Board.Board.Scene!.Paths.Any(p => p.Key.StartsWith("orig:", StringComparison.Ordinal) && p.Train == grip.Train), "the original's ghost");
        await app.Ui.Run(() =>
        {
            draft.Board.Board.Ghosts.Should().BeNull("the drag's ghost goes once the board shows the edit");
            draft.UndoCommand.Execute(null);
            (new TripNode(draft.Document, trip).DepPassTime ?? new TripNode(draft.Document, trip).ArrTime).Should().Be(before);
        });
        await app.Ui.WaitUntil(() => draft.Board.Plan?.Sequence == draft.Snapshot.Sequence && !draft.Board.Board.Scene!.Paths.Any(p => p.Key.StartsWith("orig:", StringComparison.Ordinal)), "the ghost gone with the undo");

        // A train clicked on the board is picked in the list, at the point clicked.
        await app.Ui.Run(() =>
        {
            var board = draft.Board.Board;
            var other = board.Scene!.Paths.First(p => p.Layer == GraphLayer.Plan && p.Train != grip.Train && p.Vertices.Count >= 2);
            board.PathClicker(new GraphHit(other, 0, 0, 1, other.Vertices[1].Time));
            board.Selection!.Train.Should().Be(other.Train);
            var train = draft.Snapshot.Import.Timetable.Get(other.Train!.Value);
            draft.SelectedTrainRow!.Element.Should().Be(draft.Snapshot.Import.NodeOf(train).Element);
            draft.Train!.CurrentTrip!.Element.Should().Be(draft.Snapshot.Import.TripOf(train, other.PointAt(1)!.Value).Element);

            // The right-click menu goes to it too.
            var menu = editor.MenuFor(new GraphHit(other, 0, 0, 0, other.Vertices[0].Time), default);
            menu.Should().HaveCount(2);
            menu[1].Command.Should().NotBeNull();
        });
    }

    [Fact]
    public async Task A_recordings_actuals_come_in_as_ghosts_and_the_board_exports()
    {
        await using var app = App();
        var draft = await DraftOfLoaded(app);

        app.Dialogs.Files.Enqueue(Fixtures.Path("waterloo-1x.sslog"));
        await app.Ui.RunAsync(() => draft.Board.LoadRecordingCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => draft.Board.Board.Scene!.Paths.Any(p => p.Layer == GraphLayer.Actual), "the recording's actuals");
        await app.Ui.Run(() =>
        {
            draft.Board.RecordingName.Should().Be("waterloo-1x.sslog");
            draft.Board.Board.Scene!.Paths.Where(p => p.Layer == GraphLayer.Actual).Should().OnlyContain(p => p.Colour.A < 0xFF, "faint");
            draft.Board.ClearRecordingCommand.Execute(null);
        });
        await app.Ui.WaitUntil(() => !draft.Board.Board.Scene!.Paths.Any(p => p.Layer == GraphLayer.Actual), "the actuals cleared");

        var png = Path.Combine(_root.FullName, "draft.png");
        var pdf = Path.Combine(_root.FullName, "draft.pdf");
        app.Dialogs.SaveFiles.Enqueue(png);
        app.Dialogs.SaveFiles.Enqueue(pdf);
        await app.Ui.Run(() =>
        {
            draft.Board.ExportPngCommand.Execute(null);
            draft.Board.ExportPdfCommand.Execute(null);
        });

        File.ReadAllBytes(png).Take(4).Should().Equal(0x89, (byte)'P', (byte)'N', (byte)'G');
        File.ReadAllBytes(pdf).Take(4).Should().Equal((byte)'%', (byte)'P', (byte)'D', (byte)'F');
        app.Dialogs.SaveSuggestions.Should().AllSatisfy(s => Path.GetFileName(s).Should().StartWith("waterloo-draft-"));
    }
}
