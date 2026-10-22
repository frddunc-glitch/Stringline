using System.Diagnostics;
using System.Globalization;
using BenchmarkDotNet.Running;
using Stringline.Benchmarks;

// dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- [BenchmarkDotNet args, e.g. --filter * --job short]
// dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- --quick   (a few seconds, Stopwatch timings only)

if (args.Contains("--quick"))
{
    Quick();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(GraphRenderBenchmarks).Assembly).Run(args);

static void Quick()
{
    foreach (var orientation in Enum.GetValues<Stringline.Graph.GraphOrientation>())
    {
        using var bench = new GraphRenderBenchmarks { Trains = 500, Orientation = orientation };
        bench.Setup();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"500 trains / 6 h, 1920×1080, {orientation}:"));
        Time("pan frame", 100, bench.PanFrame);
        Time("zoom frame", 20, bench.ZoomFrame);
        Time("pan frame, no tiles", 30, bench.DirectPanFrame);
        Time("zoom frame, no tiles", 30, bench.DirectZoomFrame);
        Time("update frame", 20, bench.UpdateFrame);
        Time("hit index build", 20, () => bench.BuildHitIndex());
        Time("100 hover tests (grid)", 50, () => bench.HoverHitTest());
        Time("100 hover tests (linear)", 10, () => bench.LinearHitTest());
    }

    using (var draft = new DraftBoardBenchmarks())
    {
        draft.Setup();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Draft board (S44), {draft.Trains} trains / 6 h, {draft.Ghosts} ghosts, {draft.Conflicts} booked conflicts, 1920×1080:"));
        Time("pan frame", 100, draft.PanFrame);
        Time("zoom frame", 20, draft.ZoomFrame);
        Time("build after an edit", 5, () => draft.Build());
    }

    var forecast = new ForecastBenchmarks();
    forecast.Setup();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Forecast, Waterloo ({forecast.Trains} trains, {forecast.Points} points):"));
    Time("independent", 20, () => forecast.Independent());
    Time("with knock-on (both passes)", 20, () => forecast.WithKnockOn());
    Time("conflicts", 20, () => forecast.Conflicts());
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Forecast, Waterloo 4 h at half way, with learned places ({forecast.Anchored} trains anchored at one):"));
    Time("with knock-on and places", 20, () => forecast.WithSignalPlaces());

    static void Time(string what, int runs, Action action)
    {
        for (var i = 0; i < Math.Max(5, runs / 2); i++)
        {
            action();
        }

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < runs; i++)
        {
            action();
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {what,-26} {watch.Elapsed.TotalMilliseconds / runs,8:F2} ms"));
    }
}
