using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Stringline.App.ViewModels;

namespace Stringline.App.Controls;

/// <summary>
/// The selected corridor's distance axis in the profile editor: a line from the Up end (left) to the other end, with a
/// tick and a name per location, stations heavier. Names go on a second row where they would overlap, or are left out.
/// </summary>
public sealed class AxisPreview : FrameworkElement
{
    public static readonly DependencyProperty MarksProperty = DependencyProperty.Register(
        nameof(Marks), typeof(IEnumerable<object>), typeof(AxisPreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen Line = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75)), 1));
    private static readonly Pen Station = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x21, 0x21, 0x21)), 2));
    private static readonly Pen Timing = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), 1));

    /// <summary>The marks (<c>AxisMark</c>s) to draw.</summary>
    public IEnumerable<object>? Marks
    {
        get => (IEnumerable<object>?)GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 64);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var marks = Marks?.OfType<AxisMark>().ToList() ?? [];
        if (marks.Count == 0)
        {
            return;
        }

        const double margin = 12;
        var width = Math.Max(1, ActualWidth - (2 * margin));
        const double y = 16;
        drawingContext.DrawLine(Line, new Point(margin, y), new Point(margin + width, y));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double[] rowRight = [double.NegativeInfinity, double.NegativeInfinity];
        foreach (var mark in marks)
        {
            var x = margin + (Math.Clamp(mark.Position, 0, 1) * width);
            var tick = mark.IsStation ? 7 : 4;
            drawingContext.DrawLine(mark.IsStation ? Station : Timing, new Point(x, y - tick), new Point(x, y + tick));
            var text = new FormattedText(mark.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Brushes.Black, dpi);
            var left = Math.Clamp(x - (text.Width / 2), 0, Math.Max(0, ActualWidth - text.Width));
            var row = Array.FindIndex(rowRight, r => left > r + 4);
            if (row >= 0)
            {
                drawingContext.DrawText(text, new Point(left, y + 10 + (row * 14)));
                rowRight[row] = left + text.Width;
            }
        }
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
