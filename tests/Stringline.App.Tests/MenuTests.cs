using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Graph;

namespace Stringline.App.Tests;

/// <summary>
/// The menus against the UI spec (S25, #8; <c>05-…md</c> §1): the menu bar's order, every item bound to a command,
/// no access key repeated within a menu (the main menus and the graph's right-click menus), and every shortcut shown
/// is a key binding for the same command. Also the toolbar's corridor tabs on one row, with the rest under ».
/// </summary>
public sealed class MenuTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    private static async Task<MainViewModel> LoadWaterloo(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true && vm.Corridors.Count > 0, "the WTT on the graph");
        return vm;
    }

    private static MainWindow Show(MainViewModel vm, double width = 1280, double height = 720)
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

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Each menu (the bar, then every item with items) as its path and its items.</summary>
    private static IEnumerable<(string Path, List<MenuItem> Items)> Menus(ItemsControl menu, string path)
    {
        var items = menu.Items.OfType<MenuItem>().Where(m => m.Visibility == Visibility.Visible).ToList();
        yield return (path, items);
        foreach (var item in items.Where(i => i.HasItems))
        {
            foreach (var deeper in Menus(item, $"{path} ▸ {item.Header}"))
            {
                yield return deeper;
            }
        }
    }

    private static List<string> RepeatedKeys(string path, IEnumerable<string?> headers)
    {
        var found = new List<string>();
        var keys = new Dictionary<char, string>();
        foreach (var header in headers.OfType<string>())
        {
            if (MenuText.AccessKey(header) is not { } key)
            {
                found.Add($"{path}: \"{header}\" has no access key");
            }
            else if (!keys.TryAdd(key, header))
            {
                found.Add($"{path}: {key} in \"{keys[key]}\" and \"{header}\"");
            }
        }

        return found;
    }

    [Fact]
    public async Task The_menu_bar_follows_the_spec_and_every_item_is_bound()
    {
        await using var app = new TestApp(wpf: true);
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            var window = Show(vm);
            try
            {
                var menus = Menus(window.MainMenu, "Menu").ToList();
                menus[0].Items.Select(m => (string)m.Header).Should().Equal("_File", "_Session", "_View", "_Corridor", "_Plan", "_Tools", "_Help");

                var problems = new List<string>();
                foreach (var (path, items) in menus)
                {
                    problems.AddRange(RepeatedKeys(path, items.Select(i => i.Header as string)));
                    foreach (var item in items.Where(i => !i.HasItems))
                    {
                        var bound = item.Command is not null
                            || (item.IsCheckable && BindingOperations.IsDataBound(item, MenuItem.IsCheckedProperty));
                        if (!bound)
                        {
                            problems.Add($"{path} ▸ {item.Header}: no command");
                        }
                    }
                }

                problems.Should().BeEmpty();

                // Settings moved out of View into Tools ▸ Options… (#8); View holds display switches only.
                var view = menus.Single(m => m.Path == "Menu ▸ _View").Items;
                view.Select(i => (string)i.Header).Should().NotContain(h => h.Contains("Settings") || h.Contains("Options"));
                var options = menus.Single(m => m.Path == "Menu ▸ _Tools").Items[0];
                options.Header.Should().Be("_Options…");
                options.InputGestureText.Should().Be("Ctrl+,");
                options.Command.Should().BeSameAs(vm.ShowSettingsCommand);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Every_shortcut_shown_is_bound_to_the_same_command_and_none_repeats()
    {
        await using var app = new TestApp(wpf: true);
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            var window = Show(vm);
            try
            {
                var converter = new KeyGestureConverter();
                var bindings = window.InputBindings.OfType<KeyBinding>()
                    .Select(b => (Text: converter.ConvertToInvariantString(b.Gesture)!.Replace("OemComma", ",", StringComparison.Ordinal), b.Command, b.CommandParameter))
                    .ToList();
                bindings.GroupBy(b => b.Text).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty("each shortcut does one thing");

                // The graph's own keys (Space, Esc) work while it has focus.
                var graphKeys = window.Graph.InputBindings.OfType<KeyBinding>().Select(b => (Gesture: b.Key.ToString(), b.Command)).ToList();

                var problems = new List<string>();
                var items = Menus(window.MainMenu, "Menu").SelectMany(m => m.Items).Where(i => i.InputGestureText.Length > 0).ToList();
                items.Should().NotBeEmpty();
                foreach (var item in items)
                {
                    var shown = item.InputGestureText;
                    if (shown == "Alt+F4")
                    {
                        item.Command.Should().Be(ApplicationCommands.Close, "Windows closes the window on Alt+F4");
                        continue;
                    }

                    if (shown is "Space" or "Esc")
                    {
                        var key = shown == "Esc" ? "Escape" : shown;
                        if (!graphKeys.Any(k => k.Gesture == key && SameCommand(k.Command, item.Command, vm)))
                        {
                            problems.Add($"{item.Header} ({shown}): not a key on the graph");
                        }

                        continue;
                    }

                    var binding = bindings.FirstOrDefault(b => b.Text == shown);
                    if (binding.Text is null)
                    {
                        problems.Add($"{item.Header} ({shown}): no key binding");
                    }
                    else if (item.Parent == window.CorridorMenu)
                    {
                        // A corridor's item and its Ctrl+digit pick the same corridor.
                        var number = shown["Ctrl+".Length..];
                        if (!ReferenceEquals(binding.Command, vm.SelectCorridorCommand) || !Equals(binding.CommandParameter, number)
                            || !((string)item.Header).StartsWith($"_{number} ", StringComparison.Ordinal))
                        {
                            problems.Add($"{item.Header} ({shown}): the key picks another corridor");
                        }
                    }
                    else if (!SameCommand(binding.Command, item.Command, vm) || !Equals(binding.CommandParameter, item.CommandParameter))
                    {
                        problems.Add($"{item.Header} ({shown}): the key does something else");
                    }
                }

                problems.Should().BeEmpty();
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The same command, counting Follow now (menu) and toggling follow (Space) as one: Space turns following on and off.</summary>
    private static bool SameCommand(ICommand? key, ICommand? menu, MainViewModel vm) =>
        ReferenceEquals(key, menu) || (ReferenceEquals(key, vm.ToggleFollowCommand) && ReferenceEquals(menu, vm.FollowCommand));

    [Fact]
    public async Task The_corridor_menu_lists_every_corridor_and_ctrl_digits_pick_one()
    {
        await using var app = new TestApp(wpf: true);
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            var window = Show(vm);
            try
            {
                var listed = window.CorridorMenu.Items.OfType<MenuItem>().Where(m => m.InputGestureText.StartsWith("Ctrl+", StringComparison.Ordinal) || m.Command is not null).ToList();
                listed.Should().HaveCount(vm.Corridors.Count + 1, "every corridor, then Whole corridor");
                listed[0].Header.Should().Be($"_1 {MenuText.Escape(vm.Corridors[0].Name)}");
                listed[0].InputGestureText.Should().Be("Ctrl+1");
                listed[0].IsChecked.Should().BeTrue("the corridor shown is ticked");
                listed[^1].Header.Should().Be("_Whole corridor");

                if (vm.Corridors.Count > 1)
                {
                    var ctrl2 = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == Key.D2 && b.Modifiers == ModifierKeys.Control);
                    ctrl2.Command.Execute(ctrl2.CommandParameter);
                    vm.SelectedCorridor.Should().Be(vm.Corridors[1]);
                    var ticked = window.CorridorMenu.Items.OfType<MenuItem>().Where(m => m.IsChecked).ToList();
                    ticked.Should().ContainSingle().Which.Header.Should().Be($"_2 {MenuText.Escape(vm.Corridors[1].Name)}");
                }

                // A number past the last corridor does nothing.
                var shown = vm.SelectedCorridor;
                vm.SelectCorridorCommand.Execute("9");
                if (vm.Corridors.Count < 9)
                {
                    vm.SelectedCorridor.Should().Be(shown);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task The_corridor_tabs_stay_on_one_row_and_the_rest_are_under_the_overflow()
    {
        await using var app = new TestApp(wpf: true);
        var vm = await LoadWaterloo(app);

        await app.Ui.Run(() =>
        {
            var window = Show(vm, 1280, 720);
            try
            {
                var tab = (FrameworkElement)window.CorridorTabs.ItemContainerGenerator.ContainerFromIndex(0);
                window.CorridorTabs.ActualHeight.Should().BeLessThan(tab.ActualHeight * 1.5, "one row of tabs");

                vm.Corridors.Should().HaveCountGreaterThan(1, "Waterloo has a main line and a branch");
                window.MoreCorridors.Visibility.Should().Be(Visibility.Collapsed, "both of Waterloo's tabs fit at 1280");
                window.HiddenCorridorTabs().Should().BeEmpty();

                // Room for one tab: the next is under », and picking it there brings its tab into view.
                window.CorridorTabs.MaxWidth = tab.ActualWidth + 20;
                Settle(window);
                window.CorridorTabs.ActualHeight.Should().BeLessThan(tab.ActualHeight * 1.5, "still one row");
                var hidden = window.HiddenCorridorTabs();
                hidden.Should().Equal(vm.Corridors.Skip(1));
                window.MoreCorridors.Visibility.Should().Be(Visibility.Visible);
                vm.CorridorMenuItems(hidden.ToHashSet()).Select(i => i.Header).Should().Equal(
                    vm.Corridors.Skip(1).Select((c, i) => i < 8 ? $"_{i + 2} {MenuText.Escape(c.Name)}" : MenuText.Escape(c.Name)));
                vm.SelectedCorridor = hidden[^1];
                Settle(window);
                window.HiddenCorridorTabs().Should().NotContain(hidden[^1], "the picked tab is scrolled into view");

                // At the window's least width the toolbar has no room for tabs at all: every corridor is under ».
                window.CorridorTabs.ClearValue(FrameworkElement.MaxWidthProperty);
                window.Width = window.MinWidth;
                Settle(window);
                window.MoreCorridors.Visibility.Should().Be(Visibility.Visible);
                window.HiddenCorridorTabs().Should().HaveCount(vm.Corridors.Count);
                window.MoreCorridors.TransformToAncestor(window).Transform(new Point(window.MoreCorridors.ActualWidth, 0)).X
                    .Should().BeLessThan(window.ToolbarRight.TransformToAncestor(window).Transform(new Point()).X + 0.5, "» is not under the layer boxes");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task No_access_key_repeats_in_the_graph_menus()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), Fixtures.Path(Waterloo), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Matches.Matched > 100 && vm.Scene?.InLayer(GraphLayer.Actual).Any(p => p.Train is not null) == true, "matched trains on the graph");

        var problems = await app.Ui.Run(() =>
        {
            var found = new List<string>();

            // A matched live train at a timing point: the fullest train menu (edits, link, regulation list).
            foreach (var path in vm.Scene!.InLayer(GraphLayer.Actual).Where(p => p.Train is not null).Take(20))
            {
                for (var v = 0; v < path.Vertices.Count; v++)
                {
                    var menu = vm.MenuFor(new GraphHit(path, 0, 0, v, path.Vertices[v].Time));
                    found.AddRange(RepeatedKeys($"train {path.Train}", menu.Where(m => m.Command is not null).Select(m => m.Header)));
                }
            }

            // The background, over the middle of the view.
            var view = vm.Navigator.Viewport;
            var middle = view.ToScreen((view.TimeStart + view.TimeEnd) / 2, (view.AxisStart + view.AxisEnd) / 2);
            var background = vm.MenuFor(null, middle);
            background.Should().NotBeEmpty();
            found.AddRange(RepeatedKeys("background", background.Where(m => m.Command is not null).Select(m => m.Header)));
            return found.Distinct().ToList();
        });

        problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData("_File", 'F')]
    [InlineData("Re_gulation list at A__B", 'G')]
    [InlineData("A__B _x", 'X')]
    [InlineData("No key", null)]
    [InlineData("Trailing_", null)]
    public void Access_keys_are_read_as_wpf_does(string header, char? key) => MenuText.AccessKey(header).Should().Be(key);

    [Fact]
    public void Names_in_menus_keep_their_underscores() => MenuText.Escape("UP_MAIN").Should().Be("UP__MAIN");
}
