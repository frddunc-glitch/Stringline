using System.Windows;
using System.Windows.Controls;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>Asks for one or a few values (<see cref="PromptViewModel"/>); OK closes it only once they pass.</summary>
public partial class PromptDialog : Window
{
    private readonly PromptViewModel _viewModel;

    internal PromptDialog(PromptViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => FocusFirstField();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryAccept())
        {
            DialogResult = true;
        }
    }

    /// <summary>Puts the cursor in the first box with its text selected, so a value can be typed straight over.</summary>
    private void FocusFirstField()
    {
        if (FieldList.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement container)
        {
            return;
        }

        container.ApplyTemplate();
        if (FindVisible<TextBox>(container) is { } box)
        {
            box.Focus();
            box.SelectAll();
        }
        else if (FindVisible<ComboBox>(container) is { } combo)
        {
            combo.Focus();
        }
    }

    private static T? FindVisible<T>(DependencyObject root)
        where T : FrameworkElement
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T { IsVisible: true } match)
            {
                return match;
            }

            if (FindVisible<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
