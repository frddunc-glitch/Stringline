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
        Graph.Editor = viewModel;
        Graph.PathClicked += (_, hit) => viewModel.SelectPath(hit);
        viewModel.History.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditHistoryViewModel.Latest) && viewModel.History.Latest is { } latest)
            {
                HistoryList.ScrollIntoView(latest);
            }
        };
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
        try
        {
            await _viewModel.DisposeAsync();
        }
        finally
        {
            _closed = true;
            Close();
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        MessageBox.Show(
            this,
            $"Stringline {version} — a live train graph for SimSig.\n\n" +
            "Copyright (C) 2026 Frederick Duncan and Stringline contributors.\n\n" +
            "This program comes with ABSOLUTELY NO WARRANTY. It is free software, licensed under the GNU General Public " +
            "Licence version 3 or later; you may redistribute it under those terms.\n\n" +
            "Source: https://github.com/frddunc-glitch/Stringline\n\n" +
            "Not affiliated with or endorsed by SimSig. Noto Sans is used under the SIL Open Font Licence.",
            "About Stringline",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
