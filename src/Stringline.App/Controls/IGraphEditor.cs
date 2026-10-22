using Stringline.Graph;

namespace Stringline.App.Controls;

/// <summary>
/// What the graph control asks of its view model to edit the plan on the graph (<c>05-…md</c> §3): drag edits of
/// trains, B+drag blocks and the right-click menu. The control only turns mouse and keys into these calls; the rules
/// are in <see cref="PlanDrag"/> and <see cref="GraphAnnotations"/>.
/// </summary>
internal interface IGraphEditor
{
    /// <summary>A press on a line: true if it starts a drag edit, false to pan instead.</summary>
    /// <param name="fromVertex">Alt is held: retime from the vertex.</param>
    bool BeginEdit(GraphHit hit, ScreenPoint at, bool fromVertex);

    /// <summary>The grip on the selected train at a point, if any (S26). Grips are hit before anything else.</summary>
    GraphGrip? GripAt(ScreenPoint at);

    /// <summary>The selected train's own WTT or plan line at a point, even under its forecast (S26).</summary>
    GraphHit? SelectedLineAt(ScreenPoint at);

    /// <summary>The mouse is over a grip, or the selected train's line, or neither: the status bar names the action.</summary>
    void HoverGrip(GraphGrip? grip, bool overLine);

    /// <summary>The tooltip over a grip.</summary>
    HoverInfo DescribeGrip(GraphGrip grip);

    /// <summary>A press on a grip: true if it starts a drag; false if it is locked (the reason goes in the status bar).</summary>
    bool BeginGrip(GraphGrip grip, ScreenPoint at);

    /// <summary>A click on a grip: the arrow keys nudge it.</summary>
    void SelectGrip(GraphGrip grip);

    /// <summary>How far an arrow key nudges a picked grip (Shift: a minute): 15 s, or half a minute on a draft board (S44).</summary>
    int NudgeSeconds => PlanDrag.SnapSeconds;

    /// <summary>An arrow key: nudge the picked grip by this many seconds. False if no grip is picked.</summary>
    bool Nudge(int seconds);

    /// <param name="fine">Shift is held: snap to 1 s.</param>
    void MoveEdit(ScreenPoint at, bool fine);

    /// <summary>The button is released (commit) or Esc is pressed (cancel).</summary>
    Task EndEditAsync(bool commit);

    /// <summary>A press with B held: true if it starts drawing a block (S13).</summary>
    bool BeginBlock(ScreenPoint at);

    void MoveBlock(ScreenPoint at);

    /// <summary>The button is released (commit: set the block up) or Esc is pressed (cancel).</summary>
    Task EndBlockAsync(bool commit);

    /// <summary>The right-click menu at a point (a note, a train's line or the background), or empty for none.</summary>
    IReadOnlyList<ViewModels.PlanMenuItem> MenuFor(GraphHit? hit, ScreenPoint at);
}
