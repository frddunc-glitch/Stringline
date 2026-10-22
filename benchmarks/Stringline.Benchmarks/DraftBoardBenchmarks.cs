using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Graph;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.Benchmarks;

/// <summary>
/// The draft board (S44) under GR-9's budget: a 500-train draft over 6 hours on a 30-location corridor, one train in
/// ten retimed by five minutes so the board has ghosts of the original and booked conflicts, drawn on a 1920×1080
/// surface as the editor's board draws it. Also the time to make the board after an edit (plan, conflicts and scene),
/// which runs off the UI thread.
/// </summary>
[MemoryDiagnoser]
public class DraftBoardBenchmarks : IDisposable
{
    private SkiaGraphRenderer _renderer = null!;
    private SKSurface _surface = null!;
    private GraphScene _scene = null!;
    private GraphViewport _view = null!;
    private GraphViewport _panned = null!;
    private GraphViewport _zoomed = null!;
    private StringlineProfile _profile = null!;
    private ForecastNetwork _network = null!;
    private WorkingTimetable _draft = null!;
    private WorkingTimetable _original = null!;
    private int _frame;

    [Params(500)]
    public int Trains { get; set; } = 500;

    /// <summary>The number of booked conflicts in the draft, for the report.</summary>
    public int Conflicts { get; private set; }

    /// <summary>The number of ghosts of the original drawn.</summary>
    public int Ghosts { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        var corridor = Corridor();
        _profile = new StringlineProfile { AreaId = "bench", Corridors = [corridor] };
        _original = Timetable(Trains, shifted: false);
        _draft = Timetable(Trains, shifted: true);
        _network = ForecastNetwork.Build(_profile.Corridors, _original);
        _scene = Build();
        _view = SyntheticGraph.Viewport();
        _panned = _view.PanTimePixels(3);
        _zoomed = _view.ZoomTime(1.01, _view.TimeOrigin + 500);
        _renderer = new SkiaGraphRenderer();
        _surface = SKSurface.Create(new SKImageInfo(_view.Width, _view.Height));
        _renderer.Render(_surface.Canvas, _scene, _view);
    }

    [Benchmark(Baseline = true)]
    public void PanFrame() => _renderer.Render(_surface.Canvas, _scene, ++_frame % 2 == 0 ? _view : _panned);

    [Benchmark]
    public void ZoomFrame() => _renderer.Render(_surface.Canvas, _scene, ++_frame % 2 == 0 ? _view : _zoomed);

    /// <summary>The board after an edit: the draft's plan, forecast and conflicts, the ghosts and the scene.</summary>
    [Benchmark]
    public GraphScene Build()
    {
        var plan = DraftBoard.Plan(_draft, _network, _original);
        var ghosts = DraftBoard.Changed(_original, _draft);
        (Conflicts, Ghosts) = (plan.Conflicts.Count, ghosts.Count);
        return DraftBoard.Build(new DraftBoardRequest(_profile, _profile.Corridors[0], plan, "Draft – Bench 1.0.1") { Originals = ghosts });
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _renderer?.Dispose();
        _surface?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Corridor Corridor()
    {
        var n = SyntheticGraph.Locations;
        var locations = Enumerable.Range(0, n).Select(i => new CorridorLocation(Tiploc(i), i / (double)(n - 1), Platforms: i % 3 == 0 ? 2 : 0)).ToList();
        var sections = Enumerable.Range(1, n - 1).Select(i => new Section(Tiploc(i - 1), Tiploc(i), Section.DoubleLine, Section.StandardHeadwaySeconds, 90)).ToList();
        return new Corridor("main", "Synthetic", locations, sections);
    }

    private static Tiploc Tiploc(int i) => new($"LOC{i:00}");

    /// <summary>Half Down and half Up, end to end, calling at every third location; with <paramref name="shifted"/>, one in ten five minutes later.</summary>
    private static WorkingTimetable Timetable(int trains, bool shifted)
    {
        var random = new Random(42);
        var n = SyntheticGraph.Locations;
        var span = SyntheticGraph.End.Seconds - SyntheticGraph.Start.Seconds;
        var list = new List<Train>(trains);
        for (var t = 0; t < trains; t++)
        {
            var down = t % 2 == 0;
            var time = SyntheticGraph.Start.Seconds - 3600 + (t * (span + 3600) / trains) + (shifted && t % 10 == 5 ? 300 : 0);
            var points = new List<TimingPoint>(n);
            for (var i = 0; i < n; i++)
            {
                var location = Tiploc(down ? i : n - 1 - i);
                var stops = i % 3 == 0 && i > 0 && i < n - 1;
                var arrive = new SimTime(time);
                if (stops)
                {
                    time += 60;
                }

                points.Add(new TimingPoint
                {
                    Index = i,
                    Location = location,
                    Arrive = i == 0 ? null : arrive,
                    Depart = i == n - 1 ? null : new SimTime(time),
                    IsPass = !stops && i > 0 && i < n - 1,
                    Platform = stops ? (t % 2 == 0 ? "1" : "2") : null,
                });
                time += random.Next(90, 150);
            }

            for (var k = 1; k < n - 1; k++)
            {
                if (points[k].IsPass)
                {
                    points[k] = points[k] with { Arrive = null };
                }
            }

            list.Add(new Train
            {
                Id = new TrainId($"T{t:0000}"),
                Headcode = new Headcode($"{1 + (t % 9)}{(char)('A' + (t % 26))}{t % 100:00}"),
                Points = points,
            });
        }

        return new WorkingTimetable(new TimetableHeader("bench", "5.0", "Bench", "", null, SyntheticGraph.Start, SyntheticGraph.End), [], list, ImmutableList<TimetableRule>.Empty);
    }
}
