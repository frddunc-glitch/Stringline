using Stringline.Core;
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
/// WTT rules (S32) on Waterloo's timetable and recordings: 331 "X appears after Y leaves" (a train's return working
/// comes back 25–50 min after it leaves the area), 4 "after Y enters" and 3 "X not if Y".
/// </summary>
[Collection("Forecast fixtures")] // heavy: not alongside the forecast speed test
public sealed class RuleFixtureTests(ITestOutputHelper output)
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private static List<GatewayEvent> Events(string fixture)
    {
        var parser = new MessageParser();
        return SslogRecording.Load(Fixtures.Path(fixture)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
    }

    private static Forecast Run(WorkingPlan plan, LiveState state, ForecastNetwork network, bool rules) => Forecaster.Run(new ForecastRequest(plan)
    {
        LiveTrains = state.Trains.ToArray(),
        Now = state.Clock.Reading.HasTime ? state.Clock.Reading.LastReported : null,
        SessionStart = state.SessionStart,
        EntryDelays = new Dictionary<Headcode, int>(state.EntryDelays),
        Network = network,
        Options = new ForecastOptions { TimetableRules = rules },
        LineSide = state.LineSide,
    });

    private static ForecastNetwork Network(WorkingTimetable timetable) => ForecastNetwork.Build(StringlineProfile.Create(timetable).Corridors, timetable);

    [Fact]
    public void Waterloo_rules_resolve_with_nothing_left_out()
    {
        var plan = WorkingPlan.FromTimetable(Waterloo.Value);

        var rules = TimetableRules.For(plan);

        rules.Rules.Should().HaveCount(335);
        rules.Rules.Count(r => r.Rule.Kind == RuleKind.XAppearsAfterYLeaves).Should().Be(331);
        rules.Rules.Should().OnlyContain(r => r.IsAppearance);
        rules.Notes.Should().BeEmpty();
        Waterloo.Value.Rules.Where(r => r.Kind == RuleKind.XNotIfY).Should().HaveCount(3)
            .And.OnlyContain(r => r.Train != null && rules.ExclusionsOf(r.Train.Value).Contains(r));
        TimetableRules.For(plan).Should().BeSameAs(rules, "worked out once per plan");
    }

    /// <summary>
    /// At the end of the 4× recording (08:05) 2R09 has not started (booked away from Waterloo at 07:16), so its return
    /// working 2R09-1, booked in at PUTNEY at 08:21:30, cannot appear until 40 min after 2R09 leaves the area. Before
    /// S32 it was forecast in on time.
    /// </summary>
    [Fact]
    public void A_return_working_is_not_forecast_in_before_its_train_has_been_out_long_enough()
    {
        var timetable = Waterloo.Value;
        var plan = WorkingPlan.FromTimetable(timetable);
        var state = new LiveState(timetable);
        foreach (var e in Events(Fixtures.Waterloo4x))
        {
            state.Apply(e);
        }

        var network = Network(timetable);
        var on = Run(plan, state, network, rules: true);
        var off = Run(plan, state, network, rules: false);

        var y = on.Find(new TrainId("2R09"))!;
        var x = on.Find(new TrainId("2R09-1"))!;
        var leaves = y.Points.Last(p => p.LastTime is not null).LastTime!.Value;
        output.WriteLine($"2R09 {y.Status} leaves {leaves}; 2R09-1 in at {x.Points[0].FirstTime} ({string.Join("; ", x.RuleHolds)})");
        y.Status.Should().Be(TrainForecastStatus.NotStarted);
        off.Find(x.Train)!.Points[0].FirstTime.Should().Be(SimTime.Parse("08:21:30"));
        var hold = x.RuleHolds.Should().ContainSingle().Subject;
        hold.ToString().Should().Be("held by rule: after 2R09 leaves (+40 min)");
        hold.From.Should().Be(SimTime.Parse("08:21:30"));
        on.Independent.Find(x.Train)!.Points[0].FirstTime.Should().Be(on.Independent.Find(y.Train)!.Points.Last(p => p.LastTime is not null).LastTime!.Value + (40 * SimTime.SecondsPerMinute));
        x.Points[0].FirstTime!.Value.Should().BeGreaterThanOrEqualTo(leaves + (40 * SimTime.SecondsPerMinute));

        // 2S19 is formed from it, so it goes later too.
        static SimTime Start(TrainForecast f) => f.Points.First(p => p.LastTime is not null).LastTime!.Value;
        Start(on.Find(new TrainId("2S19"))!).Should().BeGreaterThan(Start(off.Find(new TrainId("2S19"))!));
    }

    /// <summary>
    /// Through the 4 h recording no rule held a train in play: each of the 32 return workings that came in did so at
    /// about its booked time, well after its rule allowed. Forecasts every 15 min before each came in are therefore the
    /// same with the rules as without (the rules forecast none of them later than it came).
    /// </summary>
    [Fact]
    public void Rules_with_room_to_spare_in_play_change_no_forecast()
    {
        var timetable = Waterloo.Value;
        var plan = WorkingPlan.FromTimetable(timetable);
        var rules = TimetableRules.For(plan);
        var network = Network(timetable);
        var events = Events(Fixtures.Waterloo4h);

        // When each ruled train came in (its first report), by its plan ID.
        var entered = new Dictionary<TrainId, SimTime>();
        var whole = new LiveState(timetable);
        foreach (var e in events)
        {
            whole.Apply(e);
        }

        foreach (var live in whole.Trains)
        {
            for (var w = 0; w < live.Workings.Count; w++)
            {
                if (live.Workings[w] is { IsMatched: true, Train: { } id } && rules.For(id).Any(r => r.IsAppearance) && live.ActualsOf(w).FirstOrDefault() is { } first)
                {
                    entered[id] = first.Time;
                }
            }
        }

        output.WriteLine($"{entered.Count} ruled trains came in: {string.Join(", ", entered.Select(e => $"{e.Key} {e.Value}"))}");
        entered.Should().HaveCountGreaterThanOrEqualTo(5);

        var state = new LiveState(timetable);
        var next = 0;
        var checks = 0;
        foreach (var e in events)
        {
            state.Apply(e);
            if (!state.Clock.Reading.HasTime || state.Clock.Reading.LastReported.Seconds < next)
            {
                continue;
            }

            var now = state.Clock.Reading.LastReported;
            next = now.Seconds + (15 * SimTime.SecondsPerMinute);
            var forecast = Run(plan, state, network, rules: true);
            var without = Run(plan, state, network, rules: false);
            foreach (var (id, came) in entered.Where(e => e.Value > now))
            {
                var f = forecast.Find(id)!;
                static SimTime In(TrainForecast t) => t.Points.First(p => p.FirstTime is not null).FirstTime!.Value;
                output.WriteLine($"{now} {id}: forecast in {In(f)}, came {came} {string.Join("; ", f.RuleHolds)}");
                f.RuleHolds.Should().BeEmpty($"{id} at {now}");
                In(f).Should().Be(In(without.Find(id)!));
                checks++;
            }
        }

        checks.Should().BeGreaterThan(20);
    }
}
