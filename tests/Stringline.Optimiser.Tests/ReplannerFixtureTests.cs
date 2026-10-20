using System.Diagnostics;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Planning;
using Stringline.Timetable;
using Stringline.Timetable.Live;
using Xunit.Abstractions;

namespace Stringline.Optimiser.Tests;

/// <summary>Re-planning real conflicts: Waterloo part way through a recording (where it has some).</summary>
public sealed class ReplannerFixtureTests(ITestOutputHelper output)
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private static ReplanRequest Request(string recording, double part, ReplanOptions options)
    {
        var timetable = Waterloo.Value;
        var network = ForecastNetwork.For(StringlineProfile.Create(timetable), timetable);
        var plan = WorkingPlan.FromTimetable(timetable);
        var parser = new MessageParser();
        var events = SslogRecording.Load(Fixtures.Path(recording)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        var state = new LiveState(timetable);
        foreach (var e in events.Take((int)(events.Count * part)))
        {
            state.Apply(e);
        }

        return new ReplanRequest(new ForecastRequest(plan)
        {
            LiveTrains = state.Trains.ToArray(),
            Now = state.Clock.Reading.LastReported,
            SessionStart = state.SessionStart,
            EntryDelays = new Dictionary<Core.Headcode, int>(state.EntryDelays),
            Network = network,
        })
        { Options = options };
    }

    [Theory]
    [InlineData(Fixtures.Waterloo4x, 0.5, "optimiser", true)]
    [InlineData(Fixtures.Waterloo4x, 0.5, "rules", true)]
    [InlineData(Fixtures.Waterloo4x, 1.0, "optimiser", false)]
    [InlineData(Fixtures.Waterloo4x, 1.0, "rules", false)]
    [InlineData(Fixtures.WaterlooFailure, 1.0, "optimiser", true)]
    [InlineData(Fixtures.WaterlooFailure, 1.0, "rules", true)]
    public void Re_plan_the_next_hour_part_way_through_a_recording(string recording, double part, string method, bool mustImprove)
    {
        var options = new ReplanOptions { TimeLimitSeconds = 5 };
        var request = Request(recording, part, options);
        IReplanner replanner = method == "rules" ? new RuleReplanner() : new CpSatReplanner(new OutputLogger<CpSatReplanner>(output));

        var watch = Stopwatch.StartNew();
        var result = replanner.Replan(request);
        watch.Stop();

        output.WriteLine($"{recording} at {request.Forecast.Now}, {method}: {result} in {watch.Elapsed.TotalMilliseconds:0} ms, {result.Rounds} rounds{(result.Optimal ? ", optimal" : string.Empty)}");
        output.WriteLine("Before:");
        foreach (var c in result.Before)
        {
            output.WriteLine($"  {c}");
        }

        output.WriteLine("Steps:");
        foreach (var s in result.Steps)
        {
            output.WriteLine($"  {s}");
        }

        output.WriteLine("After:");
        foreach (var c in result.After)
        {
            output.WriteLine($"  {c}");
        }

        result.Before.Should().NotBeEmpty("a late-running sim has some");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(options.TimeLimitSeconds + 10), "the time limit is kept, give or take the forecasts");
        // Busy (21 conflicts at Waterloo at 08:05), what is found in the time limit depends on the machine's load;
        // a re-plan is never worse than the plan as it stands.
        if (mustImprove || result.Outcome == ReplanOutcome.Improved)
        {
            result.Outcome.Should().Be(ReplanOutcome.Improved);
            result.After.Count.Should().BeLessThanOrEqualTo(result.Before.Count);
        }
        else
        {
            result.Outcome.Should().Be(ReplanOutcome.NoImprovement);
            result.Commands.Should().BeEmpty();
            return;
        }

        // Applied, the plan has the conflicts the result says, and every hold is to the half minute.
        var plan = request.Forecast.Plan;
        var (after, applied) = plan.Apply(result.ToCommand());
        applied.Applied.Should().BeTrue(applied.Error);
        var forecast = Forecaster.Run(request.Forecast with { Plan = after });
        var conflicts = ConflictDetector.Detect(new ConflictRequest(after, forecast) { Network = request.Forecast.Network });
        conflicts.All.Count(c => c.When <= result.Until).Should().Be(result.After.Count);
        result.Changed.Should().NotBeEmpty().And.OnlyContain(id => !after.Get(id).SameAs(plan.Get(id)));
        foreach (var s in result.Steps.Where(s => s.Kind is SuggestionKind.Hold or SuggestionKind.Swap))
        {
            s.HoldSeconds.Should().BePositive();
            (s.Until!.Value.Seconds % 30).Should().Be(0, "holds are to the half minute");
        }
    }
}
