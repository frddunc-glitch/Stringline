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

    /// <summary>Asks for the prompt's values; true if OK was pressed (they have then passed its validation).</summary>
    bool Prompt(ViewModels.PromptViewModel viewModel);

    void ShowList(string title, string heading, IReadOnlyList<string> lines);

    /// <summary>Asks a question with these answers (as buttons, the first the default); the index picked, or null if the window was closed.</summary>
    int? Choose(string title, string message, IReadOnlyList<string> choices);

    /// <summary>Shows the profile editor until it is closed.</summary>
    void ShowProfileEditor(ViewModels.ProfileEditorViewModel viewModel);

    /// <summary>Opens a file in the program Windows uses for it. Returns why it could not, or null.</summary>
    string? OpenInShell(string path);

    /// <summary>A folder, or null if cancelled.</summary>
    string? PickFolder(string title, string? initialFolder);

    /// <summary>Shows the settings window; true if OK was pressed (the values have then passed its checks).</summary>
    bool ShowSettings(ViewModels.SettingsViewModel viewModel);
}

/// <summary>The real dialogs, owned by the main window.</summary>
internal sealed class WpfDialogService : IDialogService
{
    /// <summary>The window in front if it is one of ours (the profile editor, say, so its questions sit over it), else the main window.</summary>
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w is { IsActive: true, IsLoaded: true })
        ?? (Application.Current?.MainWindow is { IsLoaded: true } main ? main : null);

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

    public bool Prompt(ViewModels.PromptViewModel viewModel) =>
        new Views.PromptDialog(viewModel) { Owner = Owner }.ShowDialog() == true;

    public void ShowList(string title, string heading, IReadOnlyList<string> lines) =>
        new Views.ListWindow(title, heading, lines) { Owner = Owner }.Show();

    public int? Choose(string title, string message, IReadOnlyList<string> choices) =>
        Views.ChoiceDialog.Ask(Owner, title, message, choices);

    public void ShowProfileEditor(ViewModels.ProfileEditorViewModel viewModel) =>
        new Views.ProfileEditorWindow(viewModel) { Owner = Owner }.ShowDialog();

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrEmpty(initialFolder) && System.IO.Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public bool ShowSettings(ViewModels.SettingsViewModel viewModel) =>
        new Views.SettingsWindow(viewModel) { Owner = Owner }.ShowDialog() == true;

    public string? OpenInShell(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            return ex.Message;
        }
    }
}
