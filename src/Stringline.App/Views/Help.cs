using System.Windows;
using System.Windows.Input;
using Stringline.App.Services;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>
/// F1 (S28, #4). A window or panel names its page of the user guide with <c>views:Help.Topic</c>, which its children
/// inherit; F1 (<see cref="ApplicationCommands.Help"/>) opens the page for the element with the focus. Help ▸ User
/// guide sends the command with "contents" and opens the contents; a link that sends one of <see cref="HelpTopics"/>
/// (Help ▸ Privacy, S30) opens that page. One class binding covers every window, dialogs
/// included, which reach the main window's view model through their owners.
/// </summary>
internal static class Help
{
    /// <summary>The menu's parameter for the guide's contents, whatever has the focus.</summary>
    public const string ContentsParameter = "contents";

    public static readonly DependencyProperty TopicProperty = DependencyProperty.RegisterAttached(
        "Topic", typeof(string), typeof(Help), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    private static bool _registered;

    public static string? GetTopic(DependencyObject element) => (string?)element.GetValue(TopicProperty);

    public static void SetTopic(DependencyObject element, string? value) => element.SetValue(TopicProperty, value);

    /// <summary>Makes F1 work in every window. Called by the main window; done once.</summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(ApplicationCommands.Help, OnHelp));
    }

    /// <summary>
    /// The page for F1 pressed on <paramref name="source"/>: the topic the command names, else the element's own
    /// topic or one it inherits, else its window's, else the contents.
    /// </summary>
    public static string TopicFor(DependencyObject? source, object? parameter) =>
        parameter as string == ContentsParameter
            ? HelpTopics.Contents
            : parameter is string named && HelpTopics.All.Contains(named, StringComparer.OrdinalIgnoreCase)
            ? named
            : (source is null ? null : GetTopic(source))
              ?? (source is null ? null : Window.GetWindow(source) is { } window ? GetTopic(window) : null)
              ?? HelpTopics.Contents;

    private static void OnHelp(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is not Window window || ViewModelOf(window) is not { } viewModel)
        {
            return;
        }

        viewModel.OpenGuide(TopicFor(e.OriginalSource as DependencyObject ?? window, e.Parameter));
        e.Handled = true;
    }

    /// <summary>The main window's view model, through the window's owners.</summary>
    private static MainViewModel? ViewModelOf(Window? window)
    {
        for (; window is not null; window = window.Owner)
        {
            if (window is MainWindow { DataContext: MainViewModel viewModel })
            {
                return viewModel;
            }
        }

        return Application.Current?.MainWindow?.DataContext as MainViewModel;
    }
}
