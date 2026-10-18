using SkiaSharp;
using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>
/// Draws a <see cref="GraphScene"/> through a <see cref="GraphViewport"/> onto any <see cref="SKCanvas"/>: the WPF
/// host's surface, or an off-screen bitmap for PNGs (<see cref="RenderPng"/>). Paints and fonts are kept between
/// frames, so one renderer should be reused, from one thread at a time.
/// </summary>
public sealed class SkiaGraphRenderer : IDisposable
{
    /// <summary>Shortest a segment may be on screen, in pixels, for a headcode label to go on it.</summary>
    public const float MinLabelSegmentPixels = 24;

    private readonly Dictionary<(Colour Colour, GraphLayer Layer), SKPaint> _pathPaints = [];
    private readonly Dictionary<(Colour Colour, float Width), SKPaint> _strokes = [];
    private readonly Dictionary<Colour, SKPaint> _fills = [];
    private readonly SKFont _locationFont;
    private readonly SKFont _stationFont;
    private readonly SKFont _tiplocFont;
    private readonly SKFont _timeFont;
    private readonly SKFont _hourFont;
    private readonly SKFont _headcodeFont;
    private readonly SKFont _titleFont;
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([6, 4], 0);

    public SkiaGraphRenderer(GraphTheme? theme = null)
    {
        Theme = theme ?? GraphTheme.Light;
        _locationFont = GraphFonts.Create(GraphFonts.Regular, Theme.LocationFontSize);
        _stationFont = GraphFonts.Create(GraphFonts.Bold, Theme.LocationFontSize);
        _tiplocFont = GraphFonts.Create(GraphFonts.Regular, Theme.TiplocFontSize);
        _timeFont = GraphFonts.Create(GraphFonts.Regular, Theme.TimeFontSize);
        _hourFont = GraphFonts.Create(GraphFonts.Bold, Theme.TimeFontSize);
        _headcodeFont = GraphFonts.Create(GraphFonts.Bold, Theme.HeadcodeFontSize);
        _titleFont = GraphFonts.Create(GraphFonts.Bold, Theme.TitleFontSize);
    }

    public GraphTheme Theme { get; }

    /// <summary>Renders to a new PNG of the viewport's size.</summary>
    public byte[] RenderPng(GraphScene scene, GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var info = new SKImageInfo(viewport.Width, viewport.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException($"Cannot create a {viewport.Width}×{viewport.Height} surface.");
        Render(surface.Canvas, scene, viewport);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void Render(SKCanvas canvas, GraphScene scene, GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);

        var plot = new SKRect((float)viewport.PlotLeft, (float)viewport.PlotTop, (float)viewport.PlotRight, (float)viewport.PlotBottom);
        canvas.Clear(ToSk(Theme.Background));
        canvas.DrawRect(plot, Fill(Theme.PlotBackground));

        var ticks = TimeGrid.Ticks(viewport);
        canvas.Save();
        canvas.ClipRect(plot);
        DrawPast(canvas, scene, viewport, plot);
        DrawTimeGrid(canvas, ticks, viewport, plot);
        DrawLocationLines(canvas, scene, viewport, plot);
        var drawn = DrawPaths(canvas, scene, viewport);
        DrawNow(canvas, scene, viewport, plot);
        DrawHeadcodes(canvas, drawn, viewport, plot);
        canvas.Restore();

        canvas.DrawRect(plot, Stroke(Theme.Border, 1));
        DrawTimeLabels(canvas, ticks, viewport, plot);
        DrawLocationLabels(canvas, scene, viewport, plot);
        DrawTitle(canvas, scene);
    }

    private void DrawPast(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        if (scene.Now is not { } now)
        {
            return;
        }

        var x = Math.Clamp((float)viewport.TimeToX(now.Seconds), plot.Left, plot.Right);
        if (x > plot.Left)
        {
            canvas.DrawRect(new SKRect(plot.Left, plot.Top, x, plot.Bottom), Fill(Theme.PastShade));
        }
    }

    private void DrawTimeGrid(SKCanvas canvas, IReadOnlyList<TimeTick> ticks, GraphViewport viewport, SKRect plot)
    {
        foreach (var tick in ticks)
        {
            var x = Crisp(viewport.TimeToX(tick.Time));
            var paint = tick.Weight switch
            {
                TickWeight.Hour => Stroke(Theme.HourGrid, 1),
                TickWeight.Major => Stroke(Theme.MajorGrid, 1),
                _ => Stroke(Theme.MinorGrid, 1),
            };
            canvas.DrawLine(x, plot.Top, x, plot.Bottom, paint);
        }
    }

    private void DrawLocationLines(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        foreach (var location in scene.Locations)
        {
            var y = Crisp(viewport.AxisToY(location.Axis));
            canvas.DrawLine(plot.Left, y, plot.Right, y, location.IsStation ? Stroke(Theme.StationLine, 1) : Stroke(Theme.LocationLine, 1));
        }
    }

    /// <returns>The paths drawn (in view), for labelling.</returns>
    private List<GraphPath> DrawPaths(SKCanvas canvas, GraphScene scene, GraphViewport viewport)
    {
        var drawn = new List<GraphPath>();
        using var builder = new SKPathBuilder();
        foreach (var layer in Enum.GetValues<GraphLayer>())
        {
            foreach (var p in scene.InLayer(layer))
            {
                if (!viewport.Overlaps(p.TimeMin, p.TimeMax, p.AxisMin, p.AxisMax))
                {
                    continue;
                }

                builder.Reset();
                for (var i = 0; i < p.Vertices.Count; i++)
                {
                    var s = viewport.ToScreen(p.Vertices[i].Time, p.Vertices[i].Axis);
                    if (i == 0)
                    {
                        builder.MoveTo((float)s.X, (float)s.Y);
                    }
                    else
                    {
                        builder.LineTo((float)s.X, (float)s.Y);
                    }
                }

                using var path = builder.Snapshot();
                canvas.DrawPath(path, PathPaint(p.Colour, layer));
                drawn.Add(p);
            }
        }

        return drawn;
    }

    private void DrawNow(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        if (scene.Now is { } now && now.Seconds >= viewport.TimeStart && now.Seconds <= viewport.TimeEnd)
        {
            var x = (float)viewport.TimeToX(now.Seconds);
            canvas.DrawLine(x, plot.Top, x, plot.Bottom, Stroke(Theme.NowLine, 1.5f));
        }
    }

    /// <summary>
    /// One headcode per train, along its line at the first place that is long enough and does not overlap a label
    /// already placed. A train's actual path is labelled in preference to its WTT path.
    /// </summary>
    private void DrawHeadcodes(SKCanvas canvas, List<GraphPath> drawn, GraphViewport viewport, SKRect plot)
    {
        var withActuals = drawn.Where(p => p.Layer == GraphLayer.Actual && p.Train is not null).Select(p => p.Train).ToHashSet();
        var placed = new List<SKRect>();
        foreach (var p in drawn.OrderByDescending(p => p.Layer))
        {
            if (p.Layer is not (GraphLayer.Actual or GraphLayer.Wtt) || (p.Layer == GraphLayer.Wtt && withActuals.Contains(p.Train)))
            {
                continue;
            }

            var text = p.Headcode.Value;
            var width = _headcodeFont.MeasureText(text);
            if (FindLabelPlace(p, width, viewport, plot, placed) is not { } place)
            {
                continue;
            }

            canvas.Save();
            canvas.Translate(place.X, place.Y);
            canvas.RotateDegrees(place.Degrees);
            canvas.DrawText(text, 0, -3, SKTextAlign.Center, _headcodeFont, Fill(Darken(p.Colour)));
            canvas.Restore();
            placed.Add(place.Bounds);
        }
    }

    private readonly record struct LabelPlace(float X, float Y, float Degrees, SKRect Bounds);

    private LabelPlace? FindLabelPlace(GraphPath path, float textWidth, GraphViewport viewport, SKRect plot, List<SKRect> placed)
    {
        var height = _headcodeFont.Size;
        for (var i = 0; i + 1 < path.Vertices.Count; i++)
        {
            var a = viewport.ToScreen(path.Vertices[i].Time, path.Vertices[i].Axis);
            var b = viewport.ToScreen(path.Vertices[i + 1].Time, path.Vertices[i + 1].Axis);
            if (a.Y == b.Y || ClipToRect(a, b, plot) is not var (c, d))
            {
                continue; // a stop, or out of view
            }

            var dx = (float)(d.X - c.X);
            var dy = (float)(d.Y - c.Y);
            var length = MathF.Sqrt((dx * dx) + (dy * dy));
            if (length < Math.Max(MinLabelSegmentPixels, textWidth + 4))
            {
                continue;
            }

            var degrees = MathF.Atan2(dy, dx) * 180 / MathF.PI;
            if (degrees > 90)
            {
                degrees -= 180;
            }
            else if (degrees < -90)
            {
                degrees += 180;
            }

            foreach (var share in (ReadOnlySpan<float>)[0.5f, 0.25f, 0.75f])
            {
                var x = (float)c.X + (dx * share);
                var y = (float)c.Y + (dy * share);
                var bounds = RotatedBounds(x, y, degrees, textWidth, height);
                if (plot.Contains(bounds) && !placed.Exists(r => r.IntersectsWith(bounds)))
                {
                    return new LabelPlace(x, y, degrees, bounds);
                }
            }
        }

        return null;
    }

    /// <summary>The upright bounding box of a label centred on (x, y), sitting just above its line, rotated by <paramref name="degrees"/>.</summary>
    private static SKRect RotatedBounds(float x, float y, float degrees, float width, float height)
    {
        var matrix = SKMatrix.CreateRotationDegrees(degrees, 0, 0).PostConcat(SKMatrix.CreateTranslation(x, y));
        return matrix.MapRect(new SKRect(-width / 2, -3 - height, width / 2, -1));
    }

    /// <summary>The part of segment a–b inside <paramref name="rect"/> (Liang–Barsky), or null if none.</summary>
    internal static (ScreenPoint, ScreenPoint)? ClipToRect(ScreenPoint a, ScreenPoint b, SKRect rect)
    {
        double t0 = 0, t1 = 1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (!Clip(-dx, a.X - rect.Left) || !Clip(dx, rect.Right - a.X) || !Clip(-dy, a.Y - rect.Top) || !Clip(dy, rect.Bottom - a.Y))
        {
            return null;
        }

        return (new ScreenPoint(a.X + (t0 * dx), a.Y + (t0 * dy)), new ScreenPoint(a.X + (t1 * dx), a.Y + (t1 * dy)));

        bool Clip(double p, double q)
        {
            if (p == 0)
            {
                return q >= 0;
            }

            var r = q / p;
            if (p < 0)
            {
                if (r > t1)
                {
                    return false;
                }

                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }

                t1 = Math.Min(t1, r);
            }

            return true;
        }
    }

    private void DrawTimeLabels(SKCanvas canvas, IReadOnlyList<TimeTick> ticks, GraphViewport viewport, SKRect plot)
    {
        foreach (var tick in ticks)
        {
            if (tick.Label is null)
            {
                continue;
            }

            var x = Crisp(viewport.TimeToX(tick.Time));
            var half = _timeFont.MeasureText(tick.Label) / 2;
            canvas.DrawLine(x, plot.Top - 4, x, plot.Top, Stroke(Theme.Border, 1));
            if (x - half >= plot.Left - 2 && x + half <= plot.Right + 2)
            {
                canvas.DrawText(tick.Label, x, plot.Top - 8, SKTextAlign.Center, tick.Weight == TickWeight.Hour ? _hourFont : _timeFont, Fill(Theme.Text));
            }
        }
    }

    private void DrawLocationLabels(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        var right = plot.Left - 6;
        var lastBottom = float.NegativeInfinity;
        foreach (var location in scene.Locations.OrderBy(l => l.Axis))
        {
            var y = (float)viewport.AxisToY(location.Axis);
            if (y < plot.Top - 1 || y > plot.Bottom + 1)
            {
                continue;
            }

            var hasTiploc = !string.Equals(location.Name, location.Tiploc.Code, StringComparison.Ordinal);
            var top = y - _locationFont.Size;
            if (top < lastBottom)
            {
                continue; // too close to the label above
            }

            canvas.DrawLine(right + 2, Crisp(y), plot.Left, Crisp(y), Stroke(Theme.Border, 1));
            canvas.DrawText(location.Name, right, y + 4, SKTextAlign.Right, location.IsStation ? _stationFont : _locationFont, Fill(Theme.Text));
            lastBottom = y + 5;
            if (hasTiploc)
            {
                canvas.DrawText(location.Tiploc.Code, right, y + 4 + _tiplocFont.Size + 1, SKTextAlign.Right, _tiplocFont, Fill(Theme.MutedText));
                lastBottom += _tiplocFont.Size + 1;
            }
        }
    }

    private void DrawTitle(SKCanvas canvas, GraphScene scene)
    {
        var text = scene.Now is { } now ? $"{scene.Title}   {now}" : scene.Title;
        canvas.DrawText(text, 8, 17, SKTextAlign.Left, _titleFont, Fill(Theme.Text));
    }

    private SKPaint PathPaint(Colour colour, GraphLayer layer)
    {
        if (!_pathPaints.TryGetValue((colour, layer), out var paint))
        {
            var style = Theme.StyleOf(layer);
            paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = style.Width,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                Color = ToSk(colour with { A = (byte)Math.Round(colour.A * style.Opacity) }),
                PathEffect = style.Dashed ? _dash : null,
            };
            _pathPaints[(colour, layer)] = paint;
        }

        return paint;
    }

    private SKPaint Stroke(Colour colour, float width)
    {
        if (!_strokes.TryGetValue((colour, width), out var paint))
        {
            paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, Color = ToSk(colour) };
            _strokes[(colour, width)] = paint;
        }

        return paint;
    }

    private SKPaint Fill(Colour colour)
    {
        if (!_fills.TryGetValue(colour, out var paint))
        {
            paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ToSk(colour) };
            _fills[colour] = paint;
        }

        return paint;
    }

    /// <summary>A label colour readable on the light plot: the line colour, opaque and a little darker.</summary>
    internal static Colour Darken(Colour colour) =>
        new((byte)(colour.R * 0.75), (byte)(colour.G * 0.75), (byte)(colour.B * 0.75));

    private static SKColor ToSk(Colour colour) => new(colour.R, colour.G, colour.B, colour.A);

    /// <summary>A 1 px line at this coordinate, centred on a pixel so it is not smeared over two.</summary>
    private static float Crisp(double coordinate) => (float)Math.Floor(coordinate) + 0.5f;

    public void Dispose()
    {
        foreach (var paint in _pathPaints.Values.Concat(_strokes.Values).Concat(_fills.Values))
        {
            paint.Dispose();
        }

        _pathPaints.Clear();
        _strokes.Clear();
        _fills.Clear();
        _locationFont.Dispose();
        _stationFont.Dispose();
        _tiplocFont.Dispose();
        _timeFont.Dispose();
        _hourFont.Dispose();
        _headcodeFont.Dispose();
        _titleFont.Dispose();
        _dash.Dispose();
    }
}
