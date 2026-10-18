using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>A cell of the detail panel's plan columns that can be edited.</summary>
internal enum ScheduleField
{
    Arrive,
    Depart,
    Platform,
    Line,
}

/// <summary>One row of the detail panel: a timing point's booking, plan, actual, forecast and delay.</summary>
/// <remarks>
/// The editable cells (plan times, platform, line) do not change the row when set: they hand the text to the panel's
/// edit callback, which sends a plan command; the row shows the result when the next snapshot arrives.
/// </remarks>
internal sealed partial class ScheduleRowViewModel(int index, Action<ScheduleRowViewModel, ScheduleField, string> edit) : ObservableObject
{
    private string _planArrive = string.Empty;
    private string _planDepart = string.Empty;
    private string _platform = string.Empty;
    private string _line = string.Empty;

    public int Index { get; } = index;

    [ObservableProperty]
    public partial string Location { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Tiploc { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WttText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ActualText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ForecastText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DelayText { get; private set; } = string.Empty;

    /// <summary>Plan times differ from the WTT: shown bold.</summary>
    [ObservableProperty]
    public partial bool TimesChanged { get; private set; }

    [ObservableProperty]
    public partial bool PlatformChanged { get; private set; }

    [ObservableProperty]
    public partial bool LineChanged { get; private set; }

    /// <summary>False after a short termination: shown greyed.</summary>
    [ObservableProperty]
    public partial bool Runs { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsPass { get; private set; }

    /// <summary>The row shows arrivals over departures.</summary>
    [ObservableProperty]
    public partial bool IsTwoLine { get; private set; }

    /// <summary>The train has reported here (shown with a muted plan).</summary>
    [ObservableProperty]
    public partial bool IsPast { get; private set; }

    /// <summary>The planned arrival; setting it retimes the train from here.</summary>
    public string PlanArriveText
    {
        get => _planArrive;
        set => Edit(ScheduleField.Arrive, value, _planArrive);
    }

    /// <summary>The planned departure or pass; setting it changes the dwell (or retimes from here at an origin or pass).</summary>
    public string PlanDepartText
    {
        get => _planDepart;
        set => Edit(ScheduleField.Depart, value, _planDepart);
    }

    public string PlatformText
    {
        get => _platform;
        set => Edit(ScheduleField.Platform, value, _platform);
    }

    public string LineText
    {
        get => _line;
        set => Edit(ScheduleField.Line, value, _line);
    }

    public ScheduleRow? Row { get; private set; }

    public void Update(ScheduleRow row, StringlineProfile? profile)
    {
        Row = row;
        Location = profile?.DisplayName(row.Location) ?? row.Location.ToString();
        Tiploc = row.Location.ToString();
        // A stop with a dwell (booked or planned) shows its arrival over its departure in each column, like a timetable.
        var dwell = row.IsCall || (row.WttArrive is not null && row.WttDepart is not null);
        WttText = Times(row.WttArrive, row.WttDepart, dwell);
        Set(ref _planArrive, PlanInput.Format(row.PlanArrive), nameof(PlanArriveText));
        Set(ref _planDepart, PlanInput.Format(row.PlanDepart), nameof(PlanDepartText));
        Set(ref _platform, row.Platform ?? string.Empty, nameof(PlatformText));
        Set(ref _line, row.Line ?? string.Empty, nameof(LineText));
        ActualText = Times(row.ActualArrive, row.ActualDepart, dwell);
        ForecastText = Times(row.ForecastArrive, row.ForecastDepart, dwell);
        IsTwoLine = dwell;
        DelayText = row.DelaySeconds is { } d ? TrainListViewModel.FormatDelay(d / SimTime.SecondsPerMinute) : string.Empty;
        TimesChanged = row.TimesChanged;
        PlatformChanged = row.PlatformChanged;
        LineChanged = row.LineChanged;
        Runs = row.Runs;
        IsPass = row.IsPass;
        IsPast = row.ActualArrive is not null || row.ActualDepart is not null;
    }

    /// <summary>Arrival over departure for a stop with a dwell (either may be blank); otherwise the one time there is.</summary>
    private static string Times(SimTime? arrive, SimTime? depart, bool dwell) =>
        dwell ? $"{PlanInput.Format(arrive)}\n{PlanInput.Format(depart)}" : PlanInput.Format(arrive ?? depart);

    private void Set(ref string field, string value, string property)
    {
        if (field != value)
        {
            field = value;
            OnPropertyChanged(property);
        }
    }

    private void Edit(ScheduleField field, string? value, string current)
    {
        value = (value ?? string.Empty).Trim();
        if (value != current.Trim())
        {
            edit(this, field, value);
        }

        // Show the plan as it is until the edit comes back in a snapshot (or is refused).
        OnPropertyChanged(field switch
        {
            ScheduleField.Arrive => nameof(PlanArriveText),
            ScheduleField.Depart => nameof(PlanDepartText),
            ScheduleField.Platform => nameof(PlatformText),
            _ => nameof(LineText),
        });
    }
}

/// <summary>
/// The train detail panel (RP-1, <c>05-…md</c> §4): the selected train's schedule with the WTT, plan, actual and
/// forecast side by side, and its state. Rows are updated in place, so an edit in progress survives live updates.
/// </summary>
internal sealed partial class TrainDetailViewModel : ObservableObject
{
    private readonly Action<ScheduleRowViewModel, ScheduleField, string> _edit;
    private TrainId? _train;

    public TrainDetailViewModel(Action<TrainId, ScheduleRowViewModel, ScheduleField, string> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        _edit = (row, field, text) =>
        {
            if (_train is { } id)
            {
                edit(id, row, field, text);
            }
        };
    }

    public ObservableCollection<ScheduleRowViewModel> Rows { get; } = [];

    /// <summary>The schedule shown, if a train in the plan is selected.</summary>
    public TrainSchedule? Schedule { get; private set; }

    [ObservableProperty]
    public partial bool HasTrain { get; private set; }

    /// <summary>A live train is selected that is not matched to the WTT: there is no plan to show.</summary>
    [ObservableProperty]
    public partial bool IsLiveOnly { get; private set; }

    [ObservableProperty]
    public partial string Heading { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Colour { get; private set; } = "#8A8A8A";

    [ObservableProperty]
    public partial bool IsCancelled { get; private set; }

    /// <summary>The plan differs from the WTT (and the train is in the WTT), so it can be reset.</summary>
    [ObservableProperty]
    public partial bool CanReset { get; private set; }

    [ObservableProperty]
    public partial bool IsClone { get; private set; }

    public void Update(PlanSnapshot snapshot, GraphSelection? selection, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var schedule = selection?.Train is { } id ? TrainSchedule.For(snapshot, id) : null;
        Schedule = schedule;
        if (schedule is null)
        {
            _train = null;
            Rows.Clear();
            HasTrain = false;
            CanReset = false;
            IsCancelled = false;
            IsClone = false;
            var live = selection?.LiveUid is { } uid ? snapshot.FindByUid(uid) : null;
            IsLiveOnly = live is not null;
            Heading = live?.Headcode.ToString() ?? string.Empty;
            Description = live is null ? string.Empty : $"Live train, uid {live.SimUid}";
            StatusText = live is null ? string.Empty : $"Not matched to the WTT ({live.Current.Status}). Last at {live.Last.Location}, {live.LastSeen}.";
            Colour = GraphSceneBuilder.UnmatchedColour.ToString();
            return;
        }

        var plan = schedule.Plan;
        if (_train != plan.Id || Rows.Count != schedule.Rows.Count || !Rows.Select(r => r.Index).SequenceEqual(schedule.Rows.Select(r => r.Index)))
        {
            Rows.Clear();
            foreach (var row in schedule.Rows)
            {
                Rows.Add(new ScheduleRowViewModel(row.Index, _edit));
            }
        }

        _train = plan.Id;
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Update(schedule.Rows[i], profile);
        }

        HasTrain = true;
        IsLiveOnly = false;
        IsCancelled = plan.Cancelled;
        IsClone = plan.Origin == PlanOrigin.Clone;
        CanReset = plan.IsModified && !IsClone;
        Heading = plan.Headcode.ToString();
        Colour = (plan.Cancelled ? GraphSceneBuilder.CancelledColour : profile?.StyleFor(plan.CategoryId).Colour ?? CategoryStyle.Fallback.Colour).ToString();
        Description = DescribeTrain(schedule, profile);
        StatusText = DescribeState(schedule, profile);
    }

    private static string DescribeTrain(TrainSchedule schedule, StringlineProfile? profile)
    {
        var plan = schedule.Plan;
        var first = plan.Points.FirstOrDefault(p => p.IsTimed);
        var last = plan.RunningPoints.LastOrDefault(p => p.IsTimed);
        var route = first is null || last is null ? string.Empty
            : $"{first.LastTime} {Name(first.Location, profile)} – {Name(last.Location, profile)}";
        var what = plan.Origin == PlanOrigin.Clone ? $"Ad-hoc copy of {schedule.Plan.Source}" : schedule.Wtt?.Description ?? string.Empty;
        return string.Join(" · ", new[] { route, what }.Where(s => s.Length > 0));
    }

    private static string DescribeState(TrainSchedule schedule, StringlineProfile? profile)
    {
        var plan = schedule.Plan;
        var parts = new List<string>();
        if (plan.Cancelled)
        {
            parts.Add("Cancelled");
        }
        else if (plan.TerminatesAt is { } end)
        {
            parts.Add($"Terminates short at {Name(plan.Points[end].Location, profile)}");
        }

        if (schedule.Forecast is { } f)
        {
            var delay = f.Points.LastOrDefault(p => p.Basis is ForecastBasis.Actual or ForecastBasis.Arrived && p.DelaySeconds is not null)?.DelaySeconds;
            parts.Add(f.Status switch
            {
                TrainForecastStatus.Running => delay is { } d ? $"Running, {GraphHover.FormatDelay(d)}" : "Running",
                TrainForecastStatus.Finished => "Finished",
                TrainForecastStatus.NotStarted => "Not started",
                _ => "No forecast",
            });
            if (f.FinalDelaySeconds is { } final && f.Status != TrainForecastStatus.Finished)
            {
                parts.Add($"forecast {GraphHover.FormatDelay(final)} at the end");
            }

            if (f.KnockOnSeconds >= SimTime.SecondsPerMinute)
            {
                parts.Add($"{PlanText.Duration(f.KnockOnSeconds)} knock-on");
            }

            if (f.PossiblyStopped)
            {
                parts.Add("possibly stopped");
            }
        }

        if (plan.IsModified && plan.Origin == PlanOrigin.Wtt)
        {
            parts.Add("re-planned");
        }

        return string.Join(", ", parts);
    }

    private static string Name(Tiploc tiploc, StringlineProfile? profile) => profile?.DisplayName(tiploc) ?? tiploc.ToString();
}
