using System.Windows;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>The settings window (<see cref="SettingsViewModel"/>); OK closes it only once the values pass.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    internal SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryAccept())
        {
            DialogResult = true;
        }
    }
}
