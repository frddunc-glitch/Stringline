using System.Windows;

namespace Stringline.App.Views;

/// <summary>A plain list of text lines, e.g. timetable import warnings (TT-2).</summary>
public partial class ListWindow : Window
{
    internal ListWindow(string title, string heading, IReadOnlyList<string> lines)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = heading;
        Lines.ItemsSource = lines;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
