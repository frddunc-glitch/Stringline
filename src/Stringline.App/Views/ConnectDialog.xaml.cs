using System.Windows;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>Where SimSig is and how to log in. The password box is not bindable, so it is copied here by hand.</summary>
public partial class ConnectDialog : Window
{
    private readonly ConnectViewModel _viewModel;

    internal ConnectDialog(ConnectViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PasswordBox.Password = viewModel.Password;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordBox.Password;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Error is null)
        {
            DialogResult = true;
        }
    }
}
