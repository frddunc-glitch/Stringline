using Stringline.Core.Timetables;

namespace Stringline.Core.Tests;

public sealed class SimTitleTests
{
    private static TimetableHeader Header(string name = "Timetable B 2012", string? version = "1.0.0", string area = "llangollen") =>
        new(area, "4.5.13.8", name, string.Empty, version, SimTime.FromClock(6, 0), SimTime.FromClock(23, 0));

    [Fact]
    public void Names_the_sim_and_the_timetable_with_its_version()
    {
        SimTitle.From("Llangollen Railway", "llangollen", Header()).ToString().Should().Be("Llangollen Railway – Timetable B 2012 (v1.0.0)");
    }

    [Fact]
    public void Falls_back_from_the_profile_name_to_the_area_id_tidied_then_to_unknown()
    {
        SimTitle.From(null, "llangollen", Header()).ToString().Should().Be("Llangollen – Timetable B 2012 (v1.0.0)");
        SimTitle.From("  ", null, Header()).Sim.Should().Be("Llangollen", "the header's area ID is used when the gateway's is not known");
        SimTitle.From(null, "waterloo", (TimetableHeader?)null).ToString().Should().Be("Waterloo");
        SimTitle.From(null, null, (TimetableHeader?)null).ToString().Should().Be(SimTitle.UnknownSim);
        SimTitle.From(null, " ", Header(area: string.Empty)).ToString().Should().Be("Unknown sim – Timetable B 2012 (v1.0.0)");
    }

    [Fact]
    public void Prefers_the_gateways_area_id_to_the_timetables()
    {
        SimTitle.From(null, "llangollen2", Header()).Sim.Should().Be("Llangollen2");
    }

    [Fact]
    public void A_profile_name_that_is_only_the_timetable_title_counts_as_no_name()
    {
        SimTitle.From("timetable b 2012", "llangollen", Header()).Sim.Should().Be("Llangollen");
    }

    [Fact]
    public void Leaves_out_a_version_that_is_missing_or_already_in_the_name()
    {
        SimTitle.From(null, "x", Header(version: null)).Timetable.Should().Be("Timetable B 2012");
        SimTitle.From(null, "x", Header(name: "WTT v1.0.0")).Timetable.Should().Be("WTT v1.0.0");
        SimTitle.From(null, "x", Header(name: " ")).Timetable.Should().BeNull("a nameless timetable shows only the sim");
    }

    [Fact]
    public void Review_mode_is_marked()
    {
        SimTitle.From("Penrith", null, Header(), review: true).ToString().Should().Be("Penrith – Timetable B 2012 (v1.0.0) (review)");
        new SimTitle("Penrith", Review: true).ToString().Should().Be("Penrith (review)");
    }

    [Theory]
    [InlineData("waterloo", "Waterloo")]
    [InlineData("north_wales_coast", "North Wales Coast")]
    [InlineData("leeds-ardsley", "Leeds Ardsley")]
    [InlineData("SwindonA", "SwindonA")]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void Tidies_area_ids(string? id, string? expected)
    {
        SimTitle.TidyAreaId(id).Should().Be(expected);
    }
}
