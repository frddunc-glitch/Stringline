using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

internal enum TrainSort
{
    Delay,
    Headcode,
    LastSeen,
}

/// <summary>One live train in the list: its current working.</summary>
internal sealed partial class TrainRowViewModel(string uid) : ObservableObject
{
    public string Uid { get; } = uid;

    [ObservableProperty]
    public partial string Headcode { get; set; } = string.Empty;

    /// <summary>The WTT train of the current working, if matched.</summary>
    [ObservableProperty]
    public partial TrainId? Train { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnmatched))]
    public partial MatchStatus Status { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SimTime LastSeen { get; set; }

    [ObservableProperty]
    public partial string LastSeenText { get; set; } = string.Empty;

    /// <summary>Delay in whole minutes at the latest booked point, or null if not known.</summary>
    [ObservableProperty]
    public partial int? DelayMinutes { get; set; }

    [ObservableProperty]
    public partial string DelayText { get; set; } = string.Empty;

    /// <summary>Category colour as <c>#RRGGBB</c> (grey when not matched).</summary>
    [ObservableProperty]
    public partial string Colour { get; set; } = "#8A8A8A";

    public bool IsUnmatched => Status == MatchStatus.Unmatched;
}

/// <summary>
/// The train list (<c>05-…md</c> §1): every live train with its match, last report and delay, filtered and sorted,
/// updated in place from each snapshot so the selection and scroll position survive.
/// </summary>
internal sealed partial class TrainListViewModel : ObservableObject
{
    private readonly Dictionary<string, TrainRowViewModel> _rows = new(StringComparer.Ordinal);
    private PlanSnapshot _snapshot = PlanSnapshot.Empty;
    private StringlineProfile? _profile;

    /// <summary>The rows shown, in order.</summary>
    public ObservableCollection<TrainRowViewModel> Rows { get; } = [];

    public IReadOnlyList<TrainSort> SortOptions { get; } = Enum.GetValues<TrainSort>();

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UnmatchedOnly { get; set; }

    [ObservableProperty]
    public partial TrainSort Sort { get; set; } = TrainSort.Delay;

    [ObservableProperty]
    public partial TrainRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "No trains";

    partial void OnFilterChanged(string value) => Refresh();

    partial void OnUnmatchedOnlyChanged(bool value) => Refresh();

    partial void OnSortChanged(TrainSort value) => Refresh();

    public TrainRowViewModel? Find(string uid) => _rows.GetValueOrDefault(uid);

    /// <summary>Whole minutes late as the list shows them: <c>+3</c>, <c>0</c>, <c>−1</c> (a true minus sign).</summary>
    public static string FormatDelay(int minutes) => minutes switch
    {
        0 => "0",
        > 0 => string.Create(CultureInfo.InvariantCulture, $"+{minutes}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"−{-minutes}"),
    };

    /// <summary>
    /// Colour each row's swatch as the graph colours the train: by category, or by the band of its current delay.
    /// Takes effect at the next <see cref="Update"/>.
    /// </summary>
    public Graph.GraphColourMode ColourMode { get; set; }

    /// <summary>Take a new snapshot (and the profile for names and colours).</summary>
    public void Update(PlanSnapshot snapshot, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _profile = profile;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var live in snapshot.Trains)
        {
            seen.Add(live.SimUid);
            if (!_rows.TryGetValue(live.SimUid, out var row))
            {
                row = new TrainRowViewModel(live.SimUid);
                _rows.Add(live.SimUid, row);
            }

            Fill(row, live);
        }

        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        Refresh();
    }

    private void Fill(TrainRowViewModel row, LiveTrain live)
    {
        var working = live.Current;
        row.Headcode = working.Headcode.Value;
        row.Train = working.Train;
        row.Status = working.Status;
        row.StatusText = working.Status switch
        {
            MatchStatus.Matched => working.Train?.Value ?? "Matched",
            MatchStatus.Pending => "Matching…",
            _ => "Unmatched",
        };
        var last = live.Actuals.LastOrDefault(a => a.AtTimingPoint) ?? live.Last;
        row.LastLocation = _profile?.DisplayName(last.Location) ?? last.Location.Code;
        row.LastSeen = live.LastSeen;
        row.LastSeenText = live.LastSeen.ToString("T", CultureInfo.InvariantCulture);
        var delay = TrainDelays.Current(live, _snapshot.Timetable);
        row.DelayMinutes = delay?.Minutes;
        row.DelayText = delay is null ? string.Empty : FormatDelay(delay.Minutes);
        var colour = working is { IsMatched: true, Train: { } id } && _snapshot.Timetable?.Find(id) is { } train
            ? ColourMode == Graph.GraphColourMode.Delay
                ? Graph.DelayBands.ColourOfSeconds(delay?.Seconds)
                : _profile?.StyleFor(train.CategoryId).Colour ?? CategoryStyle.Fallback.Colour
            : Graph.GraphSceneBuilder.UnmatchedColour;
        row.Colour = colour.ToString();
    }

    private void Refresh()
    {
        var filter = Filter.Trim();
        IEnumerable<TrainRowViewModel> rows = _rows.Values;
        if (UnmatchedOnly)
        {
            rows = rows.Where(r => r.Status != MatchStatus.Matched);
        }

        if (filter.Length > 0)
        {
            rows = rows.Where(r =>
                r.Headcode.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.LastLocation.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (r.Train?.Value.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                r.Uid.Equals(filter, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = (Sort switch
        {
            TrainSort.Headcode => rows.OrderBy(r => r.Headcode, StringComparer.Ordinal).ThenBy(r => r.LastSeen),
            TrainSort.LastSeen => rows.OrderByDescending(r => r.LastSeen).ThenBy(r => r.Headcode, StringComparer.Ordinal),
            _ => rows.OrderByDescending(r => r.DelayMinutes ?? int.MinValue).ThenByDescending(r => r.LastSeen).ThenBy(r => r.Headcode, StringComparer.Ordinal),
        }).ToList();

        Sync(ordered);
        var unmatched = _rows.Values.Count(r => r.IsUnmatched);
        Summary = _rows.Count == 0 ? "No trains"
            : (Rows.Count == _rows.Count ? $"{_rows.Count} trains" : $"{Rows.Count} of {_rows.Count} trains")
              + (unmatched > 0 ? $", {unmatched} unmatched" : string.Empty);
    }

    /// <summary>Make <see cref="Rows"/> equal <paramref name="wanted"/> with the fewest moves, keeping row objects.</summary>
    private void Sync(List<TrainRowViewModel> wanted)
    {
        var keep = wanted.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], wanted[i]))
            {
                continue;
            }

            var at = Rows.IndexOf(wanted[i]);
            if (at < 0)
            {
                Rows.Insert(i, wanted[i]);
            }
            else
            {
                Rows.Move(at, i);
            }
        }

        if (Selected is not null && !keep.Contains(Selected))
        {
            Selected = null;
        }
    }
}
