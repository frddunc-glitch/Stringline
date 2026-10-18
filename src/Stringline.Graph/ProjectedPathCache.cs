using SkiaSharp;
using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>One single-colour stretch of a projected path.</summary>
internal readonly record struct PathRun(Colour Colour, SKPath Path);

/// <summary>A rectangle in world coordinates: sim seconds by axis position.</summary>
internal readonly record struct WorldRect(double TimeMin, double TimeMax, double AxisMin, double AxisMax)
{
    public static WorldRect Of(GraphPath path) => new(path.TimeMin, path.TimeMax, path.AxisMin, path.AxisMax);

    /// <summary>The bounds of the vertices from <paramref name="start"/> on.</summary>
    public static WorldRect Of(IReadOnlyList<GraphVertex> vertices, int start)
    {
        double t0 = double.MaxValue, t1 = double.MinValue, a0 = double.MaxValue, a1 = double.MinValue;
        for (var i = start; i < vertices.Count; i++)
        {
            t0 = Math.Min(t0, vertices[i].Time);
            t1 = Math.Max(t1, vertices[i].Time);
            a0 = Math.Min(a0, vertices[i].Axis);
            a1 = Math.Max(a1, vertices[i].Axis);
        }

        return new WorldRect(t0, t1, a0, a1);
    }
}

/// <summary>
/// The renderer's <see cref="SKPath"/>s for a scene's paths (<c>05-…md</c> §7), projected once per zoom and reused
/// while the view only pans: panning, following the now line and scrolling along the corridor are translations, so
/// the cached paths are drawn with the canvas translated by <see cref="Begin"/>'s offset.
/// </summary>
/// <remarks>
/// Paths are projected lazily, the first time they come into view, at the current scale (pixels per second and per
/// axis unit) relative to a reference time. A new scale or orientation drops them all. A new scene keeps the paths
/// whose key, colours and vertices are unchanged, so a live update re-projects only the trains that moved. Not
/// thread-safe; the renderer owns it.
/// </remarks>
internal sealed class ProjectedPathCache : IDisposable
{
    /// <summary>How far the view may pan from the reference time, in pixels, before paths are re-projected (float precision).</summary>
    private const double MaxOffsetPixels = 100_000;

    private readonly SKPathBuilder _builder = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private IReadOnlyList<GraphPath>? _paths;
    private bool _hasProjection;
    private GraphOrientation _orientation;
    private double _pixelsPerSecond;
    private double _pixelsPerAxis;
    private double _referenceTime;
    private double _axisPhase;
    private float _deviceScale;

    private readonly List<WorldRect> _dirty = [];

    /// <summary>How many paths have been projected since the cache was made (for tests and the benchmark).</summary>
    public int Projected { get; private set; }

    /// <summary>Changes whenever every projection is dropped (a new scale, orientation or reference time).</summary>
    public int Generation { get; private set; }

    /// <summary>Paths currently projected.</summary>
    public int Count => _entries.Count(e => e.Value.Runs is not null);

    /// <summary>
    /// Gets ready to draw <paramref name="scene"/> through <paramref name="viewport"/>, and returns the canvas
    /// translation to draw the cached paths with.
    /// </summary>
    /// <param name="device">
    /// The canvas's scale and translation to device pixels. A new projection is aligned so that the returned
    /// offset falls on whole device pixels, so tiles of the cached paths (<see cref="PathTileCache"/>) draw exactly
    /// where the paths would, at least until the view pans by a fraction of a pixel.
    /// </param>
    public SKPoint Begin(GraphScene scene, GraphViewport viewport, SKMatrix? device = null)
    {
        if (!ReferenceEquals(scene.Paths, _paths))
        {
            Rekey(scene.Paths);
        }

        var m = device ?? SKMatrix.Identity;
        var scale = m.ScaleX > 0 ? m.ScaleX : 1;
        var perSecond = viewport.PixelsPerSecond;
        var perAxis = viewport.PixelsPerAxis;
        if (!_hasProjection || viewport.Orientation != _orientation || !Same(perSecond, _pixelsPerSecond) || !Same(perAxis, _pixelsPerAxis) ||
            scale != _deviceScale || Math.Abs((_referenceTime - viewport.TimeStart) * perSecond) > MaxOffsetPixels)
        {
            DropProjections();
            Generation++;
            _dirty.Clear();
            _hasProjection = true;
            _orientation = viewport.Orientation;
            _pixelsPerSecond = perSecond;
            _pixelsPerAxis = perAxis;
            _deviceScale = scale;

            // Shift the reference time and the axis by the fractions of a device pixel that put the offsets on whole ones.
            var (timeTranslation, axisTranslation) = viewport.IsVertical ? (m.TransY, m.TransX) : (m.TransX, m.TransY);
            var timeDevice = (viewport.TimeOrigin * scale) + timeTranslation;
            _referenceTime = viewport.TimeStart + ((Math.Round(timeDevice) - timeDevice) / (perSecond * scale));
            var axisDevice = ((viewport.AxisOrigin - (viewport.AxisStart * perAxis)) * scale) + axisTranslation;
            _axisPhase = (axisDevice - Math.Round(axisDevice)) / scale;
        }

        var timeOffset = (float)(viewport.TimeOrigin + ((_referenceTime - viewport.TimeStart) * _pixelsPerSecond));
        var axisOffset = (float)(viewport.AxisOrigin - (viewport.AxisStart * _pixelsPerAxis) - _axisPhase);
        return viewport.IsVertical ? new SKPoint(axisOffset, timeOffset) : new SKPoint(timeOffset, axisOffset);
    }

    /// <summary>The path's runs at the current projection, projecting it if needed. Call after <see cref="Begin"/>.</summary>
    public IReadOnlyList<PathRun> Get(GraphPath path)
    {
        if (_entries.TryGetValue(path.Key, out var entry) && ReferenceEquals(entry.Path, path) && entry.Runs is { } runs)
        {
            return runs;
        }

        runs = Project(path);
        if (entry is not null && ReferenceEquals(entry.Path, path))
        {
            entry.Runs = runs;
        }
        else
        {
            Dispose(entry);
            _entries[path.Key] = new Entry(path) { Runs = runs };
        }

        return runs;
    }

    private PathRun[] Project(GraphPath path)
    {
        Projected++;
        var runs = new List<PathRun>(1);
        var vertices = path.Vertices;
        var start = 0;
        while (start + 1 < vertices.Count)
        {
            var colour = path.ColourOf(start);
            var end = start + 1;
            while (end + 1 < vertices.Count && path.ColourOf(end) == colour)
            {
                end++;
            }

            _builder.Reset();
            for (var i = start; i <= end; i++)
            {
                var p = Point(vertices[i]);
                if (i == start)
                {
                    _builder.MoveTo(p);
                }
                else
                {
                    _builder.LineTo(p);
                }
            }

            runs.Add(new PathRun(colour, _builder.Snapshot()));
            start = end;
        }

        return [.. runs];
    }

    private SKPoint Point(GraphVertex vertex) => Point(vertex.Time, vertex.Axis);

    private SKPoint Point(double time, double axis)
    {
        var t = (float)((time - _referenceTime) * _pixelsPerSecond);
        var a = (float)((axis * _pixelsPerAxis) + _axisPhase);
        return _orientation == GraphOrientation.Vertical ? new SKPoint(a, t) : new SKPoint(t, a);
    }

    /// <summary>A world rectangle's bounds in the projected (cached) coordinates. Call after <see cref="Begin"/>.</summary>
    public SKRect Bounds(double timeMin, double timeMax, double axisMin, double axisMax)
    {
        var a = Point(timeMin, axisMin);
        var b = Point(timeMax, axisMax);
        return new SKRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    public SKRect Bounds(GraphPath path) => Bounds(path.TimeMin, path.TimeMax, path.AxisMin, path.AxisMax);

    /// <summary>
    /// The world rectangles drawn differently since the last call, because paths that had been projected changed,
    /// appeared or went in a new scene. Anything cached of those areas (such as tiles) is stale. Emptied by the call.
    /// </summary>
    public IReadOnlyList<WorldRect> TakeDirty()
    {
        var dirty = _dirty.ToArray();
        _dirty.Clear();
        return dirty;
    }

    /// <summary>A new scene: keep the projections of paths that draw the same, drop the rest, and note where drawing changed.</summary>
    private void Rekey(IReadOnlyList<GraphPath> paths)
    {
        var next = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (_entries.Remove(path.Key, out var old))
            {
                if (old.Runs is not null && old.Path.DrawsLike(path))
                {
                    next[path.Key] = new Entry(path) { Runs = old.Runs };
                    continue;
                }

                if (old.Runs is not null)
                {
                    AddChange(old.Path, path);
                }

                Dispose(old);
            }
            else if (path.Vertices.Count > 0)
            {
                _dirty.Add(WorldRect.Of(path));
            }
        }

        foreach (var old in _entries.Values)
        {
            if (old.Runs is not null)
            {
                _dirty.Add(WorldRect.Of(old.Path));
            }

            Dispose(old);
        }

        _entries = next;
        _paths = paths;
    }

    /// <summary>
    /// Where a changed path draws differently: from the segment before the first vertex or colour that differs, to
    /// the end of whichever is longer. A train that has moved on one timing point dirties only its last stretch.
    /// </summary>
    private void AddChange(GraphPath old, GraphPath path)
    {
        if (old.Colour != path.Colour || old.Layer != path.Layer)
        {
            _dirty.Add(WorldRect.Of(old));
            _dirty.Add(WorldRect.Of(path));
            return;
        }

        var same = 0;
        var shared = Math.Min(old.Vertices.Count, path.Vertices.Count);
        while (same < shared && old.Vertices[same] == path.Vertices[same] &&
            (same == 0 || old.ColourOf(same - 1) == path.ColourOf(same - 1)))
        {
            same++;
        }

        var from = Math.Max(0, same - 1);
        AddTail(old, from);
        AddTail(path, from);

        void AddTail(GraphPath p, int start)
        {
            if (start < p.Vertices.Count)
            {
                _dirty.Add(WorldRect.Of(p.Vertices, start));
            }
        }
    }

    private void DropProjections()
    {
        foreach (var entry in _entries.Values)
        {
            Dispose(entry);
            entry.Runs = null;
        }
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));

    private static void Dispose(Entry? entry)
    {
        if (entry?.Runs is { } runs)
        {
            foreach (var run in runs)
            {
                run.Path.Dispose();
            }
        }
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            Dispose(entry);
        }

        _entries.Clear();
        _paths = null;
        _builder.Dispose();
    }

    private sealed class Entry(GraphPath path)
    {
        public GraphPath Path { get; } = path;

        public PathRun[]? Runs { get; set; }
    }
}
