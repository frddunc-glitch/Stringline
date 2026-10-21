using System.Collections.Immutable;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Xunit.Abstractions;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>S37 (#19): learning signal, track circuit and berth places from play (<see cref="SignalLearner"/>).</summary>
public sealed class SignalLearnerTests(ITestOutputHelper output)
{
    private static readonly Tiploc A = new("A");
    private static readonly Tiploc B = new("B");

    /// <summary>Timing locations A and B; anything else a train reports is a signal.</summary>
    private static readonly Core.Timetables.WorkingTimetable Tt = Of(Train("1A01", points: [Call("A", null, "06:00"), Pass("B", "06:10")]));

    private static TrainLocationEvent Signal(string uid, string headcode, string signal, string time, int? approach = 6) =>
        Location(uid, headcode, signal, time) with { AspectPassed = 6, AspectApproach = approach };

    /// <summary>A train passing A, then S1 at 40 % of its run and S2 at 75 %, then B, starting at <paramref name="start"/> (minutes past 06:00).</summary>
    private static IEnumerable<GatewayEvent> Run(string uid, int start, int seconds = 100, int? approach = 6, string headcode = "1A01")
    {
        var t0 = SimTime.FromClock(6, start);
        yield return Location(uid, headcode, "A", t0.ToString(), platform: "UF");
        yield return Signal(uid, headcode, "S1", (t0 + (seconds * 4 / 10)).ToString(), approach);
        yield return Signal(uid, headcode, "S2", (t0 + (seconds * 3 / 4)).ToString());
        yield return Location(uid, headcode, "B", (t0 + seconds).ToString());
    }

    private static SignalLearner Fed(IEnumerable<GatewayEvent> events)
    {
        var learner = new SignalLearner();
        foreach (var e in events)
        {
            learner.Observe(e);
        }

        return learner;
    }

    private static SignallingPlaces Places(LearnedPlaces learned) =>
        SignallingPlaces.Empty.Learn(learned.Samples, "test", learned.UpTo ?? default, [], out _);

    [Fact]
    public void A_known_place_comes_back()
    {
        var learned = Fed([.. Run("1", 0), .. Run("2", 10), .. Run("3", 20)]).Learn(Tt);
        var places = Places(learned);

        learned.Runs.Should().Be(3);
        learned.RunsUsed.Should().Be(3);
        places.Signals.Select(p => (p.Id, p.From, p.To, p.Fraction, p.SampleCount)).Should().Equal(("S1", A, B, 0.4, 3), ("S2", A, B, 0.75, 3));
        places.Signals[0].Line.Should().Be("UF");
        places.Signals.Should().OnlyContain(p => p.StateFor(3) == PlaceState.Used && p.Run == RunKind.Through);
        learned.UpTo.Should().Be(SimTime.Parse("06:21:40"));
    }

    [Fact]
    public void Too_few_samples_stay_learning()
    {
        var places = Places(Fed([.. Run("1", 0), .. Run("2", 10)]).Learn(Tt));

        places.Signals.Should().HaveCount(2).And.OnlyContain(p => p.StateFor(SignallingPlaces.DefaultMinSamples) == PlaceState.Learning);
    }

    [Fact]
    public void A_train_checked_at_a_signal_is_left_out()
    {
        var learned = Fed([.. Run("1", 0), .. Run("2", 10, approach: 0), .. Run("3", 20)]).Learn(Tt);

        learned.RunsUsed.Should().Be(2, "uid 2 saw S1 at red on approach");
        Places(learned).Signals.Should().OnlyContain(p => p.SampleCount == 2);
    }

    [Fact]
    public void A_slow_run_is_left_out()
    {
        var learned = Fed([.. Run("1", 0), .. Run("2", 10, seconds: 200), .. Run("3", 20)]).Learn(Tt);

        learned.RunsUsed.Should().Be(2, "twice the fastest run is more than 1.5 times it");
        Places(learned).Signals.Should().OnlyContain(p => p.SampleCount == 2 && p.Fraction != 0.2);
    }

    [Fact]
    public void Stopping_and_starting_runs_have_places_of_their_own()
    {
        IEnumerable<GatewayEvent> Stopping(string uid, int start)
        {
            var t0 = SimTime.FromClock(6, start);
            yield return Location(uid, "1A01", "A", t0.ToString());
            yield return Signal(uid, "1A01", "S1", (t0 + 30).ToString());
            yield return Location(uid, "1A01", "B", (t0 + 120).ToString(), TrainLocationAction.Arrive);
            yield return Location(uid, "1A01", "B", (t0 + 180).ToString(), TrainLocationAction.Depart);
        }

        var places = Places(Fed([.. Run("1", 0), .. Stopping("2", 10), .. Stopping("3", 20)]).Learn(Tt));

        places.Find(SignallingKind.Signal, "S1", A, B)!.Fraction.Should().Be(0.4);
        places.Find(SignallingKind.Signal, "S1", A, B, RunKind.Stopping)!.Should().Match<SignallingPlace>(p => p.Fraction == 0.25 && p.SampleCount == 2);
    }

    [Fact]
    public void A_dwell_and_a_reversal_give_no_run()
    {
        var learned = Fed(
        [
            Location("1", "1A01", "A", "06:00", TrainLocationAction.Arrive),
            Signal("1", "1A01", "S9", "06:01"),
            Location("1", "1A01", "A", "06:02", TrainLocationAction.Depart),
            Location("1", "1A01", "A", "06:03"),
        ]).Learn(Tt);

        learned.Runs.Should().Be(0);
        learned.Samples.Should().BeEmpty();
    }

    [Fact]
    public void A_replay_rewind_starts_again()
    {
        var learner = Fed([.. Run("1", 0), .. Run("2", 10)]);
        learner.Observe(new ReplayRewoundEvent(T0, SimTime.Parse("06:00")));
        foreach (var e in Run("1", 0))
        {
            learner.Observe(e);
        }

        learner.Learn(Tt).Runs.Should().Be(1);
    }

    private static SignallingStateEvent Track(string id, string time, bool clear) =>
        new(T0, "test", SignallingObjectType.Track, "track", id, SimTime.Parse(time), ImmutableDictionary<string, string>.Empty.Add("clear", clear ? "True" : "False"));

    /// <summary>Track circuit T1 starts just past S1 (40 %) and clears as the train passes S2 (75 %).</summary>
    private static IEnumerable<GatewayEvent> RunWithTrack(string uid, int start)
    {
        var t0 = SimTime.FromClock(6, start);
        foreach (var e in Run(uid, start))
        {
            yield return e;
            if (e is TrainLocationEvent { Location.Code: "S1" })
            {
                yield return Track("T1", (t0 + 50).ToString(), clear: false);
            }
            else if (e is TrainLocationEvent { Location.Code: "S2" })
            {
                yield return Track("T1", (t0 + 75).ToString(), clear: true);
            }
        }
    }

    [Fact]
    public void A_track_circuit_is_put_to_the_train_past_the_signal_before_it()
    {
        var learned = Fed([.. RunWithTrack("1", 0), .. RunWithTrack("2", 10), .. RunWithTrack("3", 20)]).Learn(Tt);

        learned.Tracks.Should().Be(new TrackAttribution(3, 3, 0, 0, 0, 3));
        var t1 = Places(learned).TrackCircuits.Should().ContainSingle().Subject;
        t1.Should().Match<SignallingPlace>(p => p.Id == "T1" && p.Fraction == 0.5 && p.EndFraction == 0.75 && p.Signal == "S1" && p.SampleCount == 3);
    }

    [Fact]
    public void A_track_circuit_with_two_trains_near_is_skipped()
    {
        // Uids 1 and 2 pass S1 together (as on two parallel lines); the occupation could be either.
        var learned = Fed([.. RunWithTrack("1", 0), .. RunWithTrack("2", 10), .. RunWithTrack("3", 20), .. Run("9", 20, headcode: "2B02")]).Learn(Tt);

        learned.Tracks.Attributed.Should().Be(2);
        learned.Tracks.Ambiguous.Should().Be(1);
        Places(learned).TrackCircuits.Single().SampleCount.Should().Be(2);
    }

    [Fact]
    public void A_track_circuit_occupied_with_no_train_near_is_skipped()
    {
        var learned = Fed([.. RunWithTrack("1", 0), .. RunWithTrack("2", 10), .. RunWithTrack("3", 20), Track("T1", "09:00", clear: false)]).Learn(Tt);

        learned.Tracks.Should().Match<TrackAttribution>(t => t.Occupations == 4 && t.Attributed == 3 && t.NoTrain == 1, "a failure, not a train");
    }

    [Fact]
    public void A_berth_step_is_placed_by_the_train_whose_headcode_it_is()
    {
        IEnumerable<GatewayEvent> WithStep(string uid, int start)
        {
            foreach (var e in Run(uid, start))
            {
                yield return e;
                if (e is TrainLocationEvent { Location.Code: "S1" } s1)
                {
                    yield return new TdStepEvent(T0, TdStepKind.Step, "test", "0001", "0002", "1A01", s1.Time);
                }
            }
        }

        var learned = Fed([.. WithStep("1", 0), .. WithStep("2", 10), .. Run("9", 10, headcode: "2B02")]).Learn(Tt);

        learned.StepsPlaced.Should().Be(2);
        Places(learned).Berths.Should().ContainSingle().Which.Should().Match<SignallingPlace>(p => p.Id == "0002" && p.Fraction == 0.4 && p.Signal == "S2", "berth 0002 belongs to signal S2");
        SignalLearner.SignalOf("0457").Should().Be("S457");
        SignalLearner.SignalOf("4205").Should().Be("S4205");
        SignalLearner.SignalOf("R021").Should().BeNull();
        SignalLearner.SignalOf("0000").Should().BeNull();
    }

    [Fact]
    public void Session_ids_are_stable()
    {
        var created = new DateTimeOffset(2026, 10, 21, 15, 36, 42, 871, TimeSpan.FromHours(11));

        SignalLearning.RecordingId(created).Should().Be("recording 2026-10-21T04:36:42.871Z");
        SignalLearning.SessionId(created).Should().Be("session 2026-10-21T04:36:42.871Z");
    }

    [Fact]
    public void Learning_into_a_profile_counts_what_is_used()
    {
        var learned = Fed([.. Run("1", 0), .. Run("2", 10), .. Run("3", 20)]).Learn(Tt);
        var profile = StringlineProfile.Create(Tt);

        var outcome = SignalLearning.Apply(profile, learned, "test");

        outcome.Added.Should().Be(6);
        outcome.Signals.Should().Be(new PlaceCounts(2, 0, 0));
        outcome.Describe("test").Should().Be("Learned 6 new samples from test: signals 2 used, 0 learning; track circuits 0 used, 0 learning; berths 0 used, 0 learning.");
        SignalLearning.Apply(outcome.Profile, learned, "test").Added.Should().Be(0, "already learned");
    }

    /// <summary>
    /// S37's gate on Waterloo (free sim): nearly every signal passed by three or more trains is placed with a small
    /// spread, track circuits are mostly put to one train, and places learned from the 4-hour recording agree with
    /// those from the 4× one (05:52–08:05, no TD).
    /// </summary>
    [Fact]
    public void Waterloo_places_are_learned_and_stable()
    {
        var tt = new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable;
        var profile = StringlineProfile.Create(tt);
        var fourHours = Learn(tt, Fixtures.Waterloo4h);
        var fourX = Learn(tt, Fixtures.Waterloo4x);
        var long4h = SignalLearning.Apply(profile, fourHours, "4h").Profile.Places;
        var short4x = SignalLearning.Apply(profile, fourX, "4x").Profile.Places;

        var passed = long4h.Signals.Where(p => p.SampleCount >= 3).ToList();
        var used = passed.Count(p => p.IsUsable(3));
        var moves = long4h.Signals.Where(p => p.IsUsable(3))
            .Select(p => short4x.Find(SignallingKind.Signal, p.Id, p.From, p.To, p.Run) is { } q && q.IsUsable(3) ? Math.Abs(p.Fraction - q.Fraction) : (double?)null)
            .OfType<double>().Order().ToList();
        output.WriteLine($"{passed.Count} signals passed 3+ times, {used} used; tracks {fourHours.Tracks}; {moves.Count} in both, most move {moves[^1]:0.000}");

        fourHours.UpTo.Should().Be(SimTime.Parse("08:30:08"));
        ((double)used / passed.Count).Should().BeGreaterThan(0.95);
        fourHours.Tracks.AttributedShare.Should().BeGreaterThan(0.8);
        PlaceCounts.Of(long4h.TrackCircuits, 3).Used.Should().BeGreaterThan(200);
        PlaceCounts.Of(long4h.Berths, 3).Used.Should().BeGreaterThan(100);
        moves.Count.Should().BeGreaterThan(100);
        moves[^1].Should().BeLessThan(SignallingPlaces.MaxSpread / 2, "the same signals are placed the same from another recording");
        fourX.Tracks.Should().Be(TrackAttribution.None, "the 4× recording has no TD topic");
    }

    private static LearnedPlaces Learn(Core.Timetables.WorkingTimetable tt, string fixture)
    {
        var parser = new MessageParser();
        var learner = new SignalLearner();
        foreach (var e in SslogRecording.Load(Fixtures.Path(fixture)).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())))
        {
            learner.Observe(e);
        }

        return learner.Learn(tt);
    }
}
