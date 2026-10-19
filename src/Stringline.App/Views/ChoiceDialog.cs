using System.Windows;
using System.Windows.Controls;

namespace Stringline.App.Views;

/// <summary>A question with a button per answer, the first the default (Enter) and the last the cancel (Esc).</summary>
internal sealed class ChoiceDialog : Window
{
    private ChoiceDialog(string title, string message, IReadOnlyList<string> choices)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = choices[i],
                MinWidth = 80,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(6, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = i == choices.Count - 1 && choices.Count > 1,
            };
            button.Click += (_, _) =>
            {
                Choice = index;
                DialogResult = true;
            };
            buttons.Children.Add(button);
        }

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        Content = panel;
    }

    public int? Choice { get; private set; }

    public static int? Ask(Window? owner, string title, string message, IReadOnlyList<string> choices)
    {
        var dialog = new ChoiceDialog(title, message, choices) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Choice : null;
    }
}
