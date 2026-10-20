using Stringline.Core.Events;
using Stringline.Core.Live;

namespace Stringline.Core.Tests;

public class SignallingIndexTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static SignallingStateEvent Sg(SignallingObjectType type, string id) =>
        new(T0, "test", type, type.ToString().ToLowerInvariant(), id, null, new Dictionary<string, string>());

    private static TdStepEvent Td(TdStepKind kind, string from, string to, string description) =>
        new(T0, kind, "test", from, to, description, null);

    [Fact]
    public void Collects_route_and_signal_IDs_in_order()
    {
        var index = SignallingIndex.Empty
            .With(Sg(SignallingObjectType.Route, "RWL2A"))
            .With(Sg(SignallingObjectType.Signal, "SWL2"))
            .With(Sg(SignallingObjectType.Route, "RWL1A"))
            .With(Sg(SignallingObjectType.Signal, " SWL1 "));

        index.Routes.Should().Equal("RWL1A", "RWL2A");
        index.Signals.Should().Equal("SWL1", "SWL2");
        index.Berths.Should().BeEmpty();
        index.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Returns_the_same_instance_when_nothing_is_new()
    {
        var index = SignallingIndex.Empty.With(Sg(SignallingObjectType.Route, "RWL1A")).With(Td(TdStepKind.Interpose, "", "0123", "1A01"));

        index.With(Sg(SignallingObjectType.Route, "RWL1A")).Should().BeSameAs(index);
        index.With(Sg(SignallingObjectType.Track, "T123")).Should().BeSameAs(index);
        index.With(Sg(SignallingObjectType.Point, "P1")).Should().BeSameAs(index);
        index.With(Td(TdStepKind.Interpose, "", "0123", "1A01")).Should().BeSameAs(index);
        index.With(new UnknownEvent(T0, "/topic/x", "odd", "{}", "test")).Should().BeSameAs(index);
        SignallingIndex.Empty.With(Sg(SignallingObjectType.Route, " ")).Should().BeSameAs(SignallingIndex.Empty);
    }

    [Fact]
    public void Follows_what_each_berth_holds()
    {
        var index = SignallingIndex.Empty
            .With(Td(TdStepKind.Interpose, "", "0101", "1A01"))
            .With(Td(TdStepKind.Step, "0101", "0103", "1A01"))
            .With(Td(TdStepKind.Interpose, "", "0201", "2B02"))
            .With(Td(TdStepKind.Cancel, "0201", "", ""));

        index.Berths.Should().Equal("0101", "0103", "0201");
        index.Descriptions.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("0103", "1A01"));
        index.BerthsOf("1a01").Should().Equal("0103");
        index.BerthsOf("2B02").Should().BeEmpty();
    }

    [Fact]
    public void An_empty_description_empties_the_berth()
    {
        var index = SignallingIndex.Empty.With(Td(TdStepKind.Interpose, "", "0101", "1A01")).With(Td(TdStepKind.Interpose, "", "0101", ""));

        index.Descriptions.Should().BeEmpty();
        index.Berths.Should().Equal("0101");
    }
}
