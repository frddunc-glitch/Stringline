using BenchmarkDotNet.Attributes;
using SkiaSharp;
using Stringline.Graph;

namespace Stringline.Benchmarks;

/// <summary>
/// GR-9: 500 trains over a 6 hour window must draw in under 33 ms a frame (30 fps) while panning. Draws
/// <see cref="SyntheticGraph"/> on a 1920×1080 CPU surface, as the app's <c>SKElement</c> does.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="PanFrame"/>: the budgeted case. Panning (and following the now line) reuses the projected paths.</item>
/// <item><see cref="ZoomFrame"/>: every path projected again, as on a zoom step (and as every frame was before S9).</item>
/// <item><see cref="UpdateFrame"/>: a new but unchanged scene, as after a live update where few trains moved.</item>
/// <item><see cref="BuildHitIndex"/> and <see cref="HoverHitTest"/> against <see cref="LinearHitTest"/>.</item>
/// </list>
/// </remarks>
[MemoryDiagnoser]
public class GraphRenderBenchmarks : IDisposable
{
    private SkiaGraphRenderer _renderer = null!;
    private SkiaGraphRenderer _direct = null!;
    private SKSurface _surface = null!;
    private GraphScene _scene = null!;
    private GraphScene _sameAgain = null!;
    private GraphViewport _view = null!;
    private GraphViewport _panned = null!;
    private GraphViewport _zoomed = null!;
    private GraphHitIndex _index = null!;
    private ScreenPoint[] _points = [];
    private int _frame;

    [Params(500, 1000)]
    public int Trains { get; set; }

    [Params(GraphOrientation.Horizontal, GraphOrientation.Vertical)]
    public GraphOrientation Orientation { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _renderer = new SkiaGraphRenderer();
        _direct = new SkiaGraphRenderer { CacheTiles = false };
        _scene = SyntheticGraph.Create(Trains);
        _sameAgain = SyntheticGraph.Create(Trains);
        _view = SyntheticGraph.Viewport(Orientation);
        _panned = _view.PanTimePixels(3);
        _zoomed = _view.ZoomTime(1.01, _view.TimeOrigin + 500);
        _surface = SKSurface.Create(new SKImageInfo(_view.Width, _view.Height));
        _index = GraphHitIndex.Build(_scene, _view);
        var random = new Random(7);
        _points = [.. Enumerable.Range(0, 100).Select(_ => new ScreenPoint(
            _view.PlotLeft + (random.NextDouble() * _view.PlotWidth), _view.PlotTop + (random.NextDouble() * _view.PlotHeight)))];
        _renderer.Render(_surface.Canvas, _scene, _view);
    }

    [Benchmark(Baseline = true)]
    public void PanFrame() => _renderer.Render(_surface.Canvas, _scene, ++_frame % 2 == 0 ? _view : _panned);

    [Benchmark]
    public void ZoomFrame() => _renderer.Render(_surface.Canvas, _scene, ++_frame % 2 == 0 ? _view : _zoomed);

    /// <summary>A pan frame without the tile cache: every line stroked again.</summary>
    [Benchmark]
    public void DirectPanFrame() => _direct.Render(_surface.Canvas, _scene, ++_frame % 2 == 0 ? _view : _panned);

    /// <summary>A zoom frame without the tile cache.</summary>
    [Benchmark]
    public void DirectZoomFrame() => _direct.Render(_surface.Canvas, _scene, ++_frame % 2 == 0 ? _view : _zoomed);

    [Benchmark]
    public void UpdateFrame() => _renderer.Render(_surface.Canvas, ++_frame % 2 == 0 ? _scene : _sameAgain, _view);

    [Benchmark]
    public GraphHitIndex BuildHitIndex() => GraphHitIndex.Build(_scene, _view);

    /// <summary>100 hover tests against a built index.</summary>
    [Benchmark]
    public int HoverHitTest()
    {
        var hits = 0;
        foreach (var p in _points)
        {
            hits += _index.HitTest(p) is null ? 0 : 1;
        }

        return hits;
    }

    /// <summary>The same 100 tests by linear scan, as S8 did.</summary>
    [Benchmark]
    public int LinearHitTest()
    {
        var hits = 0;
        foreach (var p in _points)
        {
            hits += GraphHitTester.HitTest(_scene, _view, p) is null ? 0 : 1;
        }

        return hits;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _renderer?.Dispose();
        _direct?.Dispose();
        _surface?.Dispose();
        GC.SuppressFinalize(this);
    }
}
