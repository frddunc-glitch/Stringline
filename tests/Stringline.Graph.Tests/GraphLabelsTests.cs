using System.Collections.Immutable;
using Stringline.Core;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>#3: the label margin fits the names, long ones are shortened, and the hover gives them in full.</summary>
public sealed class GraphLabelsTests
{
    /// <summary>Forty characters, which just fit in the most room.</summary>
    private const string LongName = "Clapham Junction Windsor Lines Platforms";

    /// <summary>Longer than the most room, so shortened.</summary>
    private const string TooLong = "Clapham Junction Windsor Lines Platforms 1 to 19 (Up side)";

    private static Network.Profiles.StringlineProfile Named(string? c = null) => Profile with
    {
        LocationNames = ImmutableDictionary<Tiploc, string>.Empty.Add(new Tiploc("A"), "Alpha").Add(new Tiploc("C"), c ?? "Charlie"),
    };

    private static GraphScene Scene(string? c = null) =>
        GraphSceneBuilder.Build(new GraphSceneRequest(Named(c), TestCorridor) { Timetable = TwoTrains(), Now = SimTime.FromClock(7, 12) });

    private static GraphViewport View(GraphScene scene, GraphOrientation orientation, int width = 800, int height = 400) =>
        GraphViewport.Create(SimTime.FromClock(6, 45).Seconds, SimTime.FromClock(7, 30).Seconds, width, height,
            GraphLabels.MarginsFor(orientation, scene.Locations), orientation);

    [Fact]
    public void Short_names_get_the_least_room_and_long_ones_the_most()
    {
        LongName.Length.Should().Be(40);
        GraphLabels.RoomFor(Scene().Locations).Should().Be(GraphLabels.MinRoom);
        GraphLabels.RoomFor(Scene(LongName).Locations).Should().BeLessThan(GraphLabels.MaxRoom, "forty characters show in full");
        GraphLabels.RoomFor(Scene(TooLong).Locations).Should().Be(GraphLabels.MaxRoom);
        var middling = GraphLabels.RoomFor(Scene("Clapham Junction Platform 17").Locations);
        middling.Should().BeGreaterThan(GraphLabels.MinRoom).And.BeLessThan(GraphLabels.MaxRoom);
    }

    [Fact]
    public void Margins_leave_the_room_for_the_labels_in_either_orientation()
    {
        foreach (var orientation in new[] { GraphOrientation.Horizontal, GraphOrientation.Vertical })
        {
            var view = GraphViewport.Create(0, 3600, 800, 600, GraphLabels.MarginsFor(orientation, 200), orientation);
            GraphLabels.RoomIn(view).Should().Be(200);
        }

        GraphLabels.MarginsFor(GraphOrientation.Horizontal, 138).Should().Be(GraphMargins.Default, "the old fixed margin is 138 px of room");
    }

    [Fact]
    public void A_name_too_long_is_shortened_with_an_ellipsis()
    {
        using var font = GraphFonts.Create(GraphFonts.Bold, GraphTheme.Light.LocationFontSize);
        GraphLabels.Fit("Alpha", font, 200).Should().Be("Alpha");

        var fitted = GraphLabels.Fit(LongName, font, 120);
        fitted.Should().EndWith("…").And.StartWith("Clapham");
        font.MeasureText(fitted).Should().BeLessThanOrEqualTo(120);
        font.MeasureText(GraphLabels.Fit(LongName + "x", font, 120)).Should().BeLessThanOrEqualTo(120);
        GraphLabels.Fit(LongName, font, 1).Should().Be("…");
    }

    [Theory]
    [InlineData(GraphOrientation.Horizontal)]
    [InlineData(GraphOrientation.Vertical)]
    public void The_hover_over_a_label_gives_the_full_name_and_tiploc(GraphOrientation orientation)
    {
        var scene = Scene(LongName);
        var view = View(scene, orientation);
        var c = scene.Locations.Single(l => l.Tiploc.Code == "C");
        var along = view.AxisToPixel(c.Axis);
        var at = orientation == GraphOrientation.Vertical ? new ScreenPoint(along, view.PlotTop - 40) : new ScreenPoint(view.PlotLeft - 40, along);

        var hit = GraphLabels.LocationAt(scene, view, at);

        hit.Should().Be(c);
        var info = GraphLabels.Describe(hit!);
        info.Heading.Should().Be(LongName);
        info.ValueOf("TIPLOC").Should().Be("C");
        info.ValueOf("Platforms").Should().Be("2");
        info.ValueOf("Tip").Should().Contain("Edit profile");
    }

    [Fact]
    public void No_label_is_found_in_the_plot_or_between_labels()
    {
        var scene = Scene();
        var view = View(scene, GraphOrientation.Horizontal);
        var a = view.AxisToPixel(0);
        var b = view.AxisToPixel(1.0 / 3);

        GraphLabels.LocationAt(scene, view, new ScreenPoint(view.PlotLeft + 50, a)).Should().BeNull("that is the plot");
        GraphLabels.LocationAt(scene, view, new ScreenPoint(view.PlotLeft - 20, (a + b) / 2)).Should().BeNull("that is between labels");
    }

    [Fact]
    public void The_navigator_keeps_the_fitted_room_when_the_graph_turns()
    {
        var nav = new GraphNavigator(950, 500);
        nav.FitLabels(Scene(TooLong).Locations);
        GraphLabels.RoomIn(nav.Viewport).Should().Be(GraphLabels.MaxRoom);

        nav.SetOrientation(GraphOrientation.Vertical);
        GraphLabels.RoomIn(nav.Viewport).Should().Be(GraphLabels.MaxRoom);
        nav.Viewport.Margins.Should().Be(GraphLabels.MarginsFor(GraphOrientation.Vertical, GraphLabels.MaxRoom));

        var changes = 0;
        nav.Changed += (_, _) => changes++;
        nav.FitLabels(Scene(TooLong).Locations);
        changes.Should().Be(0, "the same room moves nothing");
        nav.FitLabels(Scene().Locations);
        changes.Should().Be(1);
        GraphLabels.RoomIn(nav.Viewport).Should().Be(GraphLabels.MinRoom);
    }

    [Fact]
    public Task Long_name_snapshot()
    {
        var scene = Scene(LongName);
        return SkiaGraphRendererTests.VerifyPng(scene, View(scene, GraphOrientation.Horizontal));
    }

    [Fact]
    public Task Too_long_name_snapshot()
    {
        var scene = Scene(TooLong);
        return SkiaGraphRendererTests.VerifyPng(scene, View(scene, GraphOrientation.Horizontal));
    }

    [Fact]
    public Task Long_name_vertical_snapshot()
    {
        var scene = Scene(LongName);
        return SkiaGraphRendererTests.VerifyPng(scene, View(scene, GraphOrientation.Vertical, 400, 800));
    }
}
