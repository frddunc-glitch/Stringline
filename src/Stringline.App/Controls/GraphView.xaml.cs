using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp.Views.Desktop;
using Stringline.Core;
using Stringline.Graph;

namespace Stringline.App.Controls;

/// <summary>
/// Hosts the Skia train graph (<c>05-…md</c> §3): draws <see cref="Scene"/> through the <see cref="Navigator"/>'s
/// viewport, and turns the mouse into pan, zoom, hover tooltips and clicks. All graph logic is in Stringline.Graph;
/// this control only adapts WPF input and the <c>SKElement</c> surface to it. Units are device-independent pixels;
/// the canvas is scaled to the screen's DPI so text is sharp.
/// </summary>
#pragma warning disable CA1001 // The renderer is disposed when the dispatcher shuts down; WPF controls are not disposable.
public partial class GraphView : UserControl
#pragma warning restore CA1001
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
        nameof(Scene), typeof(GraphScene), typeof(GraphView), new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(GraphSelection), typeof(GraphView), new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty GhostsProperty = DependencyProperty.Register(
        nameof(Ghosts), typeof(IReadOnlyList<GraphPath>), typeof(GraphView), new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty NavigatorProperty = DependencyProperty.Register(
        nameof(Navigator), typeof(GraphNavigator), typeof(GraphView), new PropertyMetadata(null, OnNavigatorChanged));

    /// <summary>Pixels the mouse may move between press and release and still count as a click.</summary>
    private const double ClickSlop = 3;

    private readonly SkiaGraphRenderer _renderer = new();
    private Point? _pressedAt;
    private bool _dragged;
    private bool _editing;
    private bool _altUsed;
    private double? _now;
    private double _nowAt = double.NaN;
    private (string Key, int Vertex)? _tipFor;
    private GraphHitIndex? _hitIndex;

    public GraphView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Navigator?.Resize((int)Math.Round(Surface.ActualWidth), (int)Math.Round(Surface.ActualHeight));
        Dispatcher.ShutdownStarted += (_, _) => _renderer.Dispose();
    }

    /// <summary>Raised on a click: the path hit, or null for the background.</summary>
    public event EventHandler<GraphHit?>? PathClicked;

    public GraphScene? Scene
    {
        get => (GraphScene?)GetValue(SceneProperty);
        set => SetValue(SceneProperty, value);
    }

    public GraphSelection? Selection
    {
        get => (GraphSelection?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>Lines drawn over the graph as a preview, such as where a train being dragged would run.</summary>
    public IReadOnlyList<GraphPath>? Ghosts
    {
        get => (IReadOnlyList<GraphPath>?)GetValue(GhostsProperty);
        set => SetValue(GhostsProperty, value);
    }

    public GraphNavigator? Navigator
    {
        get => (GraphNavigator?)GetValue(NavigatorProperty);
        set => SetValue(NavigatorProperty, value);
    }

    /// <summary>Builds the tooltip for a path under the cursor.</summary>
    public Func<GraphHit, HoverInfo?>? Describer { get; set; }

    /// <summary>Edits trains by dragging and gives the right-click menu; without one, presses only pan.</summary>
    internal IGraphEditor? Editor { get; set; }

    /// <summary>Move the now line (interpolated sim seconds). Redraws only when it moves at least half a pixel.</summary>
    public void SetNow(double? now)
    {
        _now = now;
        if (Navigator is not { } nav || now is not { } seconds)
        {
            return;
        }

        var at = nav.Viewport.TimeToPixel(seconds);
        if (double.IsNaN(_nowAt) || Math.Abs(at - _nowAt) >= 0.5)
        {
            Surface.InvalidateVisual();
        }
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((GraphView)d).Surface.InvalidateVisual();

    private static void OnNavigatorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (GraphView)d;
        if (e.OldValue is GraphNavigator old)
        {
            old.Changed -= view.OnNavigatorMoved;
        }

        if (e.NewValue is GraphNavigator nav)
        {
            nav.Changed += view.OnNavigatorMoved;
            nav.Resize((int)Math.Round(view.Surface.ActualWidth), (int)Math.Round(view.Surface.ActualHeight));
        }

        view.Surface.InvalidateVisual();
    }

    private void OnNavigatorMoved(object? sender, EventArgs e)
    {
        Surface.InvalidateVisual();
        if (_tipFor is not null)
        {
            HideTip();
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        if (Navigator is not { } nav || Scene is not { } scene || Surface.ActualWidth < 1)
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            return;
        }

        var scale = (float)(e.Info.Width / Surface.ActualWidth);
        canvas.Save();
        canvas.Scale(scale);
        var now = _now is { } seconds ? new SimTime((int)Math.Floor(seconds)) : scene.Now;
        _renderer.Render(canvas, scene with { Now = now }, nav.Viewport, Selection, Ghosts);
        canvas.Restore();
        _nowAt = now is { } n ? nav.Viewport.TimeToPixel(n.Seconds) : double.NaN;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(Surface);
        _pressedAt = p;
        _dragged = false;
        var at = new ScreenPoint(p.X, p.Y);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        _editing = Editor is { } editor && HitTest(p) is { } hit && editor.BeginEdit(hit, at, alt);
        _altUsed |= _editing && alt;
        if (_editing)
        {
            HideTip();
        }
        else
        {
            Navigator?.BeginDrag(at);
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(Surface);
        if (_pressedAt is { } from && IsMouseCaptured)
        {
            if (!_dragged && (Math.Abs(p.X - from.X) > ClickSlop || Math.Abs(p.Y - from.Y) > ClickSlop))
            {
                _dragged = true;
                Cursor = !_editing ? Cursors.SizeAll : Navigator?.Viewport.IsVertical == true ? Cursors.SizeNS : Cursors.SizeWE;
                HideTip();
            }

            if (_dragged && _editing)
            {
                Editor?.MoveEdit(new ScreenPoint(p.X, p.Y), Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            }
            else if (_dragged)
            {
                Navigator?.DragTo(new ScreenPoint(p.X, p.Y));
            }

            return;
        }

        UpdateTip(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var wasClick = _pressedAt is not null && !_dragged;
        _pressedAt = null;
        if (_editing)
        {
            _editing = false;
            _ = Editor?.EndEditAsync(commit: !wasClick);
        }

        Navigator?.EndDrag();
        ReleaseMouseCapture();
        Cursor = null;
        if (wasClick)
        {
            PathClicked?.Invoke(this, HitTest(e.GetPosition(Surface)));
        }

        e.Handled = true;
    }

    /// <summary>Esc during a drag edit drops it (and does not clear the selection).</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && _editing)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    /// <summary>Releasing Alt after an Alt+drag must not open the window's menu.</summary>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (_altUsed && e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt)
        {
            _altUsed = false;
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_editing)
        {
            CancelEdit();
        }
    }

    /// <summary>A right click on a line: the plan menu for that train at that point.</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_editing || Editor is not { } editor)
        {
            return;
        }

        Focus();
        HideTip();
        var items = editor.MenuFor(HitTest(e.GetPosition(Surface)));
        if (items.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = this };
        foreach (var item in items)
        {
            menu.Items.Add(item.IsSeparator ? new Separator()
                : item.Command is null ? new MenuItem { Header = item.Header, IsEnabled = false, FontWeight = FontWeights.SemiBold }
                : new MenuItem { Header = item.Header, Command = item.Command });
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CancelEdit()
    {
        _editing = false;
        _pressedAt = null;
        _ = Editor?.EndEditAsync(commit: false);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(Surface);
        var modifiers = GraphModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= GraphModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= GraphModifiers.Control;
        }

        Navigator?.Wheel(e.Delta, p.X, p.Y, modifiers);
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HideTip();
    }

    /// <summary>The path under a point, from a hit-test grid kept until the scene or the view changes.</summary>
    private GraphHit? HitTest(Point p)
    {
        if (Scene is not { } scene || Navigator is not { } nav)
        {
            return null;
        }

        if (_hitIndex?.IsFor(scene, nav.Viewport) != true)
        {
            _hitIndex = GraphHitIndex.Build(scene, nav.Viewport);
        }

        return _hitIndex.HitTest(new ScreenPoint(p.X, p.Y));
    }

    private void UpdateTip(Point p)
    {
        if (HitTest(p) is not { } hit || Describer?.Invoke(hit) is not { } info)
        {
            HideTip();
            return;
        }

        var key = (hit.Path.Key, hit.NearestVertex);
        if (_tipFor != key)
        {
            _tipFor = key;
            TipHeading.Text = info.Heading;
            TipLines.ItemsSource = info.Lines;
        }

        Tip.HorizontalOffset = p.X + 16;
        Tip.VerticalOffset = p.Y + 16;
        Tip.IsOpen = true;
    }

    private void HideTip()
    {
        _tipFor = null;
        Tip.IsOpen = false;
    }
}
