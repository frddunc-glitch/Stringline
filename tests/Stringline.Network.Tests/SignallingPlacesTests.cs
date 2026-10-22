using Stringline.Core;
using Stringline.Network.Profiles;

namespace Stringline.Network.Tests;

/// <summary>S37: learned signal, track circuit and berth places in the profile (<see cref="SignallingPlaces"/>).</summary>
public sealed class SignallingPlacesTests
{
    private static readonly Tiploc A = new("A");
    private static readonly Tiploc B = new("B");
    private static readonly Tiploc C = new("C");

    private static readonly Corridor Main = new(
        "main",
        "Main",
        [new CorridorLocation(A, 0), new CorridorLocation(B, 0.5), new CorridorLocation(C, 1)],
        [new Section(A, B, Section.DoubleLine, 180, 120), new Section(B, C, Section.DoubleLine, 180, 120)]);

    private static PlaceSample Sample(string id, double fraction, string at, Tiploc? from = null, Tiploc? to = null, SignallingKind kind = SignallingKind.Signal, RunKind run = RunKind.Through, string? line = null) =>
        new(kind, id, from ?? A, to ?? B, fraction, SimTime.Parse(at), Line: line, Run: run);

    private static SignallingPlaces Learn(SignallingPlaces places, string session, string upTo, params PlaceSample[] samples) =>
        places.Learn(samples, session, SimTime.Parse(upTo), [Main], out _);

    [Fact]
    public void Learning_pools_samples_into_a_median_place()
    {
        var places = Learn(SignallingPlaces.Empty, "one", "06:30", Sample("S1", 0.40, "06:01"), Sample("S1", 0.42, "06:11"), Sample("S1", 0.41, "06:21"));

        var s1 = places.Signals.Should().ContainSingle().Subject;
        s1.Fraction.Should().Be(0.41);
        s1.SampleCount.Should().Be(3);
        s1.Spread.Should().BeApproximately(0.01, 1e-9);
        s1.Direction.Should().Be(TrackDirection.Down, "A to B runs down the corridor");
        s1.LastLearned.Should().Be(SimTime.Parse("06:21"));
        s1.LastSession.Should().Be("one");
        s1.StateFor(3).Should().Be(PlaceState.Used);
        s1.StateFor(4).Should().Be(PlaceState.Learning, "too few samples for that threshold");
        places.FindSession("one").Should().Be(new LearnedSession("one", SimTime.Parse("06:30")));
    }

    [Fact]
    public void Too_few_or_spread_samples_stay_learning()
    {
        var places = Learn(SignallingPlaces.Empty, "one", "07:00",
            Sample("S1", 0.4, "06:01"), Sample("S1", 0.4, "06:02"),
            Sample("S2", 0.2, "06:01"), Sample("S2", 0.5, "06:02"), Sample("S2", 0.8, "06:03"), Sample("S2", 0.3, "06:04"));

        places.Find(SignallingKind.Signal, "S1", A, B)!.StateFor(3).Should().Be(PlaceState.Learning, "two samples");
        places.Find(SignallingKind.Signal, "S2", A, B)!.Spread.Should().BeGreaterThan(SignallingPlaces.MaxSpread);
        places.Find(SignallingKind.Signal, "S2", A, B)!.StateFor(3).Should().Be(PlaceState.Learning, "the samples spread too far");
    }

    [Fact]
    public void A_session_is_never_learned_twice()
    {
        var samples = new[] { Sample("S1", 0.4, "06:01"), Sample("S1", 0.4, "06:11") };
        var once = Learn(SignallingPlaces.Empty, "one", "06:15", samples);

        var again = Learn(once, "one", "06:15", samples);
        again.Should().BeEquivalentTo(once, "a review of the same session adds nothing");

        var resumed = once.Learn([.. samples, Sample("S1", 0.5, "06:20")], "one", SimTime.Parse("06:30"), [Main], out var added);
        added.Should().Be(1, "only the run after 06:15 is new");
        resumed.Signals[0].SampleCount.Should().Be(3);
        resumed.FindSession("one")!.UpTo.Should().Be(SimTime.Parse("06:30"));

        var other = Learn(once, "two", "06:15", samples);
        other.Signals[0].SampleCount.Should().Be(4, "another session's runs are new");
    }

    [Fact]
    public void Places_are_kept_apart_by_direction_and_kind_of_run()
    {
        var places = Learn(SignallingPlaces.Empty, "one", "07:00",
            Sample("S1", 0.69, "06:01"), Sample("S1", 0.60, "06:02", run: RunKind.Stopping), Sample("S1", 0.3, "06:03", B, A));

        places.Signals.Should().HaveCount(3);
        places.Find(SignallingKind.Signal, "S1", A, B)!.Fraction.Should().Be(0.69);
        places.Find(SignallingKind.Signal, "S1", A, B, RunKind.Stopping)!.Fraction.Should().Be(0.60);
        places.Find(SignallingKind.Signal, "S1", B, A)!.Direction.Should().Be(TrackDirection.Up);
        places.Between(SignallingKind.Signal, A, B).Should().HaveCount(3);
        SignallingPlaces.RunOf(departed: true, arrived: true).Should().Be(RunKind.StartingAndStopping);
    }

    [Fact]
    public void A_line_is_kept_only_while_trains_share_it()
    {
        var shared = Learn(SignallingPlaces.Empty, "one", "07:00", Sample("S1", 0.4, "06:01", line: "UF"), Sample("S1", 0.4, "06:02", line: "UF"), Sample("S1", 0.4, "06:03"));
        shared.Signals[0].Line.Should().Be("UF");

        var mixed = Learn(shared, "two", "07:00", Sample("S1", 0.4, "06:04", line: "US"));
        mixed.Signals[0].Line.Should().BeNull();
        mixed.Signals[0].MixedLines.Should().BeTrue();
        Learn(mixed, "three", "07:00", Sample("S1", 0.4, "06:05", line: "UF")).Signals[0].Line.Should().BeNull("once mixed, always mixed");
    }

    [Fact]
    public void A_hand_set_place_is_not_moved_by_learning()
    {
        var places = SignallingPlaces.Empty.SetByHand(SignallingKind.Signal, "S1", A, B, 0.25);
        places.Signals[0].StateFor(3).Should().Be(PlaceState.HandSet);

        places = Learn(places, "one", "07:00", Sample("S1", 0.6, "06:01"), Sample("S1", 0.6, "06:02"), Sample("S1", 0.6, "06:03"));
        places.Signals[0].Fraction.Should().Be(0.25);
        places.Signals[0].SampleCount.Should().Be(3, "the samples are still kept");
        places.Signals[0].IsUsable(3).Should().BeTrue();
    }

    [Fact]
    public void Editing_by_hand_moves_adds_and_removes()
    {
        var places = Learn(SignallingPlaces.Empty, "one", "07:00", Sample("S1", 0.6, "06:01"), Sample("T1", 0.5, "06:01", kind: SignallingKind.TrackCircuit));

        places = places.SetByHand(SignallingKind.Signal, "s1", A, B, 1.5).SetByHand(SignallingKind.Berth, "0001", B, C, 0.1, corridors: [Main]);
        places.Signals[0].Should().Match<SignallingPlace>(p => p.Fraction == 1 && p.HandSet && p.SampleCount == 1, "clamped, and the same place by ID in any case");
        places.Berths.Should().ContainSingle(p => p.Id == "0001" && p.HandSet && p.Direction == TrackDirection.Down);

        places = places.Remove(SignallingKind.TrackCircuit, "T1", A, B);
        places.TrackCircuits.Should().BeEmpty();
        var act = () => places.SetByHand(SignallingKind.Signal, "S9", A, A, 0.5);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reset_forgets_learned_places_but_keeps_hand_set_ones()
    {
        var places = Learn(SignallingPlaces.Empty, "one", "07:00", Sample("S1", 0.6, "06:01"), Sample("S2", 0.3, "06:01", B, A), Sample("S3", 0.5, "06:02", B, C))
            .SetByHand(SignallingKind.Signal, "S4", A, B, 0.9);

        var section = places.Reset(A, B);
        section.Signals.Select(p => p.Id).Should().BeEquivalentTo(["S3", "S4"], "both directions of A–B go, the hand-set S4 stays");
        section.Sessions.Should().HaveCount(1, "the session is still remembered, so it is not learned again");

        var all = places.ResetAll();
        all.Signals.Select(p => p.Id).Should().Equal("S4");
        all.Sessions.Should().HaveCount(1);
        Learn(all, "one", "07:00", Sample("S1", 0.6, "06:01")).Signals.Select(p => p.Id).Should().Equal(["S4"], "the running session's earlier runs do not come back");
    }

    [Fact]
    public void Pooling_an_imported_profile_adds_samples_and_keeps_hand_set_places()
    {
        var mine = Learn(SignallingPlaces.Empty, "one", "07:00", Sample("S1", 0.40, "06:01"), Sample("S1", 0.40, "06:02"), Sample("S2", 0.5, "06:01"))
            .SetByHand(SignallingKind.Signal, "S2", A, B, 0.55);
        var theirs = Learn(SignallingPlaces.Empty, "two", "08:00", Sample("S1", 0.44, "07:01"), Sample("S1", 0.44, "07:02"), Sample("S2", 0.7, "07:01"), Sample("S3", 0.8, "07:01"))
            .SetByHand(SignallingKind.Signal, "S3", A, B, 0.85);

        var pooled = mine.Pool(theirs);

        pooled.Find(SignallingKind.Signal, "S1", A, B)!.Should().Match<SignallingPlace>(p => p.SampleCount == 4 && p.Samples.Count == 4 && Math.Abs(p.Fraction - 0.42) < 1e-9);
        pooled.Find(SignallingKind.Signal, "S2", A, B)!.Should().Match<SignallingPlace>(p => p.Fraction == 0.55 && p.HandSet && p.SampleCount == 2, "my hand-set place stays put");
        pooled.Find(SignallingKind.Signal, "S3", A, B)!.Should().Match<SignallingPlace>(p => p.Fraction == 0.85 && p.HandSet, "theirs is new here");
        pooled.Sessions.Select(s => s.Id).Should().Equal("one", "two");
    }

    [Fact]
    public void Samples_kept_are_capped_but_all_are_counted()
    {
        var samples = Enumerable.Range(0, SignallingPlaces.MaxKeptSamples + 10).Select(i => Sample("S1", 0.5, SimTime.FromClock(6, 0).AddSeconds(i).ToString())).ToArray();

        var place = Learn(SignallingPlaces.Empty, "one", "07:00", samples).Signals[0];

        place.Samples.Should().HaveCount(SignallingPlaces.MaxKeptSamples);
        place.SampleCount.Should().Be(SignallingPlaces.MaxKeptSamples + 10);
    }

    [Fact]
    public void Statistics()
    {
        SignallingPlaces.Median([]).Should().Be(0);
        SignallingPlaces.Median([0.3, 0.1, 0.2]).Should().Be(0.2);
        SignallingPlaces.Median([0.1, 0.2, 0.3, 0.4]).Should().BeApproximately(0.25, 1e-12);
        SignallingPlaces.InterquartileRange([0.5]).Should().Be(0);
        SignallingPlaces.InterquartileRange([0.1, 0.2, 0.3, 0.4, 0.5]).Should().BeApproximately(0.2, 1e-12);
    }

    [Fact]
    public void Json_round_trips_the_places()
    {
        var places = Learn(SignallingPlaces.Empty, "session 2026-10-21T04:36:42.871Z", "07:00",
                Sample("S1", 0.4, "06:01", line: "UF"),
                new PlaceSample(SignallingKind.TrackCircuit, "T1", A, B, 0.45, SimTime.Parse("06:01"), 0.55, Signal: "S1", Run: RunKind.Stopping),
                Sample("0001", 0.38, "06:01", kind: SignallingKind.Berth))
            .SetByHand(SignallingKind.Signal, "S2", B, C, 0.5);
        var profile = new StringlineProfile { AreaId = "test", Corridors = [Main] }.WithPlaces(places);

        var json = ProfileJson.Serialize(profile);
        var back = ProfileJson.Deserialize(json);

        back.Places.Should().BeEquivalentTo(places);
        back.Version.Should().Be(2, "an older Stringline ignores the places, so the version stays");
        json.Should().Contain("\"signalling\"").And.Contain("\"trackCircuits\"").And.Contain("\"run\": \"Stopping\"").And.Contain("\"handSet\": true");
        json.Should().NotContain("\"run\": \"Through\"", "the usual kind is left out");
    }

    [Fact]
    public void The_signals_passed_next_are_counted_pooled_and_saved()
    {
        PlaceSample Next(string next, string at) => Sample("S1", 0.4, at) with { Next = next };
        var places = Learn(SignallingPlaces.Empty, "one", "06:30", Next("S3", "06:01"), Next("S3", "06:11"), Next("S5", "06:21"), Sample("S1", 0.4, "06:25"));

        places.Signals[0].Next.Should().BeEquivalentTo(new Dictionary<string, int> { ["S3"] = 2, ["S5"] = 1 });
        places.Pool(places).Signals[0].Next.Should().BeEquivalentTo(new Dictionary<string, int> { ["S3"] = 4, ["S5"] = 2 });
        Learn(SignallingPlaces.Empty, "one", "06:30", Sample("S1", 0.4, "06:01")).Signals[0].Next.Should().BeNull("none was seen");

        var profile = new StringlineProfile { AreaId = "test", Corridors = [Main] }.WithPlaces(places);
        var json = ProfileJson.Serialize(profile);
        json.Should().Contain("\"next\"");
        ProfileJson.Deserialize(json).Places.Signals[0].Next.Should().BeEquivalentTo(places.Signals[0].Next);
    }

    [Fact]
    public void Usable_places_leave_out_those_still_learning_and_choose_the_trains_kind_first()
    {
        var places = Learn(SignallingPlaces.Empty, "one", "06:30",
                Sample("S1", 0.40, "06:01"), Sample("S1", 0.41, "06:02"), Sample("S1", 0.42, "06:03"),
                Sample("S1", 0.30, "06:04", run: RunKind.Stopping), Sample("S1", 0.31, "06:05", run: RunKind.Stopping), Sample("S1", 0.30, "06:06", run: RunKind.Stopping),
                Sample("S2", 0.8, "06:07"))
            .SetByHand(SignallingKind.Signal, "S9", A, B, 0.9);

        var usable = UsablePlaces.Of(places);

        usable.SignalFrom("s1", A).Should().HaveCount(2);
        usable.SignalFrom("S2", A).Should().BeEmpty("one sample is still learning");
        usable.SignalsBetween(A, B).Select(p => p.Id).Should().Equal("S1", "S1", "S9");
        UsablePlaces.Choose(usable.SignalFrom("S1", A), RunKind.Stopping)!.Fraction.Should().Be(0.30);
        UsablePlaces.Choose(usable.SignalFrom("S1", A), RunKind.Starting)!.Run.Should().Be(RunKind.Through, "not learned for starting trains: the one with most samples (a tie keeps the first)");
        UsablePlaces.Of(places, minSamples: 4).SignalFrom("S1", A).Should().BeEmpty();
        UsablePlaces.Of(places, minSamples: 4).SignalsBetween(A, B).Should().ContainSingle(p => p.Id == "S9", "a hand-set place is always used");
        UsablePlaces.PositionOn(Main, A, B, 0.5).Should().Be(0.25);
        UsablePlaces.PositionOn(Main, B, A, 0.5).Should().Be(0.25);
        UsablePlaces.PositionOn(Main, A, new Tiploc("X"), 0.5).Should().BeNull();
    }

    [Fact]
    public void Track_circuits_are_found_by_the_place_their_block_starts_at()
    {
        var places = SignallingPlaces.Empty
            .SetByHand(SignallingKind.TrackCircuit, "T1", A, B, 0.45)
            .SetByHand(SignallingKind.TrackCircuit, "T2", A, B, 0.1)
            .SetByHand(SignallingKind.Berth, "0001", A, B, 0.38);
        places = places.With(SignallingKind.TrackCircuit, [.. places.TrackCircuits.Select(p => p.Id == "T1" ? p with { Signal = "S1" } : p)]);

        var usable = UsablePlaces.Of(places);

        usable.TracksAfter("S1").Select(p => p.Id).Should().Equal("T1");
        usable.TracksAfter("A").Select(p => p.Id).Should().Equal(["T2"], "no signal before it: its block starts at the timing location");
        usable.Berth("0001").Should().ContainSingle();
    }

    [Fact]
    public void A_profile_without_places_writes_none()
    {
        var profile = new StringlineProfile { AreaId = "test" };

        ProfileJson.Serialize(profile).Should().NotContain("signalling");
        profile.WithPlaces(SignallingPlaces.Empty).Signalling.Should().BeNull();
        ProfileJson.Deserialize(ProfileJson.Serialize(profile)).Places.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"id":"S1","from":"A","to":"B","fraction":1.2}""", "outside 0–1")]
    [InlineData("""{"id":"","from":"A","to":"B","fraction":0.2}""", "needs an ID")]
    [InlineData("""{"id":"S1","from":"A","to":"A","fraction":0.2}""", "two different locations")]
    public void Json_refuses_unusable_places(string place, string message)
    {
        var act = () => ProfileJson.Deserialize("""{"version":2,"areaId":"test","signalling":{"signals":[""" + place + "]}}");

        act.Should().Throw<ProfileFormatException>().WithMessage($"*{message}*");
    }

    private static RunSample Timed(int seconds, string at, Tiploc? from = null, Tiploc? to = null, RunKind run = RunKind.Through) =>
        new(from ?? A, to ?? B, run, seconds, SimTime.Parse(at));

    [Fact]
    public void Learning_pools_run_times_and_gives_a_fast_train_time()
    {
        var places = SignallingPlaces.Empty.Learn([], [Timed(100, "06:01"), Timed(90, "06:11"), Timed(130, "06:21"), Timed(95, "06:25"), Timed(70, "06:28", run: RunKind.Starting)], "one", SimTime.Parse("06:30"), [Main], out _);

        var through = places.Runs.Should().ContainSingle(r => r.Run == RunKind.Through).Subject;
        through.SampleCount.Should().Be(4);
        through.FastestSeconds.Should().Be(90);
        through.FastSeconds.Should().Be(90, "the lower quartile of 90, 95, 100, 130");
        places.LearnedRunSeconds(A, B, 3).Should().Be(90);
        places.LearnedRunSeconds(B, A, 3).Should().Be(90, "either direction");
        places.LearnedRunSeconds(A, B, 5).Should().BeNull("too few runs");
        places.LearnedRunSeconds(B, C, 1).Should().BeNull();
        places.IsEmpty.Should().BeFalse();

        var again = places.Learn([], [Timed(100, "06:01"), Timed(80, "06:40")], "one", SimTime.Parse("06:45"), [Main], out _);
        again.Runs.Single(r => r.Run == RunKind.Through).SampleCount.Should().Be(5, "only the run after 06:30 is new");

        var pooled = places.Pool(again);
        pooled.Runs.Single(r => r.Run == RunKind.Through).Should().Match<LearnedRun>(r => r.SampleCount == 9 && r.FastestSeconds == 80);
        places.Reset(B, A).Runs.Should().BeEmpty();
        places.ResetAll().Runs.Should().BeEmpty();
        places.FindProblem().Should().BeNull();
    }

    [Fact]
    public void Learned_run_times_space_the_derived_axis()
    {
        var places = SignallingPlaces.Empty.Learn([], [Timed(60, "06:01"), Timed(60, "06:02"), Timed(60, "06:03"), Timed(180, "06:04", B, C), Timed(180, "06:05", C, B), Timed(180, "06:06", B, C)], "one", SimTime.Parse("06:30"), [Main], out _);
        var profile = new StringlineProfile { AreaId = "test", Corridors = [Main] }.WithPlaces(places);

        var spaced = profile.WithLearnedRuns().Corridors[0];

        spaced.Sections.Select(s => s.LearnedRunSeconds).Should().Equal(60, 180);
        spaced.Locations.Select(l => l.AxisPosition).Should().Equal(0, 0.25, 1);
        profile.WithLearnedRuns(4).Corridors[0].Locations.Select(l => l.AxisPosition).Should().Equal(0, 0.5, 1);
        ProfileJson.Deserialize(ProfileJson.Serialize(profile.WithLearnedRuns())).Should().BeEquivalentTo(profile.WithLearnedRuns());
    }
}
