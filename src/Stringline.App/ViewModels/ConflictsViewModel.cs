using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>One line of the conflicts list (<c>05-…md</c> §5): kind, time, location, trains and severity.</summary>
/// <param name="Badge">A short tag for the kind ("HW", "SL", "PL"…), shown in a red box.</param>
/// <param name="Severity">The delay to resolve it, in whole minutes rounded up ("3 min") or seconds under a minute.</param>
/// <param name="Tip">The tooltip: what clashes, the exact delay to resolve it and the knock-on.</param>
internal sealed record ConflictRowViewModel(Conflict Conflict, string Badge, string Kind, string Time, string Where, string Trains, string Severity, string Tip)
{
    public string Id => Conflict.Id;

    public static ConflictRowViewModel Of(Conflict c, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(c);
        string Name(Tiploc t) => profile?.DisplayName(t) ?? t.Code;
        var where = c.To is { } to && to != c.Where ? $"{Name(c.Where)} – {Name(to)}"
            : c.Platform is { } platform ? $"{Name(c.Where)} P{platform}"
            : Name(c.Where);
        var trains = c.HeadcodeB is { } b ? $"{c.HeadcodeA} / {b}" : c.DisruptionId is { } block ? $"{c.HeadcodeA} / {block}" : c.HeadcodeA.Value;
        var severity = c.SeveritySeconds < SimTime.SecondsPerMinute
            ? string.Create(CultureInfo.InvariantCulture, $"{c.SeveritySeconds} s")
            : string.Create(CultureInfo.InvariantCulture, $"{(c.SeveritySeconds + SimTime.SecondsPerMinute - 1) / SimTime.SecondsPerMinute} min");
        var tip = $"{GraphConflicts.KindName(c.Kind)}: {c.Summary}{Environment.NewLine}Resolve: delay {c.HeadcodeA} {PlanText.Duration(c.SeveritySeconds)}"
            + (c.KnockOnSeconds >= SimTime.SecondsPerMinute ? $"{Environment.NewLine}Knock-on: {c.HeadcodeA} {PlanText.Duration(c.KnockOnSeconds)} late by the forecast" : string.Empty);
        return new ConflictRowViewModel(c, BadgeOf(c.Kind), GraphConflicts.KindName(c.Kind), PlanInput.Format(c.When), where, trains, severity, tip);
    }

    public static string BadgeOf(ConflictKind kind) => kind switch
    {
        ConflictKind.Headway => "HW",
        ConflictKind.Overtake => "OT",
        ConflictKind.SingleLine => "SL",
        ConflictKind.Platform => "PL",
        ConflictKind.Disruption => "BK",
        _ => "RT",
    };
}

/// <summary>
/// The conflicts panel (FC-4): every conflict still to come, on any corridor, by time. Picking one focuses the graph
/// on it (<see cref="Picked"/>). The rows are kept in step with the engine's conflicts in place, so the selection and
/// scroll position survive the many snapshots a running sim publishes.
/// </summary>
internal sealed partial class ConflictsViewModel : ObservableObject
{
    private ConflictSet? _conflicts;
    private bool _syncing;

    public ObservableCollection<ConflictRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial ConflictRowViewModel? Selected { get; set; }

    /// <summary>"No conflicts", "1 conflict", "12 conflicts".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "No conflicts";

    /// <summary>The tab header: "Conflicts (3)".</summary>
    [ObservableProperty]
    public partial string Header { get; private set; } = "Conflicts";

    /// <summary>The user picked a row (not a refresh keeping the selection).</summary>
    public event EventHandler<Conflict>? Picked;

    partial void OnSelectedChanged(ConflictRowViewModel? value)
    {
        if (!_syncing && value is not null)
        {
            Picked?.Invoke(this, value.Conflict);
        }
    }

    /// <summary>Selects the row with this conflict ID (or none) without raising <see cref="Picked"/>.</summary>
    public void Select(string? id)
    {
        _syncing = true;
        try
        {
            Selected = id is null ? null : Rows.FirstOrDefault(r => r.Id == id);
        }
        finally
        {
            _syncing = false;
        }
    }

    public void Update(ConflictSet conflicts, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (ReferenceEquals(conflicts, _conflicts))
        {
            return;
        }

        _conflicts = conflicts;
        var rows = conflicts.All.Select(c => ConflictRowViewModel.Of(c, profile)).ToList();
        var selected = Selected?.Id;
        _syncing = true;
        try
        {
            Sync(rows);
            Selected = selected is null ? null : Rows.FirstOrDefault(r => r.Id == selected);
        }
        finally
        {
            _syncing = false;
        }

        Summary = rows.Count switch
        {
            0 => "No conflicts",
            1 => "1 conflict",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} conflicts"),
        };
        Header = rows.Count == 0 ? "Conflicts" : string.Create(CultureInfo.InvariantCulture, $"Conflicts ({rows.Count})");
    }

    /// <summary>Makes <see cref="Rows"/> equal <paramref name="rows"/>, changing only the rows that differ.</summary>
    private void Sync(List<ConflictRowViewModel> rows)
    {
        var keep = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Rows.Count && Rows[i] == rows[i])
            {
                continue;
            }

            var at = IndexOf(rows[i].Id, i);
            if (at < 0)
            {
                Rows.Insert(i, rows[i]);
            }
            else
            {
                if (at != i)
                {
                    Rows.Move(at, i);
                }

                if (Rows[i] != rows[i])
                {
                    Rows[i] = rows[i];
                }
            }
        }
    }

    private int IndexOf(string id, int from)
    {
        for (var i = from; i < Rows.Count; i++)
        {
            if (Rows[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
