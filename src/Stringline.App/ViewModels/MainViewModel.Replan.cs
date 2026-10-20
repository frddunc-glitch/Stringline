using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.Optimiser;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Plan ▸ Re-plan the next hour (S20, FC-6): the optimiser (or the suggestion rules) clears the conflicts ahead all
/// at once, off the UI thread, and the suggestions panel shows the proposal with Preview and Apply (Fred). Unlike
/// suggestions it is not worked out again as the sim moves on: Apply refuses it once the plan has changed.
/// </summary>
internal sealed partial class MainViewModel
{
    private bool _replanning;

    /// <summary>"Re-plan the next 60 min…", by the horizon in Options.</summary>
    public string ReplanHeader => $"Re-_plan the next {_settings.Planning.ReplanHorizonMinutes} min…";

    private bool CanReplan() => !_replanning && Snapshot.Plan.Count > 0;

    /// <summary>Works out a re-plan of the horizon and shows it in the suggestions panel.</summary>
    [RelayCommand(CanExecute = nameof(CanReplan))]
    private async Task ReplanAsync()
    {
        if (!CanReplan())
        {
            return;
        }

        var planning = _settings.Planning;
        var options = planning.ToReplanOptions();
        IReplanner replanner = planning.ReplanMethod == ReplanMethod.Rules
            ? new RuleReplanner()
            : new CpSatReplanner(_loggerFactory.CreateLogger<CpSatReplanner>());

        // The panel shows the re-plan, not a conflict's suggestions, until a conflict is picked.
        ConflictList.Selected = null;
        SideTab = 0;
        SuggestionPanel.WorkingReplan(planning.ReplanHorizonMinutes, options.TimeLimitSeconds);
        _replanning = true;
        ReplanCommand.NotifyCanExecuteChanged();
        var snapshot = Snapshot;
        try
        {
            var result = await Task.Run(() => replanner.Replan(ReplanRequest.For(snapshot, options)));
            LogReplanned(replanner.Name, result.Outcome, result.Before.Count, result.After.Count, result.Steps.Count, result.Elapsed.TotalMilliseconds);
            if (SuggestionPanel.IsReplan)
            {
                SuggestionPanel.ShowReplan(result, snapshot.PlanVersion, Profile);
                UpdateSuggestionPreview();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogReplanFailed(ex, replanner.Name);
            if (SuggestionPanel.IsReplan)
            {
                SuggestionPanel.Failed();
            }
        }
        finally
        {
            _replanning = false;
            ReplanCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateReplanCommand()
    {
        ReplanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ReplanHeader));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-plan by the {Method}: {Outcome}, {Before} → {After} conflicts in the horizon, {Steps} steps, in {Milliseconds:0} ms")]
    private partial void LogReplanned(string method, ReplanOutcome outcome, int before, int after, int steps, double milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-plan by the {Method} failed")]
    private partial void LogReplanFailed(Exception ex, string method);
}
