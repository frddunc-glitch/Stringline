using SkiaSharp;

namespace Stringline.Graph;

/// <summary>
/// The headcode labels placed so far in a frame, in a uniform grid of cells (<c>05-…md</c> §7), so checking a new
/// label for overlap looks only at labels in the cells it covers rather than at every label. Reused between frames.
/// </summary>
internal sealed class LabelGrid
{
    /// <summary>Cell size in pixels: a little more than a typical headcode label is long.</summary>
    public const float CellSize = 48;

    private List<SKRect>[] _cells = [];
    private SKRect _area;
    private int _columns;
    private int _rows;

    public int Count { get; private set; }

    /// <summary>Empties the grid for a new frame covering <paramref name="area"/>.</summary>
    public void Reset(SKRect area)
    {
        _area = area;
        _columns = Math.Max(1, (int)Math.Ceiling(area.Width / CellSize));
        _rows = Math.Max(1, (int)Math.Ceiling(area.Height / CellSize));
        var needed = _columns * _rows;
        if (_cells.Length < needed)
        {
            var grown = new List<SKRect>[needed];
            Array.Copy(_cells, grown, _cells.Length);
            for (var i = _cells.Length; i < needed; i++)
            {
                grown[i] = [];
            }

            _cells = grown;
        }

        for (var i = 0; i < needed; i++)
        {
            _cells[i].Clear();
        }

        Count = 0;
    }

    /// <summary>True if <paramref name="rect"/> overlaps a label already added.</summary>
    public bool Intersects(SKRect rect)
    {
        var (c0, r0, c1, r1) = CellsOf(rect);
        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                foreach (var placed in _cells[(r * _columns) + c])
                {
                    if (placed.IntersectsWith(rect))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void Add(SKRect rect)
    {
        var (c0, r0, c1, r1) = CellsOf(rect);
        for (var r = r0; r <= r1; r++)
        {
            for (var c = c0; c <= c1; c++)
            {
                _cells[(r * _columns) + c].Add(rect);
            }
        }

        Count++;
    }

    /// <summary>The cells a rectangle covers, clamped to the grid (rectangles outside it share the edge cells).</summary>
    private (int C0, int R0, int C1, int R1) CellsOf(SKRect rect) => (
        Column(rect.Left), Row(rect.Top), Column(rect.Right), Row(rect.Bottom));

    private int Column(float x) => Math.Clamp((int)Math.Floor((x - _area.Left) / CellSize), 0, _columns - 1);

    private int Row(float y) => Math.Clamp((int)Math.Floor((y - _area.Top) / CellSize), 0, _rows - 1);
}
