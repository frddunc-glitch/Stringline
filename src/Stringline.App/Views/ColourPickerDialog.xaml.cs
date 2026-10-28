using System.Windows;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>Picks a colour (<see cref="ColourPickerViewModel"/>, #24); OK closes it only once the code is a colour.</summary>
public partial class ColourPickerDialog : Window
{
    private readonly ColourPickerViewModel _viewModel;

    internal ColourPickerDialog(ColourPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            HexBox.Focus();
            HexBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryAccept())
        {
            DialogResult = true;
        }
    }
}
