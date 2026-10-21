using Stringline.Core.Events;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Stringline.Timetable.Live;
using Xunit.Abstractions;

namespace Stringline.Planning.Tests;

/// <summary>
/// Holds at signals (S33) on Waterloo's recordings, with the places learned from the 4 h one: how often a conflict's
/// train is already on its way, and what the suggestions make of it. On the 4× recording 5 of 35 are; 3 are held at a
/// signal (two outside Waterloo, one before Vauxhall), each resolving its conflict and coming first. The other two can
/// wait at a timing point first. On the failure recording the one on its way has passed its last signal.
/// </summary>
[Collection("Forecast fixtures")] // heavy: not alongside the forecast speed test
public sealed class SignalHoldFixtureTests(ITestOutputHelper output)
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private static readonly Lazy<StringlineProfile> Learned = new(() =>
    {
        var tt = Waterloo.Value;
        var learner = new SignalLearner();
        foreach (var e in Events(Fixtures.Waterloo4h))
        {
            learner.Observe(e);
        }

        return SignalLearning.Apply(StringlineProfile.Create(tt), learner.Learn(tt), "4h").Profile;
    });

    private static List<GatewayEvent> Events(string fixture)
    {
        var parser = new MessageParser();
        return SslogRecording.Load(Fixtures.Path(fixture)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
    }

    [Theory]
    [InlineData("waterloo-4x.sslog", 3)]
    [InlineData("waterloo-failure.sslog", 0)]
    public void Suggestions_hold_trains_on_their_way_at_signals(string fixture, int expected)
    {
        var tt = Waterloo.Value;
        var network = ForecastNetwork.For(Learned.Value, tt);
        var state = new LiveState(tt);
        var plan = WorkingPlan.FromTimetable(tt);
        int? step = null;
        var (conflicts, onTheirWay, offered, first, resolving) = (0, 0, 0, 0, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Events(fixture))
        {
            state.Apply(e);
            if (e is not ClockEvent clock || clock.Clock.Seconds / 600 == step)
            {
                continue;
            }

            step = clock.Clock.Seconds / 600;
            var request = new ForecastRequest(plan)
            {
                LiveTrains = state.Trains.ToArray(),
                Now = state.Clock.Reading.LastReported,
                SessionStart = state.SessionStart,
                Network = network,
                LineSide = state.LineSide,
            };
            var forecast = Forecaster.Run(request);
            var found = ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network });
            foreach (var conflict in found.All.Where(c => c.B is not null && seen.Add(c.Id)))
            {
                conflicts++;
                var train = forecast.Independent.Find(conflict.A);
                if (train is not null && SignalHolds.CurrentRun(train) is not null)
                {
                    onTheirWay++;
                }

                var suggestions = Suggester.Suggest(new SuggestionRequest(request, conflict) { Baseline = forecast, BaselineConflicts = found });
                var atSignals = suggestions.Where(s => s.Signal is not null).ToList();
                foreach (var s in atSignals)
                {
                    offered++;
                    resolving += s.Resolves ? 1 : 0;
                    output.WriteLine($"{conflict.When} {conflict}: {s} ({s.Impacts.Count} trains)");
                    s.Commands.Should().ContainSingle().Which.Should().BeOfType<HoldAtSignal>();
                    plan.Apply(s.ToCommand()).Result.Applied.Should().BeTrue();
                }

                first += suggestions.Count > 0 && suggestions[0].Signal is not null ? 1 : 0;
            }
        }

        output.WriteLine($"{fixture}: {conflicts} conflicts between trains, {onTheirWay} with the train giving way on its way; {offered} holds at signals offered ({resolving} resolving), {first} of them first.");
        conflicts.Should().BePositive();
        offered.Should().Be(expected);
        resolving.Should().Be(offered, "each resolves its conflict");
        first.Should().Be(offered, "and comes first");
    }
}
