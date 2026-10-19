using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stringline.App.ViewModels;

namespace Stringline.App;

/// <summary>
/// The main window. Code here only wires the graph control to the view model and drives the 10 Hz tick that moves
/// the now line and the clock (<c>05-…md</c> §7); everything else is bound.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _timer;
    private bool _closed;

    internal MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Graph.Describer = viewModel.Describe;
        Graph.ConflictClicker = viewModel.ClickConflict;
        Graph.Editor = viewModel;
        Graph.PathClicked += (_, hit) => viewModel.SelectPath(hit);
        viewModel.History.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditHistoryViewModel.Latest) && viewModel.History.Latest is { } latest)
            {
                HistoryList.ScrollIntoView(latest);
            }
        };
        viewModel.ConflictList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConflictsViewModel.Selected) && viewModel.ConflictList.Selected is { } selected)
            {
                ConflictsList.ScrollIntoView(selected);
            }
        };
        viewModel.Corridors.CollectionChanged += (_, _) => FillCorridorMenu();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedCorridor))
            {
                FillCorridorMenu();
            }
        };
        FillCorridorMenu();
        InitLayout();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) => Graph.SetNow(viewModel.Tick()), Dispatcher);
        Loaded += (_, _) =>
        {
            _timer.Start();
            Graph.Focus();
        };
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closed)
        {
            return;
        }

        // Stop the session (and any recording) cleanly before closing.
        e.Cancel = true;
        _timer.Stop();
        CaptureLayout();
        _viewModel.SaveLayout();
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _closed = true;
            // Later, not here: if the session was already over, the dispose finished at once and this window is still closing.
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    /// <summary>Help ▸ About Stringline (a routed command, so every menu item has a command; File ▸ Exit is <c>ApplicationCommands.Close</c>).</summary>
    public static RoutedUICommand AboutCommand { get; } = new("About Stringline", nameof(AboutCommand), typeof(MainWindow));

    private void OnExit(object sender, ExecutedRoutedEventArgs e) => Close();

    private void OnAbout(object sender, ExecutedRoutedEventArgs e) => new Views.AboutWindow { Owner = this }.ShowDialog();

    // ---- Corridors: the Corridor menu and the toolbar's tabs (S25) --------------------------------

    /// <summary>The Corridor menu's list, rebuilt whenever the corridors or the one shown change.</summary>
    internal void FillCorridorMenu()
    {
        var end = CorridorMenu.Items.IndexOf(CorridorListEnd);
        for (var i = end - 1; i >= 0; i--)
        {
            CorridorMenu.Items.RemoveAt(i);
        }

        var items = _viewModel.CorridorMenuItems();
        for (var i = 0; i < items.Count; i++)
        {
            CorridorMenu.Items.Insert(i, Views.MenuBuilder.ToWpf(items[i]));
        }

        CorridorListEnd.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The tabs not wholly in view on the toolbar's single row.</summary>
    internal IReadOnlyList<CorridorTab> HiddenCorridorTabs()
    {
        // Measured against the presenter inside the scroll viewer, which is the viewport less the list's padding.
        if (Find<ScrollContentPresenter>(CorridorTabs) is not { } viewport)
        {
            return [];
        }

        var hidden = new List<CorridorTab>();
        foreach (var tab in _viewModel.Corridors)
        {
            if (CorridorTabs.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement { IsVisible: true } container)
            {
                hidden.Add(tab);
                continue;
            }

            var bounds = container.TransformToAncestor(viewport).TransformBounds(new Rect(container.RenderSize));
            if (bounds.Left < -0.5 || bounds.Right > viewport.ActualWidth + 0.5)
            {
                hidden.Add(tab);
            }
        }

        return hidden;
    }

    private void OnCorridorTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CorridorTabs.SelectedItem is { } selected)
        {
            CorridorTabs.ScrollIntoView(selected);
        }
    }

    private void OnCorridorTabsScrolled(object sender, ScrollChangedEventArgs e) =>
        MoreCorridors.Visibility = e.ExtentWidth > e.ViewportWidth + 0.5 ? Visibility.Visible : Visibility.Collapsed;

    private void OnMoreCorridors(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreCorridors, Placement = PlacementMode.Bottom };
        foreach (var item in _viewModel.CorridorMenuItems(HiddenCorridorTabs().ToHashSet()))
        {
            menu.Items.Add(Views.MenuBuilder.ToWpf(item));
        }

        menu.IsOpen = menu.Items.Count > 0;
    }

    private static T? Find<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? Find<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnTrainDoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.CentreOnSelectedTrain();

    private void OnSeekStarted(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Transport is { } transport)
        {
            transport.IsSeeking = true;
        }
    }

    private void OnSeekFinished(object sender, MouseButtonEventArgs e) =>
        Dispatcher.BeginInvoke(() => _viewModel.Transport?.CommitSeek(), DispatcherPriority.Input);
}
