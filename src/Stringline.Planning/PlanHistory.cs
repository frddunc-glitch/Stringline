namespace Stringline.Planning;

/// <summary>One applied command in the edit history, described as it was when applied.</summary>
public sealed record PlanHistoryEntry(IPlanCommand Command, string Description, IReadOnlyList<PlanWarning> Warnings);

/// <summary>The working plan's edit history (RP-7): what can be undone, newest last, and what can be redone, next first.</summary>
public sealed class PlanHistory(IReadOnlyList<PlanHistoryEntry> done, IReadOnlyList<PlanHistoryEntry> undone)
{
    public static PlanHistory Empty { get; } = new([], []);

    /// <summary>Applied commands, oldest first. The plan is the WTT with these applied in this order.</summary>
    public IReadOnlyList<PlanHistoryEntry> Done { get; } = done ?? throw new ArgumentNullException(nameof(done));

    /// <summary>Undone commands that can be redone, the next redo first. A new command clears them.</summary>
    public IReadOnlyList<PlanHistoryEntry> Undone { get; } = undone ?? throw new ArgumentNullException(nameof(undone));

    public bool CanUndo => Done.Count > 0;

    public bool CanRedo => Undone.Count > 0;

    /// <summary>What Undo would undo, e.g. for an "Undo Cancel 1A23" menu item.</summary>
    public string? UndoDescription => CanUndo ? Done[^1].Description : null;

    public string? RedoDescription => CanRedo ? Undone[0].Description : null;
}
