using System.Windows;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>Which recording to replay, with which timetable, how fast and from when.</summary>
public partial class ReplayDialog : Window
{
    private readonly ReplayViewModel _viewModel;

    internal ReplayDialog(ReplayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Error is null)
        {
            DialogResult = true;
        }
    }
}
