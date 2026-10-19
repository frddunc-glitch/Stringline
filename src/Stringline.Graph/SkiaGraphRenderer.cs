using SkiaSharp;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>
/// Draws a <see cref="GraphScene"/> through a <see cref="GraphViewport"/> onto any <see cref="SKCanvas"/>: the WPF
/// host's surface, or an off-screen bitmap for PNGs (<see cref="RenderPng"/>). Either orientation (GR-1).
/// </summary>
/// <remarks>
/// Paints, fonts and projected paths are kept between frames (<c>05-…md</c> §7): paths are projected once per zoom and
/// drawn translated while the view pans, and a live update re-projects only the trains that changed. So one renderer
/// should be reused per surface, from one thread at a time.
/// </remarks>
public sealed class SkiaGraphRenderer : IDisposable
{
    /// <summary>Shortest a segment may be on screen, in pixels, for a headcode label to go on it.</summary>
    public const float MinLabelSegmentPixels = 24;

    /// <summary>Height of the title band at the top of the surface.</summary>
    private const float TitleBand = 24;

    private readonly Dictionary<(Colour Colour, GraphLayer Layer, bool Dim, bool Cancelled, LineStyle Style), SKPaint> _pathPaints = [];
    private readonly Dictionary<(Colour Colour, float Width), SKPaint> _strokes = [];
    private readonly Dictionary<Colour, SKPaint> _fills = [];
    private readonly ProjectedPathCache _paths = new();
    private readonly PathTileCache _tiles = new();
    private readonly Action<SKCanvas, GraphPath, bool> _drawPath;
    private readonly LabelGrid _labels = new();
    private readonly List<GraphPath> _drawn = [];
    private readonly List<GraphPath> _selected = [];
    private readonly HashSet<Core.TrainId> _withActuals = [];
    private readonly SKFont _locationFont;
    private readonly SKFont _stationFont;
    private readonly SKFont _tiplocFont;
    private readonly SKFont _timeFont;
    private readonly SKFont _hourFont;
    private readonly SKFont _headcodeFont;
    private readonly SKFont _titleFont;
    private readonly SKFont _annotationFont;
    private SKPaint? _draftBorder;
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([6, 4], 0);
    private readonly Dictionary<(LineStyle Style, float Width), SKPathEffect> _categoryDashes = [];

    public SkiaGraphRenderer(GraphTheme? theme = null)
    {
        Theme = theme ?? GraphTheme.Light;
        _drawPath = DrawPath;
        _locationFont = GraphFonts.Create(GraphFonts.Regular, Theme.LocationFontSize);
        _stationFont = GraphFonts.Create(GraphFonts.Bold, Theme.LocationFontSize);
        _tiplocFont = GraphFonts.Create(GraphFonts.Regular, Theme.TiplocFontSize);
        _timeFont = GraphFonts.Create(GraphFonts.Regular, Theme.TimeFontSize);
        _hourFont = GraphFonts.Create(GraphFonts.Bold, Theme.TimeFontSize);
        _headcodeFont = GraphFonts.Create(GraphFonts.Bold, Theme.HeadcodeFontSize);
        _titleFont = GraphFonts.Create(GraphFonts.Bold, Theme.TitleFontSize);
        _annotationFont = GraphFonts.Create(GraphFonts.Regular, GraphAnnotations.LabelFontSize);
    }

    public GraphTheme Theme { get; }

    /// <summary>Paths projected since this renderer was made: grows on zoom and on changed trains, not on panning.</summary>
    internal int PathsProjected => _paths.Projected;

    /// <summary>Headcode labels placed in the last frame.</summary>
    internal int LabelsPlaced => _labels.Count;

    /// <summary>Path tiles drawn since this renderer was made: grows on zoom, on changed trains and as new ground comes into view.</summary>
    internal int TilesRendered => _tiles.Rendered;

    /// <summary>
    /// Keep the train lines as bitmap tiles between frames, so panning does not stroke them again (the default, for
    /// on-screen use). <see cref="RenderPng"/> always draws directly.
    /// </summary>
    public bool CacheTiles { get; init; } = true;

    /// <summary>Renders to a new PNG of the viewport's size times <paramref name="scale"/> (2 for a sharper export).</summary>
    public byte[] RenderPng(GraphScene scene, GraphViewport viewport, GraphSelection? selection = null, float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 0.25f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 8f);
        var width = (int)Math.Ceiling(viewport.Width * scale);
        var height = (int)Math.Ceiling(viewport.Height * scale);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException($"Cannot create a {width}×{height} surface.");
        surface.Canvas.Scale(scale);
        Render(surface.Canvas, scene, viewport, selection, tiles: false);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Renders to a one-page vector PDF (GR-10, S17): the viewport's time and corridor range laid out to fill
    /// <paramref name="page"/> (A3 landscape unless given) inside its margins. Text is real text in the embedded fonts.
    /// </summary>
    /// <param name="title">The document title in the PDF's properties, if any.</param>
    public byte[] RenderPdf(GraphScene scene, GraphViewport viewport, GraphSelection? selection = null, PdfPage? page = null, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        page ??= PdfPage.A3Landscape;
        var fitted = page.Fit(viewport);
        var metadata = SKDocumentPdfMetadata.Default;
        metadata.Title = title ?? scene.Title;
        metadata.Creator = "Stringline";
        metadata.Producer = "Stringline (SkiaSharp)";
        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream, metadata) ?? throw new InvalidOperationException("Cannot create a PDF document."))
        {
            var canvas = document.BeginPage(page.Width, page.Height);
            if (page.Margin > 0)
            {
                canvas.Clear(ToSk(Theme.Background));
            }

            canvas.Translate(page.Margin, page.Margin);
            canvas.ClipRect(new SKRect(0, 0, fitted.Width, fitted.Height));
            Render(canvas, scene, fitted, selection, tiles: false);
            document.EndPage();
            document.Close();
        }

        return stream.ToArray();
    }

    /// <param name="selection">A selected train to draw heavier while the rest dims, if any.</param>
    /// <param name="ghosts">
    /// Paths to draw over everything as a translucent preview, such as where a train being dragged would run (S12).
    /// They are drawn directly each frame, not cached, so they can change on every mouse move.
    /// </param>
    /// <param name="draft">A block or restriction being drawn with B+drag (S13), outlined over everything.</param>
    public void Render(SKCanvas canvas, GraphScene scene, GraphViewport viewport, GraphSelection? selection = null, IReadOnlyList<GraphPath>? ghosts = null, GraphDisruption? draft = null) =>
        Render(canvas, scene, viewport, selection, CacheTiles, ghosts, draft);

    internal void Render(SKCanvas canvas, GraphScene scene, GraphViewport viewport, GraphSelection? selection, bool tiles, IReadOnlyList<GraphPath>? ghosts = null, GraphDisruption? draft = null)
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
        foreach (var disruption in scene.Disruptions)
        {
            DrawDisruption(canvas, disruption, viewport, plot);
        }

        DrawConflictHighlights(canvas, scene, viewport, plot, selection?.ConflictId);
        DrawPaths(canvas, scene, viewport, selection, tiles);
        DrawGhosts(canvas, ghosts, viewport);
        DrawNow(canvas, scene, viewport, plot);
        DrawHeadcodes(canvas, viewport, plot);
        DrawConflictRings(canvas, scene, viewport, plot, selection?.ConflictId);
        DrawNotes(canvas, scene, viewport, plot);
        if (draft is not null)
        {
            DrawDisruption(canvas, draft, viewport, plot);
        }

        canvas.Restore();

        canvas.DrawRect(plot, Stroke(Theme.Border, 1));
        DrawTimeLabels(canvas, ticks, viewport, plot);
        DrawLocationLabels(canvas, scene, viewport, plot);
        DrawTitle(canvas, scene);
    }

    /// <summary>A line across the plot at a time pixel: vertical when time runs across, horizontal when it runs down.</summary>
    private static void LineAtTime(SKCanvas canvas, GraphViewport viewport, SKRect plot, float at, SKPaint paint)
    {
        if (viewport.IsVertical)
        {
            canvas.DrawLine(plot.Left, at, plot.Right, at, paint);
        }
        else
        {
            canvas.DrawLine(at, plot.Top, at, plot.Bottom, paint);
        }
    }

    /// <summary>A line across the plot at an axis pixel (a location line).</summary>
    private static void LineAtAxis(SKCanvas canvas, GraphViewport viewport, SKRect plot, float at, SKPaint paint)
    {
        if (viewport.IsVertical)
        {
            canvas.DrawLine(at, plot.Top, at, plot.Bottom, paint);
        }
        else
        {
            canvas.DrawLine(plot.Left, at, plot.Right, at, paint);
        }
    }

    private void DrawPast(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        if (scene.Now is not { } now)
        {
            return;
        }

        if (viewport.IsVertical)
        {
            var y = Math.Clamp((float)viewport.TimeToPixel(now.Seconds), plot.Top, plot.Bottom);
            if (y > plot.Top)
            {
                canvas.DrawRect(new SKRect(plot.Left, plot.Top, plot.Right, y), Fill(Theme.PastShade));
            }
        }
        else
        {
            var x = Math.Clamp((float)viewport.TimeToPixel(now.Seconds), plot.Left, plot.Right);
            if (x > plot.Left)
            {
                canvas.DrawRect(new SKRect(plot.Left, plot.Top, x, plot.Bottom), Fill(Theme.PastShade));
            }
        }
    }

    private void DrawTimeGrid(SKCanvas canvas, IReadOnlyList<TimeTick> ticks, GraphViewport viewport, SKRect plot)
    {
        foreach (var tick in ticks)
        {
            var paint = tick.Weight switch
            {
                TickWeight.Hour => Stroke(Theme.HourGrid, 1),
                TickWeight.Major => Stroke(Theme.MajorGrid, 1),
                _ => Stroke(Theme.MinorGrid, 1),
            };
            LineAtTime(canvas, viewport, plot, Crisp(viewport.TimeToPixel(tick.Time)), paint);
        }
    }

    private void DrawLocationLines(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        foreach (var location in scene.Locations)
        {
            var paint = location.IsStation ? Stroke(Theme.StationLine, 1) : Stroke(Theme.LocationLine, 1);
            LineAtAxis(canvas, viewport, plot, Crisp(viewport.AxisToPixel(location.Axis)), paint);
        }
    }

    /// <summary>
    /// Draws the paths in view, layer by layer, from tiles when <paramref name="tiles"/> allows, then the selected
    /// train on top; leaves the paths in view in <see cref="_drawn"/> for labelling, the selected ones first.
    /// </summary>
    private void DrawPaths(SKCanvas canvas, GraphScene scene, GraphViewport viewport, GraphSelection? selection, bool tiles)
    {
        _drawn.Clear();
        _selected.Clear();
        // A frame that changes the projection (a zoom step) draws directly: building tiles costs about two direct frames,
        // so they are built on the next frame at the same zoom, when panning or following begins to reuse them.
        var generation = _paths.Generation;
        var offset = _paths.Begin(scene, viewport, canvas.TotalMatrix);
        tiles &= _paths.Generation == generation;
        foreach (var layer in Layers)
        {
            foreach (var p in scene.Paths)
            {
                if (p.Layer == layer && viewport.Overlaps(p.TimeMin, p.TimeMax, p.AxisMin, p.AxisMax))
                {
                    (selection?.Matches(p) == true ? _selected : _drawn).Add(p);
                }
            }
        }

        if (!tiles || !_tiles.Draw(canvas, scene, viewport, selection, _paths, offset, _drawPath))
        {
            _tiles.Invalidate(_paths);
            canvas.Save();
            canvas.Translate(offset);
            foreach (var p in _drawn)
            {
                _drawPath(canvas, p, selection is not null);
            }

            canvas.Restore();
        }

        // The selected train on top: a halo, then a heavy opaque line (05-…md §2).
        canvas.Save();
        canvas.Translate(offset);
        foreach (var p in _selected)
        {
            var width = Math.Max(Theme.StyleOf(p.Layer).Width, p.Layer == GraphLayer.Wtt ? Theme.SelectedWttWidth : Theme.SelectedWidth);
            var runs = _paths.Get(p);
            foreach (var run in runs)
            {
                canvas.DrawPath(run.Path, Stroke(Theme.Halo, width + (2 * Theme.HaloWidth)));
            }

            foreach (var run in runs)
            {
                canvas.DrawPath(run.Path, Stroke(run.Colour with { A = 0xFF }, width));
            }
        }

        canvas.Restore();
        _drawn.InsertRange(0, _selected);
    }

    /// <summary>The layers in drawing order.</summary>
    internal static readonly GraphLayer[] Layers = Enum.GetValues<GraphLayer>();

    /// <summary>Strokes one path's runs, in cache coordinates (see <see cref="ProjectedPathCache"/>).</summary>
    private void DrawPath(SKCanvas canvas, GraphPath path, bool dim)
    {
        foreach (var run in _paths.Get(path))
        {
            canvas.DrawPath(run.Path, PathPaint(run.Colour, path.Layer, dim, path.IsCancelled, path.Style));
        }
    }

    /// <summary>Ghost paths, projected afresh: a wide translucent line with a halo, so the preview reads over the dimmed graph.</summary>
    private void DrawGhosts(SKCanvas canvas, IReadOnlyList<GraphPath>? ghosts, GraphViewport viewport)
    {
        if (ghosts is not { Count: > 0 })
        {
            return;
        }

        using var builder = new SKPathBuilder();
        foreach (var ghost in ghosts)
        {
            if (ghost.Vertices.Count < 2)
            {
                continue;
            }

            builder.Reset();
            for (var i = 0; i < ghost.Vertices.Count; i++)
            {
                var v = ghost.Vertices[i];
                var p = viewport.ToScreen(v.Time, v.Axis);
                if (i == 0)
                {
                    builder.MoveTo((float)p.X, (float)p.Y);
                }
                else
                {
                    builder.LineTo((float)p.X, (float)p.Y);
                }
            }

            using var path = builder.Snapshot();
            var style = Theme.Ghost;
            canvas.DrawPath(path, Stroke(Theme.Halo, style.Width + (2 * Theme.HaloWidth)));
            canvas.DrawPath(path, Stroke(ghost.Colour with { A = (byte)Math.Round(ghost.Colour.A * style.Opacity) }, style.Width));
        }
    }

    /// <summary>
    /// A block as a hatched rectangle, a speed restriction as an amber band (<c>05-…md</c> §2), each with a thin border
    /// and its label in the corner; a draft (B+drag) has a dashed border and its times in the label.
    /// </summary>
    private void DrawDisruption(SKCanvas canvas, GraphDisruption disruption, GraphViewport viewport, SKRect plot)
    {
        var rect = GraphAnnotations.RectOf(disruption, viewport).ToSk();
        if (!rect.IntersectsWith(plot))
        {
            return;
        }

        var block = disruption.Kind == DisruptionKind.Block;
        var colour = block ? Theme.Block : Theme.Restriction;
        canvas.DrawRect(rect, Fill(colour with { A = (byte)(block ? 0x1A : 0x40) }));
        if (block)
        {
            canvas.Save();
            canvas.ClipRect(rect);
            var hatch = Stroke(colour with { A = 0x70 }, 1);
            var visible = SKRect.Intersect(rect, plot);
            var height = visible.Height;
            var spacing = Theme.HatchSpacing;
            // Lines at 45°, on a grid from the rectangle's corner so the hatching moves with it as the view pans.
            var first = rect.Left + (MathF.Floor((visible.Left - height - rect.Left) / spacing) * spacing);
            for (var x = first; x <= visible.Right; x += spacing)
            {
                canvas.DrawLine(x, visible.Bottom, x + height, visible.Top, hatch);
            }

            canvas.Restore();
        }

        SKPaint border;
        if (disruption.IsDraft)
        {
            border = _draftBorder ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, PathEffect = _dash };
            border.Color = ToSk(colour);
        }
        else
        {
            border = Stroke(colour with { A = 0xC0 }, 1);
        }

        canvas.DrawRect(rect, border);
        var text = disruption.IsDraft ? $"{disruption.Label}  {disruption.Disruption.Start}–{disruption.Disruption.End}" : disruption.Label;
        var width = _annotationFont.MeasureText(text);
        var left = Math.Max(rect.Left, plot.Left) + 3;
        var top = Math.Max(rect.Top, plot.Top);
        if (left + width + 3 <= rect.Right && rect.Bottom - top >= GraphAnnotations.LabelFontSize + 3)
        {
            canvas.DrawText(text, left, top + GraphAnnotations.LabelFontSize + 1, SKTextAlign.Left, _annotationFont, Fill(Darken(colour)));
        }
    }

    /// <summary>Each conflict's section (or location) over the time it covers, in translucent red under the trains; the selected one stronger.</summary>
    private void DrawConflictHighlights(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot, string? selected)
    {
        foreach (var conflict in scene.Conflicts)
        {
            var rect = GraphConflicts.RectOf(conflict, viewport).ToSk();
            if (!rect.IntersectsWith(plot))
            {
                continue;
            }

            var isSelected = conflict.Id == selected;
            canvas.DrawRect(rect, Fill(Theme.Conflict with { A = (byte)(isSelected ? 0x40 : 0x1C) }));
            if (isSelected)
            {
                canvas.DrawRect(rect, Stroke(Theme.Conflict with { A = 0xC0 }, 1));
            }
        }
    }

    /// <summary>A red ring where each conflict's train meets it (<c>05-…md</c> §2), over the trains; the selected one heavier.</summary>
    private void DrawConflictRings(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot, string? selected)
    {
        foreach (var conflict in scene.Conflicts)
        {
            var ring = GraphConflicts.RingOf(conflict, viewport);
            float x = (float)ring.X, y = (float)ring.Y, r = GraphConflicts.RingRadius;
            if (x + r < plot.Left || x - r > plot.Right || y + r < plot.Top || y - r > plot.Bottom)
            {
                continue;
            }

            var isSelected = conflict.Id == selected;
            canvas.DrawCircle(x, y, r + 1.5f, Stroke(Theme.Halo, 2));
            canvas.DrawCircle(x, y, isSelected ? r + 2 : r, Stroke(Theme.Conflict, isSelected ? 3 : 2));
        }
    }

    /// <summary>Notes: a marker at the time and location, and the first line of the text in a pale box beside it.</summary>
    private void DrawNotes(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        foreach (var note in scene.Notes)
        {
            var box = GraphAnnotations.LabelOf(note, viewport).ToSk();
            var marker = GraphAnnotations.MarkerOf(note, viewport);
            var bounds = SKRect.Union(box, new SKRect((float)marker.X - 5, (float)marker.Y - 5, (float)marker.X + 5, (float)marker.Y + 5));
            if (!bounds.IntersectsWith(plot))
            {
                continue;
            }

            canvas.DrawRoundRect(box, 2, 2, Fill(Theme.NoteFill));
            canvas.DrawRoundRect(box, 2, 2, Stroke(Theme.NoteBorder, 1));
            canvas.DrawText(note.Label, box.Left + 4, box.Bottom - 5, SKTextAlign.Left, _annotationFont, Fill(Theme.Text));
            canvas.DrawCircle((float)marker.X, (float)marker.Y, GraphAnnotations.NoteMarkerRadius, Fill(Theme.NoteFill));
            canvas.DrawCircle((float)marker.X, (float)marker.Y, GraphAnnotations.NoteMarkerRadius, Stroke(Theme.NoteBorder, 1.5f));
        }
    }

    private void DrawNow(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        if (scene.Now is { } now && now.Seconds >= viewport.TimeStart && now.Seconds <= viewport.TimeEnd)
        {
            LineAtTime(canvas, viewport, plot, (float)viewport.TimeToPixel(now.Seconds), Stroke(Theme.NowLine, 1.5f));
        }
    }

    /// <summary>
    /// One headcode per train, along its line at the first place that is long enough and does not overlap a label
    /// already placed (checked against <see cref="LabelGrid"/>). A train's actual path is labelled in preference to
    /// its WTT path; a label takes the colour of the segment it sits on.
    /// </summary>
    private void DrawHeadcodes(SKCanvas canvas, GraphViewport viewport, SKRect plot)
    {
        _withActuals.Clear();
        foreach (var p in _drawn)
        {
            if (p.Layer == GraphLayer.Actual && p.Train is { } train)
            {
                _withActuals.Add(train);
            }
        }

        _labels.Reset(plot);

        // Actual paths first, then WTT; within each, the selected train (which comes first) keeps its place.
        foreach (var layer in (ReadOnlySpan<GraphLayer>)[GraphLayer.Actual, GraphLayer.Wtt])
        {
            foreach (var p in _drawn)
            {
                if (p.Layer != layer || (layer == GraphLayer.Wtt && p.Train is { } t && _withActuals.Contains(t)))
                {
                    continue;
                }

                var text = p.Headcode.Value;
                var width = _headcodeFont.MeasureText(text);
                if (FindLabelPlace(p, width, viewport, plot) is not { } place)
                {
                    continue;
                }

                canvas.Save();
                canvas.Translate(place.X, place.Y);
                canvas.RotateDegrees(place.Degrees);
                canvas.DrawText(text, 0, -3, SKTextAlign.Center, _headcodeFont, Fill(Darken(p.ColourOf(place.Segment))));
                canvas.Restore();
                _labels.Add(place.Bounds);
            }
        }
    }

    private readonly record struct LabelPlace(float X, float Y, float Degrees, SKRect Bounds, int Segment);

    private LabelPlace? FindLabelPlace(GraphPath path, float textWidth, GraphViewport viewport, SKRect plot)
    {
        var height = _headcodeFont.Size;
        for (var i = 0; i + 1 < path.Vertices.Count; i++)
        {
            var v0 = path.Vertices[i];
            var v1 = path.Vertices[i + 1];
            if (v0.Axis == v1.Axis || !viewport.Overlaps(Math.Min(v0.Time, v1.Time), Math.Max(v0.Time, v1.Time), Math.Min(v0.Axis, v1.Axis), Math.Max(v0.Axis, v1.Axis)))
            {
                continue; // a stop, or out of view
            }

            if (ClipToRect(viewport.ToScreen(v0.Time, v0.Axis), viewport.ToScreen(v1.Time, v1.Axis), plot) is not var (c, d))
            {
                continue;
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
                if (plot.Contains(bounds) && !_labels.Intersects(bounds))
                {
                    return new LabelPlace(x, y, degrees, bounds, i);
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

            var at = Crisp(viewport.TimeToPixel(tick.Time));
            var font = tick.Weight == TickWeight.Hour ? _hourFont : _timeFont;
            if (viewport.IsVertical)
            {
                // Left of the plot, right-aligned, centred on the line.
                canvas.DrawLine(plot.Left - 4, at, plot.Left, at, Stroke(Theme.Border, 1));
                var half = font.Size / 2;
                if (at - half >= plot.Top - 2 && at + half <= plot.Bottom + 2)
                {
                    canvas.DrawText(tick.Label, plot.Left - 7, at + 4, SKTextAlign.Right, font, Fill(Theme.Text));
                }
            }
            else
            {
                var half = _timeFont.MeasureText(tick.Label) / 2;
                canvas.DrawLine(at, plot.Top - 4, at, plot.Top, Stroke(Theme.Border, 1));
                if (at - half >= plot.Left - 2 && at + half <= plot.Right + 2)
                {
                    canvas.DrawText(tick.Label, at, plot.Top - 8, SKTextAlign.Center, font, Fill(Theme.Text));
                }
            }
        }
    }

    /// <summary>
    /// Location names beside the plot: on the left, reading across, when time runs across; along the top, reading up,
    /// when it runs down. The TIPLOC goes under (or beside) a display name. A label too close to the previous one is
    /// left out.
    /// </summary>
    private void DrawLocationLabels(SKCanvas canvas, GraphScene scene, GraphViewport viewport, SKRect plot)
    {
        var vertical = viewport.IsVertical;
        var low = (float)viewport.AxisOrigin;
        var high = (float)(viewport.AxisOrigin + viewport.AxisLength);
        var lastEnd = float.NegativeInfinity;
        if (vertical)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(0, TitleBand, viewport.Width, plot.Top - 2));
        }

        foreach (var location in scene.Locations.OrderBy(l => l.Axis))
        {
            var at = (float)viewport.AxisToPixel(location.Axis);
            if (at < low - 1 || at > high + 1 || at - _locationFont.Size < lastEnd)
            {
                continue; // out of view, or too close to the previous label
            }

            var hasTiploc = !string.Equals(location.Name, location.Tiploc.Code, StringComparison.Ordinal);
            var font = location.IsStation ? _stationFont : _locationFont;
            var tiplocOffset = 4 + _tiplocFont.Size + 1;
            if (vertical)
            {
                var x = Crisp(at);
                canvas.DrawLine(x, plot.Top - 4, x, plot.Top, Stroke(Theme.Border, 1));
                canvas.Save();
                canvas.Translate(at, plot.Top - 6);
                canvas.RotateDegrees(-90);
                canvas.DrawText(location.Name, 0, 4, SKTextAlign.Left, font, Fill(Theme.Text));
                if (hasTiploc)
                {
                    canvas.DrawText(location.Tiploc.Code, 0, tiplocOffset, SKTextAlign.Left, _tiplocFont, Fill(Theme.MutedText));
                }

                canvas.Restore();
            }
            else
            {
                var right = plot.Left - 6;
                var y = Crisp(at);
                canvas.DrawLine(right + 2, y, plot.Left, y, Stroke(Theme.Border, 1));
                canvas.DrawText(location.Name, right, at + 4, SKTextAlign.Right, font, Fill(Theme.Text));
                if (hasTiploc)
                {
                    canvas.DrawText(location.Tiploc.Code, right, at + tiplocOffset, SKTextAlign.Right, _tiplocFont, Fill(Theme.MutedText));
                }
            }

            lastEnd = at + 5 + (hasTiploc ? _tiplocFont.Size + 1 : 0);
        }

        if (vertical)
        {
            canvas.Restore();
        }
    }

    private void DrawTitle(SKCanvas canvas, GraphScene scene)
    {
        var text = scene.Now is { } now ? $"{scene.Title}   {now}" : scene.Title;
        canvas.DrawText(text, 8, 17, SKTextAlign.Left, _titleFont, Fill(Theme.Text));
    }

    private SKPaint PathPaint(Colour colour, GraphLayer layer, bool dim, bool cancelled = false, LineStyle lineStyle = LineStyle.Solid)
    {
        if (!_pathPaints.TryGetValue((colour, layer, dim, cancelled, lineStyle), out var paint))
        {
            var style = cancelled ? Theme.Cancelled : Theme.StyleOf(layer);
            paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = style.Width,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                Color = ToSk(colour with { A = (byte)Math.Round(colour.A * style.Opacity * (dim ? Theme.DimOpacity : 1)) }),
                PathEffect = style.Dashed ? _dash : CategoryDash(lineStyle, style.Width),
            };
            _pathPaints[(colour, layer, dim, cancelled, lineStyle)] = paint;
        }

        return paint;
    }

    /// <summary>
    /// A category's dashes (S14a): 12–7, less the round caps, longer than the forecast's 6–4 dash so a dashed
    /// category's actual line does not read as a forecast; dots are round caps spaced two widths apart. Kept per
    /// pattern and width.
    /// </summary>
    private SKPathEffect? CategoryDash(LineStyle lineStyle, float width)
    {
        if (lineStyle == LineStyle.Solid)
        {
            return null;
        }

        if (!_categoryDashes.TryGetValue((lineStyle, width), out var effect))
        {
            effect = lineStyle == LineStyle.Dotted
                ? SKPathEffect.CreateDash([0.01f, Math.Max(3f, width * 2)], 0)
                : SKPathEffect.CreateDash([12, 7], 0);
            _categoryDashes[(lineStyle, width)] = effect;
        }

        return effect;
    }

    private SKPaint Stroke(Colour colour, float width)
    {
        if (!_strokes.TryGetValue((colour, width), out var paint))
        {
            paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeJoin = SKStrokeJoin.Round, Color = ToSk(colour) };
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
        _paths.Dispose();
        _tiles.Dispose();
        _locationFont.Dispose();
        _stationFont.Dispose();
        _tiplocFont.Dispose();
        _timeFont.Dispose();
        _hourFont.Dispose();
        _headcodeFont.Dispose();
        _titleFont.Dispose();
        _annotationFont.Dispose();
        _draftBorder?.Dispose();
        _dash.Dispose();
        foreach (var effect in _categoryDashes.Values)
        {
            effect.Dispose();
        }

        _categoryDashes.Clear();
    }
}
