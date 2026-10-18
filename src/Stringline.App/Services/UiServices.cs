using System.Windows;
using System.Windows.Threading;

namespace Stringline.App.Services;

/// <summary>Runs work on the UI thread, so view models can be driven from background threads (and tested without WPF).</summary>
internal interface IUiDispatcher
{
    /// <summary>Run <paramref name="action"/> on the UI thread soon, without waiting.</summary>
    void Post(Action action);
}

/// <summary>The WPF dispatcher of the application's main thread.</summary>
internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
}

/// <summary>Opens dialogs and pickers for the view models.</summary>
internal interface IDialogService
{
    bool ShowConnect(ViewModels.ConnectViewModel viewModel);

    bool ShowReplay(ViewModels.ReplayViewModel viewModel);

    /// <summary>A file to open, or null if cancelled.</summary>
    /// <param name="filter">A file dialog filter, e.g. <c>SimSig timetables (*.wtt)|*.wtt</c>.</param>
    string? PickFile(string title, string filter, string? initialPath);

    /// <summary>A file to save to, or null if cancelled. The user confirms overwriting an existing file.</summary>
    /// <param name="initialPath">The folder and suggested file name.</param>
    string? PickSaveFile(string title, string filter, string initialPath);

    void ShowError(string title, string message);

    void ShowList(string title, string heading, IReadOnlyList<string> lines);
}

/// <summary>The real dialogs, owned by the main window.</summary>
internal sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;

    public bool ShowConnect(ViewModels.ConnectViewModel viewModel) =>
        new Views.ConnectDialog(viewModel) { Owner = Owner }.ShowDialog() == true;

    public bool ShowReplay(ViewModels.ReplayViewModel viewModel) =>
        new Views.ReplayDialog(viewModel) { Owner = Owner }.ShowDialog() == true;

    public string? PickFile(string title, string filter, string? initialPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        if (!string.IsNullOrEmpty(initialPath))
        {
            var folder = System.IO.Directory.Exists(initialPath) ? initialPath : System.IO.Path.GetDirectoryName(initialPath);
            if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            {
                dialog.InitialDirectory = folder;
            }

            if (System.IO.File.Exists(initialPath))
            {
                dialog.FileName = System.IO.Path.GetFileName(initialPath);
            }
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string initialPath)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            OverwritePrompt = true,
            AddExtension = true,
            FileName = System.IO.Path.GetFileName(initialPath),
        };
        var folder = System.IO.Path.GetDirectoryName(initialPath);
        if (!string.IsNullOrEmpty(folder))
        {
            System.IO.Directory.CreateDirectory(folder);
            dialog.InitialDirectory = folder;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public void ShowError(string title, string message)
    {
        if (Owner is { } owner)
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ShowList(string title, string heading, IReadOnlyList<string> lines) =>
        new Views.ListWindow(title, heading, lines) { Owner = Owner }.Show();
}
