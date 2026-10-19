using System.Windows;
using System.Windows.Controls;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>Menu items for menus built in code: the graph's right-click menu, the Corridor menu and the toolbar's ».</summary>
internal static class MenuBuilder
{
    /// <summary>A separator, a heading (bold, disabled, its text as it is) or an action with its shortcut and tick.</summary>
    public static Control ToWpf(PlanMenuItem item) =>
        item.IsSeparator ? new Separator()
        : item.Command is null ? new MenuItem { Header = MenuText.Escape(item.Header!), IsEnabled = false, FontWeight = FontWeights.SemiBold }
        : new MenuItem { Header = item.Header, Command = item.Command, InputGestureText = item.Gesture ?? string.Empty, IsChecked = item.IsChecked };
}
