namespace Stringline.App.Services;

/// <summary>
/// The user guide's pages that F1 opens (S28, #4): a page of <c>docs/user-guide</c>, and a heading on it as GitHub
/// and the bundled help name it. The windows and panels set these with <c>Views.Help.Topic</c>.
/// </summary>
internal static class HelpTopics
{
    public const string Contents = "README.md";

    public const string Graph = "reading-the-graph.md";

    public const string TrainList = "reading-the-graph.md#the-train-list";

    public const string TrainDetail = "re-planning.md#the-train-detail-panel";

    public const string Conflicts = "conflicts-and-suggestions.md";

    public const string Regulation = "regulation.md";

    public const string Delays = "exports-and-reports.md#the-delays-tab";

    public const string History = "re-planning.md#undo-and-the-edit-history";

    public const string Replay = "sessions-and-review.md#replaying-a-recording";

    public const string Connect = "connecting.md";

    public const string Options = "settings.md";

    public const string ProfileEditor = "timetables-and-profiles.md#the-profile-editor";

    public const string Disruptions = "disruptions-and-notes.md";

    /// <summary>Every topic, for the test that each page and heading is there.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Contents, Graph, TrainList, TrainDetail, Conflicts, Regulation, Delays, History, Replay, Connect, Options, ProfileEditor, Disruptions];
}
