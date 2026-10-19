using System.ComponentModel;
using System.Windows;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>The profile editor (<see cref="ProfileEditorViewModel"/>). Closing it with unapplied changes asks first.</summary>
public partial class ProfileEditorWindow : Window
{
    private readonly ProfileEditorViewModel _viewModel;
    private bool _closeRequested;

    internal ProfileEditorWindow(ProfileEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += OnCloseRequested;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_closeRequested && !_viewModel.CanClose())
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        base.OnClosed(e);
    }

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        _closeRequested = true;
        Close();
    }
}
