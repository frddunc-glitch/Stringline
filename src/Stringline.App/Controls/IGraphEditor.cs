using Stringline.Graph;

namespace Stringline.App.Controls;

/// <summary>
/// What the graph control asks of its view model to edit trains by dragging (<c>05-…md</c> §3). The control only
/// turns mouse and keys into these calls; the rules are in <see cref="PlanDrag"/>.
/// </summary>
internal interface IGraphEditor
{
    /// <summary>A press on a line: true if it starts a drag edit, false to pan instead.</summary>
    /// <param name="fromVertex">Alt is held: retime from the vertex.</param>
    bool BeginEdit(GraphHit hit, ScreenPoint at, bool fromVertex);

    /// <param name="fine">Shift is held: snap to 1 s.</param>
    void MoveEdit(ScreenPoint at, bool fine);

    /// <summary>The button is released (commit) or Esc is pressed (cancel).</summary>
    Task EndEditAsync(bool commit);

    /// <summary>The right-click menu for a line, or empty for none.</summary>
    IReadOnlyList<ViewModels.PlanMenuItem> MenuFor(GraphHit? hit);
}
