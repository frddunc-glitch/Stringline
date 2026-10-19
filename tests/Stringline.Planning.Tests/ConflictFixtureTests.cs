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

/// <summary>The conflict detector over real timetables and recordings: what the WTT itself gives, and its speed.</summary>
public sealed class ConflictFixtureTests(ITestOutputHelper output)
{
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private static ForecastNetwork Network(WorkingTimetable timetable) => ForecastNetwork.For(StringlineProfile.Create(timetable), timetable);

    private void Report(string title, ConflictSet conflicts, TimeSpan elapsed)
    {
        output.WriteLine($"{title}: {conflicts.Count} conflicts in {elapsed.TotalMilliseconds:0} ms");
        foreach (var group in conflicts.All.GroupBy(c => c.Kind))
        {
            output.WriteLine($"  {group.Key}: {group.Count()}, severity {group.Min(c => c.SeveritySeconds)}–{group.Max(c => c.SeveritySeconds)} s");
        }

        foreach (var c in conflicts.All.Take(40))
        {
            output.WriteLine($"    {c}");
        }
    }

    [Fact]
    public void The_whole_waterloo_day_on_time()
    {
        var timetable = Waterloo.Value;
        var network = Network(timetable);
        var plan = WorkingPlan.FromTimetable(timetable);
        var forecast = Forecaster.Run(new ForecastRequest(plan) { Network = network });
        var watch = Stopwatch.StartNew();
        var conflicts = ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network });
        watch.Stop();
        Report("Waterloo on time", conflicts, watch.Elapsed);

        // Booked spacing is allowed, so the WTT's own day gives only a handful, where its bookings are tighter than
        // the shortest dwells (as the forecaster's knock-on finds).
        conflicts.Count.Should().BeLessThan(8);
        conflicts.All.Should().OnlyContain(c => c.SeveritySeconds >= 30 && c.SeveritySeconds <= 180);
        conflicts.All.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(Fixtures.Waterloo4x)]
    [InlineData(Fixtures.WaterlooFailure)]
    public void Through_a_recording(string recording)
    {
        var timetable = Waterloo.Value;
        var network = Network(timetable);
        var plan = WorkingPlan.FromTimetable(timetable);
        var parser = new MessageParser();
        var events = SslogRecording.Load(Fixtures.Path(recording)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        var state = new LiveState(timetable);
        for (var i = 0; i < events.Count; i++)
        {
            state.Apply(events[i]);
            if (i == events.Count / 2 || i == events.Count - 1)
            {
                var forecast = Forecaster.Run(new ForecastRequest(plan)
                {
                    LiveTrains = state.Trains.ToArray(),
                    Now = state.Clock.Reading.HasTime ? state.Clock.Reading.LastReported : null,
                    SessionStart = state.SessionStart,
                    EntryDelays = new Dictionary<Core.Headcode, int>(state.EntryDelays),
                    Network = network,
                });
                var watch = Stopwatch.StartNew();
                var conflicts = ConflictDetector.Detect(new ConflictRequest(plan, forecast) { Network = network });
                watch.Stop();
                Report($"{recording} at {state.Clock.Reading.LastReported}", conflicts, watch.Elapsed);

                // Only clashes still to come, against trains that can still give way.
                var now = state.Clock.Reading.LastReported;
                conflicts.All.Should().NotContain(c => c.When < now || c.SeveritySeconds < 30);
                foreach (var c in conflicts.All)
                {
                    var a = forecast.Independent.Find(c.A)!;
                    a.Status.Should().NotBe(TrainForecastStatus.Finished, c.ToString());
                }

                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
            }
        }
    }
}
