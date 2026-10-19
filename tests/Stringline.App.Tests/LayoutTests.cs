using Stringline.App.Services;
using Stringline.App.ViewModels;

namespace Stringline.App.Tests;

/// <summary>#9 without a window: the panel switches, Reset layout, saving the layout, and putting the window back on a screen.</summary>
public sealed class LayoutTests
{
    private static readonly ScreenArea Primary = new(0, 0, 1920, 1040);
    private static readonly ScreenArea Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void Panels_hide_and_show_and_reset_brings_everything_back()
    {
        var saved = new WindowLayout { TrainListWidth = 200, SideWidth = 700, Window = new WindowPlacement(10, 20, 1500, 900) };
        var layout = new LayoutViewModel(saved);
        var resets = 0;
        layout.Reset += (_, _) => resets++;

        layout.ToggleTrainListCommand.Execute(null);
        layout.ToggleTabsCommand.Execute(null);
        layout.Current.ShowTrainList.Should().BeFalse();
        layout.Current.ShowTabs.Should().BeFalse();
        layout.Current.SideWidth.Should().Be(700, "hiding a panel keeps the sizes");
        layout.ToggleTrainListCommand.Execute(null);
        layout.ShowTrainList.Should().BeTrue();

        layout.ResetLayoutCommand.Execute(null);
        resets.Should().Be(1);
        layout.Current.Should().Be(new WindowLayout { Window = saved.Window }, "every panel at its first size, the window where it is");
        (layout.ShowTrainList, layout.ShowSidePanel, layout.ShowTabs).Should().Be((true, true, true));
    }

    [Fact]
    public void Sizes_reported_by_the_window_keep_the_panel_switches()
    {
        var layout = new LayoutViewModel();
        layout.ShowSidePanel = false;

        layout.Update(new WindowLayout { TrainListWidth = 300, GraphWidth = 900, SideWidth = 400, ShowSidePanel = true });

        layout.Current.ShowSidePanel.Should().BeFalse();
        (layout.Current.TrainListWidth, layout.Current.GraphWidth, layout.Current.SideWidth).Should().Be((300, 900, 400));
    }

    [Fact]
    public void A_damaged_layout_falls_back_to_the_defaults()
    {
        var layout = new LayoutViewModel(new WindowLayout
        {
            TrainListWidth = double.NaN,
            GraphWidth = -5,
            SideWidth = 0,
            SuggestionsHeight = 3,
            Window = new WindowPlacement(double.PositiveInfinity, 0, 1000, 800),
        });

        var defaults = new WindowLayout();
        (layout.Current.TrainListWidth, layout.Current.GraphWidth, layout.Current.SideWidth).Should().Be((defaults.TrainListWidth, defaults.GraphWidth, defaults.SideWidth));
        layout.Current.SuggestionsHeight.Should().Be(WindowLayout.MinSuggestionsHeight);
        layout.Current.Window.Should().BeNull();
    }

    [Fact]
    public void A_window_on_a_screen_stays_where_it_was()
    {
        var onRight = new WindowPlacement(2100, 100, 1600, 900, Maximised: true);
        onRight.OnScreen([Primary, Right]).Should().Be(onRight);
    }

    [Fact]
    public void A_window_on_a_screen_that_has_gone_comes_back_to_the_primary_screen()
    {
        var placed = new WindowPlacement(2100, 100, 2400, 1300).OnScreen([Primary]);

        placed.Should().Be(new WindowPlacement(0, 0, 1920, 1040), "shrunk to the primary screen and centred on it");
    }

    [Fact]
    public void A_window_half_off_its_screen_is_pulled_back_so_its_title_bar_can_be_grabbed()
    {
        var placed = new WindowPlacement(1850, -200, 1200, 800).OnScreen([Primary]);

        placed.Top.Should().Be(0);
        placed.Left.Should().Be(1920 - WindowPlacement.GrabWidth);
        (placed.Width, placed.Height).Should().Be((1200, 800));
    }

    [Fact]
    public async Task The_layout_is_saved_and_loaded_again_and_settings_keep_it()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await app.Ui.Run(() =>
        {
            vm.Layout.Update(new WindowLayout { SideWidth = 600, Window = new WindowPlacement(5, 6, 1300, 800, Maximised: true) });
            vm.Layout.ShowTrainList = false;
            vm.SaveLayout();
        });

        var stored = app.Settings.Load().Layout;
        stored.SideWidth.Should().Be(600);
        stored.ShowTrainList.Should().BeFalse();
        stored.Window.Should().Be(new WindowPlacement(5, 6, 1300, 800, Maximised: true));

        // The settings window saves every setting; the layout as it is now goes with them.
        app.Dialogs.Settings = _ => true;
        await app.Ui.Run(() =>
        {
            vm.Layout.ShowTabs = false;
            vm.ShowSettingsCommand.Execute(null);
        });
        app.Settings.Load().Layout.ShowTabs.Should().BeFalse();

        await using var again = new TestApp(root: app.Root);
        var restored = await again.Ui.Run(() => again.ViewModel.Layout.Current);
        restored.Should().Be(app.Settings.Load().Layout);
        (await again.Ui.Run(() => again.ViewModel.Layout.ShowTrainList)).Should().BeFalse();
    }
}
