using System.Globalization;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Graph;

namespace Stringline.App.ViewModels;

/// <summary>
/// The Plot dialog (S21; File ▸ Export ▸ Plot… and, on a draft board, Draft ▸ Export ▸ Plot…, S44): the corridor,
/// the times, the minutes a page, the paper and which way time runs, starting from the choices made last time.
/// </summary>
internal static class PlotPrompt
{
    /// <summary>Asks for a plot; null if cancelled. The choices to remember next time come back with it.</summary>
    /// <param name="tab">The corridor offered first.</param>
    /// <param name="now">The time the plot starts an hour before, rounded down to the hour.</param>
    public static (GraphPlotOptions Plot, CorridorTab Corridor, PlotSettings Saved)? Ask(IDialogService dialogs, IReadOnlyList<CorridorTab> corridors, CorridorTab tab, SimTime now, PlotSettings saved)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(saved);
        var start = new SimTime(Math.Max(0, (now.Seconds - SimTime.SecondsPerHour) / SimTime.SecondsPerHour * SimTime.SecondsPerHour));
        var end = start + (int)Math.Round(Math.Clamp(saved.Hours, 0.5, 48) * SimTime.SecondsPerHour);
        var paper = PdfPage.Paper.FirstOrDefault(p => p.Name == saved.Paper) ?? PdfPage.A3Portrait;
        string[] directions = ["Down the page", "Across the page"];
        GraphPlotOptions? options = null;
        CorridorTab? chosen = null;
        var prompt = new PromptViewModel(
            "Plot the graph",
            "Plot a corridor over a stretch of time, at the same scale on every page:",
            new PromptField("Corridor", tab.Name, [.. corridors.Select(c => c.Name)]),
            new PromptField("From", PlanInput.Format(start)),
            new PromptField("To", PlanInput.Format(end), hint: "A time past midnight takes the next day; or add +1, as 00:30+1."),
            new PromptField("Minutes a page", saved.MinutesPerPage.ToString(CultureInfo.InvariantCulture), ["30", "60", "120", "180", "240"], "Two hours a page gives about 3 mm a minute on A3."),
            new PromptField("Paper", paper.Name, [.. PdfPage.Paper.Select(p => p.Name)]),
            new PromptField("Time runs", directions[saved.Across ? 1 : 0], directions))
        {
            AcceptText = "Plot…",
            Validate = p =>
            {
                chosen = corridors.FirstOrDefault(c => c.Name == p[0]);
                if (chosen is null)
                {
                    return "Choose a corridor from the list.";
                }

                if (!PlanInput.TryParseTime(p[1], now, out var from) || !PlanInput.TryParseTime(p[2], from + (12 * SimTime.SecondsPerHour), out var to))
                {
                    return "Enter the times as HH:mm, such as 06:00 and 12:00.";
                }

                if (!int.TryParse(p[3], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                {
                    return "Enter the minutes a page as a whole number, such as 120.";
                }

                if (PdfPage.Paper.FirstOrDefault(page => page.Name == p[4]) is not { } page)
                {
                    return "Choose the paper from the list.";
                }

                if (!directions.Contains(p[5]))
                {
                    return "Choose which way time runs from the list.";
                }

                options = new GraphPlotOptions(from, to)
                {
                    SecondsPerPage = minutes * SimTime.SecondsPerMinute,
                    Page = page,
                    Orientation = p[5] == directions[1] ? GraphOrientation.Horizontal : GraphOrientation.Vertical,
                };
                return options.Problem;
            },
        };
        if (!dialogs.Prompt(prompt) || options is not { } plot || chosen is not { } corridor)
        {
            return null;
        }

        var remember = new PlotSettings
        {
            Hours = (plot.To.Seconds - plot.From.Seconds) / (double)SimTime.SecondsPerHour,
            MinutesPerPage = plot.SecondsPerPage / SimTime.SecondsPerMinute,
            Paper = plot.Page.Name,
            Across = plot.Orientation == GraphOrientation.Horizontal,
        };
        return (plot, corridor, remember);
    }
}
