using Stringline.Core.Events;
using Stringline.Core.Live;

namespace Stringline.Core.Tests;

/// <summary>S38: the signals, track circuits and berths as last reported (<see cref="LineSideState"/>).</summary>
public sealed class LineSideStateTests
{
    private static SignallingStateEvent Sg(SignallingObjectType type, string id, string field, string value, string? time) =>
        new(DateTimeOffset.UnixEpoch, "test", type, type.ToString().ToLowerInvariant(), id, time is null ? null : SimTime.Parse(time), new Dictionary<string, string> { [field] = value });

    private static TdStepEvent Td(TdStepKind kind, string from, string to, string description, string time) =>
        new(DateTimeOffset.UnixEpoch, kind, "test", from, to, description, SimTime.Parse(time));

    [Fact]
    public void A_signal_keeps_its_aspect_and_since_when()
    {
        var red = LineSideState.Empty.With(Sg(SignallingObjectType.Signal, "S457", "aspect", "0", "06:00"));
        var same = red.With(Sg(SignallingObjectType.Signal, "S457", "aspect", "0", "06:01"));
        var green = red.With(Sg(SignallingObjectType.Signal, "s457", "aspect", "6", "06:02"));

        red.Signal("S457").Should().Be(new SignalState(0, SimTime.Parse("06:00")));
        red.Signal("S457")!.Value.IsRed.Should().BeTrue();
        same.Should().BeSameAs(red, "nothing changed");
        green.Signal("S457").Should().Be(new SignalState(6, SimTime.Parse("06:02")));
        red.Signal("S1").Should().BeNull();
    }

    [Fact]
    public void A_track_circuit_remembers_when_it_was_last_occupied_after_it_clears()
    {
        var occupied = LineSideState.Empty.With(Sg(SignallingObjectType.Track, "T810", "clear", "False", "06:00"));
        var clear = occupied.With(Sg(SignallingObjectType.Track, "T810", "clear", "True", "06:00:12"));

        occupied.Track("T810").Should().Be(new TrackState(false, SimTime.Parse("06:00"), SimTime.Parse("06:00")));
        clear.Track("T810").Should().Be(new TrackState(true, SimTime.Parse("06:00:12"), SimTime.Parse("06:00")));
        LineSideState.Empty.With(Sg(SignallingObjectType.Track, "T1", "clear", "True", "06:00")).Track("T1")!.Value.LastOccupied.Should().BeNull();
    }

    [Fact]
    public void Berths_follow_steps_cancels_and_interposes()
    {
        var state = LineSideState.Empty
            .With(Td(TdStepKind.Interpose, "", "0457", "1A01", "06:00"))
            .With(Td(TdStepKind.Step, "0457", "0459", "1A01", "06:01"));

        state.Berth("0457").Should().BeNull();
        state.Berth("0459").Should().Be(new BerthState("1A01", SimTime.Parse("06:01")));
        state.With(Td(TdStepKind.Cancel, "0459", "", "", "06:02")).Berths.Should().BeEmpty();
    }

    [Fact]
    public void An_event_without_a_time_takes_the_clock_or_is_skipped()
    {
        var untimed = Sg(SignallingObjectType.Signal, "S1", "aspect", "2", null);

        LineSideState.Empty.With(untimed).Should().BeSameAs(LineSideState.Empty);
        LineSideState.Empty.With(untimed, SimTime.Parse("07:00")).Signal("S1").Should().Be(new SignalState(2, SimTime.Parse("07:00")));
        LineSideState.Empty.With(Sg(SignallingObjectType.Route, "R1A", "is_set", "True", "07:00")).Should().BeSameAs(LineSideState.Empty);
    }

    [Theory]
    [InlineData(0, "red")]
    [InlineData(1, "shunt")]
    [InlineData(2, "single yellow")]
    [InlineData(4, "double yellow")]
    [InlineData(6, "green")]
    [InlineData(9, "aspect 9")]
    public void Aspects_are_named_as_SimSig_names_them(int aspect, string name) => SignalAspects.Name(aspect).Should().Be(name);
}
