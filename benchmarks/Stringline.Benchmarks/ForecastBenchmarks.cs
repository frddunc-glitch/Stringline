using BenchmarkDotNet.Attributes;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Planning;
using Stringline.Timetable;
using Stringline.Timetable.Live;

namespace Stringline.Benchmarks;

/// <summary>
/// The forecaster (<c>06-…md</c> §2: 500 trains × 40 points well under 50 ms) on Waterloo's 1 768 trains, part way
/// through the 4× recording, with and without the interaction pass.
/// </summary>
[MemoryDiagnoser]
public class ForecastBenchmarks
{
    private ForecastRequest _request = null!;

    public static string FixturePath(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tests", "fixtures", name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"No tests/fixtures/{name} above {AppContext.BaseDirectory}.");
    }

    [GlobalSetup]
    public void Setup()
    {
        var timetable = new WttReader().Read(FixturePath("waterloo 2010.WTT")).Timetable;
        var parser = new MessageParser();
        var events = SslogRecording.Load(FixturePath("waterloo-4x.sslog")).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        var state = new LiveState(timetable);
        foreach (var e in events.Take(events.Count / 2))
        {
            state.Apply(e);
        }

        _request = new ForecastRequest(WorkingPlan.FromTimetable(timetable))
        {
            LiveTrains = state.Trains.ToArray(),
            Now = state.Clock.Reading.LastReported,
            SessionStart = state.SessionStart,
            EntryDelays = new Dictionary<Headcode, int>(state.EntryDelays),
            Network = ForecastNetwork.For(StringlineProfile.Create(timetable), timetable),
        };
    }

    public int Trains => _request.Plan.Count;

    public int Points => _request.Plan.Trains.Sum(t => t.Points.Count);

    [Benchmark]
    public Forecast Independent() => Forecaster.Run(_request with { Options = new ForecastOptions { Interaction = false } });

    [Benchmark]
    public Forecast WithKnockOn() => Forecaster.Run(_request);
}
