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
/// through the 4× recording, with and without the interaction pass; and (S38) half way through the 4 h recording with
/// the places learned from it and its signals and track circuits, which re-anchor every running train.
/// </summary>
[MemoryDiagnoser]
public class ForecastBenchmarks
{
    private ForecastRequest _request = null!;
    private ForecastRequest _placed = null!;

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

        var fourHours = SslogRecording.Load(FixturePath("waterloo-4h.sslog")).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        var learner = new SignalLearner();
        fourHours.ForEach(learner.Observe);
        var profile = SignalLearning.Apply(StringlineProfile.Create(timetable), learner.Learn(timetable), "benchmark").Profile;
        var placed = new LiveState(timetable);
        foreach (var e in fourHours.Take(fourHours.Count / 2))
        {
            placed.Apply(e);
        }

        _placed = new ForecastRequest(WorkingPlan.FromTimetable(timetable))
        {
            LiveTrains = placed.Trains.ToArray(),
            Now = placed.Clock.Reading.LastReported,
            SessionStart = placed.SessionStart,
            Network = ForecastNetwork.For(profile, timetable),
            LineSide = placed.LineSide,
        };
    }

    /// <summary>Running trains re-anchored at a learned place in <see cref="WithSignalPlaces"/>.</summary>
    public int Anchored => Forecaster.Run(_placed).Trains.Count(t => t.Anchor?.Place is not null);

    /// <summary>Both passes, with learned places and the line side (S38).</summary>
    [Benchmark]
    public Forecast WithSignalPlaces() => Forecaster.Run(_placed);

    public int Trains => _request.Plan.Count;

    public int Points => _request.Plan.Trains.Sum(t => t.Points.Count);

    [Benchmark]
    public Forecast Independent() => Forecaster.Run(_request with { Options = new ForecastOptions { Interaction = false } });

    [Benchmark]
    public Forecast WithKnockOn() => Forecaster.Run(_request);

    /// <summary>Conflict detection (S14) on a forecast made once, as the engine runs it after each forecast.</summary>
    [Benchmark]
    public ConflictSet Conflicts() => ConflictDetector.Detect(new ConflictRequest(_request.Plan, _forecast ??= Forecaster.Run(_request)) { Network = _request.Network });

    private Forecast? _forecast;
}
