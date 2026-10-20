using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Stringline.Core;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>Multi-page plots (S21): a time range at one scale across pages, vertical on A3 portrait by default.</summary>
public sealed partial class GraphPlotTests
{
    private static GraphScene Scene() =>
        GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor) { Timetable = TwoTrains(), Now = SimTime.FromClock(7, 8) });

    private static GraphPlotOptions Options(int fromHour, int toHour, int toMinute = 0) =>
        new(SimTime.FromClock(fromHour, 0), SimTime.FromClock(toHour, toMinute));

    [Fact]
    public void Defaults_to_two_hours_a_page_time_down_on_A3_portrait()
    {
        var options = Options(6, 12);

        options.Orientation.Should().Be(GraphOrientation.Vertical);
        options.Page.Should().Be(PdfPage.A3Portrait);
        options.Page.IsPortrait.Should().BeTrue();
        options.SecondsPerPage.Should().Be(7200);
        options.PageCount.Should().Be(3);
        options.Problem.Should().BeNull();
    }

    [Fact]
    public void Each_page_shows_the_whole_corridor_over_its_share_of_the_time()
    {
        var pages = GraphPlot.Layout(Scene(), Options(6, 12));

        pages.Select(p => (p.Number, p.Count, p.From, p.To)).Should().Equal(
            (1, 3, SimTime.FromClock(6, 0), SimTime.FromClock(8, 0)),
            (2, 3, SimTime.FromClock(8, 0), SimTime.FromClock(10, 0)),
            (3, 3, SimTime.FromClock(10, 0), SimTime.FromClock(12, 0)));
        pages.Should().AllSatisfy(p =>
        {
            p.Viewport.IsVertical.Should().BeTrue();
            p.Viewport.ShowsFullAxis.Should().BeTrue();
            p.Viewport.Width.Should().Be(PdfPage.A3Portrait.InnerWidth);
            p.Viewport.Height.Should().Be(PdfPage.A3Portrait.InnerHeight);
        });
        pages[1].Heading("Waterloo — Main").Should().Be("Waterloo — Main — 08:00–10:00 — page 2 of 3");
    }

    [Fact]
    public void The_last_page_is_cut_short_at_the_same_scale()
    {
        var pages = GraphPlot.Layout(Scene(), Options(6, 8, 30));

        pages.Should().HaveCount(2);
        var (full, last) = (pages[0].Viewport, pages[1].Viewport);
        last.TimeSpan.Should().Be(1800);
        last.PixelsPerMinute.Should().BeApproximately(full.PixelsPerMinute, 0.05, "every page has the same scale");
        last.Height.Should().BeLessThan(full.Height);
        last.Margins.Should().Be(full.Margins, "the labels are repeated on every page");
    }

    [Fact]
    public void Horizontal_plots_run_time_across()
    {
        var pages = GraphPlot.Layout(Scene(), Options(6, 7, 30) with { Orientation = GraphOrientation.Horizontal, Page = PdfPage.A4Landscape, SecondsPerPage = 3600 });

        pages.Should().HaveCount(2);
        pages[0].Viewport.IsVertical.Should().BeFalse();
        pages[1].Viewport.Width.Should().BeLessThan(pages[0].Viewport.Width);
        pages[1].Viewport.Height.Should().Be(pages[0].Viewport.Height);
    }

    [Fact]
    public void Says_what_is_wrong_with_options_it_cannot_plot()
    {
        Options(8, 6).Problem.Should().Contain("end after it starts");
        (Options(6, 8) with { SecondsPerPage = 60 }).Problem.Should().Contain("at least 5 minutes");
        (Options(0, 23) with { SecondsPerPage = 600 }).Problem.Should().Contain("138 pages");
        FluentActions.Invoking(() => GraphPlot.Layout(Scene(), Options(8, 6))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Renders_one_pdf_page_for_each_page_of_the_plot()
    {
        using var renderer = new SkiaGraphRenderer();

        var pdf = Encoding.Latin1.GetString(renderer.RenderPlot(Scene(), Options(6, 11), "Test plot"));

        pdf.Should().StartWith("%PDF-");
        PagePattern().Matches(pdf).Should().HaveCount(3);
        var box = MediaBoxPattern().Match(pdf);
        double.Parse(box.Groups[1].Value, CultureInfo.InvariantCulture).Should().BeApproximately(841.89, 0.5, "A3 portrait is 297 mm wide");
        double.Parse(box.Groups[2].Value, CultureInfo.InvariantCulture).Should().BeApproximately(1190.55, 0.5, "and 420 mm high");
        pdf.Should().Contain("(Test plot)");
    }

    [Theory]
    [InlineData("a3p", "A3 portrait")]
    [InlineData("A4-portrait", "A4 portrait")]
    public void Portrait_pages_by_name(string name, string expected) =>
        PdfPage.Parse(name, GraphViewport.Create(0, 3600, 800, 600))!.Name.Should().Be(expected);

    [GeneratedRegex(@"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]")]
    private static partial Regex MediaBoxPattern();

    [GeneratedRegex(@"/Type\s*/Page\b(?!s)")]
    private static partial Regex PagePattern();
}
