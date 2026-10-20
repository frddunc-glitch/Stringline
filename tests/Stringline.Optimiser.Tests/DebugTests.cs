using Stringline.Core;
using Stringline.Planning;
using Xunit.Abstractions;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Optimiser.Tests;

public sealed class DebugTests(ITestOutputHelper output)
{
    [Fact]
    public void Debug()
    {
        var timetable = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]),
            Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]));
        var network = ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C")], timetable);
        var request = new ForecastRequest(WorkingPlan.FromTimetable(timetable)) { LiveTrains = [Running("11", "2S01", Dep("A", "07:55"), Arr("B", "07:59"))], Now = SimTime.Parse("08:05"), Network = network };
        var evaluator = new ReplanEvaluator(new ReplanRequest(request) { Options = new ReplanOptions { Workers = 1 } });
        foreach (var t in evaluator.Baseline.Independent.Trains) output.WriteLine(string.Join(" | ", Times(t)));
        var model = new ReplanModel(evaluator);
        model.Add(evaluator.Before, evaluator.Plan, evaluator.Baseline.Independent);
        output.WriteLine($"{model.TrainCount} trains, {model.DisjunctionCount} pairs");
        var s = model.Solve(TimeSpan.FromSeconds(5), default)!;
        foreach (var (k, v) in s.Times.OrderBy(x => x.Key.Train).ThenBy(x => x.Key.Index)) output.WriteLine($"{k} {new SimTime((int)v)}");
        foreach (var (k, v) in s.Holds) output.WriteLine($"hold {k} {v}");
    }
}
