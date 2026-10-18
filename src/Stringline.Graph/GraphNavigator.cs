namespace Stringline.Graph;

/// <summary>Modifier keys held during a mouse gesture.</summary>
[Flags]
public enum GraphModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>
/// Pan, zoom and follow for one graph surface (<c>05-…md</c> §3), independent of any UI toolkit: the host passes in
/// sizes, wheel turns, drags and the sim time, and draws <see cref="Viewport"/>. Not thread-safe; the UI thread owns it.
/// </summary>
/// <remarks>
/// Follow mode keeps now at <see cref="NowFraction"/> of the way across (GR-3). Dragging or panning in time turns it
/// off; <see cref="Follow"/> (the Follow button, or Space) turns it back on. Zooming in time keeps following, about the
/// now line rather than the cursor, so the now line stays put.
/// </remarks>
public sealed class GraphNavigator
{
    /// <summary>Zoom factor for one wheel notch (120 units).</summary>
    public const double WheelZoomStep = 1.2;

    /// <summary>Pixels panned for one Shift+wheel notch.</summary>
    public const double WheelPanPixels = 80;

    private const int WheelNotch = 120;

    private ScreenPoint? _dragFrom;

    public GraphNavigator(int width = 800, int height = 600, int spanSeconds = 3 * 3600, double nowFraction = 1.0 / 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nowFraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nowFraction, 1);
        NowFraction = nowFraction;
        Viewport = GraphViewport.Create(0, spanSeconds, Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>Raised whenever <see cref="Viewport"/> or <see cref="IsFollowing"/> changes.</summary>
    public event EventHandler? Changed;

    public GraphViewport Viewport { get; private set; }

    /// <summary>True while the view tracks the now line.</summary>
    public bool IsFollowing { get; private set; } = true;

    /// <summary>Where now sits across the view in follow mode, 0 (left) to 1 (right).</summary>
    public double NowFraction { get; }

    /// <summary>The last sim time given to <see cref="Tick"/>, in seconds, if any.</summary>
    public double? Now { get; private set; }

    public bool IsDragging => _dragFrom is not null;

    /// <summary>The surface changed size (in the same units as the mouse positions).</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width != Viewport.Width || height != Viewport.Height)
        {
            Set(Viewport.Resize(width, height));
        }
    }

    /// <summary>
    /// The sim time moved on. In follow mode the view moves with it. The first time ever also centres the view on now
    /// (so a fresh window starts at the sim time, not midnight). Returns true if the view moved.
    /// </summary>
    public bool Tick(double now)
    {
        var first = Now is null;
        Now = now;
        if (!IsFollowing && !first)
        {
            return false;
        }

        var followed = Viewport.Follow(now, NowFraction);
        if (Math.Abs(followed.TimeStart - Viewport.TimeStart) < 1e-6)
        {
            return false;
        }

        Set(followed);
        return true;
    }

    /// <summary>
    /// A wheel turn at (x, y): zoom time about the cursor, Ctrl zooms along the corridor, Shift pans time (in either
    /// orientation).
    /// <paramref name="delta"/> is in wheel units (120 per notch; positive away from the user zooms in).
    /// </summary>
    public void Wheel(double delta, double x, double y, GraphModifiers modifiers = GraphModifiers.None)
    {
        var notches = delta / WheelNotch;
        if (notches == 0)
        {
            return;
        }

        if (modifiers.HasFlag(GraphModifiers.Shift))
        {
            IsFollowing = false;
            Set(Viewport.PanTimePixels(notches * WheelPanPixels), force: true);
        }
        else if (modifiers.HasFlag(GraphModifiers.Control))
        {
            Set(Viewport.ZoomAxis(Math.Pow(WheelZoomStep, notches), Viewport.AxisPixelOf(new ScreenPoint(x, y))));
        }
        else
        {
            ZoomTime(Math.Pow(WheelZoomStep, notches), Viewport.TimePixelOf(new ScreenPoint(x, y)));
        }
    }

    /// <summary>
    /// Zoom time by <paramref name="factor"/> (above 1 zooms in) about the time pixel <paramref name="timePixel"/> (X, or
    /// Y when vertical), or about now when following.
    /// </summary>
    public void ZoomTime(double factor, double timePixel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        if (IsFollowing && Now is not null)
        {
            ShowSpan(Viewport.TimeSpan / factor);
        }
        else
        {
            Set(Viewport.ZoomTime(factor, timePixel));
        }
    }

    /// <summary>A zoom preset (1 h, 3 h, 6 h): this many seconds across, about now when following, else about the centre.</summary>
    public void ShowSpan(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        var span = Math.Clamp(seconds, GraphViewport.MinTimeSpan, GraphViewport.MaxTimeSpan);
        var (anchor, share) = IsFollowing && Now is { } now ? (now, NowFraction) : ((Viewport.TimeStart + Viewport.TimeEnd) / 2, 0.5);
        Set(Viewport with { TimeStart = anchor - (span * share), TimeEnd = anchor + (span * (1 - share)) });
    }

    /// <summary>Show the whole corridor again after a distance zoom.</summary>
    public void ShowFullCorridor() => Set(Viewport.WithFullAxis());

    /// <summary>Turn follow mode on (snapping to now) or off.</summary>
    public void SetFollowing(bool follow)
    {
        if (follow == IsFollowing)
        {
            return;
        }

        IsFollowing = follow;
        if (follow && Now is { } now)
        {
            Set(Viewport.Follow(now, NowFraction), force: true);
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Follow mode back on (the Follow button); see <see cref="SetFollowing"/>.</summary>
    public void Follow() => SetFollowing(true);

    /// <summary>Toggle follow mode (Space).</summary>
    public void ToggleFollow() => SetFollowing(!IsFollowing);

    /// <summary>Stop following and centre the view on a sim time, e.g. a train picked from the list.</summary>
    public void CentreOn(double time)
    {
        IsFollowing = false;
        Set(Viewport.PanTime(time - ((Viewport.TimeStart + Viewport.TimeEnd) / 2)), force: true);
    }

    /// <summary>A drag on the background starts here.</summary>
    public void BeginDrag(ScreenPoint at) => _dragFrom = at;

    /// <summary>
    /// The drag moved. Panning in time (across, or down when vertical) turns follow off; moving only along the
    /// corridor while zoomed in pans the corridor and keeps following.
    /// </summary>
    public void DragTo(ScreenPoint at)
    {
        if (_dragFrom is not { } from)
        {
            return;
        }

        var dt = Viewport.TimePixelOf(at) - Viewport.TimePixelOf(from);
        var da = Viewport.AxisPixelOf(at) - Viewport.AxisPixelOf(from);
        var next = Viewport;
        if (dt != 0)
        {
            IsFollowing = false;
            next = next.PanTimePixels(dt);
        }

        if (da != 0)
        {
            next = next.PanAxisPixels(da);
        }

        _dragFrom = at;
        Set(next, force: dt != 0);
    }

    /// <summary>Turn the graph (GR-1): the same time and corridor ranges, time across or down.</summary>
    public void SetOrientation(GraphOrientation orientation)
    {
        if (orientation != Viewport.Orientation)
        {
            Set(Viewport.WithOrientation(orientation));
        }
    }

    public void EndDrag() => _dragFrom = null;

    private void Set(GraphViewport viewport, bool force = false)
    {
        if (viewport == Viewport && !force)
        {
            return;
        }

        Viewport = viewport;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
