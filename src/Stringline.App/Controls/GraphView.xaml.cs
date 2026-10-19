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

    public static readonly DependencyProperty GripsProperty = DependencyProperty.Register(
        nameof(Grips), typeof(GraphGrips), typeof(GraphView), new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty DraftProperty = DependencyProperty.Register(
        nameof(Draft), typeof(GraphDisruption), typeof(GraphView), new PropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty NavigatorProperty = DependencyProperty.Register(
        nameof(Navigator), typeof(GraphNavigator), typeof(GraphView), new PropertyMetadata(null, OnNavigatorChanged));

    /// <summary>Pixels the mouse may move between press and release and still count as a click.</summary>
    private const double ClickSlop = 3;

    private readonly SkiaGraphRenderer _renderer = new();
    private Point? _pressedAt;
    private bool _dragged;
    private bool _editing;
    private bool _blocking;
    private bool _altUsed;
    private GraphGrip? _gripPressed;
    private double? _now;
    private double _nowAt = double.NaN;
    private (string Key, int Vertex)? _tipFor;
    private string? _annotationTip;
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

    /// <summary>The selected train's grips (S26), drawn over the graph and hit before anything else.</summary>
    public GraphGrips? Grips
    {
        get => (GraphGrips?)GetValue(GripsProperty);
        set => SetValue(GripsProperty, value);
    }

    /// <summary>A block being drawn with B+drag, outlined over the graph.</summary>
    public GraphDisruption? Draft
    {
        get => (GraphDisruption?)GetValue(DraftProperty);
        set => SetValue(DraftProperty, value);
    }

    public GraphNavigator? Navigator
    {
        get => (GraphNavigator?)GetValue(NavigatorProperty);
        set => SetValue(NavigatorProperty, value);
    }

    /// <summary>
    /// Builds the tooltip for what is under the cursor (#12): a conflict's ring, a note, a train line, a conflict's
    /// highlight, a block or speed restriction, or a location's label (<see cref="GraphHitTester.TargetAt"/>).
    /// </summary>
    public Func<GraphTarget, HoverInfo?>? Describer { get; set; }

    /// <summary>A click: true if a conflict's ring or highlight took it (S14, #12), so it does not select a line or clear the selection.</summary>
    public Func<GraphTarget?, bool>? ConflictClicker { get; set; }

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
        if (_tipFor is not null || _annotationTip is not null)
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
        _renderer.Render(canvas, scene with { Now = now }, nav.Viewport, Selection, Ghosts, Draft, Grips);
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
        _blocking = Keyboard.IsKeyDown(Key.B) && Editor is { } blocks && blocks.BeginBlock(at);

        // A grip on the selected train is hit first (S26); then the train's own line, even under its forecast.
        _gripPressed = !_blocking ? Editor?.GripAt(at) : null;
        if (_gripPressed is { } grip)
        {
            _editing = Editor!.BeginGrip(grip, at);
        }
        else
        {
            _editing = !_blocking && Editor is { } editor && (editor.SelectedLineAt(at) ?? HitTest(p)) is { } hit && editor.BeginEdit(hit, at, alt);
        }

        // Releasing Alt after an Alt+press on the graph must not open the window's menu, whether or not it edited.
        _altUsed |= alt;
        if (_blocking)
        {
            HideTip();
            Cursor = Cursors.Cross;
        }
        else if (_editing || _gripPressed is not null)
        {
            // A locked grip neither drags nor pans: its reason is in the status bar.
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
        if (_blocking && IsMouseCaptured)
        {
            Editor?.MoveBlock(new ScreenPoint(p.X, p.Y));
            return;
        }

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
            else if (_dragged && _gripPressed is null)
            {
                Navigator?.DragTo(new ScreenPoint(p.X, p.Y));
            }

            return;
        }

        UpdateHover(p);
    }

    /// <summary>
    /// The mouse moves with no button down: over a grip or the selected train's line, the cursor shows it can be
    /// dragged (or not, for a locked grip) and the status bar names the action (S26); then the tooltip.
    /// </summary>
    private void UpdateHover(Point p)
    {
        var at = new ScreenPoint(p.X, p.Y);
        var grip = Editor?.GripAt(at);
        var overLine = grip is null && Editor?.SelectedLineAt(at) is not null;
        Editor?.HoverGrip(grip, overLine);
        Cursor = grip is { IsLocked: true } ? Cursors.No
            : grip is not null || overLine ? (Navigator?.Viewport.IsVertical == true ? Cursors.SizeNS : Cursors.SizeWE)
            : null;
        UpdateTip(p, grip);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var wasClick = _pressedAt is not null && !_dragged;
        _pressedAt = null;
        if (_blocking)
        {
            _blocking = false;
            ReleaseMouseCapture();
            Cursor = null;
            _ = Editor?.EndBlockAsync(commit: true);
            e.Handled = true;
            return;
        }

        if (_editing)
        {
            _editing = false;
            _ = Editor?.EndEditAsync(commit: !wasClick);
        }

        Navigator?.EndDrag();
        ReleaseMouseCapture();
        Cursor = null;
        var up = e.GetPosition(Surface);
        var grip = _gripPressed;
        _gripPressed = null;
        if (wasClick && grip is not null)
        {
            // A click on a grip picks it for the arrow keys; the train stays selected.
            Editor?.SelectGrip(grip);
        }
        else if (wasClick && ConflictClicker?.Invoke(TargetAt(up)) != true)
        {
            PathClicked?.Invoke(this, HitTest(up));
        }

        e.Handled = true;
    }

    /// <summary>
    /// Esc during a drag edit drops it (and does not clear the selection). The arrow keys along the time axis nudge the
    /// picked grip by 15 s, or a minute with Shift (S26).
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && (_editing || _blocking))
        {
            CancelEdit();
            e.Handled = true;
            return;
        }

        if (_editing || _blocking || Editor is not { } editor || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return;
        }

        var vertical = Navigator?.Viewport.IsVertical == true;
        var sign = (e.Key, vertical) switch
        {
            (Key.Right, false) or (Key.Down, true) => 1,
            (Key.Left, false) or (Key.Up, true) => -1,
            _ => 0,
        };
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 60 : PlanDrag.SnapSeconds;
        if (sign != 0 && editor.Nudge(sign * step))
        {
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
        if (_editing || _blocking)
        {
            CancelEdit();
        }
    }

    /// <summary>A right click on a line: the plan menu for that train at that point.</summary>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_editing || _blocking || Editor is not { } editor)
        {
            return;
        }

        Focus();
        HideTip();
        var p = e.GetPosition(Surface);
        var items = editor.MenuFor(HitTest(p), new ScreenPoint(p.X, p.Y));
        if (items.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = this };
        foreach (var item in items)
        {
            menu.Items.Add(Views.MenuBuilder.ToWpf(item));
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CancelEdit()
    {
        if (_blocking)
        {
            _blocking = false;
            _ = Editor?.EndBlockAsync(commit: false);
        }
        else
        {
            _ = Editor?.EndEditAsync(commit: false);
        }

        _editing = false;
        _pressedAt = null;
        _gripPressed = null;
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
        if (!IsMouseCaptured)
        {
            Editor?.HoverGrip(null, overLine: false);
            Cursor = null;
        }
    }

    /// <summary>The path under a point, from a hit-test grid kept until the scene or the view changes.</summary>
    private GraphHit? HitTest(Point p)
    {
        if (Scene is not { } scene || Navigator is not { } nav)
        {
            return null;
        }

        return IndexFor(scene, nav).HitTest(new ScreenPoint(p.X, p.Y));
    }

    private GraphHitIndex IndexFor(GraphScene scene, GraphNavigator nav)
    {
        if (_hitIndex?.IsFor(scene, nav.Viewport) != true)
        {
            _hitIndex = GraphHitIndex.Build(scene, nav.Viewport);
        }

        return _hitIndex;
    }

    /// <summary>What is under a point, using the same hit-test grid as <see cref="HitTest"/>.</summary>
    private GraphTarget? TargetAt(Point p)
    {
        if (Scene is not { } scene || Navigator is not { } nav)
        {
            return null;
        }

        return GraphHitTester.TargetAt(scene, nav.Viewport, new ScreenPoint(p.X, p.Y), IndexFor(scene, nav));
    }

    private void UpdateTip(Point p, GraphGrip? grip)
    {
        if (grip is not null && Editor is { } editor)
        {
            // A grip: keyed by what it is and whether it is locked.
            var tip = editor.DescribeGrip(grip);
            var key = $"grip:{grip.Kind}:{grip.Index}:{grip.LockReason}";
            if (_annotationTip != key)
            {
                _tipFor = null;
                _annotationTip = key;
                TipHeading.Text = tip.Heading;
                TipLines.ItemsSource = tip.Lines;
            }

            ShowTipAt(p);
            return;
        }

        if (TargetAt(p) is not { } target || Describer?.Invoke(target) is not { } info)
        {
            HideTip();
            return;
        }

        if (target.Hit is { } hit)
        {
            // A train line: keyed by the timing point described.
            var key = (hit.Path.Key, hit.NearestVertex);
            if (_tipFor != key)
            {
                _tipFor = key;
                _annotationTip = null;
                TipHeading.Text = info.Heading;
                TipLines.ItemsSource = info.Lines;
            }
        }
        else if (_annotationTip != info.Heading)
        {
            // A conflict, note, disruption or label: keyed by its heading, which names it.
            _tipFor = null;
            _annotationTip = info.Heading;
            TipHeading.Text = info.Heading;
            TipLines.ItemsSource = info.Lines;
        }

        ShowTipAt(p);
    }

    private void ShowTipAt(Point p)
    {
        Tip.HorizontalOffset = p.X + 16;
        Tip.VerticalOffset = p.Y + 16;
        Tip.IsOpen = true;
    }

    private void HideTip()
    {
        _tipFor = null;
        _annotationTip = null;
        Tip.IsOpen = false;
    }
}
