using System.Diagnostics;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;
using Xunit.Abstractions;

namespace Stringline.Planning.Tests;

/// <summary>The suggestion engine on real conflicts: Waterloo part way through a recording (where it has some).</summary>
public sealed class SuggesterFixtureTests(ITestOutputHelper output)
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    [Theory]
    [InlineData(Fixtures.Waterloo4x, 0.5)]
    [InlineData(Fixtures.WaterlooFailure, 1.0)]
    public void Suggestions_for_the_conflicts_part_way_through_a_recording(string recording, double part)
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

        var request = new ForecastRequest(plan)
        {
            LiveTrains = state.Trains.ToArray(),
            Now = state.Clock.Reading.LastReported,
            SessionStart = state.SessionStart,
            EntryDelays = new Dictionary<Core.Headcode, int>(state.EntryDelays),
            Network = network,
        };
        var forecast = Forecaster.Run(request);
        var conflicts = ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network });
        output.WriteLine($"{recording} at {request.Now}: {conflicts.Count} conflicts");
        conflicts.Count.Should().BePositive("a late-running sim has some");

        var offered = 0;
        var resolved = 0;
        foreach (var conflict in conflicts.All.Take(8))
        {
            var watch = Stopwatch.StartNew();
            var suggestions = Suggester.Suggest(new SuggestionRequest(request, conflict) { Baseline = forecast, BaselineConflicts = conflicts });
            watch.Stop();
            output.WriteLine($"  {conflict} — {suggestions.Count} in {watch.Elapsed.TotalMilliseconds:0} ms");
            foreach (var s in suggestions)
            {
                output.WriteLine($"    {s} (clears {s.Cleared}, adds {s.Added})");
            }

            suggestions.Select(s => s.Id).Should().OnlyHaveUniqueItems();
            suggestions.Count.Should().BeLessThanOrEqualTo(3);
            foreach (var s in suggestions)
            {
                var (after, result) = plan.Apply(s.ToCommand());
                result.Applied.Should().BeTrue(s.ToString());
                s.Changed.Should().NotBeEmpty().And.OnlyContain(id => !after.Get(id).SameAs(plan.Get(id)));
                if (s.Kind is SuggestionKind.Hold or SuggestionKind.Swap)
                {
                    s.HoldSeconds.Should().BePositive();
                    (s.Until!.Value.Seconds % 30).Should().Be(0, "holds are to the half minute");
                }
            }

            offered += suggestions.Count > 0 ? 1 : 0;
            resolved += suggestions.Any(s => s.Resolves) ? 1 : 0;
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }

        output.WriteLine($"Suggestions for {offered} of {Math.Min(8, conflicts.Count)}, resolving {resolved}");
        offered.Should().BePositive();
        resolved.Should().BePositive();
    }
}
