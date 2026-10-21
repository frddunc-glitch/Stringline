using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// S38 (#17, #19): a running train is forecast on from the learned signals, track circuits and berths it has passed,
/// and a train standing at a red signal is told from a late one. 1A01 leaves A at 06:00 and passes C at 06:10 (its
/// 10 min booked run is the minimum); signals S2 and S3 are half and four fifths of the way.
/// </summary>
public sealed class ForecastSignalTests
{
    private static readonly TrainId Fast = new("1A01");
    private static readonly Tiploc A = new("A");
    private static readonly Tiploc C = new("C");

    private static readonly WorkingTimetable Tt = Of(Train("1A01", points: [Call("A", null, "06:00"), Pass("C", "06:10"), Call("D", "06:15", null)]));

    private static readonly Corridor Main = new(
        "main",
        "Main",
        [new CorridorLocation(A, 0), new CorridorLocation(C, 0.5), new CorridorLocation(new Tiploc("D"), 1)],
        [new Section(A, C, Section.DoubleLine, 180, 600), new Section(C, new Tiploc("D"), Section.DoubleLine, 180, 300)]);

    /// <summary>Hand-set places for trains starting from A: S2 at 0.5 (S3 next), S3 at 0.8, T5 in S2's block at 0.6, berth 0003 (S3's) at 0.7.</summary>
    private static SignallingPlaces Places(RunKind run = RunKind.Starting)
    {
        var places = SignallingPlaces.Empty
            .SetByHand(SignallingKind.Signal, "S2", A, C, 0.5, run)
            .SetByHand(SignallingKind.Signal, "S3", A, C, 0.8, run)
            .SetByHand(SignallingKind.TrackCircuit, "T5", A, C, 0.6, run)
            .SetByHand(SignallingKind.Berth, "0003", A, C, 0.7, run);
        places = places.With(SignallingKind.Signal, [.. places.Signals.Select(p => p.Id == "S2" ? p with { Next = new Dictionary<string, int> { ["S3"] = 5, ["S9"] = 1 } } : p)]);
        places = places.With(SignallingKind.TrackCircuit, [.. places.TrackCircuits.Select(p => p with { Signal = "S2" })]);
        return places.With(SignallingKind.Berth, [.. places.Berths.Select(p => p with { Signal = "S3" })]);
    }

    private static ForecastNetwork Network(SignallingPlaces? places = null) =>
        ForecastNetwork.Build([Main], Tt).WithPlaces(UsablePlaces.Of(places ?? Places()));

    /// <summary>1A01 (uid 10) with these reports; a location named S… is a signal, with the aspect passed after a colon (<c>S2:2</c>).</summary>
    private static LiveTrain Train1A01(params (string Location, string Time, TrainLocationAction Action)[] actuals) =>
        new("10",
            [new LiveWorking(new Headcode("1A01"), 0, MatchStatus.Matched, Fast, MatchMethod.HeadcodeAndTime)],
            actuals.Select(a =>
            {
                var parts = a.Location.Split(':');
                var signal = parts[0].StartsWith('S');
                return new ActualEvent(new Tiploc(parts[0]), a.Action, SimTime.Parse(a.Time), "", !signal, 0, parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : signal ? 6 : null);
            }).ToImmutableList(),
            false);

    private static LineSideState LineSide(params GatewayEvent[] events) => events.Aggregate(LineSideState.Empty, (s, e) => s.With(e));

    private static SignallingStateEvent Signal(string id, int aspect, string time) =>
        new(DateTimeOffset.UnixEpoch, "test", SignallingObjectType.Signal, "signal", id, SimTime.Parse(time), new Dictionary<string, string> { ["aspect"] = aspect.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static SignallingStateEvent Track(string id, bool clear, string time) =>
        new(DateTimeOffset.UnixEpoch, "test", SignallingObjectType.Track, "track", id, SimTime.Parse(time), new Dictionary<string, string> { ["clear"] = clear ? "True" : "False" });

    private static TdStepEvent Step(string to, string description, string time) =>
        new(DateTimeOffset.UnixEpoch, TdStepKind.Step, "test", "0001", to, description, SimTime.Parse(time));

    private static Forecast Forecast(string now, LiveTrain[] live, ForecastNetwork? network = null, LineSideState? lineSide = null, bool signalPlaces = true) =>
        Forecaster.Run(new ForecastRequest(WorkingPlan.FromTimetable(Tt))
        {
            LiveTrains = live,
            Now = SimTime.Parse(now),
            Network = network ?? Network(),
            LineSide = lineSide ?? LineSideState.Empty,
            Options = new ForecastOptions { Interaction = false, SignalPlaces = signalPlaces },
        });

    [Fact]
    public void A_signal_passed_moves_the_forecast_to_its_place()
    {
        // Left A two minutes late and passed S2, half way, four minutes later: the rest is half the minimum, 5 min, so
        // C at 06:11 rather than 06:12 from the departure alone.
        var live = Train1A01(Dep("A", "06:02"), Passed("S2", "06:06"));
        var forecast = Forecast("06:06:10", [live]);

        var train = forecast.Find(Fast)!;
        train.Anchor!.Place.Should().BeEquivalentTo(new { Kind = SignallingKind.Signal, Id = "S2", From = A, To = C, Fraction = 0.5, Run = RunKind.Starting, Aspect = (int?)6 });
        train.Anchor.Share.Should().Be(0.5);
        train.Anchor.ToString().Should().Be("passed S2 06:06:00");
        Times(train)[1].Should().Be("C pass 06:11:00 F");

        Times(Forecast("06:06:10", [live], signalPlaces: false).Find(Fast)!)[1].Should().Be("C pass 06:12:00 F", "as before S38");
    }

    [Fact]
    public void A_train_running_slowly_in_the_section_keeps_its_pace_up_to_a_limit()
    {
        // 7½ min to half way, whose minimum is 5: half as slow again, so the 5 min left take 7½.
        var forecast = Forecast("06:09:40", [Train1A01(Dep("A", "06:02"), Passed("S2", "06:09:30"))]);
        Times(forecast.Find(Fast)!)[1].Should().Be("C pass 06:17:00 F");

        // Slower still is held to 1.5 times.
        Times(Forecast("06:12:10", [Train1A01(Dep("A", "06:02"), Passed("S2", "06:12"))]).Find(Fast)!)[1].Should().Be("C pass 06:19:30 F");
    }

    [Fact]
    public void A_place_of_another_kind_of_run_is_used_when_the_trains_own_is_not_learned()
    {
        // Only a place for trains running through A is learned; 1A01 started there.
        var forecast = Forecast("06:06:10", [Train1A01(Dep("A", "06:02"), Passed("S2", "06:06"))], Network(Places(RunKind.Through)));
        forecast.Find(Fast)!.Anchor!.Place!.Run.Should().Be(RunKind.Through);
        Times(forecast.Find(Fast)!)[1].Should().Be("C pass 06:11:00 F");
    }

    [Fact]
    public void A_track_circuit_entered_since_the_last_signal_moves_it_on()
    {
        var live = Train1A01(Dep("A", "06:00"), Passed("S2", "06:05"));
        var forecast = Forecast("06:06:50", [live], lineSide: LineSide(Track("T5", false, "06:06:00")));

        var train = forecast.Find(Fast)!;
        train.Anchor!.Place.Should().BeEquivalentTo(new { Kind = SignallingKind.TrackCircuit, Id = "T5", Block = "S2" });
        train.Anchor.Share.Should().Be(0.6);
        Times(train)[1].Should().Be("C pass 06:10:00 F");
        train.Anchor.ToString().Should().Be("entered track circuit T5 06:06:00");

        Forecast("06:06:50", [live], lineSide: LineSide(Track("T5", false, "06:04:00"))).Find(Fast)!.Anchor!.Place!.Id.Should().Be("S2", "occupied before the train passed S2: not by it");
    }

    [Fact]
    public void A_track_circuit_is_not_put_to_a_train_when_another_is_near()
    {
        var other = new LiveTrain("11", [new LiveWorking(new Headcode("2B01"), 0)], [new ActualEvent(new Tiploc("S2"), TrainLocationAction.Pass, SimTime.Parse("06:05:30"), "", false, 0)], false);
        var forecast = Forecast("06:06:50", [Train1A01(Dep("A", "06:00"), Passed("S2", "06:05")), other], lineSide: LineSide(Track("T5", false, "06:06:00")));

        forecast.Find(Fast)!.Anchor!.Place!.Id.Should().Be("S2");
    }

    [Fact]
    public void A_berth_its_description_stepped_into_moves_it_on()
    {
        var forecast = Forecast("06:07:10", [Train1A01(Dep("A", "06:00"), Passed("S2", "06:05"))], lineSide: LineSide(Step("0003", "1A01", "06:07:00")));

        var train = forecast.Find(Fast)!;
        train.Anchor!.Place.Should().BeEquivalentTo(new { Kind = SignallingKind.Berth, Id = "0003", Block = "S3" });
        train.Anchor.ToString().Should().Be("stepped into berth 0003 06:07:00");
    }

    [Fact]
    public void A_train_overdue_at_a_red_signal_ahead_is_standing_there()
    {
        // S3 is 3 min on from S2 at the minimum; red, and the train not there 1.2 × 3 min + 10 s after S2 (06:09:46): standing at
        // S3 since 06:09, and the last fifth of the run (2 min) is forecast from now.
        var live = Train1A01(Dep("A", "06:00"), Passed("S2:2", "06:06"));
        var red = LineSide(Signal("S3", 0, "06:05:30"));
        var forecast = Forecast("06:11:00", [live], lineSide: red);

        var train = forecast.Find(Fast)!;
        train.Standing.Should().Be(new StandingAtSignal("S3", SimTime.Parse("06:09"), A, C, 0.8, SimTime.Parse("06:11"), AspectKnown: true));
        train.Standing!.ToString().Should().Be("standing at S3 since 06:09:00");
        train.Anchor!.Place!.Id.Should().Be("S3");
        train.PossiblyStopped.Should().BeFalse();
        Times(train)[1].Should().Be("C pass 06:13:00 F");

        Forecast("06:09:40", [live], lineSide: red).Find(Fast)!.Standing.Should().BeNull("not yet overdue at S3");
        Forecast("06:11:00", [live], lineSide: LineSide(Signal("S3", 0, "06:05:30"), Signal("S3", 4, "06:10:50"))).Find(Fast)!.Standing.Should().BeNull("S3 has cleared");
    }

    [Fact]
    public void Without_the_TD_topic_a_single_yellow_passed_says_the_signal_ahead_is_red()
    {
        var yellow = Forecast("06:11:00", [Train1A01(Dep("A", "06:00"), Passed("S2:2", "06:06"))]).Find(Fast)!;
        yellow.Standing!.AspectKnown.Should().BeFalse();

        Forecast("06:11:00", [Train1A01(Dep("A", "06:00"), Passed("S2:6", "06:06"))]).Find(Fast)!.Standing.Should().BeNull("passed at green");
    }

    [Fact]
    public void The_signal_ahead_is_the_one_trains_pass_next_not_the_nearest()
    {
        // A parallel line's S4 is nearer than S3, but trains passing S2 go on to S3.
        var places = Places();
        places = places.SetByHand(SignallingKind.Signal, "S4", A, C, 0.6, RunKind.Starting);
        var forecast = Forecast("06:11:00", [Train1A01(Dep("A", "06:00"), Passed("S2", "06:06"))], Network(places), LineSide(Signal("S4", 0, "06:00"), Signal("S3", 0, "06:00")));

        forecast.Find(Fast)!.Standing!.Signal.Should().Be("S3");
        Forecast("06:11:00", [Train1A01(Dep("A", "06:00"), Passed("S2", "06:06"))], Network(places), LineSide(Signal("S4", 0, "06:00"))).Find(Fast)!.Standing.Should().BeNull("S3 is not red");
    }
}
