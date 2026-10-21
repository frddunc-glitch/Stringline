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
}
