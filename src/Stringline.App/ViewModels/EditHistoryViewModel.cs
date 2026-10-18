using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>One line of the edit history.</summary>
/// <param name="IsUndone">Undone, so it would be redone next (shown greyed).</param>
internal sealed record HistoryEntryViewModel(int Number, string Description, string Warnings, bool IsUndone);

/// <summary>
/// The working plan's edit history (RP-7): every edit applied, oldest first, then those undone (greyed) that Redo would
/// bring back. Rebuilt only when the engine publishes a new history.
/// </summary>
internal sealed partial class EditHistoryViewModel : ObservableObject
{
    private PlanHistory? _history;

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "No edits";

    /// <summary>The newest entry applied, for scrolling it into view.</summary>
    [ObservableProperty]
    public partial HistoryEntryViewModel? Latest { get; private set; }

    public void Update(PlanHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (ReferenceEquals(history, _history))
        {
            return;
        }

        _history = history;
        Entries.Clear();
        var n = 0;
        foreach (var entry in history.Done)
        {
            Entries.Add(new HistoryEntryViewModel(++n, entry.Description, Warnings(entry), false));
        }

        Latest = Entries.LastOrDefault();
        foreach (var entry in history.Undone)
        {
            Entries.Add(new HistoryEntryViewModel(++n, entry.Description, Warnings(entry), true));
        }

        Summary = history.Done.Count == 0 && history.Undone.Count == 0 ? "No edits"
            : string.Create(CultureInfo.InvariantCulture, $"{history.Done.Count} edit{(history.Done.Count == 1 ? string.Empty : "s")}")
              + (history.Undone.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $", {history.Undone.Count} undone") : string.Empty);
    }

    private static string Warnings(PlanHistoryEntry entry) => string.Join(Environment.NewLine, entry.Warnings.Select(w => w.Message));
}
