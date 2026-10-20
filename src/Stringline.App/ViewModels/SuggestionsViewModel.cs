using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// One suggestion for the picked conflict (<c>05-…md</c> §5), or a re-plan of the horizon (§15): what it does and what
/// it costs, with the plan it makes for Preview and the command that applies it.
/// </summary>
internal sealed partial class SuggestionRowViewModel(string id, IPlanCommand command, WorkingPlan? plan, IReadOnlyList<TrainId> changed, string title, string detail, string tip) : ObservableObject
{
    /// <summary>The suggestion, for a suggestion's row.</summary>
    public Suggestion? Suggestion { get; private init; }

    /// <summary>The re-plan, for a re-plan's row.</summary>
    public ReplanResult? Replan { get; private init; }

    public string Id { get; } = id;

    /// <summary>The edit that applies it, as one undoable step.</summary>
    public IPlanCommand Command { get; } = command;

    /// <summary>The plan with it applied, for the preview.</summary>
    public WorkingPlan? Plan { get; } = plan;

    /// <summary>The trains whose plan it changes.</summary>
    public IReadOnlyList<TrainId> Changed { get; } = changed;

    /// <summary>A re-plan's changes, one a line ("• Hold 2O05 at Queens Road until 06:11½ (+57 s)"); empty for a suggestion.</summary>
    public string Steps { get; private init; } = string.Empty;

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

        return new SuggestionRowViewModel(s.Id, s.ToCommand(), s.Plan, s.Changed, title, detail, string.Join(Environment.NewLine, lines)) { Suggestion = s };
    }

    /// <summary>The row for a re-plan that improves the plan.</summary>
    public static SuggestionRowViewModel Of(ReplanResult r, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(r);
        static string Time(SimTime t) => PlanInput.Format(t);
        var title = r.Describe(Time);
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Conflicts to {Time(r.Until)}: {r.Before.Count} → {r.After.Count}"),
        };
        var delay = (int)Math.Round(r.WeightedDelaySeconds);
        parts.Add(delay == 0 ? "no more delay" : $"weighted delay {PlanText.WithSign(delay)}");
        if (r.Added > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{r.Added} new"));
        }

        var steps = string.Join(Environment.NewLine, r.Steps.Select(s => "• " + s.Describe(t => profile?.DisplayName(t) ?? t.Code, Time)));
        var lines = new List<string>();
        foreach (var i in r.Impacts.Take(8))
        {
            var weight = Math.Abs(i.Weight - 1) < 0.005 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" (× {i.Weight:0.##})");
            lines.Add($"{i.Headcode} {PlanText.WithSign(i.DelaySeconds)}{weight}");
        }

        if (r.Impacts.Count > 8)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"and {r.Impacts.Count - 8} more"));
        }

        if (lines.Count == 0)
        {
            lines.Add("No train runs later or sooner by the forecast.");
        }

        if (r.Penalty > 0)
        {
            lines.Add($"Counted for the platform changes: {PlanText.Duration((int)Math.Round(r.Penalty))}");
        }

        lines.Add(string.Create(CultureInfo.InvariantCulture, $"Worked out by the {r.Method} in {r.Elapsed.TotalSeconds:0.0} s{(r.Optimal ? " (the best plan for its model)" : string.Empty)}."));
        var command = r.ToCommand();
        return new SuggestionRowViewModel(command.SuggestionId, command, r.Plan, r.Changed, title, string.Join(" · ", parts), string.Join(Environment.NewLine, lines))
        {
            Replan = r,
            Steps = steps,
        };
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

    /// <summary>The panel shows a re-plan of the horizon (Plan ▸ Re-plan), not a conflict's suggestions.</summary>
    [ObservableProperty]
    public partial bool IsReplan { get; private set; }

    /// <summary>The plan version the rows were worked out on: applying one on a newer plan is refused.</summary>
    public long PlanVersion { get; private set; }

    public void Clear()
    {
        Rows.Clear();
        (ConflictId, IsVisible, IsReplan, Heading, Status) = (null, false, false, string.Empty, string.Empty);
    }

    /// <summary>Working out a re-plan of the next <paramref name="minutes"/> minutes, for up to <paramref name="seconds"/>.</summary>
    public void WorkingReplan(int minutes, double seconds)
    {
        Rows.Clear();
        (ConflictId, IsReplan, IsVisible) = (null, true, true);
        Heading = string.Create(CultureInfo.InvariantCulture, $"Re-plan the next {minutes} min");
        Status = string.Create(CultureInfo.CurrentCulture, $"Working out a re-plan (up to {seconds:0.#} s)…");
    }

    public void ShowReplan(ReplanResult result, long planVersion, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(result);
        Rows.Clear();
        (ConflictId, PlanVersion, IsReplan, IsVisible) = (null, planVersion, true, true);
        Heading = $"Re-plan to {PlanInput.Format(result.Until)}";
        var left = result.After.Count(c => !ReplanEvaluator.IsBetweenTrains(c));
        switch (result.Outcome)
        {
            case ReplanOutcome.Improved:
                Rows.Add(SuggestionRowViewModel.Of(result, profile));
                Status = result.After.Count == 0 ? string.Empty
                    : left > 0 ? string.Create(CultureInfo.InvariantCulture, $"{result.After.Count} conflicts would be left; {left} of them are blocks or runs planned too fast, which have their own suggestions.")
                    : string.Create(CultureInfo.InvariantCulture, $"{result.After.Count} conflicts would be left: pick one for its suggestions.");
                break;
            case ReplanOutcome.NoConflicts:
                Status = $"No conflicts to clear before {PlanInput.Format(result.Until)}.";
                break;
            default:
                Status = string.Create(CultureInfo.InvariantCulture, $"Nothing better found for the {result.Before.Count} conflicts before {PlanInput.Format(result.Until)}: no train that could be held or moved clears them. Pick a conflict for its suggestions.");
                break;
        }
    }

    /// <summary>The re-plan was applied: the row goes, as the plan it was for has changed.</summary>
    public void ReplanApplied(string title)
    {
        Rows.Clear();
        Status = $"Applied: {title}. Undo takes it back.";
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
        (IsReplan, IsVisible) = (false, true);
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

        (ConflictId, PlanVersion, Heading, IsVisible, IsReplan) = (conflict.Id, planVersion, $"Resolve: {conflict.Summary}", true, false);
        Status = suggestions.Count > 0 ? string.Empty
            : conflict.B is null ? $"No suggestion: {conflict.HeadcodeA} cannot wait anywhere before it."
            : $"No suggestion: neither {conflict.HeadcodeA} nor {conflict.HeadcodeB} can wait anywhere before it.";
    }

    public void Failed()
    {
        Rows.Clear();
        Status = IsReplan ? "The re-plan could not be worked out (see the log)." : "The suggestions could not be worked out (see the log).";
    }

    private static SuggestionRowViewModel Previewed(SuggestionRowViewModel row)
    {
        row.IsPreviewed = true;
        return row;
    }
}
