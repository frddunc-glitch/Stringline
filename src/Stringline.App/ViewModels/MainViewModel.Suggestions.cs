using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Suggestions and the regulation list in the shell (S15, FC-5 and RP-9). Suggestions are worked out off the UI thread
/// for the conflict picked in the list (Fred), again straight away when the plan changes and at most every
/// <see cref="SuggestionRefresh"/> as the live state moves on. Preview draws the plans a suggestion changes as ghosts;
/// Apply makes it one undoable edit.
/// </summary>
internal sealed partial class MainViewModel
{
    /// <summary>The least time between working out suggestions again for live updates alone.</summary>
    private static readonly TimeSpan SuggestionRefresh = TimeSpan.FromSeconds(2);

    private (string Conflict, long Plan, long State, SuggestionOptions Options)? _suggestKey;
    private DateTimeOffset _suggestedAt;
    private bool _suggesting;
    private bool _suggestAgain;
    private string? _previewId;
    private bool _previewShown;

    /// <summary>The suggestions for the picked conflict, under the conflicts list.</summary>
    public SuggestionsViewModel SuggestionPanel { get; } = new();

    /// <summary>The regulation list (a tab beside the conflicts; Fred).</summary>
    public RegulationViewModel Regulation { get; } = new();

    /// <summary>The tab shown under the train detail: 0 conflicts, 1 regulation, 2 delays, 3 history.</summary>
    [ObservableProperty]
    public partial int SideTab { get; set; }

    public const int RegulationTab = 1;

    private void InitSuggestions()
    {
        SuggestionPanel.AllowCancellation = _settings.SuggestCancellations;
        SuggestionPanel.PropertyChanged += OnSuggestionPanelChanged;
        ConflictList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConflictsViewModel.Selected))
            {
                UpdateSuggestions();
            }
        };
        Regulation.LocationChanged += (_, _) => Regulation.Update(Snapshot);
    }

    private void OnSuggestionPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SuggestionsViewModel.AllowCancellation))
        {
            return;
        }

        if (_settings.SuggestCancellations != SuggestionPanel.AllowCancellation)
        {
            _settings = _settings with { SuggestCancellations = SuggestionPanel.AllowCancellation };
            _settingsStore.Save(_settings);
        }

        UpdateSuggestions();
    }

    /// <summary>Work out the picked conflict's suggestions if they are out of date (a new conflict, plan or live state).</summary>
    private void UpdateSuggestions()
    {
        if (ConflictList.Selected?.Id is not { } id || Snapshot.Conflicts.Find(id) is not { } conflict)
        {
            _suggestKey = null;
            ClearSuggestionPreview();
            SuggestionPanel.Clear();
            return;
        }

        var key = (id, Snapshot.PlanVersion, Snapshot.StateVersion, Options: _settings.Planning.ToSuggestionOptions(SuggestionPanel.AllowCancellation));
        if (key == _suggestKey)
        {
            return;
        }

        if (_suggesting)
        {
            _suggestAgain = true;
            return;
        }

        var now = _time.GetUtcNow();
        var liveOnly = _suggestKey is { } last && last.Conflict == key.id && last.Plan == key.PlanVersion && last.Options == key.Options;
        if (liveOnly && now - _suggestedAt < SuggestionRefresh)
        {
            return; // the next snapshot after the interval brings it up to date
        }

        _suggestKey = key;
        _suggestedAt = now;
        _suggesting = true;
        SuggestionPanel.Working(conflict);
        var snapshot = Snapshot;
        var options = key.Options;
        _ = Task.Run(() => Suggester.Suggest(SuggestionRequest.For(snapshot, conflict, options))).ContinueWith(
            t => _ui.Post(() =>
            {
                _suggesting = false;
                if (ConflictList.Selected?.Id == conflict.Id)
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        SuggestionPanel.Show(conflict, t.Result, snapshot.PlanVersion, Profile);
                    }
                    else
                    {
                        LogSuggestionsFailed(t.Exception?.GetBaseException(), conflict.Id);
                        SuggestionPanel.Failed();
                    }

                    UpdateSuggestionPreview();
                }

                if (_suggestAgain)
                {
                    _suggestAgain = false;
                    UpdateSuggestions();
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>Preview a suggestion on the graph (its changed trains' plans as ghosts), or stop previewing it.</summary>
    [RelayCommand]
    private void PreviewSuggestion(SuggestionRowViewModel? row)
    {
        if (row is null || _drag is not null)
        {
            return;
        }

        _previewId = _previewId == row.Id ? null : row.Id;
        UpdateSuggestionPreview();
    }

    /// <summary>Apply a suggestion as one edit. Refused if the plan has changed since it was worked out.</summary>
    [RelayCommand]
    private async Task ApplySuggestionAsync(SuggestionRowViewModel? row)
    {
        if (row is null || _drag is not null)
        {
            return;
        }

        // Its commands are for the plan it was worked out on (a shift is relative): never apply them to another.
        if (SuggestionPanel.PlanVersion != Snapshot.PlanVersion || _engine.Current.PlanVersion != Snapshot.PlanVersion ||
            (_suggestKey is { } key && key.Plan != SuggestionPanel.PlanVersion))
        {
            EditStatus = "The plan has changed since; the suggestions are being worked out again.";
            _suggestKey = null;
            UpdateSuggestions();
            return;
        }

        // Keep its ghost until a scene drawn from the edited plan replaces it, so the lines do not flick back.
        var version = Snapshot.PlanVersion;
        _previewId = null;
        _previewShown = false;
        Ghosts = PreviewPaths(row.Suggestion);
        var result = await ExecuteAsync(row.Suggestion.ToCommand());
        if (result.Applied && Ghosts is not null)
        {
            _ghostUntil = version + 1;
        }
        else
        {
            Ghosts = null;
        }
    }

    /// <summary>Show the previewed suggestion's ghosts on the corridor shown, or clear them if it has gone.</summary>
    private void UpdateSuggestionPreview()
    {
        var row = _previewId is null ? null : SuggestionPanel.Rows.FirstOrDefault(r => r.Id == _previewId);
        _previewId = row?.Id;
        foreach (var r in SuggestionPanel.Rows)
        {
            r.IsPreviewed = r == row;
        }

        if (_drag is not null)
        {
            return;
        }

        if (row is not null)
        {
            Ghosts = PreviewPaths(row.Suggestion);
            _previewShown = true;
        }
        else if (_previewShown)
        {
            Ghosts = null;
            _previewShown = false;
        }
    }

    private void ClearSuggestionPreview()
    {
        _previewId = null;
        foreach (var r in SuggestionPanel.Rows)
        {
            r.IsPreviewed = false;
        }

        if (_previewShown)
        {
            _previewShown = false;
            if (_drag is null)
            {
                Ghosts = null;
            }
        }
    }

    /// <summary>The plans a suggestion changes, on the corridor shown, as ghosts.</summary>
    private List<GraphPath>? PreviewPaths(Suggestion suggestion)
    {
        if (SelectedCorridor is not { } tab || suggestion.Plan is not { } plan)
        {
            return null;
        }

        var paths = new List<GraphPath>();
        foreach (var id in suggestion.Changed)
        {
            if (plan.Find(id) is { Cancelled: false } train)
            {
                var colour = Profile?.StyleFor(train.CategoryId).Colour ?? CategoryStyle.Fallback.Colour;
                paths.AddRange(GraphSceneBuilder.PlanPaths(tab.Corridor, train, colour, $"ghost:{id}"));
            }
        }

        return paths;
    }

    /// <summary>Show the regulation list for a location (from the graph's right-click menu).</summary>
    public void ShowRegulation(Tiploc location)
    {
        Regulation.Pick(location, Profile);
        Regulation.Update(Snapshot);
        SideTab = RegulationTab;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Suggestions for conflict {Conflict} failed")]
    private partial void LogSuggestionsFailed(Exception? ex, string conflict);
}
