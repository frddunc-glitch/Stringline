using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>One suggestion for the picked conflict (<c>05-…md</c> §5): what it does and what it costs.</summary>
internal sealed partial class SuggestionRowViewModel(Suggestion suggestion, string title, string detail, string tip) : ObservableObject
{
    public Suggestion Suggestion { get; } = suggestion;

    public string Id => Suggestion.Id;

    /// <summary>"Hold 1F01 at Clapham Junction until 08:11 (+2 min)".</summary>
    public string Title { get; } = title;

    /// <summary>"Weighted delay +4 min · clears 2 conflicts · 1 new".</summary>
    public string Detail { get; } = detail;

    /// <summary>The trains it makes later or sooner, and how the score adds up.</summary>
    public string Tip { get; } = tip;

    /// <summary>Its plan is drawn on the graph as a ghost.</summary>
    [ObservableProperty]
    public partial bool IsPreviewed { get; set; }

    public static SuggestionRowViewModel Of(Suggestion s, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(s);
        var title = s.Describe(t => profile?.DisplayName(t) ?? t.Code, t => PlanInput.Format(t));
        var parts = new List<string>();
        if (!s.Resolves)
        {
            parts.Add("does not fully clear it");
        }

        var delay = (int)Math.Round(s.WeightedDelaySeconds);
        parts.Add(delay == 0 ? "no more delay" : $"weighted delay {PlanText.WithSign(delay)}");
        if (s.Cleared > 0)
        {
            parts.Add(s.Cleared == 1 ? "clears 1 conflict" : string.Create(CultureInfo.InvariantCulture, $"clears {s.Cleared} conflicts"));
        }

        if (s.Added > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{s.Added} new"));
        }

        var detail = string.Join(" · ", parts);
        detail = char.ToUpperInvariant(detail[0]) + detail[1..];

        var lines = new List<string>();
        foreach (var i in s.Impacts.Take(8))
        {
            var weight = Math.Abs(i.Weight - 1) < 0.005 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" (× {i.Weight:0.##})");
            lines.Add($"{i.Headcode} {PlanText.WithSign(i.DelaySeconds)}{weight}");
        }

        if (s.Impacts.Count > 8)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"and {s.Impacts.Count - 8} more"));
        }

        if (lines.Count == 0)
        {
            lines.Add("No train runs later or sooner by the forecast.");
        }

        if (s.Penalty > 0)
        {
            var what = s.Kind switch
            {
                SuggestionKind.Platform => "platform change",
                SuggestionKind.Line => "line change",
                SuggestionKind.TerminateShort => "terminating short",
                _ => "cancelling",
            };
            lines.Add($"Counted for the {what}: {PlanText.Duration((int)Math.Round(s.Penalty))}");
        }

        if (s.ConflictCost > 0)
        {
            lines.Add($"Counted for new platform clashes: {PlanText.Duration((int)Math.Round(s.ConflictCost))}");
        }

        return new SuggestionRowViewModel(s, title, detail, string.Join(Environment.NewLine, lines));
    }
}

/// <summary>
/// The suggestions for the conflict picked in the list (FC-5): up to three ways to resolve it, best first, each with
/// Preview and Apply. Worked out off the UI thread by <see cref="MainViewModel"/>.
/// </summary>
internal sealed partial class SuggestionsViewModel : ObservableObject
{
    public ObservableCollection<SuggestionRowViewModel> Rows { get; } = [];

    /// <summary>A conflict is picked, so the panel shows.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    /// <summary>"Resolve: 1F01 too close behind 2S01".</summary>
    [ObservableProperty]
    public partial string Heading { get; private set; } = string.Empty;

    /// <summary>"Working out suggestions…", why there are none, or empty.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    /// <summary>Suggest terminating short or cancelling a train for a block (remembered; off by default).</summary>
    [ObservableProperty]
    public partial bool AllowCancellation { get; set; }

    /// <summary>The conflict the rows are for.</summary>
    public string? ConflictId { get; private set; }

    /// <summary>The plan version the rows were worked out on: applying one on a newer plan is refused.</summary>
    public long PlanVersion { get; private set; }

    public void Clear()
    {
        Rows.Clear();
        (ConflictId, IsVisible, Heading, Status) = (null, false, string.Empty, string.Empty);
    }

    /// <summary>Working them out for this conflict; the rows already shown stay if they are for it.</summary>
    public void Working(Conflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (conflict.Id != ConflictId)
        {
            Rows.Clear();
            ConflictId = conflict.Id;
            Status = "Working out suggestions…";
        }

        Heading = $"Resolve: {conflict.Summary}";
        IsVisible = true;
    }

    public void Show(Conflict conflict, IReadOnlyList<Suggestion> suggestions, long planVersion, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(suggestions);
        var previewed = Rows.FirstOrDefault(r => r.IsPreviewed)?.Id;
        Rows.Clear();
        foreach (var s in suggestions)
        {
            Rows.Add(SuggestionRowViewModel.Of(s, profile) is var row && row.Id == previewed ? Previewed(row) : row);
        }

        (ConflictId, PlanVersion, Heading, IsVisible) = (conflict.Id, planVersion, $"Resolve: {conflict.Summary}", true);
        Status = suggestions.Count > 0 ? string.Empty
            : conflict.B is null ? $"No suggestion: {conflict.HeadcodeA} cannot wait anywhere before it."
            : $"No suggestion: neither {conflict.HeadcodeA} nor {conflict.HeadcodeB} can wait anywhere before it.";
    }

    public void Failed()
    {
        Rows.Clear();
        Status = "The suggestions could not be worked out (see the log).";
    }

    private static SuggestionRowViewModel Previewed(SuggestionRowViewModel row)
    {
        row.IsPreviewed = true;
        return row;
    }
}
