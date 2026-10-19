using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using static Stringline.Graph.Tests.TestData;

namespace Stringline.Graph.Tests;

/// <summary>PDF export of the graph (GR-10, S17): a vector page, A3 landscape by default.</summary>
public sealed partial class GraphPdfTests
{
    private static GraphScene Scene() =>
        GraphSceneBuilder.Build(new GraphSceneRequest(Profile, TestCorridor)
        {
            Timetable = TwoTrains(),
            LiveTrains = [Live("901", "1A01", MatchStatus.Matched, "1A01", ("A", TrainLocationAction.Depart, "07:01:30"), ("B", TrainLocationAction.Pass, "07:06:00"))],
            Now = SimTime.FromClock(7, 8),
        });

    private static GraphViewport View(int width = 1200, int height = 700) =>
        GraphViewport.Create(SimTime.FromClock(6, 45).Seconds, SimTime.FromClock(7, 30).Seconds, width, height);

    private static string Render(PdfPage? page = null, GraphViewport? view = null)
    {
        using var renderer = new SkiaGraphRenderer();
        // Latin-1 keeps every byte as one character, so the binary streams do not disturb the text around them.
        return Encoding.Latin1.GetString(renderer.RenderPdf(Scene(), view ?? View(), page: page, title: "Test graph"));
    }

    private static (double Width, double Height) MediaBox(string pdf)
    {
        var match = MediaBoxPattern().Match(pdf);
        match.Success.Should().BeTrue("a page has a media box");
        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Is_one_A3_landscape_page_by_default_with_fonts_embedded()
    {
        var pdf = Render();

        pdf.Should().StartWith("%PDF-");
        pdf.TrimEnd().Should().EndWith("%%EOF");
        var (width, height) = MediaBox(pdf);
        width.Should().BeApproximately(1190.55, 0.5, "A3 is 420 mm wide (Skia rounds the box to whole points)");
        height.Should().BeApproximately(841.89, 0.5, "and 297 mm high");
        PagePattern().Matches(pdf).Should().ContainSingle();
        pdf.Should().Contain("/FontFile2", "the graph's fonts are embedded, so it prints the same anywhere");
        pdf.Should().Contain("(Stringline)").And.Contain("(Test graph)");
    }

    [Fact]
    public void Other_page_sizes()
    {
        var (width, height) = MediaBox(Render(PdfPage.A4Landscape));
        (width, height).Should().BeEquivalentTo((841.89, 595.28), o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 0.5)).WhenTypeIs<double>());

        MediaBox(Render(PdfPage.OfView(View(900, 500)), View(900, 500))).Should().Be((900, 500));
    }

    [Fact]
    public void The_view_is_laid_out_to_fill_the_page_inside_its_margins()
    {
        var view = View(1600, 900) with { AxisStart = 0.2, AxisEnd = 0.7 };

        var fitted = PdfPage.A3Landscape.Fit(view);

        (fitted.TimeStart, fitted.TimeEnd, fitted.AxisStart, fitted.AxisEnd).Should().Be((view.TimeStart, view.TimeEnd, 0.2, 0.7), "the same time and corridor range");
        fitted.Width.Should().Be(1133, "420 mm less 2 × 10 mm, in whole points");
        fitted.Height.Should().Be(785);
        fitted.Margins.Should().Be(view.Margins, "text and margins keep their size");
        PdfPage.OfView(view).Fit(view).Should().Be(view);
    }

    [Theory]
    [InlineData("a3", "A3 landscape")]
    [InlineData("A4", "A4 landscape")]
    [InlineData(" view ", "view size")]
    [InlineData("letter", null)]
    [InlineData(null, null)]
    public void Pages_by_name(string? name, string? expected) => PdfPage.Parse(name, View())?.Name.Should().Be(expected);

    [GeneratedRegex(@"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]")]
    private static partial Regex MediaBoxPattern();

    [GeneratedRegex(@"/Type\s*/Page\b(?!s)")]
    private static partial Regex PagePattern();
}
