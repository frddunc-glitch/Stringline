using Stringline.Core.Events;
using Stringline.Core.Live;
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
/// The forecast through learned places (S38) on Waterloo's recordings, with the places learned from the 4 h one:
/// progress between timing points, trains held at signals, and how near the next timing point's forecast comes.
/// </summary>
[Collection("Forecast fixtures")] // heavy: not alongside the forecast speed test
public sealed class SignalForecastFixtureTests(ITestOutputHelper output)
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

    /// <summary>A forecast request at the start of every sim minute through a recording.</summary>
    private static IEnumerable<ForecastRequest> Minutes(List<GatewayEvent> events, ForecastNetwork network)
    {
        var tt = Waterloo.Value;
        var state = new LiveState(tt);
        var plan = WorkingPlan.FromTimetable(tt);
        int? minute = null;
        foreach (var e in events)
        {
            state.Apply(e);
            if (e is not ClockEvent clock || clock.Clock.Seconds / 60 == minute)
            {
                continue;
            }

            minute = clock.Clock.Seconds / 60;
            yield return new ForecastRequest(plan)
            {
                LiveTrains = state.Trains.ToArray(),
                Now = state.Clock.Reading.LastReported,
                SessionStart = state.SessionStart,
                Network = network,
                LineSide = state.LineSide,
            };
        }
    }

    /// <summary>What a recording's minute-by-minute forecasts said, with and without the learned places.</summary>
    private sealed record Outcome(Dictionary<SignallingKind, int> Anchors, int Standing, int StandingThenRed, int Holds, int HoldsFound, double ErrorWith, double ErrorWithout, int Compared, IReadOnlyList<ProgressAnchor> AllAnchors);

    private static Outcome Run(string fixture)
    {
        var tt = Waterloo.Value;
        var profile = Learned.Value;
        var events = Events(fixture);
        var with = ForecastNetwork.For(profile, tt);
        var without = ForecastNetwork.For(profile.WithPlaces(SignallingPlaces.Empty), tt);
        var reports = events.OfType<TrainLocationEvent>().GroupBy(e => e.Uid).ToDictionary(g => g.Key, g => g.ToList());

        // Long holds: a signal passed from red more than a minute after the fastest other train between the same two reports.
        var gaps = new List<(string Uid, string Signal, int Gap, string Pair, SimTimeSpan Span)>();
        foreach (var (uid, list) in reports)
        {
            for (var i = 1; i < list.Count; i++)
            {
                if (list[i].AspectApproach is { } approach)
                {
                    gaps.Add((uid, list[i].Location.Code, list[i].Time - list[i - 1].Time, $"{list[i - 1].Location}>{list[i].Location}|{approach == 0}", new SimTimeSpan(list[i - 1].Time.Seconds, list[i].Time.Seconds)));
                }
            }
        }

        var fastest = gaps.Where(g => g.Pair.EndsWith("False", StringComparison.Ordinal)).GroupBy(g => g.Pair[..g.Pair.IndexOf('|', StringComparison.Ordinal)]).ToDictionary(g => g.Key, g => g.Min(x => x.Gap));
        var holds = gaps.Where(g => g.Pair.EndsWith("True", StringComparison.Ordinal) && fastest.TryGetValue(g.Pair[..g.Pair.IndexOf('|', StringComparison.Ordinal)], out var f) && g.Gap - f >= 60).ToList();
        var found = new HashSet<(string, string)>();

        var anchors = new Dictionary<SignallingKind, int>();
        var all = new List<ProgressAnchor>();
        int standing = 0, standingRight = 0, compared = 0;
        double errWith = 0, errWithout = 0;
        foreach (var request in Minutes(events, with))
        {
            var a = Forecaster.Run(request);
            var b = Forecaster.Run(request with { Network = without, LineSide = LineSideState.Empty });
            var now = request.Now!.Value;
            foreach (var t in a.Trains.Where(t => t.Status == TrainForecastStatus.Running))
            {
                var uid = t.LiveUid!;
                if (t.Anchor is { Place: { } place } anchor)
                {
                    anchors[place.Kind] = anchors.GetValueOrDefault(place.Kind) + 1;
                    all.Add(anchor);
                }

                if (t.Standing is { } s)
                {
                    standing++;
                    if (reports[uid].FirstOrDefault(r => r.Time > now && r.Location.Code == s.Signal) is { AspectApproach: 0 })
                    {
                        standingRight++;
                    }

                    foreach (var h in holds.Where(h => h.Uid == uid && h.Span.Contains(now.Seconds)))
                    {
                        found.Add((h.Uid, h.Signal));
                    }
                }

                // The next timing point forecast, against when the train got there.
                var first = t.FirstForecast;
                if (first < 0 || t.Points[first].Basis != ForecastBasis.Forecast || b.Find(t.Train) is not { } tb)
                {
                    continue;
                }

                var p = t.Points[first];
                if (reports[uid].FirstOrDefault(r => r.Time >= now && r.Location == p.Location) is not { } actual || p.FirstTime is not { } fa || tb.Points[first].FirstTime is not { } fb)
                {
                    continue;
                }

                errWith += Math.Abs(fa - actual.Time);
                errWithout += Math.Abs(fb - actual.Time);
                compared++;
            }
        }

        return new Outcome(anchors, standing, standingRight, holds.Count, found.Count, errWith / compared, errWithout / compared, compared, all);
    }

    private readonly record struct SimTimeSpan(int From, int To)
    {
        public bool Contains(int t) => t > From && t < To;
    }

    private void Write(string fixture, Outcome o) => output.WriteLine(
        $"{fixture}: anchors {string.Join(", ", o.Anchors.Select(k => $"{k.Key} {k.Value}"))}; standing {o.Standing}, then passed from red {o.StandingThenRed}; " +
        $"holds of a minute or more {o.Holds}, found {o.HoldsFound}; next point error {o.ErrorWith:0.0} s with places, {o.ErrorWithout:0.0} s without, over {o.Compared}");

    [Fact]
    public void Waterloo_4h_forecasts_on_from_signals_and_tells_held_trains()
    {
        var o = Run(Fixtures.Waterloo4h);
        Write(Fixtures.Waterloo4h, o);

        o.Anchors.Keys.Should().Contain([SignallingKind.Signal, SignallingKind.TrackCircuit]);
        o.ErrorWith.Should().BeLessThan(0.9 * o.ErrorWithout, "forecasting on from where the train is known to be brings the next point nearer");
        o.Standing.Should().BeGreaterThan(20);
        ((double)o.StandingThenRed / o.Standing).Should().BeGreaterThan(0.85, "a train told as standing passes that signal from red when it moves");
        ((double)o.HoldsFound / o.Holds).Should().BeGreaterThan(0.5, "most long holds at signals are found");
    }

    [Fact]
    public void The_T1620_failure_does_not_count_as_a_train_held()
    {
        var o = Run(Fixtures.WaterlooFailure);
        Write(Fixtures.WaterlooFailure, o);

        o.AllAnchors.Should().NotContain(a => a.Place!.Id == "T1620", "T1620 was occupied by the failure, with no train in it");
        if (o.Standing > 0)
        {
            ((double)o.StandingThenRed / o.Standing).Should().BeGreaterThan(0.8);
        }

        o.ErrorWith.Should().BeLessThanOrEqualTo(o.ErrorWithout);
    }
}
