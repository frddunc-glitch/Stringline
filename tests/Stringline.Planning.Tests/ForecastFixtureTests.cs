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

/// <summary>The forecaster over a real recording and its WTT: the properties of <c>06-…md</c> §6, and its speed.</summary>
[Collection("Forecast fixtures")]
public sealed class ForecastFixtureTests(ITestOutputHelper output)
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private static ForecastNetwork Network(WorkingTimetable timetable) => ForecastNetwork.For(StringlineProfile.Create(timetable), timetable);

    /// <summary>Requests at points through the recording: every <paramref name="every"/> events.</summary>
    private static List<ForecastRequest> Moments(string recording, WorkingTimetable timetable, ForecastNetwork network, int every)
    {
        var parser = new MessageParser();
        var events = SslogRecording.Load(Fixtures.Path(recording)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        var state = new LiveState(timetable);
        var plan = WorkingPlan.FromTimetable(timetable);
        var requests = new List<ForecastRequest>();
        for (var i = 0; i < events.Count; i++)
        {
            state.Apply(events[i]);
            if ((i + 1) % every == 0 || i == events.Count - 1)
            {
                requests.Add(new ForecastRequest(plan)
                {
                    LiveTrains = state.Trains.ToArray(),
                    Now = state.Clock.Reading.HasTime ? state.Clock.Reading.LastReported : null,
                    SessionStart = state.SessionStart,
                    EntryDelays = new Dictionary<Core.Headcode, int>(state.EntryDelays),
                    Network = network,
                });
            }
        }

        return requests;
    }

    [Theory]
    [InlineData(Fixtures.Waterloo1x)]
    [InlineData(Fixtures.Waterloo4x)]
    [InlineData(Fixtures.WaterlooFailure)]
    public void Forecasts_through_a_recording_keep_the_rules(string recording)
    {
        var timetable = Waterloo.Value;
        var network = Network(timetable);
        var plan = WorkingPlan.FromTimetable(timetable);
        var moments = Moments(recording, timetable, network, every: 500);
        var checkedPoints = 0;
        foreach (var request in moments)
        {
            var forecast = Forecaster.Run(request);
            forecast.Trains.Should().NotContain(t => t.Status == TrainForecastStatus.Unavailable);
            foreach (var train in forecast.Trains)
            {
                checkedPoints += Check(train, plan.Get(train.Train), plan, request);
            }
        }

        checkedPoints.Should().BeGreaterThan(1000);
        output.WriteLine($"{moments.Count} moments, {checkedPoints} forecast points checked");
    }

    /// <summary>Asserts the rules on one train's forecast points; returns how many were checked.</summary>
    private static int Check(TrainForecast forecast, TrainPlan plan, WorkingPlan working, ForecastRequest request)
    {
        var points = plan.RunningPoints;
        var count = 0;
        var previous = -1;
        for (var k = 0; k < forecast.Points.Count; k++)
        {
            var p = forecast.Points[k];
            if (p.Basis is ForecastBasis.Forecast or ForecastBasis.Arrived)
            {
                count++;
                if (request.Now is { } now && p.LastTime is { } last)
                {
                    last.Seconds.Should().BeGreaterThanOrEqualTo(now.Seconds, $"{forecast.Headcode} at {p.Location} is forecast, so not in the past");
                }

                if (!points[k].IsPass && points[k].Depart is { } plannedDepart && p.Depart is { } depart)
                {
                    depart.Seconds.Should().BeGreaterThanOrEqualTo(plannedDepart.Seconds, $"{forecast.Headcode} does not leave {p.Location} early");
                }

                if (previous >= 0 && p.Basis == ForecastBasis.Forecast && forecast.Points[previous].LastTime is { } from && p.FirstTime is { } to &&
                    working.MinRuns.Along(points, previous, k) is { } min)
                {
                    if (forecast.Anchor is { } anchor && anchor.After == previous && anchor.Before == k)
                    {
                        // Part of the run is actual (S36): the rest, from the unbooked location passed, takes its share of the minimum.
                        var rest = working.MinRuns.Between(anchor.Location, p.Location) ?? (int)Math.Floor(min * (1 - anchor.Share));
                        (to - anchor.Time).Should().BeGreaterThanOrEqualTo(rest - 1, $"{forecast.Headcode} runs on from {anchor.Location} to {p.Location} no faster than its share of the minimum");
                    }
                    else
                    {
                        (to - from).Should().BeGreaterThanOrEqualTo(min, $"{forecast.Headcode} runs {points[previous].Location}–{p.Location} no faster than the minimum");
                    }
                }
            }

            if (p.LastTime is not null)
            {
                previous = k;
            }
        }

        return count;
    }

    [Fact]
    public void Forecasts_are_deterministic()
    {
        var timetable = Waterloo.Value;
        var request = Moments(Fixtures.Waterloo4x, timetable, Network(timetable), every: 4000)[^1];

        var a = Forecaster.Run(request);
        var b = Forecaster.Run(request);

        a.Trains.Should().HaveCount(b.Trains.Count);
        for (var i = 0; i < a.Trains.Count; i++)
        {
            a.Trains[i].Points.Should().Equal(b.Trains[i].Points);
            a.Trains[i].KnockOnSeconds.Should().Be(b.Trains[i].KnockOnSeconds);
        }
    }

    [Fact]
    public void The_on_time_WTT_makes_no_knock_on_delay()
    {
        var timetable = Waterloo.Value;
        var forecast = Forecaster.Run(new ForecastRequest(WorkingPlan.FromTimetable(timetable)) { Network = Network(timetable) });

        var knocked = forecast.Trains.Where(t => t.KnockOnSeconds > 0).ToList();
        output.WriteLine($"{knocked.Count} of {forecast.Trains.Count} trains knocked on: {string.Join(", ", knocked.Take(20).Select(t => $"{t.Headcode} +{t.KnockOnSeconds}s"))}");
        knocked.Count.Should().BeLessThan(forecast.Trains.Count / 50, "the WTT is assumed to work: at most a few trains whose booking is tighter than the model");
    }

    [Fact]
    public void A_Waterloo_forecast_is_quick()
    {
        var timetable = Waterloo.Value;
        var request = Moments(Fixtures.Waterloo4x, timetable, Network(timetable), every: 2000);
        for (var i = 0; i < 10; i++)
        {
            Forecaster.Run(request[^1]); // warm up: tiered JIT
        }

        var times = new List<double>();
        foreach (var r in request)
        {
            var watch = Stopwatch.StartNew();
            Forecaster.Run(r);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        output.WriteLine($"{timetable.Trains.Count} trains, both passes: median {median:0.0} ms, worst {times[^1]:0.0} ms over {times.Count} moments");
        median.Should().BeLessThan(250, "the budget is 50 ms for 500 trains; Waterloo has 1 768, and this may be a Debug build on a busy machine");
    }
}
