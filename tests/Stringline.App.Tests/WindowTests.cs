using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stringline.App.Services;
using Stringline.App.ViewModels;

namespace Stringline.App.Tests;

/// <summary>
/// S18's windows load and lay out with no binding errors, every tab shown. With <c>STRINGLINE_WINDOW_SHOTS</c> set to a
/// folder, each tab is also saved there as a PNG to look at.
/// </summary>
public sealed class WindowTests
{
    private static readonly string? Shots = Environment.GetEnvironmentVariable("STRINGLINE_WINDOW_SHOTS");

    [Fact]
    public void The_settings_window_loads_every_tab() => OnUiThread(() =>
    {
        var root = Directory.CreateTempSubdirectory("stringline-window-");
        try
        {
            var vm = new SettingsViewModel(new AppSettings(), AppPaths.Under(root.FullName), new ScriptedDialogs());
            Show(new Views.SettingsWindow(vm), "settings");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    });

    [Fact]
    public void The_about_window_loads() => OnUiThread(() => Show(new Views.AboutWindow(), "about"));

    private static void Show(Window window, string name)
    {
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        (window.Left, window.Top) = (-10000, -10000);
        window.Show();
        try
        {
            var tabs = FindChild<TabControl>(window);
            for (var i = 0; i < (tabs?.Items.Count ?? 1); i++)
            {
                tabs?.SelectedIndex = i;
                window.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                window.ActualHeight.Should().BeGreaterThan(100);
                Save(window, $"{name}-{i + 1}.png");
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void Save(Window window, string file)
    {
        if (Shots is null || window.Content is not FrameworkElement content)
        {
            return;
        }

        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var white = new DrawingVisual();
        using (var dc = white.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(white);
        bitmap.Render(content);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Shots);
        using var stream = File.Create(Path.Combine(Shots, file));
        png.Save(stream);
    }

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var errors = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                using var listener = new ProfileEditorWindowTests.BindingErrorListener(errors);
                action();
            }
#pragma warning disable CA1031 // Reported on the test thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();
        failure.Should().BeNull();
        errors.Should().BeEmpty();
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
