using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Stringline.App.Services;
using Stringline.App.ViewModels;

namespace Stringline.App;

/// <summary>
/// The window's layout (#9): applies <see cref="LayoutViewModel.Current"/> to the panels' grid as star sizes, hides
/// and shows panels, reports the splitters' sizes back, and puts the window where it was (on a screen that is still
/// there). The rules are in <see cref="LayoutViewModel"/> and <see cref="WindowPlacement"/>; this only moves WPF.
/// </summary>
public partial class MainWindow
{
    private LayoutViewModel Layout => _viewModel.Layout;

    private void InitLayout()
    {
        PlaceWindow(Layout.Current.Window);
        ApplyLayout();
        Layout.Reset += (_, _) => ApplyLayout();
        Layout.PropertyChanged += OnLayoutChanged;
        _viewModel.SuggestionPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SuggestionsViewModel.IsVisible))
            {
                ApplySuggestionsHeight();
            }
        };
    }

    /// <summary>
    /// The layout as the window shows it now, in pixels, with where the window is. A hidden panel's room is in the
    /// graph's (or the train detail's) share now, so it is taken back out of it at the same scale.
    /// </summary>
    internal WindowLayout SeenLayout()
    {
        var current = Layout.Current;
        var (trainShown, sideShown, tabsShown) = (TrainListPanel.IsVisible, SidePanel.IsVisible, SideTabs.IsVisible);
        var perWeight = PerWeight(GraphColumn.Width, GraphColumn.ActualWidth);
        var train = trainShown ? TrainListColumn.ActualWidth : current.TrainListWidth * perWeight;
        var side = sideShown ? SideColumn.ActualWidth : current.SideWidth * perWeight;
        var graph = GraphColumn.ActualWidth - (trainShown ? 0 : train) - (sideShown ? 0 : side);
        var (detail, tabs) = (current.DetailHeight, current.TabsHeight);
        if (sideShown)
        {
            tabs = tabsShown ? TabsRow.ActualHeight : current.TabsHeight * PerWeight(DetailRow.Height, DetailRow.ActualHeight);
            detail = DetailRow.ActualHeight - (tabsShown ? 0 : tabs);
        }

        // The split view's shares (S21), from whichever way it is split now.
        var (mainBoard, splitBoard) = (current.MainBoardSize, current.SplitBoardSize);
        if (SplitBoardRow.ActualHeight > 0)
        {
            (mainBoard, splitBoard) = (MainBoardRow.ActualHeight, SplitBoardRow.ActualHeight);
        }
        else if (SplitBoardColumn.ActualWidth > 0)
        {
            (mainBoard, splitBoard) = (MainBoardColumn.ActualWidth, SplitBoardColumn.ActualWidth);
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        return current with
        {
            MainBoardSize = mainBoard,
            SplitBoardSize = splitBoard,
            TrainListWidth = train,
            GraphWidth = graph,
            SideWidth = side,
            DetailHeight = detail,
            TabsHeight = tabs,
            SuggestionsHeight = SuggestionsRow.ActualHeight > 0 ? SuggestionsRow.ActualHeight : current.SuggestionsHeight,
            Window = bounds.IsEmpty ? current.Window
                : new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized),
        };
    }

    private static double PerWeight(GridLength length, double actual) => length.IsStar && length.Value > 0 ? actual / length.Value : 1;

    /// <summary>Remember the layout as it is now: on closing, after a splitter drag, and before a panel hides or shows.</summary>
    internal void CaptureLayout()
    {
        if (IsLoaded)
        {
            Layout.Update(SeenLayout());
        }
    }

    /// <summary>
    /// Show <see cref="LayoutViewModel.Current"/>: each column's and row's share, with a hidden panel's share given to
    /// the graph (or to the train detail).
    /// </summary>
    private void ApplyLayout()
    {
        var layout = Layout.Current;
        SetColumn(TrainListColumn, TrainListPanel, layout.ShowTrainList, layout.TrainListWidth, WindowLayout.MinTrainListWidth);
        SetColumn(SideColumn, SidePanel, layout.ShowSidePanel, layout.SideWidth, WindowLayout.MinSideWidth);
        GraphColumn.Width = new GridLength(
            layout.GraphWidth + (layout.ShowTrainList ? 0 : layout.TrainListWidth) + (layout.ShowSidePanel ? 0 : layout.SideWidth), GridUnitType.Star);
        SideTabs.Visibility = layout.ShowTabs ? Visibility.Visible : Visibility.Collapsed;
        TabsRow.MinHeight = layout.ShowTabs ? WindowLayout.MinTabsHeight : 0;
        TabsRow.Height = layout.ShowTabs ? new GridLength(layout.TabsHeight, GridUnitType.Star) : new GridLength(0);
        DetailRow.Height = new GridLength(layout.DetailHeight + (layout.ShowTabs ? 0 : layout.TabsHeight), GridUnitType.Star);
        ApplySuggestionsHeight();
        ApplySplit();
    }

    /// <summary>
    /// The split view (S21): the second board under the main graph (stacked) or beside it, with a splitter between,
    /// each keeping its share; or only the main graph.
    /// </summary>
    private void ApplySplit()
    {
        var layout = Layout.Current;
        var (stacked, sideBySide) = (Layout.Split == BoardSplit.Stacked, Layout.Split == BoardSplit.SideBySide);
        var on = stacked || sideBySide;
        BoardSplitter.Visibility = SplitBoardView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        MainBoardRow.Height = new GridLength(stacked ? layout.MainBoardSize : 1, GridUnitType.Star);
        BoardSplitterRow.Height = new GridLength(stacked ? 5 : 0);
        SplitBoardRow.Height = stacked ? new GridLength(layout.SplitBoardSize, GridUnitType.Star) : new GridLength(0);
        MainBoardRow.MinHeight = SplitBoardRow.MinHeight = stacked ? WindowLayout.MinBoardSize : 0;

        MainBoardColumn.Width = new GridLength(sideBySide ? layout.MainBoardSize : 1, GridUnitType.Star);
        BoardSplitterColumn.Width = new GridLength(sideBySide ? 5 : 0);
        SplitBoardColumn.Width = sideBySide ? new GridLength(layout.SplitBoardSize, GridUnitType.Star) : new GridLength(0);
        MainBoardColumn.MinWidth = SplitBoardColumn.MinWidth = sideBySide ? WindowLayout.MinBoardSize : 0;

        Grid.SetRow(BoardSplitter, stacked ? 1 : 0);
        Grid.SetColumn(BoardSplitter, sideBySide ? 1 : 0);
        BoardSplitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        Grid.SetRow(SplitBoardView, stacked ? 2 : 0);
        Grid.SetColumn(SplitBoardView, sideBySide ? 2 : 0);
    }

    private static void SetColumn(ColumnDefinition column, FrameworkElement panel, bool show, double width, double minWidth)
    {
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        column.MinWidth = show ? minWidth : 0;
        column.Width = show ? new GridLength(width, GridUnitType.Star) : new GridLength(0);
    }

    private void ApplySuggestionsHeight()
    {
        var shown = _viewModel.SuggestionPanel.IsVisible;
        SuggestionsRow.MinHeight = shown ? WindowLayout.MinSuggestionsHeight : 0;
        SuggestionsRow.Height = new GridLength(shown ? Layout.Current.SuggestionsHeight : 0);
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutViewModel.ShowTrainList) or nameof(LayoutViewModel.ShowSidePanel) or nameof(LayoutViewModel.ShowTabs)
            or nameof(LayoutViewModel.Split))
        {
            // The sizes as shown until now (no panel has hidden or shown yet), then the new switches.
            CaptureLayout();
            ApplyLayout();
        }
    }

    private void OnSplitterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender == TrainListSplitter)
        {
            Layout.ToggleTrainListCommand.Execute(null);
        }
        else if (sender == SideSplitter)
        {
            Layout.ToggleSidePanelCommand.Execute(null);
        }
        else if (sender == TabsSplitter)
        {
            Layout.ToggleTabsCommand.Execute(null);
        }

        e.Handled = true;
    }

    /// <summary>A splitter beside a hidden panel does not drag it open; a double-click shows it.</summary>
    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        if ((sender == TrainListSplitter && !Layout.ShowTrainList) || (sender == SideSplitter && !Layout.ShowSidePanel)
            || (sender == TabsSplitter && !Layout.ShowTabs))
        {
            ((GridSplitter)sender).CancelDrag();
        }
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e) => CaptureLayout();

    /// <summary>Put the window where it was last time, moved onto a screen if that one has gone.</summary>
    private void PlaceWindow(WindowPlacement? saved)
    {
        if (saved is null)
        {
            return;
        }

        var placed = saved.OnScreen(WorkAreas());
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (placed.Left, placed.Top, Math.Max(MinWidth, placed.Width), Math.Max(MinHeight, placed.Height));
        if (placed.Maximised)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Every screen's work area in device-independent pixels, the primary first. Scaled by the system DPI, which is
    /// what WPF places a window by before it is shown.
    /// </summary>
    internal static List<ScreenArea> WorkAreas()
    {
        var areas = new List<(ScreenArea Area, bool Primary)>();
        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual());
        var handle = GCHandle.Alloc(areas);
        try
        {
            unsafe
            {
                if (NativeMethods.EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle)) == 0)
                {
                    areas.Clear(); // failed part way: use the primary work area below
                }
            }
        }
        finally
        {
            handle.Free();
        }

        var result = areas.OrderByDescending(a => a.Primary).Select(a => new ScreenArea(
            a.Area.Left / dpi.DpiScaleX, a.Area.Top / dpi.DpiScaleY, a.Area.Width / dpi.DpiScaleX, a.Area.Height / dpi.DpiScaleY)).ToList();
        if (result.Count == 0)
        {
            var work = SystemParameters.WorkArea;
            result.Add(new ScreenArea(work.Left, work.Top, work.Width, work.Height));
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, nint rect, nint data)
    {
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (GCHandle.FromIntPtr(data).Target is List<(ScreenArea, bool)> areas && NativeMethods.GetMonitorInfo(monitor, ref info) != 0)
        {
            var w = info.Work;
            areas.Add((new ScreenArea(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top), (info.Flags & NativeMethods.PrimaryMonitor) != 0));
        }

        return 1; // carry on to the next screen
    }
}

/// <summary>The few Win32 calls the window needs to find the screens.</summary>
internal static partial class NativeMethods
{
    public const int PrimaryMonitor = 1;

    [LibraryImport("user32.dll")]
    public static unsafe partial int EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    public static partial int GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect32 Monitor;
        public Rect32 Work;
        public int Flags;
    }
}
