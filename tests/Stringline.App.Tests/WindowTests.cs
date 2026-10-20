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

    [Fact]
    public void An_assist_prompt_shows_its_note_and_send_button() => OnUiThread(() =>
    {
        var prompt = new PromptViewModel(
            "Request a route in SimSig",
            "2S01 calls at Bletchley (platform 2) at 08:06, from Denbigh Hall towards Milton Keynes.",
            new PromptField("Route ID", "RBY2A", ["RBY2A", "RBY2B"], "As SimSig names it, e.g. RWL123A. Or leave it empty and give the signals."),
            new PromptField("From signal", string.Empty, ["SBY2"]),
            new PromptField("To signal", string.Empty, ["SBY4"]))
        {
            Note = MainViewModel.AssistNote,
            AcceptText = "Send to SimSig",
        };
        Show(new Views.PromptDialog(prompt), "assist-route", window =>
        {
            FindChildren<Button>(window).Select(b => b.Content).Should().Contain("Send to SimSig");
            FindChildren<TextBlock>(window).Select(t => t.Text).Should().Contain(MainViewModel.AssistNote);
        });
    });

    private static void Show(Window window, string name, Action<Window>? check = null)
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
                check?.Invoke(window);
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

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var deeper in FindChildren<T>(child))
            {
                yield return deeper;
            }
        }
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
