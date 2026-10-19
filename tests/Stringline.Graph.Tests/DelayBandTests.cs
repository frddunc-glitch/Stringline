using SkiaSharp;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>Colouring actual lines by delay band (GR-7), segment by segment (Fred, S9).</summary>
public sealed class DelayBandTests
{
    /// <summary>1A01 is booked A 07:00, B 07:04, C 07:08: on time at A, 6 min late at B, 22 min at C.</summary>
    private static readonly LiveTrain Losing = Live("901", "1A01", MatchStatus.Matched, "1A01",
        ("A", TrainLocationAction.Depart, "07:00:30"), ("B", TrainLocationAction.Pass, "07:10:00"), ("C", TrainLocationAction.Pass, "07:30:00"));

    private static readonly LiveTrain Unknown = Live("902", "9Z99", MatchStatus.Unmatched, null,
        ("D", TrainLocationAction.Depart, "06:50"), ("C", TrainLocationAction.Arrive, "06:58"));

    private static GraphScene Scene(GraphColourMode mode, params LiveTrain[] trains) => GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
    {
        Timetable = TwoTrains(),
        LiveTrains = trains.Length == 0 ? [Losing, Unknown] : trains,
        ColourMode = mode,
    });

    [Theory]
    [InlineData(-3, DelayBand.OnTime)]
    [InlineData(0, DelayBand.OnTime)]
    [InlineData(1, DelayBand.Slight)]
    [InlineData(5, DelayBand.Slight)]
    [InlineData(6, DelayBand.Late)]
    [InlineData(15, DelayBand.Late)]
    [InlineData(16, DelayBand.VeryLate)]
    public void Bands_follow_the_design(int minutes, DelayBand band) => DelayBands.BandOf(minutes).Should().Be(band);

    [Fact]
    public void Seconds_round_towards_zero_first()
    {
        DelayBands.BandOfSeconds(59).Should().Be(DelayBand.OnTime);
        DelayBands.BandOfSeconds(60).Should().Be(DelayBand.Slight);
        DelayBands.BandOfSeconds(359).Should().Be(DelayBand.Slight);
        DelayBands.BandOfSeconds(-120).Should().Be(DelayBand.OnTime);
        DelayBands.ColourOfSeconds(null).Should().Be(DelayBands.Unknown);
    }

    [Fact]
    public void Each_segment_takes_the_band_where_it_arrives()
    {
        var path = Scene(GraphColourMode.Delay).InLayer(GraphLayer.Actual).Single(p => p.LiveUid == "901");

        path.SegmentColours.Should().Equal(DelayBands.Late, DelayBands.VeryLate);
        path.Colour.Should().Be(DelayBands.VeryLate, "the path's own colour is its latest delay");
        path.ColourOf(0).Should().Be(DelayBands.Late);
    }

    [Fact]
    public void Unmatched_trains_stay_grey_and_category_mode_is_unchanged()
    {
        var delay = Scene(GraphColourMode.Delay);
        var category = Scene(GraphColourMode.Category);

        delay.InLayer(GraphLayer.Actual).Single(p => p.LiveUid == "902").Colour.Should().Be(GraphSceneBuilder.UnmatchedColour);
        category.InLayer(GraphLayer.Actual).Should().OnlyContain(p => p.SegmentColours == null);
        category.InLayer(GraphLayer.Actual).Single(p => p.LiveUid == "901").Colour.Should().Be(Express);
        delay.InLayer(GraphLayer.Wtt).Select(p => p.Colour).Should().Equal(category.InLayer(GraphLayer.Wtt).Select(p => p.Colour), "the WTT keeps category colours");
    }

    [Fact]
    public void A_train_in_one_band_has_no_segment_colours()
    {
        var onTime = Live("903", "1A01", MatchStatus.Matched, "1A01",
            ("A", TrainLocationAction.Depart, "07:00:20"), ("B", TrainLocationAction.Pass, "07:04:10"), ("C", TrainLocationAction.Pass, "07:08:50"));

        var path = Scene(GraphColourMode.Delay, onTime).InLayer(GraphLayer.Actual).Single();

        path.SegmentColours.Should().BeNull();
        path.Colour.Should().Be(DelayBands.OnTime);
    }

    [Theory]
    [InlineData(new[] { 0, 400, 1400 }, new[] { "Late", "VeryLate" })]
    [InlineData(new[] { -1, 400, -1 }, new[] { "Late", "Late" })] // an unbooked point carries the last delay on
    [InlineData(new[] { -1, -1, 400, 100 }, new[] { "Late", "Late", "Slight" })] // segments before the first booked point take its delay
    [InlineData(new[] { -1, -1 }, new[] { "Unknown" })]
    public void Unbooked_points_carry_the_nearest_known_delay(int[] delays, string[] expected)
    {
        // -1 stands for a point the train is not booked at.
        var list = delays.Select(d => d < 0 ? (int?)null : d).ToList();
        var (colour, segments) = GraphSceneBuilder.DelayColours(list);

        (segments ?? Enumerable.Repeat(colour, list.Count - 1)).Select(Name).Should().Equal(expected);
    }

    [Fact]
    public void Segments_are_drawn_in_their_own_colours()
    {
        var view = GraphViewport.Create(SimTime.FromClock(6, 50).Seconds, SimTime.FromClock(7, 40).Seconds, 800, 400);
        using var renderer = new SkiaGraphRenderer();
        using var bitmap = SKBitmap.Decode(renderer.RenderPng(Scene(GraphColourMode.Delay, Losing), view));

        // A→B (orange) halfway: 07:05:15 at ⅙; B→C (red) halfway: 07:20 at ½.
        var orange = Pixel(bitmap, view.ToScreen(SimTime.Parse("07:05:15").Seconds, 1.0 / 6));
        var red = Pixel(bitmap, view.ToScreen(SimTime.Parse("07:20:00").Seconds, 0.5));
        orange.Red.Should().BeGreaterThan(200);
        orange.Green.Should().BeInRange(80, 140);
        red.Red.Should().BeGreaterThan(160);
        red.Green.Should().BeLessThan(70);
    }

    private static SKColor Pixel(SKBitmap bitmap, ScreenPoint p) => bitmap.GetPixel((int)Math.Round(p.X), (int)Math.Round(p.Y));

    private static string Name(Network.Profiles.Colour c) =>
        c == DelayBands.OnTime ? "OnTime" : c == DelayBands.Slight ? "Slight" : c == DelayBands.Late ? "Late" : c == DelayBands.VeryLate ? "VeryLate" : "Unknown";
}
