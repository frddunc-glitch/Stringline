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

    /// <summary>The split view and board windows (S21).</summary>
    public const string Boards = "reading-the-graph.md#boards-split-view-and-board-windows";

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

    /// <summary>The profile editor's Signals tab (S37).</summary>
    public const string SignalPlaces = "signal-places.md#the-signals-tab";

    /// <summary>Options ▸ Profile (S37).</summary>
    public const string LearningOptions = "settings.md#profile";

    /// <summary>The privacy policy (S30, #14), a copy of <c>PRIVACY.md</c>; Help ▸ Privacy, About and the Connect dialog link to it.</summary>
    public const string Privacy = "privacy.md";

    /// <summary>The timetable editor (S43, #20).</summary>
    public const string TimetableEditor = "timetable-editor.md";

    public const string TimetableEditorTrains = "timetable-editor.md#the-trains-list";

    public const string TimetableEditorTrips = "timetable-editor.md#the-train-and-its-timing-points";

    public const string TimetableEditorIssues = "timetable-editor.md#the-issues-panel";

    /// <summary>Every topic, for the test that each page and heading is there.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Contents, Graph, TrainList, Boards, TrainDetail, Conflicts, Regulation, Delays, History, Replay, Connect, Options, ProfileEditor, Disruptions, SignalPlaces, LearningOptions, Privacy,
         TimetableEditor, TimetableEditorTrains, TimetableEditorTrips, TimetableEditorIssues];
}
