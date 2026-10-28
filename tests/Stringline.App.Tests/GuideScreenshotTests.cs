using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Network.Profiles;

namespace Stringline.App.Tests;

/// <summary>
/// The user guide's pictures of the app (S28, #4), from free-sim data only (Waterloo), so they can be made again:
/// <c>build/guide-shots.ps1</c> runs these with <c>STRINGLINE_WINDOW_SHOTS</c> set and copies the <c>guide-*.png</c>
/// files into <c>docs/user-guide/images</c>. Without it they only check the windows lay out.
/// </summary>
public sealed class GuideScreenshotTests
{
    private static readonly string? Shots = Environment.GetEnvironmentVariable("STRINGLINE_WINDOW_SHOTS");

    [Fact]
    public async Task The_main_window_with_a_running_train_selected_and_its_grips()
    {
        await using var app = new TestApp(wpf: true);
        var vm = await MainWindowTests.Replay(app);

        // An hour across, following now, with a train in view that has run part of the way and has some still to go,
        // so its grips show both kinds: hollow for points passed and filled for those to come.
        await app.Ui.Run(() =>
        {
            vm.Navigator.ShowSpan(3600);
            vm.Navigator.Follow();
        });
        var window = await app.Ui.Run(() => MainWindowTests.Show(vm, 1600, 900));
        try
        {
            await app.Ui.WaitUntil(() => vm.Scene is not null && vm.Navigator.Viewport.Width > 0, "the graph laid out");
            var selected = await app.Ui.Run(() => SelectRunningTrain(vm));
            selected.Should().BeTrue("a train in view is part-way through its run");
            await app.Ui.WaitUntil(() => vm.Grips is { } g && g.Grips.Any(x => x.IsLocked) && g.Grips.Any(x => !x.IsLocked), "the train's grips");

            await app.Ui.Run(() =>
            {
                MainWindowTests.Settle(window);
                window.Graph.ActualWidth.Should().BeGreaterThan(300);
                Save((FrameworkElement)window.Content, "guide-main-window.png");
                Save(window.Graph, "guide-grips.png");
            });
        }
        finally
        {
            await app.Ui.Run(window.Close);
        }
    }

    /// <summary>The timetable editor (S48): a draft of Waterloo with a train chosen, its trip grid and the draft board.</summary>
    [Fact]
    public async Task The_timetable_editor()
    {
        await using var app = new TestApp(s => s with { TimetableFolder = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid()) }, wpf: true);
        var vm = app.ViewModel;
        await app.Ui.Run(() => vm.OpenNewDraft(Fixtures.Path("waterloo 2010.WTT")));
        var draft = await app.Ui.Run(() => vm.Draft!);
        await app.Ui.WaitUntil(() => draft.Board.IsReady, "the draft board");
        await app.Ui.Run(() =>
        {
            var window = new Views.EditorWindow(draft) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Width = 1600, Height = 1000 };
            window.Show();
            try
            {
                draft.SelectedTrainRow = draft.Trains.First(r => r.Headcode == "1W51");
                draft.Train!.CurrentTrip = draft.Train.Trips[0];
                MainWindowTests.Settle(window);
                window.TripGrid.Items.Count.Should().BeGreaterThan(2);
                Save((FrameworkElement)window.Content, "guide-timetable-editor.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Selects the WTT train in view with the most timing points either side of now, all on screen.</summary>
    private static bool SelectRunningTrain(MainViewModel vm)
    {
        var view = vm.Navigator.Viewport;
        var now = vm.Navigator.Now ?? vm.Navigator.Viewport.TimeStart;
        var best = vm.Scene!.InLayer(GraphLayer.Wtt)
            .Select(path =>
            {
                var shown = path.Vertices.Where(v => view.IsInPlot(view.ToScreen(v.Time, v.Axis))).ToList();
                return (Path: path, Before: shown.Count(v => v.Time < now), After: shown.Count(v => v.Time > now + 120));
            })
            .Where(c => c.Before >= 2 && c.After >= 2)
            .MaxBy(c => Math.Min(c.Before, c.After));
        if (best.Path is null)
        {
            return false;
        }

        var v = best.Path.Vertices.Count - 1;
        vm.SelectPath(new GraphHit(best.Path, v - 1, 0, v, best.Path.Vertices[v].Time));
        return true;
    }

    [Fact]
    public void The_dialogs() => OnUiThread(() =>
    {
        var root = Directory.CreateTempSubdirectory("stringline-guide-");
        try
        {
            var dialogs = new ScriptedDialogs();
            var waterloo = Fixtures.Path("waterloo 2010.WTT");
            Show(new Views.ConnectDialog(new ConnectViewModel(dialogs) { UserName = "your-forum-name" }), "guide-connect.png");

            // The files by name only, from the folder they are in, so the picture shows no folder of this machine's.
            var folder = Environment.CurrentDirectory;
            Environment.CurrentDirectory = Path.GetDirectoryName(waterloo)!;
            try
            {
                Show(
                    new Views.ReplayDialog(new ReplayViewModel(dialogs) { RecordingPath = Fixtures.Waterloo4x, TimetablePath = Path.GetFileName(waterloo) }),
                    "guide-replay.png");
            }
            finally
            {
                Environment.CurrentDirectory = folder;
            }
            Show(new Views.SettingsWindow(new SettingsViewModel(new AppSettings(), AppPaths.Under(root.FullName), dialogs)), "guide-options.png");

            var timetable = new Timetable.WttReader().Read(waterloo).Timetable;
            var editor = new ProfileEditorViewModel(StringlineProfile.Create(timetable), timetable, dialogs, p => new ProfileApplyResult(p, null));
            var window = new Views.ProfileEditorWindow(editor);
            Show(window, "guide-profile-editor.png", w =>
            {
                FindChild<TabControl>(w)!.SelectedIndex = 1; // Corridors
                editor.SelectedCorridor = editor.Corridors[0];
            });

            // The colour picker (#24) as a category's swatch opens it.
            ColourPickerViewModel? picker = null;
            dialogs.ColourPicks.Enqueue(p => (picker = p) is null);
            editor.Categories.First(c => c.Trains > 0).PickColourCommand.Execute(null);
            Show(new Views.ColourPickerDialog(picker!), "guide-colour-picker.png");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    });

    private static void Show(Window window, string file, Action<Window>? arrange = null)
    {
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        (window.Left, window.Top) = (-10000, -10000);
        window.Show();
        try
        {
            arrange?.Invoke(window);
            for (var i = 0; i < 3; i++)
            {
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }

            window.ActualHeight.Should().BeGreaterThan(100);
            Save((FrameworkElement)window.Content, file);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Save(FrameworkElement content, string file)
    {
        if (Shots is null)
        {
            return;
        }

        // Drawn through a brush, so a margin round the content (the dialogs have one) neither shifts nor crops it.
        var (width, height) = (content.ActualWidth + content.Margin.Left + content.Margin.Right, content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        var page = new DrawingVisual();
        using (var dc = page.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var area = new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight);
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
        }

        bitmap.Render(page);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Shots);
        using var stream = File.Create(Path.Combine(Shots, file));
        png.Save(stream);
    }

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // Reported on the test thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();
        failure.Should().BeNull();
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
