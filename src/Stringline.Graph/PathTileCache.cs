using SkiaSharp;

namespace Stringline.Graph;

/// <summary>
/// The train lines already drawn, kept as bitmap tiles of <see cref="TileSize"/> device pixels (<c>05-…md</c> §7), so a
/// frame that only pans blits tiles instead of stroking every path again (GR-9: anti-aliased stroking is most of a
/// frame's cost).
/// </summary>
/// <remarks>
/// <para>
/// Tiles live in the <see cref="ProjectedPathCache"/>'s coordinates at the canvas's device scale, so panning only moves
/// them. The projection is aligned so that tiles land on whole device pixels, exactly where a direct draw would put the
/// lines; after a pan by a fraction of a pixel they are placed on the nearest whole pixel, up to half a pixel out (the
/// grid, now line and labels are not snapped). A frame at a new zoom draws directly and the next one builds the tiles,
/// so a zoom gesture costs no more than drawing directly.
/// </para>
/// <para>
/// All tiles are dropped on a new projection (zoom, resize, orientation), a new selection (others dim) or device scale.
/// On a new scene only the tiles where paths changed are dropped (<see cref="ProjectedPathCache.TakeDirty"/>), so a
/// live update usually redraws one or two tiles near the now line. Tiles are drawn lazily as they come into view, and
/// the least recently used are dropped beyond a limit. The selected train is never in a tile; the renderer draws it on
/// top each frame. Not thread-safe; the renderer owns it.
/// </para>
/// </remarks>
internal sealed class PathTileCache : IDisposable
{
    /// <summary>Tile edge in device pixels.</summary>
    public const int TileSize = 256;

    /// <summary>How far outside a tile, in cache units, a path may be and still draw into it (half the widest line, and some).</summary>
    private const float Bleed = 4;

    /// <summary>Fewest tiles kept, whatever is in view.</summary>
    private const int MinTiles = 64;

    private readonly Dictionary<(int Column, int Row), Tile> _tiles = [];
    private readonly List<(int Column, int Row)> _missing = [];
    private readonly List<SKRect> _inBox = [];
    private long _frame;
    private int _generation = -1;
    private GraphSelection? _selection;
    private float _scale;

    /// <summary>Tiles drawn since the cache was made (for tests and the benchmark).</summary>
    public int Rendered { get; private set; }

    public int Count => _tiles.Count;

    /// <summary>
    /// Draws the unselected paths of <paramref name="scene"/> onto <paramref name="canvas"/> from tiles, drawing any
    /// missing ones with <paramref name="draw"/> (which strokes one path, dimmed or not, in cache coordinates). Returns
    /// false, drawing nothing, if the canvas is rotated or skewed, so the caller must draw directly.
    /// </summary>
    /// <param name="offset">The cache offset from <see cref="ProjectedPathCache.Begin"/> this frame.</param>
    public bool Draw(SKCanvas canvas, GraphScene scene, GraphViewport viewport, GraphSelection? selection, ProjectedPathCache paths, SKPoint offset,
        Action<SKCanvas, GraphPath, bool> draw)
    {
        var m = canvas.TotalMatrix;
        if (m.SkewX != 0 || m.SkewY != 0 || m.ScaleX <= 0 || Math.Abs(m.ScaleX - m.ScaleY) > 1e-6 || m.Persp0 != 0 || m.Persp1 != 0)
        {
            return false;
        }

        var scale = m.ScaleX;
        if (paths.Generation != _generation || selection != _selection || scale != _scale)
        {
            Clear();
            _generation = paths.Generation;
            _selection = selection;
            _scale = scale;
            paths.TakeDirty();
        }
        else
        {
            Invalidate(paths);
        }

        _frame++;
        var snapX = MathF.Round(m.TransX + (offset.X * scale));
        var snapY = MathF.Round(m.TransY + (offset.Y * scale));
        var plot = m.MapRect(new SKRect((float)viewport.PlotLeft, (float)viewport.PlotTop, (float)viewport.PlotRight, (float)viewport.PlotBottom));
        var c0 = (int)Math.Floor((plot.Left - snapX) / TileSize);
        var c1 = (int)Math.Floor((plot.Right - snapX - 1) / TileSize);
        var r0 = (int)Math.Floor((plot.Top - snapY) / TileSize);
        var r1 = (int)Math.Floor((plot.Bottom - snapY - 1) / TileSize);

        _missing.Clear();
        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                if (!_tiles.ContainsKey((c, r)))
                {
                    _missing.Add((c, r));
                }
            }
        }

        if (_missing.Count > 0)
        {
            RenderMissing(scene, selection, paths, draw);
        }

        canvas.Save();
        canvas.ResetMatrix();
        canvas.ClipRect(plot);
        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                var tile = _tiles[(c, r)];
                tile.LastUsed = _frame;
                if (tile.Image is { } image)
                {
                    canvas.DrawImage(image, (c * TileSize) + snapX, (r * TileSize) + snapY, SKSamplingOptions.Default);
                }
            }
        }

        canvas.Restore();
        var visible = (c1 - c0 + 1) * (r1 - r0 + 1);
        Evict(Math.Max(MinTiles, visible + (visible / 2)));
        return true;
    }

    /// <summary>
    /// Draws the missing tiles in one pass over the box around them, then cuts the box into tiles: stroking each path
    /// once rather than once per tile it crosses. Tiles no path touches get no image.
    /// </summary>
    private void RenderMissing(GraphScene scene, GraphSelection? selection, ProjectedPathCache paths, Action<SKCanvas, GraphPath, bool> draw)
    {
        int bc0 = int.MaxValue, bc1 = int.MinValue, br0 = int.MaxValue, br1 = int.MinValue;
        foreach (var (c, r) in _missing)
        {
            (bc0, bc1, br0, br1) = (Math.Min(bc0, c), Math.Max(bc1, c), Math.Min(br0, r), Math.Max(br1, r));
        }

        var box = Area(bc0, br0, bc1, br1);
        using var bitmap = new SKBitmap(new SKImageInfo((bc1 - bc0 + 1) * TileSize, (br1 - br0 + 1) * TileSize, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-bc0 * TileSize, -br0 * TileSize);
            canvas.Scale(_scale);
            _inBox.Clear();
            foreach (var layer in SkiaGraphRenderer.Layers)
            {
                foreach (var path in scene.Paths)
                {
                    if (path.Layer == layer && path.Vertices.Count >= 2 && selection?.Matches(path) != true && paths.Bounds(path) is var bounds && bounds.IntersectsWith(box))
                    {
                        draw(canvas, path, selection is not null);
                        _inBox.Add(bounds);
                    }
                }
            }
        }

        using var pixels = bitmap.PeekPixels();
        foreach (var (c, r) in _missing)
        {
            var area = Area(c, r, c, r);
            SKImage? image = null;
            if (_inBox.Exists(b => b.IntersectsWith(area)))
            {
                using var tile = pixels.ExtractSubset(SKRectI.Create((c - bc0) * TileSize, (r - br0) * TileSize, TileSize, TileSize));
                image = SKImage.FromPixelCopy(tile);
            }

            _tiles[(c, r)] = new Tile(image);
            Rendered++;
        }
    }

    /// <summary>The cache-coordinate area tiles (c0, r0)–(c1, r1) draw, with the bleed.</summary>
    private SKRect Area(int c0, int r0, int c1, int r1) => new(
        (c0 * TileSize / _scale) - Bleed,
        (r0 * TileSize / _scale) - Bleed,
        ((c1 + 1) * TileSize / _scale) + Bleed,
        ((r1 + 1) * TileSize / _scale) + Bleed);

    /// <summary>
    /// Drops the tiles under whatever <paramref name="paths"/> reports as drawn differently since it was last asked.
    /// Call it on frames drawn without tiles too, so tiles kept from earlier frames do not go stale.
    /// </summary>
    public void Invalidate(ProjectedPathCache paths)
    {
        var dirty = paths.TakeDirty();
        if (paths.Generation != _generation)
        {
            return; // every tile goes at the next Draw anyway
        }

        foreach (var rect in dirty)
        {
            DropTilesUnder(paths.Bounds(rect.TimeMin, rect.TimeMax, rect.AxisMin, rect.AxisMax));
        }
    }

    private void DropTilesUnder(SKRect bounds)
    {
        bounds.Inflate(Bleed, Bleed);
        var c0 = (int)Math.Floor(bounds.Left * _scale / TileSize);
        var c1 = (int)Math.Floor(bounds.Right * _scale / TileSize);
        var r0 = (int)Math.Floor(bounds.Top * _scale / TileSize);
        var r1 = (int)Math.Floor(bounds.Bottom * _scale / TileSize);
        if ((long)(c1 - c0 + 1) * (r1 - r0 + 1) > _tiles.Count)
        {
            foreach (var key in _tiles.Keys.Where(k => k.Column >= c0 && k.Column <= c1 && k.Row >= r0 && k.Row <= r1).ToList())
            {
                Remove(key);
            }

            return;
        }

        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                Remove((c, r));
            }
        }
    }

    private void Evict(int keep)
    {
        if (_tiles.Count <= keep)
        {
            return;
        }

        foreach (var key in _tiles.OrderBy(t => t.Value.LastUsed).Take(_tiles.Count - keep).Select(t => t.Key).ToList())
        {
            Remove(key);
        }
    }

    private void Remove((int, int) key)
    {
        if (_tiles.Remove(key, out var tile))
        {
            tile.Image?.Dispose();
        }
    }

    private void Clear()
    {
        foreach (var tile in _tiles.Values)
        {
            tile.Image?.Dispose();
        }

        _tiles.Clear();
    }

    public void Dispose() => Clear();

    private sealed class Tile(SKImage? image)
    {
        public SKImage? Image { get; } = image;

        public long LastUsed { get; set; }
    }
}
