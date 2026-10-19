using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
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
        Graph.AnnotationDescriber = viewModel.DescribeAt;
        Graph.ConflictDescriber = viewModel.DescribeConflictAt;
        Graph.ConflictClicker = viewModel.ClickConflictAt;
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

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e) => new Views.AboutWindow { Owner = this }.ShowDialog();

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
