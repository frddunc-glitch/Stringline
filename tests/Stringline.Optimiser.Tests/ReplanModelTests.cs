using Stringline.Core;
using Stringline.Planning;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Optimiser.Tests;

/// <summary>The model's trains against the forecaster: with the same holds, the same times.</summary>
public sealed class ReplanModelTests
{
    [Fact]
    public void Held_trains_run_as_the_forecaster_runs_them()
    {
        // 2S01 leaves the loop at B at 08:09 (3 min late, held there or at A, as it keeps its dwell at B): it uses up
        // its 2 min allowance to C, arriving at 08:13. 2T01 is held at its origin A until 08:20: its plan moves on
        // only as far as the minimum run to B needs (08:24), and it keeps its booked 2 min dwell there, as the plan
        // builder keeps dwells, so it leaves B at 08:26 and reaches C at 08:30.
        var timetable = Of(
            Train("1F01", points: [Pass("A", "08:00"), Pass("B", "08:04"), Pass("C", "08:08")]),
            Train("2S01", points: [Call("A", null, "07:55"), Call("B", "07:59", "08:06"), Call("C", "08:12", null)]),
            Train("2T01", points: [Call("A", null, "08:15"), Call("B", "08:20", "08:22"), Call("C", "08:29", null)]));
        var network = ForecastNetwork.Build([Line("main", 180, single: false, "A", "B", "C")], timetable);
        var request = new ForecastRequest(WorkingPlan.FromTimetable(timetable)) { Now = SimTime.Parse("07:50"), Network = network };
        var evaluator = new ReplanEvaluator(new ReplanRequest(request) { Options = new ReplanOptions { Workers = 1 } });
        var model = new ReplanModel(evaluator);
        var stopper = model.Trains.Single(t => t.Plan.Headcode.Value == "2S01");
        var late = model.Trains.Single(t => t.Plan.Headcode.Value == "2T01");

        var solution = model.Solve(TimeSpan.FromSeconds(5), default, new Dictionary<EventKey, long>
        {
            [new EventKey(stopper.Number, 1, true)] = SimTime.Parse("08:09").Seconds,
            [new EventKey(late.Number, 0, true)] = SimTime.Parse("08:20").Seconds,
        })!;
        var (commands, steps, _) = model.Commands(solution);
        commands.Should().HaveCount(2).And.ContainEquivalentOf(new SetDwell(late.Id, 0, SimTime.Parse("08:20")));
        steps.Select(s => s.Describe()).Should().Contain("Hold 2T01 at A until 08:20 (+5 min)");

        var forecast = evaluator.Evaluate(commands)!.Forecast.Independent;
        foreach (var t in model.Trains)
        {
            foreach (var k in t.Timed)
            {
                var f = forecast.Find(t.Id)!.Points[k];
                if (t.HasArrive(k))
                {
                    solution.Times[new EventKey(t.Number, k, false)].Should().Be((f.Arrive ?? f.Depart)!.Value.Seconds, $"{t.Plan.Headcode} arrives {f.Location}");
                }

                if (t.HasDepart(k))
                {
                    solution.Times[new EventKey(t.Number, k, true)].Should().Be(f.Depart!.Value.Seconds, $"{t.Plan.Headcode} leaves {f.Location}");
                }
            }
        }

        solution.Times[new EventKey(stopper.Number, 2, false)].Should().Be(SimTime.Parse("08:13").Seconds);
        solution.Times[new EventKey(late.Number, 1, true)].Should().Be(SimTime.Parse("08:26").Seconds);
        solution.Times[new EventKey(late.Number, 2, false)].Should().Be(SimTime.Parse("08:30").Seconds);
    }
}
