using Stringline.Core;

namespace Stringline.Graph;

/// <summary>A point in pixels, origin top left.</summary>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>Space around the plot area, in pixels: location labels on the left, time labels on top.</summary>
public readonly record struct GraphMargins(double Left, double Top, double Right, double Bottom)
{
    public static GraphMargins Default { get; } = new(150, 44, 12, 12);
}

/// <summary>
/// What part of the graph is shown and at what size: a time range across, an axis range down. Time runs left to
/// right; axis position 0 (the corridor's Up end) is at the top. World coordinates are sim seconds and corridor
/// axis positions (0..1), so panning and zooming change only the viewport, never the scene.
/// </summary>
/// <param name="TimeStart">Sim seconds at the left edge of the plot area.</param>
/// <param name="TimeEnd">Sim seconds at the right edge; greater than <paramref name="TimeStart"/>.</param>
/// <param name="AxisStart">Axis position at the top edge of the plot area (slightly below 0 leaves room above the first location).</param>
/// <param name="AxisEnd">Axis position at the bottom edge; greater than <paramref name="AxisStart"/>.</param>
/// <param name="Width">Whole surface width in pixels, margins included.</param>
/// <param name="Height">Whole surface height in pixels, margins included.</param>
public sealed record GraphViewport(double TimeStart, double TimeEnd, double AxisStart, double AxisEnd, int Width, int Height, GraphMargins Margins)
{
    /// <summary>Axis padding above the first and below the last location, as a share of the corridor.</summary>
    public const double DefaultAxisPadding = 0.04;

    /// <summary>Narrowest time range a zoom may reach: 5 minutes.</summary>
    public const double MinTimeSpan = 300;

    /// <summary>Widest time range a zoom may reach: 2 days.</summary>
    public const double MaxTimeSpan = 2 * SimTime.SecondsPerDay;

    /// <summary>Narrowest share of the corridor a distance zoom may reach.</summary>
    public const double MinAxisSpan = 0.05;

    /// <summary>The top of the whole padded corridor.</summary>
    public const double FullAxisStart = -DefaultAxisPadding;

    /// <summary>The bottom of the whole padded corridor.</summary>
    public const double FullAxisEnd = 1 + DefaultAxisPadding;

    public double TimeSpan => TimeEnd - TimeStart;

    public double AxisSpan => AxisEnd - AxisStart;

    public double PlotLeft => Margins.Left;

    public double PlotTop => Margins.Top;

    public double PlotRight => Math.Max(PlotLeft + 1, Width - Margins.Right);

    public double PlotBottom => Math.Max(PlotTop + 1, Height - Margins.Bottom);

    public double PlotWidth => PlotRight - PlotLeft;

    public double PlotHeight => PlotBottom - PlotTop;

    /// <summary>Pixels per sim minute across.</summary>
    public double PixelsPerMinute => PlotWidth * SimTime.SecondsPerMinute / TimeSpan;

    /// <summary>
    /// A viewport of <paramref name="seconds"/> with <paramref name="now"/> at <paramref name="nowFraction"/> of the
    /// way across (⅓ shows an hour of history in a 3 hour view), showing the whole corridor.
    /// </summary>
    public static GraphViewport Around(SimTime now, int seconds, int width, int height, double nowFraction = 1.0 / 3, GraphMargins? margins = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(nowFraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nowFraction, 1);
        var start = now.Seconds - (seconds * nowFraction);
        return Create(start, start + seconds, width, height, margins);
    }

    /// <summary>A viewport from <paramref name="timeStart"/> to <paramref name="timeEnd"/> showing the whole corridor.</summary>
    public static GraphViewport Create(double timeStart, double timeEnd, int width, int height, GraphMargins? margins = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeEnd, timeStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new GraphViewport(timeStart, timeEnd, -DefaultAxisPadding, 1 + DefaultAxisPadding, width, height, margins ?? GraphMargins.Default);
    }

    public double TimeToX(double time) => PlotLeft + ((time - TimeStart) / TimeSpan * PlotWidth);

    public double AxisToY(double axis) => PlotTop + ((axis - AxisStart) / AxisSpan * PlotHeight);

    public ScreenPoint ToScreen(double time, double axis) => new(TimeToX(time), AxisToY(axis));

    public double XToTime(double x) => TimeStart + ((x - PlotLeft) / PlotWidth * TimeSpan);

    public double YToAxis(double y) => AxisStart + ((y - PlotTop) / PlotHeight * AxisSpan);

    /// <summary>The world point (sim seconds, axis position) under a pixel.</summary>
    public (double Time, double Axis) ToWorld(ScreenPoint point) => (XToTime(point.X), YToAxis(point.Y));

    public bool IsInPlot(ScreenPoint point) =>
        point.X >= PlotLeft && point.X <= PlotRight && point.Y >= PlotTop && point.Y <= PlotBottom;

    /// <summary>True if any part of this world rectangle is in view.</summary>
    public bool Overlaps(double timeMin, double timeMax, double axisMin, double axisMax) =>
        timeMax >= TimeStart && timeMin <= TimeEnd && axisMax >= AxisStart && axisMin <= AxisEnd;

    /// <summary>Moved by this many sim seconds (positive moves the view later).</summary>
    public GraphViewport PanTime(double seconds) => this with { TimeStart = TimeStart + seconds, TimeEnd = TimeEnd + seconds };

    /// <summary>Moved by this many pixels across, as a drag would (dragging right shows earlier times).</summary>
    public GraphViewport PanPixels(double dx) => PanTime(-dx / PlotWidth * TimeSpan);

    /// <summary>
    /// Zoomed in time about the pixel column <paramref name="anchorX"/>, which keeps its time. A factor above 1 zooms
    /// in. The span is held between <see cref="MinTimeSpan"/> and <see cref="MaxTimeSpan"/>.
    /// </summary>
    public GraphViewport ZoomTime(double factor, double anchorX)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var anchor = XToTime(anchorX);
        var span = Math.Clamp(TimeSpan / factor, MinTimeSpan, MaxTimeSpan);
        var share = (anchor - TimeStart) / TimeSpan;
        var start = anchor - (share * span);
        return this with { TimeStart = start, TimeEnd = start + span };
    }

    /// <summary>The same view, re-centred so <paramref name="now"/> sits <paramref name="nowFraction"/> of the way across (follow mode).</summary>
    public GraphViewport Follow(double now, double nowFraction = 1.0 / 3)
    {
        var start = now - (TimeSpan * nowFraction);
        return this with { TimeStart = start, TimeEnd = start + TimeSpan };
    }

    /// <summary>
    /// Zoomed along the corridor about the pixel row <paramref name="anchorY"/>, which keeps its axis position. A
    /// factor above 1 zooms in. The view never shows more than the whole padded corridor, nor less than
    /// <see cref="MinAxisSpan"/> of it.
    /// </summary>
    public GraphViewport ZoomAxis(double factor, double anchorY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var anchor = YToAxis(anchorY);
        var span = Math.Clamp(AxisSpan / factor, MinAxisSpan, FullAxisEnd - FullAxisStart);
        var share = (anchor - AxisStart) / AxisSpan;
        return WithAxis(anchor - (share * span), span);
    }

    /// <summary>Moved along the corridor by this many pixels, as a drag would, but never past either padded end.</summary>
    public GraphViewport PanAxisPixels(double dy) => WithAxis(AxisStart - (dy / PlotHeight * AxisSpan), AxisSpan);

    /// <summary>The same time range, showing the whole corridor.</summary>
    public GraphViewport WithFullAxis() => this with { AxisStart = FullAxisStart, AxisEnd = FullAxisEnd };

    /// <summary>True if the whole corridor is in view.</summary>
    public bool ShowsFullAxis => AxisStart <= FullAxisStart + 1e-9 && AxisEnd >= FullAxisEnd - 1e-9;

    private GraphViewport WithAxis(double start, double span)
    {
        if (span >= FullAxisEnd - FullAxisStart - 1e-12)
        {
            return WithFullAxis();
        }

        start = Math.Max(FullAxisStart, Math.Min(start, FullAxisEnd - span));
        return this with { AxisStart = start, AxisEnd = start + span };
    }

    /// <summary>The same world range drawn on a surface of a new size.</summary>
    public GraphViewport Resize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return this with { Width = width, Height = height };
    }
}
