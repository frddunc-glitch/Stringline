using System.Text.Json.Serialization;

namespace Stringline.App.Services;

/// <summary>The main window's split view (S21): one graph, two one above the other, or two side by side.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BoardSplit>))]
internal enum BoardSplit
{
    None,
    Stacked,
    SideBySide,
}

/// <summary>A board window to open again at the next start (S21): its corridor and where it was.</summary>
internal sealed record BoardLayout(string? CorridorId, WindowPlacement? Window);

/// <summary>
/// The main window's layout (#9), remembered between runs: the share each column and row has, which panels are shown,
/// and where the window was. Widths and heights are star weights (the sizes last seen, in pixels), so the panels keep
/// their share whatever the window's size. No WPF types, so the view model and its tests can use it.
/// </summary>
internal sealed record WindowLayout
{
    public const double DefaultTrainListWidth = 330;
    public const double DefaultGraphWidth = 780;
    public const double DefaultSideWidth = 490;

    /// <summary>Least widths in pixels: below these, a panel's lists would need a horizontal scroll bar (#2).</summary>
    public const double MinTrainListWidth = 270;
    public const double MinGraphWidth = 300;
    public const double MinSideWidth = 450;

    /// <summary>Least heights in pixels of the train detail and the tabs under it.</summary>
    public const double MinDetailHeight = 140;
    public const double MinTabsHeight = 120;

    /// <summary>Least height of the suggestions under the conflict list, and its default.</summary>
    public const double MinSuggestionsHeight = 60;
    public const double DefaultSuggestionsHeight = 200;

    /// <summary>The least width or height of each graph in the split view (S21).</summary>
    public const double MinBoardSize = 150;

    /// <summary>The most board windows opened again at a start, so a damaged file cannot open hundreds.</summary>
    public const int MaxBoards = 8;

    public double TrainListWidth { get; init; } = DefaultTrainListWidth;

    public double GraphWidth { get; init; } = DefaultGraphWidth;

    public double SideWidth { get; init; } = DefaultSideWidth;

    /// <summary>The train detail's share of the side panel's height.</summary>
    public double DetailHeight { get; init; } = 2;

    /// <summary>The tabs' (Conflicts, Regulation, Delays, History) share of the side panel's height.</summary>
    public double TabsHeight { get; init; } = 1;

    /// <summary>The suggestions' height in pixels, under the conflict list.</summary>
    public double SuggestionsHeight { get; init; } = DefaultSuggestionsHeight;

    public bool ShowTrainList { get; init; } = true;

    public bool ShowSidePanel { get; init; } = true;

    public bool ShowTabs { get; init; } = true;

    /// <summary>Where the window was, in device-independent pixels; null the first time.</summary>
    public WindowPlacement? Window { get; init; }

    /// <summary>The split view (S21), off at first.</summary>
    public BoardSplit Split { get; init; }

    /// <summary>The main graph's share of the split (its size last seen, in pixels).</summary>
    public double MainBoardSize { get; init; } = 1;

    /// <summary>The split view's second graph's share.</summary>
    public double SplitBoardSize { get; init; } = 1;

    /// <summary>The corridor the split view's second graph last showed.</summary>
    public string? SplitCorridor { get; init; }

    /// <summary>The board windows open when the app closed (S21).</summary>
    public IReadOnlyList<BoardLayout> Boards { get; init; } = [];

    /// <summary>Equal when every value is, the board windows compared one by one rather than as lists.</summary>
    public bool Equals(WindowLayout? other) =>
        other is not null && Values == other.Values && Boards.SequenceEqual(other.Boards);

    public override int GetHashCode() => HashCode.Combine(Values, Boards.Count);

    private (double, double, double, double, double, double, bool, bool, bool, WindowPlacement?, BoardSplit, (double, double, string?)) Values =>
        (TrainListWidth, GraphWidth, SideWidth, DetailHeight, TabsHeight, SuggestionsHeight, ShowTrainList, ShowSidePanel, ShowTabs, Window, Split,
            (MainBoardSize, SplitBoardSize, SplitCorridor));

    /// <summary>The same layout with nonsense (from a hand-edited or damaged file) put back to the defaults.</summary>
    public WindowLayout Checked()
    {
        var defaults = new WindowLayout();
        return this with
        {
            TrainListWidth = Positive(TrainListWidth, defaults.TrainListWidth),
            GraphWidth = Positive(GraphWidth, defaults.GraphWidth),
            SideWidth = Positive(SideWidth, defaults.SideWidth),
            DetailHeight = Positive(DetailHeight, defaults.DetailHeight),
            TabsHeight = Positive(TabsHeight, defaults.TabsHeight),
            SuggestionsHeight = Math.Max(MinSuggestionsHeight, Positive(SuggestionsHeight, defaults.SuggestionsHeight)),
            Window = Window is { IsUsable: true } ? Window : null,
            Split = Enum.IsDefined(Split) ? Split : BoardSplit.None,
            MainBoardSize = Positive(MainBoardSize, defaults.MainBoardSize),
            SplitBoardSize = Positive(SplitBoardSize, defaults.SplitBoardSize),
            Boards = [.. (Boards ?? []).Where(b => b is not null).Take(MaxBoards).Select(b => b with { Window = b.Window is { IsUsable: true } ? b.Window : null })],
        };
    }

    private static double Positive(double value, double fallback) => double.IsFinite(value) && value > 0 ? value : fallback;
}

/// <summary>A window's place and size (the restored size when maximised), in device-independent pixels.</summary>
internal sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximised = false)
{
    /// <summary>How much of the title bar must be on a screen for the window to count as on it.</summary>
    public const double TitleBarHeight = 30;

    /// <summary>The least width of title bar that must be on a screen to grab.</summary>
    public const double GrabWidth = 120;

    public bool IsUsable => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) && Width >= 100 && Height >= 100;

    /// <summary>
    /// The placement kept on the screen it overlaps most, no bigger than it and with its title bar inside it to grab; or,
    /// if it is on no screen (a monitor has gone, or the resolution fell), centred on the primary screen, which is the
    /// first area.
    /// </summary>
    public WindowPlacement OnScreen(IReadOnlyList<ScreenArea> workAreas)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (workAreas.Count == 0)
        {
            return this;
        }

        // Its screen is the one it overlaps most; none means the screen it was on has gone.
        var window = new ScreenArea(Left, Top, Width, Height);
        var home = workAreas
            .Select(a => (Area: a, Overlap: a.Overlap(window)))
            .Where(x => x.Overlap.Width > 0 && x.Overlap.Height > 0)
            .OrderByDescending(x => x.Overlap.Width * x.Overlap.Height)
            .Select(x => x.Area)
            .FirstOrDefault();
        if (home is null)
        {
            // Lost: centre it on the primary screen.
            var primary = workAreas[0];
            var (width, height) = (Math.Min(Width, primary.Width), Math.Min(Height, primary.Height));
            return this with
            {
                Left = primary.Left + ((primary.Width - width) / 2),
                Top = primary.Top + ((primary.Height - height) / 2),
                Width = width,
                Height = height,
            };
        }

        // On a screen: keep it there, no bigger than the screen, and with its top edge inside it.
        var w2 = Math.Min(Width, home.Width);
        var h2 = Math.Min(Height, home.Height);
        var left = Math.Clamp(Left, home.Left - w2 + GrabWidth, home.Right - GrabWidth);
        var top = Math.Clamp(Top, home.Top, home.Bottom - TitleBarHeight);
        return this with { Left = left, Top = top, Width = w2, Height = h2 };
    }
}

/// <summary>A screen's work area (without the taskbar), in device-independent pixels.</summary>
internal sealed record ScreenArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    /// <summary>The width and height of the overlap with another area; zero or less where they do not meet.</summary>
    public (double Width, double Height) Overlap(ScreenArea other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return (Math.Min(Right, other.Right) - Math.Max(Left, other.Left), Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
    }
}
