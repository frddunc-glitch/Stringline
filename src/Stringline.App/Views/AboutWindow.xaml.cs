using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Serilog;
using Stringline.App.Services;

namespace Stringline.App.Views;

/// <summary>
/// About Stringline (S18): the version and commit, the notices GPLv3 §5(d) asks for (copyright, no warranty, the
/// licence, where the source is), the full licence text, and credits for what it is built with.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        // The icon's 64 px frame, so it is drawn sharp rather than scaled from another size.
        var icon = BitmapDecoder.Create(new Uri("pack://application:,,,/Stringline;component/Assets/stringline.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Logo.Source = icon.Frames.FirstOrDefault(f => f.PixelWidth == 64) ?? icon.Frames[^1];
        ProductText.Text = $"{AboutInfo.Product} {AboutInfo.VersionText}";
        CopyrightText.Text = AboutInfo.Copyright;
        WarrantyText.Text = AboutInfo.Warranty;
        LicenceText.Text = AboutInfo.LicenceName;
        LicenceLink.NavigateUri = new Uri(AboutInfo.LicenceUrl);
        SourceText.Text = AboutInfo.SourceUrl;
        SourceLink.NavigateUri = new Uri(AboutInfo.SourceUrl);
        CreditList.ItemsSource = AboutInfo.Credits;
        DisclaimerText.Text = AboutInfo.Disclaimer;
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning(ex, "Could not open {Uri}", e.Uri);
            MessageBox.Show(this, $"Could not open {e.Uri}: {ex.Message}", "Stringline", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyVersion(object sender, RoutedEventArgs e) => Clipboard.SetText(AppLogging.Environment());

    private void OnViewLicence(object sender, RoutedEventArgs e)
    {
        var text = new TextBox
        {
            Text = AboutInfo.LicenceText(),
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
        };
        new Window
        {
            Title = "GNU General Public Licence, version 3",
            Content = text,
            Width = 720,
            Height = 640,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        }.ShowDialog();
    }
}
