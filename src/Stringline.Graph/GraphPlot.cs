using System.Globalization;
using Stringline.Core;

namespace Stringline.Graph;

/// <summary>
/// What a plot covers and how (S21, Fred: a multi-page plot): a time range at a fixed scale of
/// <see cref="SecondsPerPage"/> a page, on <see cref="Page"/>, time running down (<see cref="GraphOrientation.Vertical"/>,
/// the default, as on a printed train control diagram) or across.
/// </summary>
public sealed record GraphPlotOptions(SimTime From, SimTime To)
{
    /// <summary>The most pages a plot may take, so a slip of the keyboard does not make thousands.</summary>
    public const int MaxPages = 100;

    /// <summary>Two hours a page: an A3 portrait page then gives about 3 mm a minute.</summary>
    public const int DefaultSecondsPerPage = 2 * SimTime.SecondsPerHour;

    public int SecondsPerPage { get; init; } = DefaultSecondsPerPage;

    public PdfPage Page { get; init; } = PdfPage.A3Portrait;

    public GraphOrientation Orientation { get; init; } = GraphOrientation.Vertical;

    /// <summary>How many pages the range takes at this scale.</summary>
    public int PageCount => To > From && SecondsPerPage > 0 ? (int)Math.Ceiling((double)(To.Seconds - From.Seconds) / SecondsPerPage) : 0;

    /// <summary>What is wrong with these options, or null if they can be plotted.</summary>
    public string? Problem =>
        To <= From ? "The plot must end after it starts."
        : SecondsPerPage < GraphViewport.MinTimeSpan ? $"A page must cover at least {GraphViewport.MinTimeSpan / SimTime.SecondsPerMinute:0} minutes."
        : SecondsPerPage > GraphViewport.MaxTimeSpan ? $"A page can cover at most {GraphViewport.MaxTimeSpan / SimTime.SecondsPerHour:0} hours."
        : PageCount > MaxPages ? $"That would take {PageCount} pages; the most is {MaxPages}. Plot less time, or more a page."
        : null;
}

/// <summary>One page of a plot: its number, the time it covers and the viewport it is drawn through.</summary>
public sealed record GraphPlotPage(int Number, int Count, SimTime From, SimTime To, GraphViewport Viewport)
{
    /// <summary>The page's heading after the scene's title: "Main — 06:00–08:00 — page 2 of 6".</summary>
    public string Heading(string title) =>
        string.Create(CultureInfo.InvariantCulture, $"{title} — {From.ToString("t", CultureInfo.InvariantCulture)}–{To.ToString("t", CultureInfo.InvariantCulture)} — page {Number} of {Count}");
}

/// <summary>
/// Lays a plot out on pages (S21): each page shows the whole corridor and the next <see cref="GraphPlotOptions.SecondsPerPage"/>
/// of the range, at the same scale on every page, with the location labels and time labels repeated. The last page is
/// cut short where the range ends rather than stretched, so the scale never changes.
/// </summary>
public static class GraphPlot
{
    /// <summary>The pages for a scene; throws if <see cref="GraphPlotOptions.Problem"/> says the options cannot be plotted.</summary>
    public static IReadOnlyList<GraphPlotPage> Layout(GraphScene scene, GraphPlotOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Problem is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        var margins = GraphLabels.MarginsFor(options.Orientation, scene.Locations);
        var page = options.Page;
        var full = GraphViewport.Create(0, options.SecondsPerPage, page.InnerWidth, page.InnerHeight, margins, options.Orientation);
        var count = options.PageCount;
        var pages = new List<GraphPlotPage>(count);
        for (var i = 0; i < count; i++)
        {
            var from = options.From.Seconds + (i * options.SecondsPerPage);
            var to = Math.Min(options.To.Seconds, from + options.SecondsPerPage);
            var shortBy = (int)Math.Floor(full.TimeLength * (1 - ((double)(to - from) / options.SecondsPerPage)));
            var viewport = (full with { TimeStart = from, TimeEnd = to }).Resize(
                full.IsVertical ? full.Width : full.Width - shortBy,
                full.IsVertical ? full.Height - shortBy : full.Height);
            pages.Add(new GraphPlotPage(i + 1, count, new SimTime(from), new SimTime(to), viewport));
        }

        return pages;
    }
}
