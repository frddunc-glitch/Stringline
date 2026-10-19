using Stringline.Core;

namespace Stringline.Graph;

/// <summary>A point in pixels, origin top left.</summary>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>Which way time runs (GR-1). The corridor runs the other way, its Up end first.</summary>
public enum GraphOrientation
{
    /// <summary>Time left to right, the Up end at the top: the usual train control diagram.</summary>
    Horizontal,

    /// <summary>Time top to bottom, the Up end on the left (Fred, S9).</summary>
    Vertical,
}

/// <summary>
/// Space around the plot area, in pixels. A horizontal graph has location labels on the left and time labels on top;
/// a vertical one has time labels on the left and location labels (rotated) on top.
/// </summary>
public readonly record struct GraphMargins(double Left, double Top, double Right, double Bottom)
{
    public static GraphMargins Default { get; } = new(150, 44, 12, 12);

    /// <summary>The default for a vertical graph: room for <c>00:00+1</c> on the left and rotated names on top.</summary>
    public static GraphMargins DefaultVertical { get; } = new(60, 170, 12, 12);

    public static GraphMargins DefaultFor(GraphOrientation orientation) =>
        orientation == GraphOrientation.Vertical ? DefaultVertical : Default;
}

/// <summary>
/// What part of the graph is shown and at what size: a time range and an axis range. In the default
/// <see cref="GraphOrientation.Horizontal"/> orientation time runs left to right and axis position 0 (the corridor's
/// Up end) is at the top; <see cref="GraphOrientation.Vertical"/> turns it, time running down and the Up end on the
/// left. World coordinates are sim seconds and corridor axis positions (0..1), so panning, zooming and turning the
/// graph change only the viewport, never the scene.
/// </summary>
/// <remarks>
/// "Time pixels" and "axis pixels" are screen coordinates along the time and axis directions: X and Y when
/// horizontal, Y and X when vertical. Code written in those terms works in both orientations.
/// </remarks>
/// <param name="TimeStart">Sim seconds at the near edge of the plot area (left, or top when vertical).</param>
/// <param name="TimeEnd">Sim seconds at the far edge; greater than <paramref name="TimeStart"/>.</param>
/// <param name="AxisStart">Axis position at the near edge (top, or left when vertical); slightly below 0 leaves room before the first location.</param>
/// <param name="AxisEnd">Axis position at the far edge; greater than <paramref name="AxisStart"/>.</param>
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

    /// <summary>The near end of the whole padded corridor.</summary>
    public const double FullAxisStart = -DefaultAxisPadding;

    /// <summary>The far end of the whole padded corridor.</summary>
    public const double FullAxisEnd = 1 + DefaultAxisPadding;

    /// <summary>Which way time runs. Change it with <see cref="WithOrientation"/>, which also swaps the margins.</summary>
    public GraphOrientation Orientation { get; init; } = GraphOrientation.Horizontal;

    public bool IsVertical => Orientation == GraphOrientation.Vertical;

    public double TimeSpan => TimeEnd - TimeStart;

    public double AxisSpan => AxisEnd - AxisStart;

    public double PlotLeft => Margins.Left;

    public double PlotTop => Margins.Top;

    public double PlotRight => Math.Max(PlotLeft + 1, Width - Margins.Right);

    public double PlotBottom => Math.Max(PlotTop + 1, Height - Margins.Bottom);

    public double PlotWidth => PlotRight - PlotLeft;

    public double PlotHeight => PlotBottom - PlotTop;

    /// <summary>Length of the plot area along the time direction, in pixels.</summary>
    public double TimeLength => IsVertical ? PlotHeight : PlotWidth;

    /// <summary>Length of the plot area along the corridor, in pixels.</summary>
    public double AxisLength => IsVertical ? PlotWidth : PlotHeight;

    /// <summary>Where the time direction starts on screen: the plot's left edge, or its top when vertical.</summary>
    public double TimeOrigin => IsVertical ? PlotTop : PlotLeft;

    /// <summary>Where the axis direction starts on screen: the plot's top edge, or its left when vertical.</summary>
    public double AxisOrigin => IsVertical ? PlotLeft : PlotTop;

    /// <summary>Pixels per sim second along the time direction.</summary>
    public double PixelsPerSecond => TimeLength / TimeSpan;

    /// <summary>Pixels per unit of axis position (the whole corridor) along the axis direction.</summary>
    public double PixelsPerAxis => AxisLength / AxisSpan;

    /// <summary>Pixels per sim minute along the time direction.</summary>
    public double PixelsPerMinute => TimeLength * SimTime.SecondsPerMinute / TimeSpan;

    /// <summary>
    /// A viewport of <paramref name="seconds"/> with <paramref name="now"/> at <paramref name="nowFraction"/> of the
    /// way along (⅓ shows an hour of history in a 3 hour view), showing the whole corridor.
    /// </summary>
    public static GraphViewport Around(SimTime now, int seconds, int width, int height, double nowFraction = 1.0 / 3, GraphMargins? margins = null, GraphOrientation orientation = GraphOrientation.Horizontal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(nowFraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nowFraction, 1);
        var start = now.Seconds - (seconds * nowFraction);
        return Create(start, start + seconds, width, height, margins, orientation);
    }

    /// <summary>A viewport from <paramref name="timeStart"/> to <paramref name="timeEnd"/> showing the whole corridor.</summary>
    /// <param name="margins">Default: <see cref="GraphMargins.DefaultFor"/> the orientation.</param>
    public static GraphViewport Create(double timeStart, double timeEnd, int width, int height, GraphMargins? margins = null, GraphOrientation orientation = GraphOrientation.Horizontal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeEnd, timeStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new GraphViewport(timeStart, timeEnd, -DefaultAxisPadding, 1 + DefaultAxisPadding, width, height, margins ?? GraphMargins.DefaultFor(orientation))
        {
            Orientation = orientation,
        };
    }

    /// <summary>The same time and axis ranges turned the other way, with that orientation's default margins unless given.</summary>
    public GraphViewport WithOrientation(GraphOrientation orientation, GraphMargins? margins = null) =>
        this with { Orientation = orientation, Margins = margins ?? GraphMargins.DefaultFor(orientation) };

    /// <summary>The screen coordinate along the time direction (X, or Y when vertical) of a sim time.</summary>
    public double TimeToPixel(double time) => TimeOrigin + ((time - TimeStart) / TimeSpan * TimeLength);

    /// <summary>The screen coordinate along the axis direction (Y, or X when vertical) of an axis position.</summary>
    public double AxisToPixel(double axis) => AxisOrigin + ((axis - AxisStart) / AxisSpan * AxisLength);

    public ScreenPoint ToScreen(double time, double axis) =>
        IsVertical ? new(AxisToPixel(axis), TimeToPixel(time)) : new(TimeToPixel(time), AxisToPixel(axis));

    /// <summary>The sim time at a screen coordinate along the time direction.</summary>
    public double PixelToTime(double pixel) => TimeStart + ((pixel - TimeOrigin) / TimeLength * TimeSpan);

    /// <summary>The axis position at a screen coordinate along the axis direction.</summary>
    public double PixelToAxis(double pixel) => AxisStart + ((pixel - AxisOrigin) / AxisLength * AxisSpan);

    /// <summary>A point's coordinate along the time direction: its X, or its Y when vertical.</summary>
    public double TimePixelOf(ScreenPoint point) => IsVertical ? point.Y : point.X;

    /// <summary>A point's coordinate along the axis direction: its Y, or its X when vertical.</summary>
    public double AxisPixelOf(ScreenPoint point) => IsVertical ? point.X : point.Y;

    /// <summary>The world point (sim seconds, axis position) under a pixel.</summary>
    public (double Time, double Axis) ToWorld(ScreenPoint point) => (PixelToTime(TimePixelOf(point)), PixelToAxis(AxisPixelOf(point)));

    public bool IsInPlot(ScreenPoint point) =>
        point.X >= PlotLeft && point.X <= PlotRight && point.Y >= PlotTop && point.Y <= PlotBottom;

    /// <summary>True if any part of this world rectangle is in view.</summary>
    public bool Overlaps(double timeMin, double timeMax, double axisMin, double axisMax) =>
        timeMax >= TimeStart && timeMin <= TimeEnd && axisMax >= AxisStart && axisMin <= AxisEnd;

    /// <summary>Moved by this many sim seconds (positive moves the view later).</summary>
    public GraphViewport PanTime(double seconds) => this with { TimeStart = TimeStart + seconds, TimeEnd = TimeEnd + seconds };

    /// <summary>
    /// Moved by this many pixels along the time direction, as a drag would: dragging right (down when vertical)
    /// shows earlier times.
    /// </summary>
    public GraphViewport PanTimePixels(double pixels) => PanTime(-pixels / TimeLength * TimeSpan);

    /// <summary>
    /// Zoomed in time about the time pixel <paramref name="anchorPixel"/>, which keeps its time. A factor above 1 zooms
    /// in. The span is held between <see cref="MinTimeSpan"/> and <see cref="MaxTimeSpan"/>.
    /// </summary>
    public GraphViewport ZoomTime(double factor, double anchorPixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var anchor = PixelToTime(anchorPixel);
        var span = Math.Clamp(TimeSpan / factor, MinTimeSpan, MaxTimeSpan);
        var share = (anchor - TimeStart) / TimeSpan;
        var start = anchor - (share * span);
        return this with { TimeStart = start, TimeEnd = start + span };
    }

    /// <summary>The same view, re-centred so <paramref name="now"/> sits <paramref name="nowFraction"/> of the way along (follow mode).</summary>
    public GraphViewport Follow(double now, double nowFraction = 1.0 / 3)
    {
        var start = now - (TimeSpan * nowFraction);
        return this with { TimeStart = start, TimeEnd = start + TimeSpan };
    }

    /// <summary>
    /// Zoomed along the corridor about the axis pixel <paramref name="anchorPixel"/>, which keeps its axis position. A
    /// factor above 1 zooms in. The view never shows more than the whole padded corridor, nor less than
    /// <see cref="MinAxisSpan"/> of it.
    /// </summary>
    public GraphViewport ZoomAxis(double factor, double anchorPixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        var anchor = PixelToAxis(anchorPixel);
        var span = Math.Clamp(AxisSpan / factor, MinAxisSpan, FullAxisEnd - FullAxisStart);
        var share = (anchor - AxisStart) / AxisSpan;
        return WithAxis(anchor - (share * span), span);
    }

    /// <summary>Moved along the corridor by this many axis pixels, as a drag would, but never past either padded end.</summary>
    public GraphViewport PanAxisPixels(double pixels) => WithAxis(AxisStart - (pixels / AxisLength * AxisSpan), AxisSpan);

    /// <summary>The same time range, showing the whole corridor.</summary>
    public GraphViewport WithFullAxis() => this with { AxisStart = FullAxisStart, AxisEnd = FullAxisEnd };

    /// <summary>The same corridor span, moved to centre on an axis position (never past either padded end).</summary>
    public GraphViewport CentredOnAxis(double axis) => WithAxis(axis - (AxisSpan / 2), AxisSpan);

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
