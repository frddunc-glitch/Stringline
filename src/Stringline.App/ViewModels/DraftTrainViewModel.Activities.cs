using System.Collections.ObjectModel;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Drafts;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>The activities of the trip the grid is on (S45), and what formed the train.</summary>
internal sealed partial class DraftTrainViewModel
{
    private readonly Dictionary<XElement, ActivityRow> _activityRows = [];

    public ObservableCollection<ActivityRow> Activities { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveActivityCommand), nameof(MoveActivityUpCommand), nameof(MoveActivityDownCommand), nameof(GoToAssociatedCommand))]
    public partial ActivityRow? SelectedActivity { get; set; }

    public IReadOnlyList<string> ActivityKinds { get; } = DraftParts.ActivityNames;

    /// <summary>The kind the next Add makes.</summary>
    [ObservableProperty]
    public partial string NewActivityKind { get; set; } = TimetableParts.Name(ActivityKind.Next);

    /// <summary>The train the next Add names: picked from <see cref="ActivityCandidates"/>, or typed.</summary>
    [ObservableProperty]
    public partial string NewActivityTrain { get; set; } = string.Empty;

    /// <summary>Trains booked at the trip's location near its time first (a train formed there first for a forming kind), then the rest.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TrainChoice> ActivityCandidates { get; private set; } = [];

    /// <summary>"Formed by 1A01 at WLOO (next working)" for a train another forms; empty otherwise.</summary>
    [ObservableProperty]
    public partial string FormedBy { get; private set; } = string.Empty;

    /// <summary>Where the activities are: "Activities at WOKING".</summary>
    [ObservableProperty]
    public partial string ActivitiesHeading { get; private set; } = "Activities";

    partial void OnCurrentTripChanged(TripRow? value) => LoadActivities(_owner.Snapshot);

    partial void OnNewActivityKindChanged(string value) => LoadCandidates();

    [RelayCommand]
    private void AddActivity()
    {
        if (CurrentTrip is not { } row)
        {
            _owner.Refuse("Choose the point in the grid to add the activity at.");
            return;
        }

        try
        {
            var kind = DraftParts.ActivityKinds.FirstOrDefault(k => TimetableParts.Name(k) == NewActivityKind);
            var trip = new TripNode(_owner.Document, row.Element);
            TrainNode? with = null;
            SimTime? crewTime = null;
            if (TimetableParts.NamesATrain(kind))
            {
                with = ActivityCandidates.FirstOrDefault(c => c.Label == NewActivityTrain) is { } picked
                    ? new TrainNode(_owner.Document, picked.Element)
                    : _owner.FindTrain(NewActivityTrain) ?? throw new InvalidOperationException($"Choose the train the {TimetableParts.Name(kind).ToLowerInvariant()} is with.");
            }
            else
            {
                crewTime = trip.ArrTime ?? trip.DepPassTime;
            }

            // After the selected activity; a next working always goes last.
            var index = kind != ActivityKind.Next && SelectedActivity is { } selected ? Activities.IndexOf(selected) + 1 : (int?)null;
            if (_owner.TryApply(TimetableParts.AddActivity(trip, TimetableParts.NewActivity(_owner.Document, kind, with, crewTime), index)))
            {
                SelectedActivity = index is { } i && i < Activities.Count ? Activities[i] : Activities.LastOrDefault();
            }
        }
        catch (InvalidOperationException ex)
        {
            _owner.Refuse(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasActivity))]
    private void RemoveActivity()
    {
        if (SelectedActivity is { } row)
        {
            _owner.TryApply(TimetableParts.RemoveActivity(row.Node));
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveActivityUp))]
    private void MoveActivityUp() => MoveActivity(-1);

    [RelayCommand(CanExecute = nameof(CanMoveActivityDown))]
    private void MoveActivityDown() => MoveActivity(1);

    /// <summary>Goes to the train the selected activity names.</summary>
    [RelayCommand(CanExecute = nameof(HasAssociated))]
    private void GoToAssociated()
    {
        if (SelectedActivity?.With is not { Length: > 0 } with)
        {
            return;
        }

        try
        {
            if (_owner.FindTrain(with) is { } train)
            {
                _owner.GoTo(train.Element, null);
            }
        }
        catch (InvalidOperationException ex)
        {
            _owner.Refuse(ex.Message);
        }
    }

    private bool HasActivity() => SelectedActivity is not null;

    private bool HasAssociated() => SelectedActivity is { NamesATrain: true, With.Length: > 0 };

    private bool CanMoveActivityUp() => SelectedActivity is { } row && Activities.IndexOf(row) > 0;

    private bool CanMoveActivityDown() => SelectedActivity is { } row && Activities.IndexOf(row) is var i && i >= 0 && i < Activities.Count - 1;

    private void MoveActivity(int by)
    {
        if (SelectedActivity is not { } row)
        {
            return;
        }

        try
        {
            if (_owner.TryApply(TimetableParts.MoveActivity(row.Node, Activities.IndexOf(row) + by)))
            {
                SelectedActivity = row;
            }
        }
        catch (InvalidOperationException ex)
        {
            _owner.Refuse(ex.Message);
        }
    }

    private void LoadActivities(DraftSnapshot snapshot)
    {
        var trip = CurrentTrip is { } row && row.Element.Parent is not null ? new TripNode(_owner.Document, row.Element) : null;
        ActivitiesHeading = trip is null ? "Activities (choose a point in the grid)" : $"Activities at {trip.Location}";
        var wanted = new List<ActivityRow>();
        foreach (var activity in trip?.Activities ?? [])
        {
            if (!_activityRows.TryGetValue(activity.Element, out var activityRow))
            {
                _activityRows[activity.Element] = activityRow = new ActivityRow(_owner, activity.Element);
            }

            activityRow.Load();
            wanted.Add(activityRow);
        }

        if (!wanted.SequenceEqual(Activities))
        {
            var selected = SelectedActivity;
            Activities.Clear();
            foreach (var a in wanted)
            {
                Activities.Add(a);
            }

            SelectedActivity = selected is not null && Activities.Contains(selected) ? selected : null;
        }

        MoveActivityUpCommand.NotifyCanExecuteChanged();
        MoveActivityDownCommand.NotifyCanExecuteChanged();
        GoToAssociatedCommand.NotifyCanExecuteChanged();
        LoadCandidates();
        FormedBy = FormedByText(snapshot);
    }

    private void LoadCandidates()
    {
        if (CurrentTrip is not { } row || row.Element.Parent is null)
        {
            ActivityCandidates = [];
            return;
        }

        var kind = DraftParts.ActivityKinds.FirstOrDefault(k => TimetableParts.Name(k) == NewActivityKind);
        var all = _owner.TrainChoices.ToDictionary(c => c.Element);
        ActivityCandidates = [.. TimetableParts.Candidates(_owner.Snapshot.Import, new TripNode(_owner.Document, row.Element), kind)
            .Select(t => all.GetValueOrDefault(t.Element)).OfType<TrainChoice>()];
    }

    private string FormedByText(DraftSnapshot snapshot)
    {
        if (snapshot.Import.TrainOf(Node) is not { } me)
        {
            return string.Empty;
        }

        foreach (var train in snapshot.Timetable.Trains)
        {
            foreach (var point in train.Points)
            {
                foreach (var activity in point.Activities)
                {
                    if (activity.FormsAssociatedTrain && activity.Train == me.Id)
                    {
                        return $"Formed by {train.Headcode} at {point.Location} ({TimetableParts.Name(activity.Kind).ToLowerInvariant()})";
                    }
                }
            }
        }

        return string.Empty;
    }
}
