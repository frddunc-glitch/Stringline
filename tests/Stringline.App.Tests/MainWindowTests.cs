using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;

namespace Stringline.App.Tests;

/// <summary>
/// The main window over a Waterloo replay (#2, #9): every list in the side panels fits at the panels' least width,
/// the panels keep their share at small and large window sizes, and the layout comes back after a restart. With
/// <c>STRINGLINE_WINDOW_SHOTS</c> set to a folder, each state is saved there as a PNG to look at.
/// </summary>
public sealed class MainWindowTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static readonly string? Shots = Environment.GetEnvironmentVariable("STRINGLINE_WINDOW_SHOTS");

    /// <summary>A replay run to its end, with a train, a conflict (and its suggestions) and a regulation location picked, so every list has rows.</summary>
    private static async Task<MainViewModel> Replay(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), Fixtures.Path(Waterloo), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Matches.Matched > 100 && vm.Scene?.InLayer(GraphLayer.Actual).Any() == true, "matched trains on the graph");
        await app.Ui.WaitUntil(() => vm.ConflictList.Rows.Count > 0 && vm.Trains.Rows.Count > 0, "trains and conflicts listed");
        await app.Ui.Run(() =>
        {
            // The train with the most timing points, so the detail and delay tables are as full as they get.
            vm.Trains.Selected = vm.Trains.Rows.Where(r => r.Train is not null).MaxBy(r => vm.Snapshot.Timetable?.Find(r.Train!.Value)?.Points.Count ?? 0);
            vm.ConflictList.Selected = vm.ConflictList.Rows[0];
            vm.Regulation.Location = vm.Regulation.Locations.FirstOrDefault();
        });
        await app.Ui.WaitUntil(() => vm.Detail.HasTrain, "a train picked");
        await app.Ui.WaitUntil(() => vm.SuggestionPanel.IsVisible, "a conflict picked");
        var tab = await app.Ui.Run(() => (vm.SideTab, vm.Delays.ShowAll = true, vm.SideTab = 2).SideTab); // the Delays tab fills only while shown
        await app.Ui.WaitUntil(() => vm.Delays.Points.Count > 0, "the train's delays");
        await app.Ui.Run(() => vm.SideTab = tab);
        return vm;
    }

    private static MainWindow Show(MainViewModel vm, double width, double height)
    {
        var window = new MainWindow(vm)
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Width = width,
            Height = height,
        };
        window.Show();
        Settle(window);
        return window;
    }

    /// <summary>Let layout, data binding and the grids' second measure (Auto columns) finish.</summary>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>
    /// Every list in the panels that does not fit: one needing to scroll sideways (a column past its right edge), or a
    /// grid that squeezed a column narrower than its content (DataGrid does that to Auto columns before it scrolls).
    /// </summary>
    private static List<string> Overflowing(MainWindow window)
    {
        var lists = Descendants<Selector>(window.Panels).Where(l => l is DataGrid or ListBox && l.IsVisible).ToList();
        lists.Should().NotBeEmpty();
        if (Shots is not null)
        {
            File.AppendAllLines(Path.Combine(Shots, "lists.txt"), lists.Select(l =>
            {
                var sv = Descendants<ScrollViewer>(l).FirstOrDefault();
                var cols = l is DataGrid g ? string.Join(" ", g.Columns.Select(c => $"{c.Header}={c.ActualWidth:0}/{c.Width.DesiredValue:0}")) : "";
                return $"{l.Name} w={l.ActualWidth:0} extent={sv?.ExtentWidth:0} viewport={sv?.ViewportWidth:0} {cols}";
            }).Append("--"));
        }

        var found = new List<string>();
        foreach (var list in lists)
        {
            if (Descendants<ScrollViewer>(list).FirstOrDefault() is { } scroll && scroll.ExtentWidth > scroll.ViewportWidth + 0.5)
            {
                found.Add($"{list.Name}: {scroll.ExtentWidth:0} wide in {scroll.ViewportWidth:0}");
            }

            if (list is DataGrid grid)
            {
                found.AddRange(grid.Columns
                    .Where(c => !c.Width.IsStar && c.ActualWidth < c.Width.DesiredValue - 0.5)
                    .Select(c => $"{grid.Name}: {c.Header} squeezed to {c.ActualWidth:0} of {c.Width.DesiredValue:0}"));
            }
        }

        return found;
    }

    [Fact]
    public async Task Every_list_fits_at_the_panels_least_width()
    {
        await using var app = new TestApp(wpf: true);
        var vm = await Replay(app);

        var overflowing = await app.Ui.Run(() =>
        {
            var window = Show(vm, 1280, 720);
            try
            {
                // Squeeze both panels to their least width: a tiny share, held up by MinWidth.
                window.TrainListColumn.Width = new GridLength(1, GridUnitType.Star);
                window.SideColumn.Width = new GridLength(1, GridUnitType.Star);
                Settle(window);
                window.TrainListColumn.ActualWidth.Should().BeApproximately(WindowLayout.MinTrainListWidth, 1);
                window.SideColumn.ActualWidth.Should().BeApproximately(WindowLayout.MinSideWidth, 1);

                var found = new List<string>();
                for (var tab = 0; tab < window.SideTabs.Items.Count; tab++)
                {
                    window.SideTabs.SelectedIndex = tab;
                    Settle(window);
                    found.AddRange(Overflowing(window).Select(o => $"tab {tab + 1}, {o}"));
                    Save(window, $"main-least-{tab + 1}.png");
                }

                return found;
            }
            finally
            {
                window.Close();
            }
        });

        overflowing.Should().BeEmpty("every column fits at the panels' least width");
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("Huge")]
    public async Task The_sim_clock_and_the_whole_toolbar_fit_at_1280(string size)
    {
        await using var app = new TestApp(s => s with { ClockSize = Enum.Parse<ClockSize>(size) }, wpf: true);
        var vm = await Replay(app);

        await app.Ui.Run(() =>
        {
            var window = Show(vm, 1280, 720);
            try
            {
                var content = (FrameworkElement)window.Content;
                Rect BoundsOf(FrameworkElement e) => e.TransformToAncestor(content).TransformBounds(new Rect(e.RenderSize));
                var (clock, toolbar) = (BoundsOf(window.SimClock), BoundsOf(window.ToolbarRight));

                clock.Right.Should().BeLessThanOrEqualTo(content.ActualWidth + 0.5, "the clock is not cut off");
                toolbar.Right.Should().BeLessThanOrEqualTo(content.ActualWidth + 0.5, "the toolbar's last button is not pushed off");
                toolbar.Left.Should().BeGreaterThanOrEqualTo(0);
                clock.Bottom.Should().BeLessThanOrEqualTo(toolbar.Top + 0.5, "the clock is in the menu row, above the toolbar");
                Descendants<TextBlock>(window.SimClock).First().Text.Should().MatchRegex(@"^\d\d:\d\d:\d\d$", "the replay's clock");
                Save(window, $"main-clock-{size}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    public async Task The_panels_keep_their_share_as_the_window_changes_size(int width, int height)
    {
        await using var app = new TestApp(wpf: true);
        var vm = await Replay(app);

        await app.Ui.Run(() =>
        {
            var window = Show(vm, 1280, 720);
            try
            {
                // Windows keeps a window no bigger than the screen, so lay its content out at the size wanted inside a
                // canvas, which lets it be any size: the client area of a window that big.
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                content.Width = width - 16;
                content.Height = height - 39;
                window.Content = new Canvas { Children = { content } };
                Settle(window);
                content.ActualWidth.Should().Be(width - 16);

                var total = window.Panels.ActualWidth - 10; // less the two splitters
                var (train, graph, side) = (window.TrainListColumn.ActualWidth, window.GraphColumn.ActualWidth, window.SideColumn.ActualWidth);
                var weights = WindowLayout.DefaultTrainListWidth + WindowLayout.DefaultGraphWidth + WindowLayout.DefaultSideWidth;
                if (total >= 1590)
                {
                    // Room for every share: each column has its share of the width.
                    train.Should().BeApproximately(total * WindowLayout.DefaultTrainListWidth / weights, 2);
                    side.Should().BeApproximately(total * WindowLayout.DefaultSideWidth / weights, 2);
                }
                else
                {
                    // Small: the panels are held at their least width, and the graph has the rest.
                    train.Should().BeGreaterThanOrEqualTo(WindowLayout.MinTrainListWidth - 0.5);
                    side.Should().BeGreaterThanOrEqualTo(WindowLayout.MinSideWidth - 0.5);
                }

                graph.Should().BeGreaterThan(Math.Max(train, side), "the graph has the biggest share");
                (train + graph + side).Should().BeApproximately(total, 1, "nothing is pushed off the window");
                Overflowing(window).Should().BeEmpty();
                Save(content, $"main-{width}x{height}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task The_layout_comes_back_after_a_restart()
    {
        await using var app = new TestApp(wpf: true);
        var vm = await Replay(app);
        var shares = await app.Ui.Run(() =>
        {
            var window = Show(vm, 1400, 800);
            try
            {
                window.SideColumn.Width = new GridLength(window.SideColumn.ActualWidth + 100, GridUnitType.Star);
                window.GraphColumn.Width = new GridLength(window.GraphColumn.ActualWidth - 100, GridUnitType.Star);
                Settle(window);
                window.CaptureLayout();
                var side = window.SideColumn.ActualWidth;

                // A double-click on the splitter hides the train list; the graph takes its room.
                var graphBefore = window.GraphColumn.ActualWidth;
                vm.Layout.ToggleTrainListCommand.Execute(null);
                Settle(window);
                window.TrainListPanel.IsVisible.Should().BeFalse();
                window.GraphColumn.ActualWidth.Should().BeGreaterThan(graphBefore + 200);
                window.SideColumn.ActualWidth.Should().BeApproximately(side, 1, "the side panel keeps its size");

                // The tabs too: the train detail takes their room.
                vm.Layout.ShowTabs = false;
                Settle(window);
                window.SideTabs.IsVisible.Should().BeFalse();
                window.DetailRow.ActualHeight.Should().BeGreaterThan(window.SidePanel.ActualHeight - 10);
                return (Side: side, Total: window.Panels.ActualWidth);
            }
            finally
            {
                window.Close(); // saves the layout
            }
        });

        await using var again = new TestApp(root: app.Root, wpf: true);
        var layout = await again.Ui.Run(() => again.ViewModel.Layout.Current);
        layout.ShowTrainList.Should().BeFalse();
        layout.ShowTabs.Should().BeFalse();
        layout.ShowSidePanel.Should().BeTrue();
        layout.Window.Should().NotBeNull();
        layout.Window!.Width.Should().Be(1400);

        await again.Ui.Run(() =>
        {
            var window = new MainWindow(again.ViewModel) { ShowActivated = false };
            window.Show();
            try
            {
                Settle(window);
                (window.Width, window.Height).Should().Be((1400, 800));
                window.TrainListPanel.IsVisible.Should().BeFalse();
                window.SideTabs.IsVisible.Should().BeFalse();
                window.SideColumn.ActualWidth.Should().BeApproximately(shares.Side, 2);

                // Shown again, the train list comes back at its share; Reset layout puts everything back.
                again.ViewModel.Layout.ShowTrainList = true;
                Settle(window);
                window.TrainListColumn.ActualWidth.Should().BeApproximately(WindowLayout.DefaultTrainListWidth * (shares.Total - 10) / (1400 - 16 - 10), 30);
                again.ViewModel.Layout.ResetLayoutCommand.Execute(null);
                Settle(window);
                window.SideTabs.IsVisible.Should().BeTrue();
                window.TrainListPanel.IsVisible.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Save(Window window, string file) => Save((FrameworkElement)window.Content, file);

    private static void Save(FrameworkElement content, string file)
    {
        if (Shots is null)
        {
            return;
        }

        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var white = new DrawingVisual();
        using (var dc = white.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(white);
        bitmap.Render(content);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Shots);
        using var stream = File.Create(Path.Combine(Shots, file));
        png.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
